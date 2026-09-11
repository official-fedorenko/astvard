using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        internal static GameObject ZoneCategoryButton;

        internal static GameObject ZoneHint;

        internal static GameObject ZoneSizeInput;

        internal static GameObject ZoneAddButton;

        internal static GameObject ZoneClearButton;

        internal static GameObject ZoneOthersButton;

        internal static GameObject ZoneEditHint;

        internal static GameObject ZoneMoveButton;

        internal static GameObject ZoneRadiusButton;

        internal static GameObject ZoneDeleteButton;

        private const int MaxZoneButtons = 8;

        internal static readonly GameObject[] ZoneButtons = new GameObject[MaxZoneButtons];

        internal static readonly GameObject[] OwnerButtons = new GameObject[MaxZoneButtons];

        private static float _zoneReportTime = float.NegativeInfinity;

        private static float _pokeTime = float.NegativeInfinity;

        private static float _sectorCacheTime = float.NegativeInfinity;

        private static float _claimTime = float.NegativeInfinity;

        private static int _shownZoneLoaded;

        // ---------------- kept zones ----------------

        private const string RpcZoneAdd = "AstvardZoneAdd";

        private const string RpcZoneDel = "AstvardZoneDel";

        private const string RpcZoneMove = "AstvardZoneMove";

        private const string RpcZoneQuery = "AstvardZoneQuery";

        private const string RpcZoneList = "AstvardZoneList";

        private const int MinZoneRadius = 32;

        private const int MaxZoneRadius = 256;

        private static readonly System.Globalization.CultureInfo Invariant =
            System.Globalization.CultureInfo.InvariantCulture;

        private static readonly System.Reflection.MethodInfo MPokeLocalZone =
            AccessTools.Method(typeof(ZoneSystem), "PokeLocalZone");

        /// <summary>
        /// PokeLocalZone bound to the current ZoneSystem, so the poke loop can call it
        /// directly instead of through MethodInfo.Invoke.
        ///
        /// Invoke boxes every argument into a fresh object[]. A 256 m zone is nine cells
        /// square, poked twice a second, which was 162 reflection calls and 324 throwaway
        /// allocations a second for one zone - and the radius box could be left holding
        /// 256 by accident. Binding once costs one delegate per world.
        ///
        /// The delegate has to say what the method returns - whether the zone was built -
        /// even though nobody reads it. Bound as an Action it failed to bind at all, the
        /// quiet overload handed back null, and from 10.09.2026 until 11.09.2026 no kept
        /// zone had its ground poked, while its objects went on loading. Hence the log
        /// line either way, at world start, where a failure cannot hide.
        /// </summary>
        private static System.Func<Vector2s, bool> _pokeLocalZone;

        private static ZoneSystem _pokeBoundTo;

        private static System.Func<Vector2s, bool> PokeFor(ZoneSystem system)
        {
            if (system == null || MPokeLocalZone == null) return null;
            // A failed bind is kept too: this is asked thirty times a second.
            if (_pokeBoundTo == system) return _pokeLocalZone;

            _pokeBoundTo = system;
            _pokeLocalZone = (System.Func<Vector2s, bool>)System.Delegate.CreateDelegate(
                typeof(System.Func<Vector2s, bool>), system, MPokeLocalZone, false);

            if (_pokeLocalZone != null)
                Log.LogInfo("[AstvardServerMod] Kept zones: ZoneSystem.PokeLocalZone bound.");
            else
                Log.LogError("[AstvardServerMod] Kept zones: ZoneSystem.PokeLocalZone did not bind - "
                             + "its signature has moved. Kept zones are off: their objects will not load "
                             + "over ground that is not there.");

            return _pokeLocalZone;
        }

        /// <summary>
        /// An area the server keeps loaded, as a radius in metres around a point.
        /// The world's zone grid is fixed — cell i spans [64i-32, 64i+32] — so the
        /// radius is not free to land anywhere; it is rounded out to whole cells.
        /// Asking in metres rather than in cells is what keeps a base from being
        /// clipped when it happens to straddle a border.
        /// </summary>
        internal struct KeptZone
        {
            public float X;
            public float Z;
            public int Radius;
            public string Owner;

            /// <summary>
            /// A player's own zone: the platform id it belongs to. Kept in the server's
            /// config and never sent out; empty for a zone an admin made.
            /// </summary>
            public string OwnerId;
        }

        // Server: the authoritative list. Client: left empty, see ShownZones.
        private static readonly List<KeptZone> Zones = new List<KeptZone>();

        private static readonly List<KeptZone> ShownZones = new List<KeptZone>();

        private static readonly List<Minimap.PinData> ZonePins = new List<Minimap.PinData>();

        private static readonly List<ZDO> KeptZoneObjects = new List<ZDO>();

        private static string PackZones(List<KeptZone> zones, bool withIds)
        {
            var packed = new System.Text.StringBuilder();
            foreach (var zone in zones)
            {
                if (packed.Length > 0) packed.Append(';');
                packed.Append(zone.X.ToString("F1", Invariant)).Append(',')
                      .Append(zone.Z.ToString("F1", Invariant)).Append(',')
                      .Append(zone.Radius.ToString(Invariant)).Append(',')
                      .Append(CleanName(zone.Owner));
                // The id is how the server tells whose a player's zone is; nobody else
                // needs another player's platform id, so it stays in the config.
                if (withIds && !string.IsNullOrEmpty(zone.OwnerId)) packed.Append(',').Append(CleanName(zone.OwnerId));
            }
            return packed.ToString();
        }

        private static void ParseZones(string packed, List<KeptZone> into)
        {
            into.Clear();
            if (string.IsNullOrEmpty(packed)) return;

            foreach (var chunk in packed.Split(';'))
            {
                var parts = chunk.Split(',');
                if (parts.Length < 3) continue;
                if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float, Invariant, out var x)) continue;
                if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float, Invariant, out var z)) continue;
                if (!int.TryParse(parts[2], System.Globalization.NumberStyles.Integer, Invariant, out var radius)) continue;

                into.Add(new KeptZone
                {
                    X = x,
                    Z = z,
                    Radius = Mathf.Clamp(radius, MinZoneRadius, MaxZoneRadius),
                    Owner = parts.Length > 3 ? parts[3] : "?",
                    OwnerId = parts.Length > 4 ? parts[4] : "",
                });
            }
        }

        /// <summary>
        /// The cells a zone actually occupies. Rounding outwards is the whole point:
        /// a 32 m radius takes one cell when the point sits mid-cell and four when it
        /// sits on a corner, and either way the ground under the base is covered.
        /// </summary>
        // Names ride inside a comma/semicolon separated blob, so anything that would
        // split a record has to go before it is written.
        private static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            // BepInEx rewrites backslash escapes when it reloads the config, which
            // would quietly rename the owner and orphan their zones.
            return name.Replace(',', ' ').Replace(';', ' ').Replace('\\', ' ')
                       .Replace('\n', ' ').Replace('\r', ' ').Trim();
        }

        private static void ZoneCells(KeptZone zone, out Vector2s min, out Vector2s max)
        {
            // Half-open on the far side: a cell owns [centre-32, centre+32), so treating
            // the far edge as inclusive would drag in the next cell on every axis and
            // quadruple a zone that fits in one.
            const float edge = 0.01f;
            min = ZoneSystem.GetZone(new Vector3(zone.X - zone.Radius, 0f, zone.Z - zone.Radius));
            max = ZoneSystem.GetZone(new Vector3(zone.X + zone.Radius - edge, 0f, zone.Z + zone.Radius - edge));
        }

        private static int ZoneCellCount(KeptZone zone)
        {
            ZoneCells(zone, out var min, out var max);
            return (max.x - min.x + 1) * (max.y - min.y + 1);
        }

        // ---------------- rpc ----------------

        private static ZRoutedRpc _rpcRegisteredOn;

        internal static void RegisterZoneRpcs()
        {
            var rpc = ZRoutedRpc.instance;
            // Register() uses Add(), so registering twice on the same instance throws
            // — and that would happen inside Game.Start, taking the load down with it.
            if (rpc == null || ReferenceEquals(rpc, _rpcRegisteredOn)) return;
            _rpcRegisteredOn = rpc;

            // Bound now rather than at the first zone, so the log says at every start
            // whether kept zones can work on this build of the game.
            PokeFor(ZoneSystem.instance);

            rpc.Register<float, float, int>(RpcZoneAdd, OnZoneAdd);
            rpc.Register<float, float, float, float, int>(RpcZoneMove, OnZoneMove);
            rpc.Register<float, float, bool>(RpcZoneDel, OnZoneDel);
            rpc.Register(RpcZoneQuery, OnZoneQuery);
            rpc.Register<string, int>(RpcZoneList, OnZoneList);

            RegisterTemplateRpcs();
            RegisterAdminRpcs();
            RegisterPlayerFeatureRpcs(rpc);
            RegisterRoadJobRpcs(rpc);
        }

        /// <summary>
        /// Server side. Zones are a server-wide resource, so the sender's admin rights
        /// are checked here — a client-side gate would let anyone add them.
        /// </summary>
        private static readonly System.Reflection.MethodInfo MPeerByRpc =
            AccessTools.Method(typeof(ZNet), "GetPeer", new[] { typeof(ZRpc) });

        /// <summary>
        /// The sender id on a routed RPC is written by the sender, so anyone can claim to
        /// be the server or a known admin. The socket the packet physically arrived on
        /// cannot be forged, so authorisation is decided from that instead.
        /// A null socket means the call originated on this machine — the host pressing
        /// the button, or our own code — which is allowed.
        /// </summary>
        private static bool ServerAllows(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return false;

            var rpc = RoutedSenderTracker.Current;
            if (rpc == null) return true;
            if (MPeerByRpc == null) return false;

            var peer = MPeerByRpc.Invoke(ZNet.instance, new object[] { rpc }) as ZNetPeer;
            if (peer == null || peer.m_socket == null) return false;
            return ZNet.instance.IsAdmin(peer.m_socket.GetHostName());
        }

        private static void OnZoneAdd(long sender, float x, float z, int radius)
        {
            if (!ServerAllows(sender)) return;

            var owner = SenderName(sender);
            Zones.Add(new KeptZone
            {
                X = x,
                Z = z,
                Radius = Mathf.Clamp(radius, MinZoneRadius, MaxZoneRadius),
                Owner = owner
            });
            SaveZones();

            Log.LogInfo($"[AstvardServerMod] Kept zone added at {x:F0},{z:F0} radius {radius} m by {owner}.");
            BroadcastZoneList();
        }

        private static void OnZoneDel(long sender, float x, float z, bool all)
        {
            if (!ServerAllows(sender)) return;

            if (all)
            {
                Zones.Clear();
            }
            else
            {
                var best = NearestZone(x, z);
                if (best >= 0) Zones.RemoveAt(best);
            }

            SaveZones();
            Log.LogInfo($"[AstvardServerMod] Kept zones now: {Zones.Count}.");
            BroadcastZoneList();
        }

        /// <summary>
        /// Identifies a zone by where it is rather than by its index: two admins editing
        /// at once would otherwise shift each other's indices between send and arrival.
        /// </summary>
        // The client names a zone by the exact coordinates it was shown, so anything
        // further than a step away is a different zone — most likely one that moved or
        // was removed while the menu was open. Hitting the neighbour instead would
        // delete something nobody asked about.
        private const float ZoneMatchTolerance = 2f;

        private static int NearestZone(float x, float z)
        {
            var best = -1;
            var bestSqr = ZoneMatchTolerance * ZoneMatchTolerance;
            for (var i = 0; i < Zones.Count; i++)
            {
                var dx = Zones[i].X - x;
                var dz = Zones[i].Z - z;
                var sqr = dx * dx + dz * dz;
                if (sqr > bestSqr) continue;
                best = i;
                bestSqr = sqr;
            }
            return best;
        }

        private static int NearestShownZone(float x, float z)
        {
            var best = -1;
            var bestSqr = ZoneMatchTolerance * ZoneMatchTolerance;
            for (var i = 0; i < ShownZones.Count; i++)
            {
                var dx = ShownZones[i].X - x;
                var dz = ShownZones[i].Z - z;
                var sqr = dx * dx + dz * dz;
                if (sqr > bestSqr) continue;
                best = i;
                bestSqr = sqr;
            }
            return best;
        }

        private static void OnZoneMove(long sender, float oldX, float oldZ,
                                       float newX, float newZ, int radius)
        {
            if (!ServerAllows(sender)) return;

            var index = NearestZone(oldX, oldZ);
            if (index < 0) return;

            var zone = Zones[index];
            zone.X = newX;
            zone.Z = newZ;
            zone.Radius = Mathf.Clamp(radius, MinZoneRadius, MaxZoneRadius);
            Zones[index] = zone;

            SaveZones();
            Log.LogInfo($"[AstvardServerMod] Kept zone moved to {newX:F0},{newZ:F0} radius {radius} m.");
            BroadcastZoneList();
        }

        private static string SenderName(long sender)
        {
            var rpc = RoutedSenderTracker.Current;
            if (rpc == null) return "сервер";

            var peer = MPeerByRpc != null
                ? MPeerByRpc.Invoke(ZNet.instance, new object[] { rpc }) as ZNetPeer
                : null;
            return CleanName(peer != null ? peer.m_playerName : null);
        }

        private static void OnZoneQuery(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            ReplyZoneList(sender);
        }

        private static void SaveZones()
        {
            // Dropping the cache forces the next frame to rebuild from the new list,
            // so a removed zone stops being kept alive immediately.
            _zones.Value = PackZones(Zones, true);
            _sectorCacheTime = float.NegativeInfinity;
            _pokeTime = float.NegativeInfinity;
        }

        // Zone 0 means everybody, so every admin's menu updates on any change instead
        // of only the one who pressed the button.
        private static void BroadcastZoneList()
        {
            ReplyZoneList(0L);
        }

        private static void ReplyZoneList(long target)
        {
            // The object count is the only honest measure of what the zones cost, and
            // a client has no way to see it otherwise.
            var scene = ZNetScene.instance;
            var loaded = scene != null ? scene.NrOfInstances() : 0;

            ZRoutedRpc.instance?.InvokeRoutedRPC(target, RpcZoneList, PackZones(Zones, false), loaded);
        }

        private static void RequestZoneList()
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcZoneQuery);
        }

        /// <summary>Client side: remember what the server said, so the menu can show it.</summary>
        private static void OnZoneList(long sender, string packed, int loaded)
        {
            // Indices shift whenever anyone removes a zone, so the open edit page is
            // re-anchored by position — the same identity the Del/Move calls use.
            var edited = EditedZone();

            ParseZones(packed, ShownZones);
            _shownZoneLoaded = loaded;

            _editingZone = edited != null ? NearestShownZone(edited.Value.X, edited.Value.Z) : -1;
            if (_editingZone < 0 && MenuState == StateZoneEdit) MenuState = StateZone;

            UpdateZonePins();
            UpdateZoneOverlay();

            // The reply lands frames after the request, so without this the page that
            // asked for the list would keep showing the state from before it arrived.
            // RefreshMenu rebuilds the views, and the hints follow from there.
            RefreshMenu();
        }

        /// <summary>
        /// Coordinates in a menu are hard to place; pins put the zones where the player
        /// already looks. Not saved — they are re-made from the server's answer.
        /// </summary>
        private static void UpdateZonePins()
        {
            if (GUIManager.IsHeadless()) return;

            var map = Minimap.instance;
            if (map == null) return;

            foreach (var pin in ZonePins)
                if (pin != null) map.RemovePin(pin);
            ZonePins.Clear();

            for (var i = 0; i < ShownZones.Count; i++)
            {
                var zone = ShownZones[i];
                ZonePins.Add(map.AddPin(new Vector3(zone.X, 0f, zone.Z),
                    Minimap.PinType.Icon3, $"Зона {i + 1} (радиус {zone.Radius} м)",
                    false, false));
            }
        }

        private static readonly List<int> VisibleZones = new List<int>();

        private static readonly List<string> ZoneOwners = new List<string>();

        private static int _editingZone = -1;

        private static string _selectedOwner = "";

        private static KeptZone? EditedZone()
        {
            if (_editingZone < 0 || _editingZone >= ShownZones.Count) return null;
            return ShownZones[_editingZone];
        }

        private static string LocalPlayerName()
        {
            return Player.m_localPlayer != null ? CleanName(Player.m_localPlayer.GetPlayerName()) : "";
        }

        /// <summary>
        /// Works out which zones the current page lists and relabels the button pools.
        /// The pools are fixed size, so the lists move under them rather than the other
        /// way round.
        /// </summary>
        /// <summary>
        /// Whether the wipe button is waiting for its second press.
        ///
        /// One press used to be enough, and it clears every admin's zones with no undo:
        /// the coordinates only ever lived in the config string it empties. The wipe
        /// stays server-wide on purpose - zones are a shared resource here and every
        /// other button on this page already edits anybody's - so the safety is a second
        /// press rather than a narrower delete.
        ///
        /// It disarms after five seconds and whenever the page is rebuilt, so an armed
        /// button is never left lying about for the next person to lean on.
        /// </summary>
        internal static bool ZoneWipeArmed
        {
            get
            {
                return _zoneWipeArmedAt > 0f
                       && Time.realtimeSinceStartup - _zoneWipeArmedAt <= 5f;
            }
        }

        private static float _zoneWipeArmedAt;

        private static void SetLabel(GameObject button, string text)
        {
            var label = button != null ? button.GetComponentInChildren<Text>(true) : null;
            if (label != null) label.text = text;
        }

        internal static void ArmZoneWipe()
        {
            _zoneWipeArmedAt = Time.realtimeSinceStartup;
            SetLabel(ZoneClearButton, "Точно? Нажми ещё раз");
        }

        internal static void DisarmZoneWipe()
        {
            if (_zoneWipeArmedAt <= 0f) return;

            _zoneWipeArmedAt = 0f;
            SetLabel(ZoneClearButton, "Удалить все зоны сервера");
        }

        private static void RebuildZoneViews()
        {
            DisarmZoneWipe();

            var me = LocalPlayerName();

            VisibleZones.Clear();
            ZoneOwners.Clear();

            for (var i = 0; i < ShownZones.Count; i++)
            {
                var owner = ShownZones[i].Owner;
                var mine = owner == me;

                if (MenuState == StateZone && mine) VisibleZones.Add(i);
                else if (MenuState == StateZoneOwner && owner == _selectedOwner) VisibleZones.Add(i);

                if (!mine && !ZoneOwners.Contains(owner)) ZoneOwners.Add(owner);
            }

            for (var i = 0; i < MaxZoneButtons; i++)
            {
                var zoneLabel = ZoneButtons[i] != null ? ZoneButtons[i].GetComponentInChildren<Text>(true) : null;
                if (zoneLabel != null)
                    zoneLabel.text = i < VisibleZones.Count
                        ? ZoneLabel(ShownZones[VisibleZones[i]])
                        : "";

                var ownerLabel = OwnerButtons[i] != null ? OwnerButtons[i].GetComponentInChildren<Text>(true) : null;
                if (ownerLabel != null)
                    ownerLabel.text = i < ZoneOwners.Count
                        ? $"{ZoneOwners[i]} ({CountZonesBy(ZoneOwners[i])})"
                        : "";
            }

            UpdateZoneEditHint();
            UpdateZoneHint();
        }

        private static string ZoneLabel(KeptZone zone)
        {
            return $"{zone.X:F0}, {zone.Z:F0} — {zone.Radius} м";
        }

        private static int CountZonesBy(string owner)
        {
            var count = 0;
            foreach (var zone in ShownZones)
                if (zone.Owner == owner) count++;
            return count;
        }

        private static void UpdateZoneEditHint()
        {
            var label = ZoneEditHint != null ? ZoneEditHint.GetComponentInChildren<Text>(true) : null;
            if (label == null) return;

            var zone = EditedZone();
            label.text = zone == null
                ? "Зона не выбрана."
                : $"Зона {zone.Value.X:F0}, {zone.Value.Z:F0}{NEWLINE}" +
                  $"радиус {zone.Value.Radius} м, ячеек {ZoneCellCount(zone.Value)}{NEWLINE}" +
                  $"Поставил: {zone.Value.Owner}";
        }

        private const string ZoneOverlayName = "AstvardZones";

        /// <summary>
        /// A pin says where a zone is; it cannot say how far it reaches. This paints the
        /// area actually covered — cell bounds, not the requested radius — so the
        /// rounding out to whole 64 m cells is visible rather than something to be
        /// taken on trust.
        /// </summary>
        private static MinimapManager.MapOverlay _zoneOverlay;

        private static readonly List<int[]> DrawnRects = new List<int[]>();

        private static void UpdateZoneOverlay()
        {
            // A dedicated server reaches this through its own local dispatch of the
            // broadcast. Jotunn hands out a MinimapManager even headless, but the
            // textures behind it never exist there, so the guard is on the map itself.
            if (GUIManager.IsHeadless() || Minimap.instance == null) return;

            var manager = MinimapManager.Instance;
            if (manager == null) return;

            // Fetched once and kept: removing and re-fetching stacks another layer on
            // the minimap and leaks the texture behind the old one every refresh.
            if (_zoneOverlay == null) _zoneOverlay = manager.GetMapOverlay(ZoneOverlayName, true);
            if (_zoneOverlay == null || _zoneOverlay.OverlayTex == null) return;

            var tex = _zoneOverlay.OverlayTex;
            var size = _zoneOverlay.TextureSize;

            foreach (var rect in DrawnRects)
                PaintRect(tex, rect[0], rect[1], rect[2], rect[3], Color.clear, Color.clear);
            DrawnRects.Clear();

            if (ShownZones.Count == 0)
            {
                tex.Apply();
                return;
            }

            var fill = new Color(1f, 0.8f, 0.27f, 0.18f);
            var edge = new Color(1f, 0.8f, 0.27f, 0.85f);

            foreach (var zone in ShownZones)
            {
                ZoneCells(zone, out var min, out var max);

                // A cell spans its centre +/- 32 m, so the covered ground runs from the
                // low corner of the first cell to the high corner of the last.
                var low = ZoneSystem.GetZonePos(min) - new Vector3(32f, 0f, 32f);
                var high = ZoneSystem.GetZonePos(max) + new Vector3(32f, 0f, 32f);

                var a = manager.WorldToOverlayCoords(low, size);
                var b = manager.WorldToOverlayCoords(high, size);

                var rect = new[]
                {
                    Mathf.RoundToInt(Mathf.Min(a.x, b.x)), Mathf.RoundToInt(Mathf.Min(a.y, b.y)),
                    Mathf.RoundToInt(Mathf.Max(a.x, b.x)), Mathf.RoundToInt(Mathf.Max(a.y, b.y))
                };
                DrawnRects.Add(rect);
                PaintRect(tex, rect[0], rect[1], rect[2], rect[3], fill, edge);
            }

            tex.Apply();
        }

        private static void UpdateZoneHint()
        {
            var label = ZoneHint != null ? ZoneHint.GetComponentInChildren<Text>(true) : null;
            if (label == null) return;

            if (MenuState == StateZoneOwner)
            {
                label.text = VisibleZones.Count == 0
                    ? $"У игрока {_selectedOwner}{NEWLINE}зон не осталось."
                    : $"Зоны игрока {_selectedOwner}: {VisibleZones.Count}."
                      + (VisibleZones.Count > MaxZoneButtons
                          ? $"{NEWLINE}Показаны первые {MaxZoneButtons}."
                          : "");
                return;
            }

            if (ShownZones.Count == 0)
            {
                label.text = $"Зон нет: станции работают,{NEWLINE}только пока рядом игрок.{NEWLINE}" +
                             $"Встань где нужно, задай радиус{NEWLINE}и нажми «Добавить здесь».";
                return;
            }

            var text = new System.Text.StringBuilder();
            text.Append($"Зон всего: {ShownZones.Count}, объектов{NEWLINE}загружено: {_shownZoneLoaded}.{NEWLINE}");
            text.Append(VisibleZones.Count > 0
                ? $"Твоих: {VisibleZones.Count} — кнопками ниже.{NEWLINE}"
                : $"Своих зон нет.{NEWLINE}");
            if (VisibleZones.Count > MaxZoneButtons)
                text.Append($"Показаны первые {MaxZoneButtons}.{NEWLINE}");
            text.Append("Все отмечены на карте.");
            label.text = text.ToString();
        }

        // ---------------- server side application ----------------

        private static bool KeptZonesActive()
        {
            return Zones.Count > 0 && ZNet.instance != null && ZNet.instance.IsServer();
        }

        /// <summary>
        /// Terrain and vegetation live in ZoneSystem, not ZNetScene, so a kept zone has
        /// to be poked here as well — buildings standing over missing ground would be a
        /// far worse problem than not loading them at all.
        /// </summary>
        internal static void PokeKeptZones()
        {
            if (!KeptZonesActive() || MPokeLocalZone == null) return;
            if (Time.time - _pokeTime < 0.5f) return;
            _pokeTime = Time.time;

            var system = ZoneSystem.instance;
            // Update() refuses to build local zones until the world's locations exist;
            // poking from a postfix would run ahead of that and place zones the
            // generator has not finished with.
            if (system == null || !system.LocationsGenerated) return;

            // A null here means the signature moved under us; falling back to Invoke
            // would only fail the same way one line later.
            var poke = PokeFor(system);
            if (poke == null) return;

            foreach (var zone in Zones)
            {
                ZoneCells(zone, out var min, out var max);
                for (var y = min.y; y <= max.y; y++)
                for (var x = min.x; x <= max.x; x++)
                    poke(new Vector2s(x, y));
            }
        }

        /// <summary>
        /// The same list this appends to is handed to RemoveObjects straight afterwards,
        /// so adding here both creates the objects and spares them from being culled.
        /// </summary>
        internal static void AppendKeptZoneObjects(List<ZDO> currentNearObjects)
        {
            if (!KeptZonesActive() || currentNearObjects == null) return;

            // Objects only where the ground under them is poked too: buildings loaded over
            // missing terrain would come down, and that is worse than not loading them.
            if (PokeFor(ZoneSystem.instance) == null) return;

            // CreateDestroyObjects runs 30 times a second; walking the sectors that
            // often would be pure waste when the zones do not move.
            if (Time.time - _sectorCacheTime > 0.5f)
            {
                _sectorCacheTime = Time.time;
                KeptZoneObjects.Clear();

                var man = ZDOMan.instance;
                if (man != null)
                    foreach (var zone in Zones)
                    {
                        ZoneCells(zone, out var min, out var max);
                        // A zero simulation distance is exactly the one centre cell, so
                        // walking the range cell by cell gives the same set the square
                        // would, no more.
                        for (var y = min.y; y <= max.y; y++)
                        for (var x = min.x; x <= max.x; x++)
                        {
                            // Cell by cell, not only once the poke is bound: the objects of a
                            // cell whose ground is still being built wait for it. A terrain
                            // compiler woken with no heightmap under it never initialises -
                            // its first load throws - and is never found by anyone looking
                            // for the zone's compiler.
                            var cell = new Vector2s(x, y);
                            if (ZoneHasGround(cell))
                                man.FindSectorObjects(cell, new SimulationDistance(0, 0), KeptZoneObjects);
                        }
                    }

                // Overlapping zones share cells, and the same ZDO twice would have the
                // scene try to instantiate one object two times over.
                SeenZdos.Clear();
                for (var i = KeptZoneObjects.Count - 1; i >= 0; i--)
                {
                    var zdo = KeptZoneObjects[i];
                    if (zdo == null || !zdo.IsValid() || !SeenZdos.Add(zdo.m_uid))
                        KeptZoneObjects.RemoveAt(i);
                }

                ClaimKeptZoneObjects();
                ReportKeptZones();
            }

            currentNearObjects.AddRange(KeptZoneObjects);
        }

        /// <summary>
        /// Station logic only runs for the owner. Nobody hands ownership out this far
        /// from any player, so the server takes what is going spare — and only that, so
        /// a player standing in the zone keeps whatever they picked up.
        /// </summary>
        private static readonly HashSet<ZDOID> SeenZdos = new HashSet<ZDOID>();

        private static void ClaimKeptZoneObjects()
        {
            if (Time.time - _claimTime < 2f) return;
            _claimTime = Time.time;

            var id = ZDOMan.GetSessionID();
            foreach (var zdo in KeptZoneObjects)
            {
                if (zdo == null || zdo.IsOwner()) continue;

                // An owner id left behind by someone who has since disconnected never
                // clears itself, and waiting for it would park the zone forever.
                if (zdo.HasOwner() && ZNet.instance != null
                    && ZNet.instance.GetPeer(zdo.GetOwner()) != null) continue;

                zdo.SetOwner(id);
            }
        }

        private static void ReportKeptZones()
        {
            if (Time.time - _zoneReportTime < 60f) return;
            _zoneReportTime = Time.time;

            var scene = ZNetScene.instance;
            if (scene == null) return;

            // NrOfInstances only counts networked objects, so it says nothing about the
            // ground. Terrain lives in ZoneSystem and has to be checked separately —
            // buildings standing over a hole would be far worse than not loading them.
            var cells = 0;
            var loadedCells = 0;
            var system = ZoneSystem.instance;
            foreach (var zone in Zones)
            {
                ZoneCells(zone, out var min, out var max);
                for (var y = min.y; y <= max.y; y++)
                for (var x = min.x; x <= max.x; x++)
                {
                    cells++;
                    if (system != null && system.IsZoneLoaded(new Vector2s(x, y))) loadedCells++;
                }
            }

            Log.LogInfo($"[AstvardServerMod] Kept zones: {Zones.Count}, "
                        + $"terrain {loadedCells}/{cells} cells, "
                        + $"{KeptZoneObjects.Count} zdos, {scene.NrOfInstances()} objects loaded.");
        }
    }
}
