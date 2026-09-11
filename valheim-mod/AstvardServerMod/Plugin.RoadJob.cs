using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Splatform;
using UnityEngine;

namespace AstvardServerMod
{
    /// <summary>
    /// A long road, laid by the server.
    ///
    /// A client can change only the ground it has loaded - the zones round its player, 130
    /// to 190 m each way - so a road much longer than that cannot be laid from one spot:
    /// by the time the player reaches the far end, the near one is gone. The server has no
    /// such limit. It keeps every record in the world and can load ground anywhere, the way
    /// it already does for kept zones: it pokes the zones along the way, builds their
    /// objects, and lays the road a hundred metres at a time with the very code a client
    /// uses. The admin marks the two ends as always and is free to go; every other client
    /// sees the road as its zones come in.
    /// </summary>
    public partial class Plugin
    {
        private const string RpcRoadJob = "AstvardRoadJob";

        private const string RpcRoadJobStop = "AstvardRoadJobStop";

        private const string RpcRoadJobUndo = "AstvardRoadJobUndo";

        private const string RpcRoadJobNews = "AstvardRoadJobNews";

        // Past this an admin's road is the server's to lay, whatever the client could reach.
        private const float ServerRoadFrom = 100f;

        // Path points a metre apart: a piece is this many metres of road.
        private const int RoadJobPiece = 100;

        // How far either side of a piece's centreline its zones are loaded and their objects
        // built. The paint and smoothing reach about 10 m, clearing looks 8 m further, and a
        // location's own radius is 20 m by default: a ruin beside the road has to be there to
        // keep its trees standing and to keep torches out of it. Locations also flatten their
        // ground with modifiers of their own, so without them the heights the smoothing reads
        // would not be the ground anyone sees.
        private const float RoadJobMargin = 40f;

        // Undo records the server keeps: a few long roads back, whoever laid them.
        private const int RoadJobRecordsKept = 8;

        private enum RoadNews
        {
            Accepted,
            Progress,
            Done,
            Stopped,
            Failed,
            Refused,
            Undone,

            // The road asked to be undone is still going down; the asker has it back to stop.
            UndoKept,

            // Another road is going down: undo now would race its pieces for the same zones.
            UndoLater,
        }

        // ---------------- client ----------------

        internal static GameObject RoadServerStopButton;

        // The job the server is laying for this player: its id, or 0.
        private static int _roadServerJob;

        // Whether the server has taken it; until then it may yet be turned down, or not answered.
        private static bool _roadServerTaken;

        private static float _roadServerAskedAt;

        private static string _roadServerProgress = "";

        // A server undo asked for and not yet answered: the job's id, and when it was asked.
        private static int _roadUndoAsked;

        private static float _roadUndoAskedAt;

        /// <summary>A job belongs to its world: a disconnect forgets it.</summary>
        internal static void ForgetServerRoad()
        {
            _roadServerJob = 0;
            _roadServerTaken = false;
            _roadServerProgress = "";
            // Its step stays: after a reconnect it may well be undone yet.
            _roadUndoAsked = 0;
        }

        /// <summary>
        /// Hands a marked road to the server. Only the shape and the settings go: the server
        /// draws the same path from them, so a kilometre of points need not cross the wire.
        /// </summary>
        private static void StartServerRoad(Player player, Vector3 from, Vector3 to, float length,
                                            float width, float radius)
        {
            if (ZRoutedRpc.instance == null) return;

            // The start stays marked, so another press once the first road is down is all it takes.
            if (_roadServerJob != 0)
            {
                player.Message(MessageHud.MessageType.Center,
                    "Сервер ещё строит прошлую дорожку — дождись или «Остановить укладку»");
                return;
            }

            var id = Random.Range(1, int.MaxValue);

            // All of it before the call: a player hosting the world is the server, and there
            // the answer comes back inside InvokeRoutedRPC itself - before any line after it.
            _roadStarted = false;
            _roadPinned = false;
            _roadServerJob = id;
            _roadServerTaken = false;
            _roadServerAskedAt = Time.time;
            _roadServerProgress = "Дорожка ушла серверу, ждёт ответа";
            if (_roadPreview != null) _roadPreview.SetActive(false);
            player.Message(MessageHud.MessageType.Center, $"Отдаю дорожку {length:F0} м серверу…");

            var pkg = new ZPackage();
            pkg.Write(id);
            pkg.Write(from);
            pkg.Write(to);
            pkg.Write(RoadSagitta(length));
            pkg.Write(radius);
            pkg.Write(width);
            pkg.Write(_roadPaved);
            pkg.Write(RoadSmoothingActive);
            pkg.Write(RoadClearingActive);
            pkg.Write(RoadTorchesActive ? _roadTorch : 0);
            pkg.Write(TorchSpacing());
            pkg.Write(player.GetPlayerID());
            pkg.Write(PlatformManager.DistributionPlatform.LocalUser.PlatformUserID.ToString());
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcRoadJob, pkg);

            UpdateRoadHint();
            Log.LogInfo($"[AstvardServerMod] Road job {id} asked: {length:F1} m from {from} to {to}.");
        }

        /// <summary>
        /// A server without this part of the mod never answers; the player is told, and the
        /// buttons stop waiting for it.
        /// </summary>
        internal static void TickServerRoad()
        {
            if (_roadServerJob != 0 && !_roadServerTaken && Time.time - _roadServerAskedAt > 15f)
            {
                ForgetServerRoad();
                // The start was handed over; with nobody taking it, it is the player's again.
                _roadStarted = true;
                UpdateRoadHint();
                if (InventoryGui.IsVisible()) RefreshMenu();
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    "Сервер не ответил про дорожку — на нём старая версия мода?");
            }

            // An undo nobody answers would sit on top of the stack for good, and every step
            // under it with it. The server answers even a refusal, so silence means it cannot.
            if (_roadUndoAsked != 0 && Time.time - _roadUndoAskedAt > 10f)
            {
                var id = _roadUndoAsked;
                _roadUndoAsked = 0;
                UndoStack.RemoveAll(step => step.ServerJob == id);
                if (InventoryGui.IsVisible()) RefreshMenu();
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    "Сервер не ответил на откат — ступень снята, дорожка осталась как есть");
            }
        }

        private static void StopServerRoad()
        {
            if (_roadServerJob == 0) return;
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcRoadJobStop, _roadServerJob);
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Останавливаю укладку…");
        }

        private static void AskServerRoadUndo(int id)
        {
            // Before the call, for the same reason as the job itself: a host is answered inside it.
            _roadUndoAsked = id;
            _roadUndoAskedAt = Time.time;
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Откатываю дорожку на сервере…");
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcRoadJobUndo, id);
        }

        private static void OnRoadJobNews(long sender, ZPackage pkg)
        {
            int id, state;
            string text;
            Vector3 end;
            float laid;
            try
            {
                id = pkg.ReadInt();
                state = pkg.ReadInt();
                text = pkg.ReadString();
                end = pkg.ReadVector3();
                laid = pkg.ReadSingle();
            }
            catch (System.Exception e)
            {
                Log.LogWarning($"[AstvardServerMod] Road news unreadable: {e.Message}");
                return;
            }

            var mine = id != 0 && id == _roadServerJob;
            var player = Player.m_localPlayer;

            switch ((RoadNews)state)
            {
                case RoadNews.Accepted:
                    // An answer to a request already given up on is no job of this player's.
                    if (!mine) return;
                    _roadServerTaken = true;
                    _roadServerProgress = "Сервер кладёт дорожку: начал";
                    PushUndoStep(new TerrainUndoStep
                    {
                        Label = "дорожка (сервер)",
                        ServerJob = id,
                        Zones = new List<ZoneSnapshot>(),
                    });
                    break;

                case RoadNews.Progress:
                    if (!mine) return;
                    _roadServerProgress = text;
                    player?.Message(MessageHud.MessageType.TopLeft, text);
                    UpdateRoadHint();
                    return;

                case RoadNews.Done:
                case RoadNews.Stopped:
                case RoadNews.Failed:
                    if (mine) ForgetServerRoad();
                    // What went down can be carried on from, even when the road stopped short.
                    if (laid > 0f)
                    {
                        _roadHasLastEnd = true;
                        _roadLastEnd = end;
                    }
                    break;

                case RoadNews.Refused:
                    if (!mine) break;
                    ForgetServerRoad();
                    // Turned down, like a road refused here, it leaves the start marked.
                    _roadStarted = true;
                    break;

                case RoadNews.Undone:
                    // Off the stack only now, whether it came back or the server no longer
                    // knew it: either way there is nothing left to undo.
                    if (id == _roadUndoAsked) _roadUndoAsked = 0;
                    UndoStack.RemoveAll(step => step.ServerJob == id);
                    ForgetRoadEnd();
                    break;

                case RoadNews.UndoKept:
                    // Still going down - this player's again, to stop and then undo. After a
                    // reconnect this is the only way back to the button that stops it.
                    if (id == _roadUndoAsked) _roadUndoAsked = 0;
                    _roadServerJob = id;
                    _roadServerTaken = true;
                    break;

                case RoadNews.UndoLater:
                    // The step stays for when the other road is down.
                    if (id == _roadUndoAsked) _roadUndoAsked = 0;
                    break;
            }

            if (!string.IsNullOrEmpty(text)) player?.Message(MessageHud.MessageType.Center, text);
            UpdateRoadHint();
            if (InventoryGui.IsVisible()) RefreshMenu();
        }

        // ---------------- server ----------------

        private sealed class RoadJob
        {
            public int Id;
            public long Sender;
            public List<Vector3> Path;
            public float Radius;
            public float Width;
            public Color Paint;
            public bool Smooth;
            public bool Clear;
            public int Torch;
            public float Spacing;
            public long Creator;
            public string Platform;
            public bool Stop;
            public RoadJobRecord Record;

            // The zones the piece in hand needs: poked, and their objects built.
            public readonly HashSet<Vector2s> Zones = new HashSet<Vector2s>();
        }

        /// <summary>
        /// What a job changed, for undo: each compiler's record as it was before the job first
        /// wrote to it, and the torches it put up. Records rather than the five arrays a
        /// client keeps, because most of these zones will not be loaded anywhere by the time
        /// undo is asked for, and a record needs no loading to be written back.
        /// </summary>
        private sealed class RoadJobRecord
        {
            public int Id;
            public bool Running;
            public readonly Dictionary<ZDOID, byte[]> Before = new Dictionary<ZDOID, byte[]>();
            public readonly List<ZDOID> Torches = new List<ZDOID>();
        }

        private static RoadJob _roadJob;

        private static readonly List<RoadJobRecord> RoadJobRecords = new List<RoadJobRecord>();

        private static float _roadJobPokeTime = float.NegativeInfinity;

        private static readonly List<ZDO> RoadJobObjects = new List<ZDO>();

        private static readonly HashSet<ZDO> RoadJobPresent = new HashSet<ZDO>();

        private static readonly List<ZDO> RoadJobScratch = new List<ZDO>();

        private static readonly WaitForSeconds RoadJobWait = new WaitForSeconds(0.25f);

        private static void RegisterRoadJobRpcs(ZRoutedRpc rpc)
        {
            rpc.Register<ZPackage>(RpcRoadJob, OnRoadJob);
            rpc.Register<int>(RpcRoadJobStop, OnRoadJobStop);
            rpc.Register<int>(RpcRoadJobUndo, OnRoadJobUndo);
            rpc.Register<ZPackage>(RpcRoadJobNews, OnRoadJobNews);
        }

        /// <summary>The world is going: a job in hand stops where it is.</summary>
        internal static void StopRoadJobOnShutdown()
        {
            if (_roadJob != null) _roadJob.Stop = true;
        }

        private static void SendRoadNews(long target, int id, RoadNews state, string text,
                                         Vector3 end = default(Vector3), float laid = 0f)
        {
            var pkg = new ZPackage();
            pkg.Write(id);
            pkg.Write((int)state);
            pkg.Write(text ?? "");
            pkg.Write(end);
            pkg.Write(laid);
            ZRoutedRpc.instance?.InvokeRoutedRPC(target, RpcRoadJobNews, pkg);
        }

        private static void OnRoadJob(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            var job = new RoadJob { Sender = sender };
            Vector3 from, to;
            float sagitta;
            bool paved;
            try
            {
                job.Id = pkg.ReadInt();
                from = pkg.ReadVector3();
                to = pkg.ReadVector3();
                sagitta = pkg.ReadSingle();
                job.Radius = pkg.ReadSingle();
                job.Width = pkg.ReadSingle();
                paved = pkg.ReadBool();
                job.Smooth = pkg.ReadBool();
                job.Clear = pkg.ReadBool();
                job.Torch = pkg.ReadInt();
                job.Spacing = pkg.ReadSingle();
                job.Creator = pkg.ReadLong();
                job.Platform = pkg.ReadString();
            }
            catch (System.Exception e)
            {
                Log.LogWarning($"[AstvardServerMod] Road job unreadable from {SenderName(sender)}: {e.Message}");
                return;
            }

            if (!ServerAllows(sender))
            {
                Log.LogWarning($"[AstvardServerMod] Road job {job.Id} refused: {SenderName(sender)} is not an admin.");
                SendRoadNews(sender, job.Id, RoadNews.Refused, "Длинные дорожки строит сервер только админам");
                return;
            }

            if (_roadJob != null)
            {
                SendRoadNews(sender, job.Id, RoadNews.Refused,
                    "Сервер уже строит дорожку — дождись её или останови");
                return;
            }

            // Without the poke there is no ground to lay on, and waiting a minute to learn it
            // helps nobody.
            if (PokeFor(ZoneSystem.instance) == null || ZoneSystem.instance == null || !ZoneSystem.instance.LocationsGenerated)
            {
                SendRoadNews(sender, job.Id, RoadNews.Refused, "Сервер сейчас не может подгружать землю — см. его лог");
                return;
            }

            // A number that is not one gets past every comparison below - NaN is neither
            // under a limit nor over it - and would end up in the path and in the ground.
            if (!Finite(from.x) || !Finite(from.y) || !Finite(from.z) || !Finite(to.x) || !Finite(to.y)
                || !Finite(to.z) || !Finite(sagitta) || !Finite(job.Radius) || !Finite(job.Width)
                || !Finite(job.Spacing))
            {
                Log.LogWarning($"[AstvardServerMod] Road job {job.Id} from {SenderName(sender)} carried a non-number.");
                SendRoadNews(sender, job.Id, RoadNews.Refused, "Дорожка пришла битой — отметь её заново");
                return;
            }

            var length = new Vector3(to.x - from.x, 0f, to.z - from.z).magnitude;
            if (length < 1f || length > AdminMaxRoadLength)
            {
                SendRoadNews(sender, job.Id, RoadNews.Refused,
                    $"Дорожка {length:F0} м — сервер кладёт от 1 до {AdminMaxRoadLength:F0} м");
                return;
            }

            // What the client says is asked for, held to what its own page allows. The bend
            // most of all: the path has a point for every metre of chord and bulge, so a bulge
            // of any size would be a path of any size. The page's widest, a semicircle, bows
            // out half the chord.
            sagitta = Mathf.Clamp(sagitta, -0.5f * length, 0.5f * length);
            job.Radius = Mathf.Clamp(job.Radius, 0.5f, 4.5f);
            job.Width = Mathf.Clamp(job.Width, 1f, 8f);
            job.Spacing = Mathf.Clamp(job.Spacing, 4f, 50f);
            if (job.Torch < 0 || job.Torch >= TorchPrefabs.Length) job.Torch = 0;
            job.Paint = paved ? Heightmap.m_paintMaskPaved : Heightmap.m_paintMaskDirt;

            job.Path = new List<Vector3>();
            RoadPoints(from, to, sagitta, 1f, job.Path);
            if (job.Path.Count < 2)
            {
                SendRoadNews(sender, job.Id, RoadNews.Refused, "Точки слишком близко");
                return;
            }

            job.Record = new RoadJobRecord { Id = job.Id, Running = true };
            RoadJobRecords.Add(job.Record);
            for (var i = 0; i < RoadJobRecords.Count && RoadJobRecords.Count > RoadJobRecordsKept; )
            {
                if (RoadJobRecords[i].Running) i++;
                else RoadJobRecords.RemoveAt(i);
            }

            _roadJob = job;
            Instance.StartCoroutine(RunRoadJob(job));

            SendRoadNews(sender, job.Id, RoadNews.Accepted,
                $"Сервер взялся за дорожку {length:F0} м: кладёт по {RoadJobPiece} м, землю грузит сам. "
                + "Можно заниматься своим");
            Log.LogInfo($"[AstvardServerMod] Road job {job.Id} from {SenderName(sender)}: {length:F1} m, "
                        + $"brush {job.Radius:F2}, smooth={job.Smooth} clear={job.Clear} "
                        + $"torches={TorchPrefabs[job.Torch] ?? "none"}/{job.Spacing:F0} m, {job.Path.Count} points.");
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void OnRoadJobStop(long sender, int id)
        {
            if (!ServerAllows(sender)) return;

            if (_roadJob != null && _roadJob.Id == id)
            {
                _roadJob.Sender = sender;
                _roadJob.Stop = true;
            }
            // A client left thinking a job is still going gets the word that it is not.
            else SendRoadNews(sender, id, RoadNews.Stopped, "Эта дорожка уже не строится");
        }

        /// <summary>
        /// Lays a job a piece at a time: loads the ground, builds its objects, lays the paint,
        /// puts up the torches, says how far it got. Waiting happens here, between frames;
        /// the work on each piece happens all at once in <see cref="LayRoadJobPiece"/>, so it
        /// can be caught when it throws and the admin told why the road stopped.
        /// </summary>
        private static IEnumerator RunRoadJob(RoadJob job)
        {
            var flat = new List<Vec2>(job.Path.Count);
            foreach (var point in job.Path) flat.Add(new Vec2(point.x, point.z));
            var along = Geometry.Distances(flat);
            var total = along[along.Length - 1];
            var reach = job.Smooth ? job.Radius + SmoothBlend(job.Radius) : job.Radius;

            // Placed for the whole road at once and handed out by station, so the step runs
            // on across the joins instead of starting over at each piece.
            var posts = job.Torch > 0
                ? Geometry.EdgePosts(flat, job.Spacing, job.Radius + TorchMargin)
                : new List<Post>();
            var nextPost = 0;

            var laidTo = 0;
            var cleared = 0;
            var placed = 0;
            var skipped = 0;
            string failure = null;
            var pieces = 0;
            var incomplete = 0;
            var began = Time.realtimeSinceStartup;

            try
            {
                for (var first = 0; first < job.Path.Count - 1; )
                {
                    if (job.Stop || ZNet.instance == null) break;

                    var last = Mathf.Min(first + RoadJobPiece, job.Path.Count - 1);
                    var piece = job.Path.GetRange(first, last - first + 1);
                    var pieceBegan = Time.realtimeSinceStartup;

                    SetRoadJobZones(job, piece);

                    // Ground first, and without it no road: there is nothing to lay the paint in.
                    var deadline = Time.time + 60f;
                    while (!RoadJobHasGround(job) && !job.Stop && Time.time < deadline && ZNet.instance != null)
                        yield return RoadJobWait;
                    if (job.Stop || ZNet.instance == null) break;
                    if (!RoadJobHasGround(job))
                    {
                        failure = "земля не подгрузилась за минуту";
                        break;
                    }

                    // Then the objects, which come in as the scene gets to them, a few dozen a
                    // frame, and a location's own prefab, which loads on its own time. A minute
                    // of neither is a zone that will not finish; the road goes on without it, and
                    // a compiler that did not wake still stops it below.
                    deadline = Time.time + 60f;
                    while ((RoadJobObjectsPending(job) > 0 || !RoadJobZonesSettled(job))
                           && !job.Stop && Time.time < deadline && ZNet.instance != null)
                        yield return RoadJobWait;
                    if (job.Stop || ZNet.instance == null) break;

                    // Laid without all of it, the piece goes without the parts that ask what
                    // stands there. A ruin whose prefab is still loading has no Location yet, so
                    // clearing would take its trees and a torch could go up inside it.
                    var pending = RoadJobObjectsPending(job);
                    var complete = pending == 0 && RoadJobZonesSettled(job);
                    if (!complete)
                    {
                        incomplete++;
                        Log.LogWarning($"[AstvardServerMod] Road job {job.Id}: {pending} objects still not built "
                                       + "after a minute; laying the paint only.");
                    }

                    // A frame for what has just woken - compilers loading their edits, locations
                    // flattening their ground - to reach the heightmaps the smoothing reads.
                    yield return null;
                    yield return null;

                    // Asked again: a stop or a shutdown that came in those two frames would
                    // otherwise still get a whole piece laid.
                    if (job.Stop || ZNet.instance == null || ZDOMan.instance == null) break;

                    failure = LayRoadJobPiece(job, piece, reach, complete, ref cleared);
                    if (failure != null) break;

                    laidTo = last;
                    pieces++;

                    if (posts.Count > 0)
                    {
                        // A frame on, so the rays that find the torches' footing hit the new ground.
                        yield return null;
                        if (ZNet.instance == null || ZNetScene.instance == null) break;

                        // A stop that came in that frame still gets this piece's torches: the
                        // piece is down, and a stretch without them would look unfinished.
                        var batch = new List<Post>();
                        var end = last == job.Path.Count - 1 ? float.MaxValue : along[last];
                        while (nextPost < posts.Count && posts[nextPost].Station <= end) batch.Add(posts[nextPost++]);
                        if (complete) PlaceRoadJobTorches(job, batch, ref placed, ref skipped);
                        else skipped += batch.Count;
                    }

                    Log.LogInfo($"[AstvardServerMod] Road job {job.Id}: {along[first]:F0}-{along[last]:F0} m laid, "
                                + $"{job.Zones.Count} zones, {Time.realtimeSinceStartup - pieceBegan:F1} s.");
                    SendRoadNews(job.Sender, job.Id, RoadNews.Progress,
                        $"Сервер кладёт дорожку: {along[last]:F0} из {total:F0} м", job.Path[last], along[last]);

                    first = last;
                }
            }
            finally
            {
                // Let go of the ground: what nothing else keeps unloads a few seconds later.
                job.Zones.Clear();
                job.Record.Running = false;
                if (_roadJob == job) _roadJob = null;
            }

            var laid = along[laidTo];
            var note = (job.Smooth && laid > 0f ? ", сглажена" : "")
                       + (cleared > 0 ? $", снесено: {cleared}" : "")
                       + (job.Torch > 0 ? $", факелов: {placed}" + (skipped > 0 ? $", {skipped} некуда поставить" : "") : "")
                       + (incomplete > 0 && (job.Clear || job.Torch > 0)
                           ? $", кусков без сноса и факелов: {incomplete} — там не догрузились объекты"
                           : "");

            if (failure != null)
                SendRoadNews(job.Sender, job.Id, RoadNews.Failed,
                    $"Дорожка встала на {laid:F0} из {total:F0} м: {failure}{note}", job.Path[laidTo], laid);
            else if (laidTo < job.Path.Count - 1)
                SendRoadNews(job.Sender, job.Id, RoadNews.Stopped,
                    $"Укладка остановлена: {laid:F0} из {total:F0} м{note}", job.Path[laidTo], laid);
            else
                SendRoadNews(job.Sender, job.Id, RoadNews.Done,
                    $"Дорожка {total:F0} м готова, ширина {job.Width:F1} м{note}", job.Path[laidTo], laid);

            Log.LogInfo($"[AstvardServerMod] Road job {job.Id} {(failure != null ? "failed: " + failure : laidTo < job.Path.Count - 1 ? "stopped" : "done")}: "
                        + $"{laid:F0}/{total:F0} m in {pieces} pieces, {Time.realtimeSinceStartup - began:F0} s, "
                        + $"{job.Record.Before.Count} zones, cleared {cleared}, torches {placed} placed {skipped} skipped.");
        }

        /// <summary>
        /// Every zone within the margin of the piece's centreline. Probes half a zone apart over
        /// the square round a point every few metres cannot step over a 64 m zone.
        /// </summary>
        private static void SetRoadJobZones(RoadJob job, List<Vector3> piece)
        {
            job.Zones.Clear();
            for (var i = 0; i < piece.Count; i += 8) AddZonesAround(job.Zones, piece[i], RoadJobMargin);
            AddZonesAround(job.Zones, piece[piece.Count - 1], RoadJobMargin);
        }

        private static void AddZonesAround(HashSet<Vector2s> into, Vector3 at, float margin)
        {
            const float step = 32f;
            for (var dx = -margin; dx < margin + step; dx += step)
                for (var dz = -margin; dz < margin + step; dz += step)
                    into.Add(ZoneSystem.GetZone(at + new Vector3(Mathf.Min(dx, margin), 0f, Mathf.Min(dz, margin))));
        }

        /// <summary>Whether the zone's ground is built here - its heightmap is up.</summary>
        private static bool ZoneHasGround(Vector2s zone)
        {
            return Heightmap.FindHeightmap(ZoneSystem.GetZonePos(zone)) != null;
        }

        private static bool RoadJobHasGround(RoadJob job)
        {
            foreach (var zone in job.Zones)
                if (!ZoneHasGround(zone)) return false;
            return true;
        }

        /// <summary>No zone of the job still waiting on a location's prefab to load.</summary>
        private static bool RoadJobZonesSettled(RoadJob job)
        {
            var system = ZoneSystem.instance;
            if (system == null) return false;

            foreach (var zone in job.Zones)
                if (!system.IsZoneLoaded(zone)) return false;
            return true;
        }

        /// <summary>Records in the job's zones the scene has still to build here.</summary>
        private static int RoadJobObjectsPending(RoadJob job)
        {
            var man = ZDOMan.instance;
            var scene = ZNetScene.instance;
            if (man == null || scene == null) return 0;

            RoadJobScratch.Clear();
            foreach (var zone in job.Zones)
                man.FindSectorObjects(zone, new SimulationDistance(0, 0), RoadJobScratch);

            var pending = 0;
            foreach (var zdo in RoadJobScratch)
            {
                // A record of a prefab this game does not have is never built; the scene
                // deletes it on the server instead, and waiting for it would be forever.
                if (zdo == null || !zdo.IsValid() || zdo.Created) continue;
                var prefab = zdo.GetPrefab();
                if (prefab != 0 && scene.HasPrefab(prefab)) pending++;
            }

            return pending;
        }

        /// <summary>
        /// Keeps the job's ground loaded, from ZoneSystem.Update. A zone nobody has visited is
        /// generated as it is built - its whole forest placed in one call - so no more than
        /// two new ones a round.
        /// </summary>
        internal static void PokeRoadJobZones()
        {
            var job = _roadJob;
            if (job == null || job.Zones.Count == 0) return;
            if (Time.time - _roadJobPokeTime < 0.25f) return;
            _roadJobPokeTime = Time.time;

            var system = ZoneSystem.instance;
            if (system == null || !system.LocationsGenerated) return;

            var poke = PokeFor(system);
            if (poke == null) return;

            var built = 0;
            foreach (var zone in job.Zones)
            {
                if (ZoneHasGround(zone)) poke(zone);
                else if (built < 2 && poke(zone)) built++;
            }
        }

        /// <summary>
        /// Adds the job's objects to the ones the scene keeps, from ZNetScene.CreateObjects -
        /// as kept zones do, and for the same reason: the list is handed on to RemoveObjects,
        /// so adding here both builds the objects and spares them.
        ///
        /// Only over ground that is there. A terrain compiler that wakes where its zone has no
        /// heightmap never initialises and never joins the registry: the zone's edits are
        /// missing from the ground here, and the job, finding the compiler's record with no
        /// live compiler for it, has to stop rather than make a second one. And each record
        /// once: the same one twice is two objects for one record.
        /// </summary>
        internal static void AppendRoadJobObjects(List<ZDO> near)
        {
            var job = _roadJob;
            if (job == null || near == null || ZDOMan.instance == null || job.Zones.Count == 0) return;

            RoadJobObjects.Clear();
            foreach (var zone in job.Zones)
                if (ZoneHasGround(zone))
                    ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(0, 0), RoadJobObjects);
            if (RoadJobObjects.Count == 0) return;

            RoadJobPresent.Clear();
            foreach (var zdo in near) RoadJobPresent.Add(zdo);
            foreach (var zdo in RoadJobObjects)
                if (zdo != null && zdo.IsValid() && RoadJobPresent.Add(zdo)) near.Add(zdo);
        }

        private static readonly System.Reflection.MethodInfo MTerrainCheckLoad =
            AccessTools.Method(typeof(TerrainComp), "CheckLoad");

        /// <summary>
        /// The work on one piece, all in one frame: compilers, the undo record, clearing,
        /// smoothing, paint, save, fresh heightmaps. The reason it stopped, or null.
        /// <paramref name="complete"/> false - some of the piece's objects never came - lays
        /// the paint and the smoothing and leaves out the clearing.
        /// </summary>
        private static string LayRoadJobPiece(RoadJob job, List<Vector3> piece, float reach, bool complete,
                                              ref int cleared)
        {
            if (ZDOMan.instance == null || ZNetScene.instance == null) return "сервер закрывается";

            try
            {
                var comps = RoadJobComps(piece, reach, out var failure);
                if (comps == null) return failure;

                var save = AccessTools.Method(typeof(TerrainComp), "Save");
                foreach (var comp in comps)
                {
                    // A write to the record since the compiler's own Update this frame - a
                    // player's hoe nearby, say - would otherwise be saved over from arrays that
                    // never saw it. CheckLoad reloads them only when the record has moved on.
                    MTerrainCheckLoad?.Invoke(comp, null);
                    RememberBefore(job.Record, comp, save);
                }

                if (job.Clear && complete) cleared += ClearAlongPath(piece, job.Radius, true);
                if (job.Smooth) SmoothAlongPath(piece, comps, job.Radius);

                foreach (var comp in comps)
                {
                    var target = MakeTarget(comp);
                    if (target != null) PaintStretch(target, piece, 0, piece.Count - 1, job.Radius, job.Paint);
                }

                // Save gained an optional paintOnly in 1.0; reflection does not fill it in.
                foreach (var comp in comps) save.Invoke(comp, new object[] { false });

                // The next piece reads these heights: its smoothing starts where this one ended.
                // Measured to the piece's furthest point, not along its chord - on a tight bend
                // the ends curl round well away from the middle.
                var mid = piece[piece.Count / 2];
                var spread = 0f;
                foreach (var point in piece)
                    spread = Mathf.Max(spread, new Vector3(point.x - mid.x, 0f, point.z - mid.z).magnitude);
                RebuildHeightmaps(mid, spread + reach + 16f);
                return null;
            }
            catch (System.Exception e)
            {
                Log.LogError($"[AstvardServerMod] Road job {job.Id} piece failed: {e}");
                return "ошибка на сервере, см. его лог";
            }
        }

        /// <summary>
        /// The compilers of every zone the piece writes, made where there are none. The server
        /// holds every record in the world, so there is no guessing here of the kind a client
        /// has to do: a compiler record that is not live after its zone's objects are built is
        /// one that failed to wake, and making a second beside it would have the two remove
        /// each other later - so the road stops instead.
        /// </summary>
        private static List<TerrainComp> RoadJobComps(List<Vector3> piece, float reach, out string failure)
        {
            failure = null;

            var zones = new HashSet<Vector2s>();
            foreach (var point in piece)
            {
                zones.Add(ZoneSystem.GetZone(point + new Vector3(-reach, 0f, -reach)));
                zones.Add(ZoneSystem.GetZone(point + new Vector3(reach, 0f, -reach)));
                zones.Add(ZoneSystem.GetZone(point + new Vector3(-reach, 0f, reach)));
                zones.Add(ZoneSystem.GetZone(point + new Vector3(reach, 0f, reach)));
            }

            var comps = new List<TerrainComp>();
            var missing = new List<Vector2s>();
            var records = new List<ZDO>();
            foreach (var zone in zones)
            {
                var comp = TerrainComp.FindTerrainCompiler(ZoneSystem.GetZonePos(zone));
                if (comp != null)
                {
                    comps.Add(comp);
                    continue;
                }

                records.Clear();
                ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(0, 0), records);
                foreach (var zdo in records)
                {
                    if (zdo.GetPrefab() != TerrainCompilerHash) continue;
                    failure = $"рельеф зоны {zone.x},{zone.y} не поднялся на сервере";
                    return null;
                }

                missing.Add(zone);
            }

            foreach (var zone in missing)
            {
                var comp = CreateTerrainCompiler(ZoneSystem.GetZonePos(zone));
                if (comp == null)
                {
                    failure = "не вышло создать рельеф зоны";
                    return null;
                }

                comps.Add(comp);
            }

            foreach (var comp in comps)
            {
                var view = comp.GetComponent<ZNetView>();
                if (view != null && view.IsValid() && !view.IsOwner()) view.ClaimOwnership();
            }

            return comps;
        }

        /// <summary>
        /// Keeps a compiler's record as it was before the job first wrote to it. A compiler
        /// that has never saved - one just made - has no record to go back to, so it is saved
        /// as it stands first, untouched: written back, that clean record has every client
        /// redraw the zone as it was.
        /// </summary>
        private static void RememberBefore(RoadJobRecord record, TerrainComp comp, System.Reflection.MethodInfo save)
        {
            var view = comp.GetComponent<ZNetView>();
            var zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null || record.Before.ContainsKey(zdo.m_uid)) return;

            var bytes = zdo.GetByteArray(ZDOVars.s_TCData);
            if (bytes == null)
            {
                save.Invoke(comp, new object[] { false });
                bytes = zdo.GetByteArray(ZDOVars.s_TCData);
            }

            if (bytes != null) record.Before[zdo.m_uid] = (byte[])bytes.Clone();
        }

        /// <summary>
        /// The torches for one piece. An admin's road, so free and full of fuel, and wards do
        /// not stop them - as the hammer would not stop an admin. The creator is the admin,
        /// as if they had put each one up by hand.
        /// </summary>
        private static void PlaceRoadJobTorches(RoadJob job, List<Post> posts, ref int placed, ref int skipped)
        {
            if (posts.Count == 0) return;

            try
            {
                var name = TorchPrefabs[job.Torch];
                var prefab = ZNetScene.instance != null && name != null ? ZNetScene.instance.GetPrefab(name) : null;
                if (prefab == null)
                {
                    skipped += posts.Count;
                    return;
                }

                var lift = PivotAboveBase(prefab);
                var platform = new PlatformUserID(job.Platform ?? "");

                foreach (var post in posts)
                {
                    if (!FindTorchSpot(post, true, out var spot))
                    {
                        skipped++;
                        continue;
                    }

                    var go = Instantiate(prefab, spot + Vector3.up * (lift - TorchSink), Quaternion.identity);
                    go.GetComponent<Piece>()?.SetCreator(job.Creator, platform);

                    var fire = go.GetComponentInChildren<Fireplace>();
                    if (fire != null && !fire.m_infiniteFuel) fire.SetFuel(fire.m_maxFuel);

                    var view = go.GetComponent<ZNetView>();
                    if (view != null && view.IsValid()) job.Record.Torches.Add(view.GetZDO().m_uid);
                    placed++;
                }
            }
            catch (System.Exception e)
            {
                // Torches are the road's trimmings; a road is worth finishing without them.
                Log.LogError($"[AstvardServerMod] Road job {job.Id} torches failed: {e}");
            }
        }

        /// <summary>
        /// Puts back what a job changed: each compiler's record as it was, and its torches
        /// taken down. Written straight into the records, which is all a client needs to see
        /// it - one with the zone loaded reloads it on the spot, the rest when they get there.
        /// Clearing is not undone, as on a client: what it took out is gone.
        /// </summary>
        private static void OnRoadJobUndo(long sender, int id)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            // Always an answer, a refusal too: the asker keeps the step on top of its stack
            // until one comes, and every step under it waits on it.
            if (!ServerAllows(sender))
            {
                SendRoadNews(sender, id, RoadNews.Undone, "Откатывать дорожки сервера может только админ");
                return;
            }

            if (_roadJob != null)
            {
                if (_roadJob.Id == id)
                {
                    // Its news goes to whoever asked last: after a reconnect that is a new peer id.
                    _roadJob.Sender = sender;
                    SendRoadNews(sender, id, RoadNews.UndoKept,
                        "Эта дорожка ещё строится — «Остановить укладку», потом откатывай");
                }
                else
                {
                    // A piece of the running road can be writing the very zone this would put
                    // back, from arrays loaded before the write-back - and then wins.
                    SendRoadNews(sender, id, RoadNews.UndoLater,
                        "Сервер сейчас кладёт другую дорожку — откати, когда он закончит");
                }

                return;
            }

            var record = RoadJobRecords.Find(r => r.Id == id);
            if (record == null)
            {
                SendRoadNews(sender, id, RoadNews.Undone, "Сервер уже не помнит эту дорожку — откатить нечего");
                return;
            }

            RoadJobRecords.Remove(record);

            var session = ZDOMan.GetSessionID();
            var zones = 0;
            var lost = 0;
            foreach (var entry in record.Before)
            {
                var zdo = ZDOMan.instance.GetZDO(entry.Key);
                if (zdo == null)
                {
                    lost++;
                    continue;
                }

                zdo.SetOwner(session);
                zdo.Set(ZDOVars.s_TCData, entry.Value);
                zones++;
            }

            var torches = 0;
            foreach (var torch in record.Torches)
            {
                var zdo = ZDOMan.instance.GetZDO(torch);
                if (zdo == null) continue;

                var go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(torch) : null;
                if (go != null)
                {
                    go.GetComponent<ZNetView>()?.ClaimOwnership();
                    ZNetScene.instance.Destroy(go);
                }
                else
                {
                    zdo.SetOwner(session);
                    ZDOMan.instance.DestroyZDO(zdo);
                }

                torches++;
            }

            SendRoadNews(sender, id, RoadNews.Undone,
                $"Откат: дорожка (сервер) — {zones} зон"
                + (torches > 0 ? $", факелов убрано: {torches}" : "")
                + (lost > 0 ? $", {lost} зон уже нет" : ""));
            Log.LogInfo($"[AstvardServerMod] Road job {id} undone: {zones} zones written back, {lost} gone, "
                        + $"{torches} torches taken down.");
        }
    }
}
