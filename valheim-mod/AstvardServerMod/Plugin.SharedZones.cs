using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>
        /// Зона сортировки, которую можно разделить с другими.
        ///
        /// A zone lived in the config of whoever drew it, and everything that followed from
        /// it lived there too: the labels over the chests and the sorting itself. On a server
        /// where people keep one base together that is the wrong owner - a cart wheeled home
        /// stays full because the one man whose config holds the zone is not online tonight.
        ///
        /// So the zones move to the server, each with the person who put it down and the
        /// people written into it. The marks on the chests were shared from the start - they
        /// live in the chests - so this was the last piece that was not.
        ///
        /// The server keeps them and pushes each player the ones they are in, comparing
        /// against what that peer was last sent: a base where nothing changes costs one
        /// string comparison a second. The client takes what it is given and stops reading
        /// its own config; until a server ever answers, everything works the old way.
        /// </summary>
        private const string RpcZonesQuery = "AstvardZonesQuery";

        private const string RpcZones = "AstvardZones";

        private const string RpcZonePut = "AstvardZonePut";

        private const string RpcZoneDrop = "AstvardZoneDrop";

        private const string RpcZoneInvite = "AstvardZoneInvite";

        private const string RpcZoneStrike = "AstvardZoneStrike";

        private const string RpcZoneSay = "AstvardZoneSay";

        private const string ZonesFile = "astvard-zones.txt";

        private const float ZonesTick = 1f;

        internal static void RegisterSharedZoneRpcs(ZRoutedRpc rpc)
        {
            rpc.Register(RpcZonesQuery, (System.Action<long>)OnZonesQuery);
            rpc.Register<string>(RpcZones, OnZones);
            rpc.Register<string>(RpcZonePut, OnZonePut);
            rpc.Register<int>(RpcZoneDrop, OnZoneDrop);
            rpc.Register<int, string>(RpcZoneInvite, OnZoneInvite);
            rpc.Register<int, string>(RpcZoneStrike, OnZoneStrike);
            rpc.Register<string>(RpcZoneSay, OnZoneSay);
        }

        // ---------------- server ----------------

        private static readonly List<Sorting.Zone> ServerZones = new List<Sorting.Zone>();

        private static bool _serverZonesRead;

        private static int _nextZoneId;

        private static float _zonesTickAt;

        // What each peer was last sent, so the tick sends only what changed.
        private static readonly Dictionary<long, string> ZonesSent = new Dictionary<long, string>();

        private static string ZonesPath
        {
            get { return Path.Combine(Paths.ConfigPath, ZonesFile); }
        }

        private static void LoadServerZones()
        {
            if (_serverZonesRead) return;
            _serverZonesRead = true;

            try
            {
                if (!File.Exists(ZonesPath)) return;

                // One zone to a line, so a torn record costs one zone and not the file.
                foreach (var line in File.ReadAllLines(ZonesPath))
                {
                    var read = Sorting.Parse(line);
                    if (read.Count != 1 || read[0].Id <= 0) continue;

                    ServerZones.Add(read[0]);
                    if (read[0].Id > _nextZoneId) _nextZoneId = read[0].Id;
                }

                Log.LogInfo($"[AstvardServerMod] Sorting zones: {ServerZones.Count} read.");
            }
            catch (System.Exception bad)
            {
                Log.LogWarning($"[AstvardServerMod] Sorting zones could not be read: {bad.Message}");
            }
        }

        private static void SaveServerZones()
        {
            try
            {
                var lines = new List<string>();
                foreach (var zone in ServerZones) lines.Add(Sorting.Pack(new[] { zone }));

                File.WriteAllLines(ZonesPath, lines.ToArray());
            }
            catch (System.Exception bad)
            {
                Log.LogWarning($"[AstvardServerMod] Sorting zones could not be written: {bad.Message}");
            }
        }

        private static int IndexOfZone(int id)
        {
            for (var i = 0; i < ServerZones.Count; i++)
                if (ServerZones[i].Id == id) return i;

            return -1;
        }

        private static int ZonesOwnedBy(string who)
        {
            var count = 0;
            foreach (var zone in ServerZones)
                if (zone.Owner == who) count++;

            return count;
        }

        /// <summary>
        /// Who carries things in each zone this second. One client, because two of them
        /// taking the same stack out of the same cart is an item made from nothing or an item
        /// gone, and neither leaves a line in any log.
        ///
        /// Position comes from what the client itself reports - the same number the game
        /// trusts to decide what to load for them - so somebody the server counts as standing
        /// in the zone is exactly somebody who has its chests loaded.
        /// </summary>
        internal static void TickSharedZones()
        {
            var net = ZNet.instance;
            if (net == null || !net.IsServer()) return;

            var now = Time.realtimeSinceStartup;
            if (now - _zonesTickAt < ZonesTick) return;
            _zonesTickAt = now;

            var peers = net.GetPeers();
            if (peers == null) return;

            LoadServerZones();

            var driver = new Dictionary<int, string>();
            foreach (var zone in ServerZones)
            {
                string best = null;

                foreach (var peer in peers)
                {
                    if (peer == null || !peer.IsReady() || peer.m_socket == null) continue;

                    var id = Sorting.CleanId(peer.m_socket.GetHostName());
                    if (id.Length == 0 || !Sorting.Sees(zone, id)) continue;
                    // Та же кайма, что у клиента. Разойдись эти два правила - и
                    // подошедший к зоне считал бы, что работает, а сервер держал бы её за
                    // ничьей: зона стоит, и сказать об этом некому.
                    if (!Sorting.Near(zone, peer.m_refPos.x, peer.m_refPos.z,
                            Sorting.NearReach)) continue;

                    if (best == null || string.CompareOrdinal(id, best) < 0) best = id;
                }

                if (best != null) driver[zone.Id] = best;
            }

            var here = new HashSet<long>();
            foreach (var peer in peers)
            {
                if (peer == null || !peer.IsReady() || peer.m_socket == null) continue;

                var id = Sorting.CleanId(peer.m_socket.GetHostName());
                if (id.Length == 0) continue;

                here.Add(peer.m_uid);
                SendSortKinds(peer);
                SendSortCats(peer);
                SendSky(peer);

                var theirs = new List<Sorting.Zone>();
                foreach (var zone in ServerZones)
                {
                    if (!Sorting.Sees(zone, id)) continue;

                    var copy = zone.Copy();
                    string drives;
                    copy.Drive = driver.TryGetValue(zone.Id, out drives) && drives == id;
                    theirs.Add(copy);
                }

                var packed = ZoneNames(theirs, id) + "\n" + Sorting.Pack(theirs);

                string last;
                if (ZonesSent.TryGetValue(peer.m_uid, out last) && last == packed) continue;

                ZonesSent[peer.m_uid] = packed;
                ZRoutedRpc.instance?.InvokeRoutedRPC(peer.m_uid, RpcZones, packed);
            }

            if (ZonesSent.Count <= here.Count) return;

            // Somebody left: their entry would otherwise keep a stale answer waiting for
            // whoever is given that id next.
            var gone = new List<long>();
            foreach (var pair in ZonesSent)
                if (!here.Contains(pair.Key)) gone.Add(pair.Key);

            foreach (var uid in gone)
            {
                ZonesSent.Remove(uid);
                SortKindsSent.Remove(uid);
                SkySent.Remove(uid);
            }
        }

        /// <summary>
        /// Who the ids in these zones are, by name, and which of them is the man being
        /// written to. Names come from the purses, which the server has kept for everyone who
        /// ever played here, so somebody written in months ago still reads as a person.
        ///
        /// Names and not ids on the screen is the whole point of this line: the panel hangs
        /// in front of every player, and a platform id is the one thing in a zone that is
        /// nobody else's business.
        /// </summary>
        private static string ZoneNames(List<Sorting.Zone> zones, string me)
        {
            var text = new System.Text.StringBuilder();
            text.Append("me=").Append(me);

            var said = new HashSet<string>();
            foreach (var zone in zones)
            {
                var all = new List<string> { zone.Owner };
                all.AddRange(zone.Members);

                foreach (var who in all)
                {
                    if (string.IsNullOrEmpty(who) || !said.Add(who)) continue;

                    text.Append('|').Append(who).Append('=').Append(PlainName(NameOfPurse(who)));
                }
            }

            return text.ToString();
        }

        /// <summary>
        /// Имя без разделителей этой строки. Ник в Steam бывает любым, и «a|b» разорвал
        /// бы список имён так, что половина зоны стала бы безымянной.
        /// </summary>
        private static string PlainName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";

            var kept = new System.Text.StringBuilder();
            foreach (var symbol in name)
            {
                if (symbol == '|' || symbol == '=' || symbol == '\n' || symbol == '\r') continue;
                kept.Append(symbol);
            }

            return kept.ToString();
        }

        /// <summary>Forgetting what they were sent is enough: the next tick sends it again.</summary>
        private static void OnZonesQuery(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            ZonesSent.Remove(ReplyTarget(sender));
        }

        /// <summary>
        /// A zone put down or changed. The client sends what it wants the zone to look like;
        /// the owner and the people written into it stay the server's to keep, because a
        /// client that could write those could write itself into somebody else's base.
        /// </summary>
        private static void OnZonePut(long sender, string packed)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            var who = Sorting.CleanId(SenderHostName());
            if (who.Length == 0) return;

            var wanted = Sorting.Parse(packed);
            if (wanted.Count != 1) return;

            LoadServerZones();

            var zone = wanted[0];
            zone.Drive = false;
            var at = IndexOfZone(zone.Id);
            var admin = ServerAllows(sender);

            if (at < 0)
            {
                if (ZonesOwnedBy(who) >= Sorting.MaxZones)
                {
                    SayAboutZone(sender, "Больше зон уже нельзя");
                    return;
                }

                zone.Id = ++_nextZoneId;
                zone.Owner = who;
                zone.Members = new List<string>();
            }
            else
            {
                var old = ServerZones[at];
                if (!Sorting.MayEdit(old, who, admin))
                {
                    SayAboutZone(sender, "Эта зона не твоя");
                    return;
                }

                zone.Owner = old.Owner;
                zone.Members = new List<string>(old.Members);
            }

            // Against every zone on the server, not only this player's: a chest inside two
            // zones has two hosts, and whose they are makes no difference to the chest.
            for (var i = 0; i < ServerZones.Count; i++)
            {
                if (i == at || !Sorting.Overlap(zone, ServerZones[i])) continue;

                SayAboutZone(sender, "Здесь она налезет на другую зону");
                return;
            }

            if (at < 0) ServerZones.Add(zone);
            else ServerZones[at] = zone;

            SaveServerZones();
            Log.LogInfo($"[AstvardServerMod] Sorting zone {zone.Id} "
                        + (at < 0 ? "put down" : "changed") + $" by {who}.");
        }

        private static void OnZoneDrop(long sender, int id)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            var who = Sorting.CleanId(SenderHostName());
            if (who.Length == 0) return;

            LoadServerZones();

            var at = IndexOfZone(id);
            if (at < 0) return;

            if (!Sorting.MayEdit(ServerZones[at], who, ServerAllows(sender)))
            {
                SayAboutZone(sender, "Эта зона не твоя");
                return;
            }

            ServerZones.RemoveAt(at);
            SaveServerZones();
            Log.LogInfo($"[AstvardServerMod] Sorting zone {id} taken away by {who}.");
        }

        /// <summary>
        /// Writing somebody into a zone - by the name they are playing under, and only while
        /// they are on. The name is resolved here, against the sockets, so the id of the man
        /// being written in never travels to anybody's client. Someone who has never been
        /// here while you were cannot be written in, and that is the point of it.
        /// </summary>
        private static void OnZoneInvite(long sender, int id, string name)
        {
            var zone = ZoneToEdit(sender, id);
            if (zone == null) return;

            var peer = PeerByPlayerName(name);
            if (peer == null || peer.m_socket == null)
            {
                SayAboutZone(sender, "Такого игрока нет в игре");
                return;
            }

            var whom = Sorting.CleanId(peer.m_socket.GetHostName());
            if (whom.Length == 0) return;

            if (whom == zone.Owner || zone.Members.Contains(whom))
            {
                SayAboutZone(sender, "Он уже в этой зоне");
                return;
            }

            if (zone.Members.Count >= Sorting.MaxMembers)
            {
                SayAboutZone(sender, "В зоне уже некуда вписывать");
                return;
            }

            zone.Members.Add(whom);
            SaveServerZones();
            SayAboutZone(sender, $"{NameOfPurse(whom)} вписан в зону");
            Log.LogInfo($"[AstvardServerMod] Sorting zone {id}: {whom} written in.");
        }

        private static void OnZoneStrike(long sender, int id, string whom)
        {
            var zone = ZoneToEdit(sender, id);
            if (zone == null) return;

            var target = Sorting.CleanId(whom);
            if (target.Length == 0 || !zone.Members.Remove(target)) return;

            SaveServerZones();
            SayAboutZone(sender, $"{NameOfPurse(target)} убран из зоны");
            Log.LogInfo($"[AstvardServerMod] Sorting zone {id}: {target} struck out.");
        }

        /// <summary>
        /// The zone this caller is allowed to change the membership of, or null with the
        /// refusal already sent. Both gates: whose zone it is, and whether the admin has
        /// left «Общие зоны» open at all.
        /// </summary>
        private static Sorting.Zone ZoneToEdit(long sender, int id)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return null;

            var who = Sorting.CleanId(SenderHostName());
            if (who.Length == 0) return null;

            LoadServerZones();

            var at = IndexOfZone(id);
            if (at < 0) return null;

            var admin = ServerAllows(sender);
            if (!Sorting.MayEdit(ServerZones[at], who, admin))
            {
                SayAboutZone(sender, "Эта зона не твоя");
                return null;
            }

            if (!admin && !ServerRuleOpen("sharezone"))
            {
                SayAboutZone(sender, "Общие зоны закрыты админом");
                return null;
            }

            return ServerZones[at];
        }

        private static ZNetPeer PeerByPlayerName(string name)
        {
            var net = ZNet.instance;
            if (net == null || string.IsNullOrEmpty(name)) return null;

            foreach (var peer in net.GetPeers())
            {
                if (peer == null || !peer.IsReady()) continue;
                if (peer.m_playerName == name) return peer;
            }

            return null;
        }

        /// <summary>The server's own reading of a player rule - its config is where they live.</summary>
        private static bool ServerRuleOpen(string key)
        {
            var rule = FindRule(key);
            return rule != null && rule.PlayersValue > 0;
        }

        private static void SayAboutZone(long sender, string text)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(ReplyTarget(sender), RpcZoneSay, text);
        }

        // ---------------- client ----------------

        private static bool _zonesFromServer;

        private static float _zonesAskedAt = -100f;

        private static string _myZoneId = "";

        private static readonly Dictionary<string, string> ZonePeople = new Dictionary<string, string>();

        /// <summary>
        /// Whether the zones come from the server. Until one ever answers - single player, or
        /// a server without the mod - everything works the old way, out of this config.
        /// </summary>
        internal static bool ZonesOnServer
        {
            get { return _zonesFromServer; }
        }

        /// <summary>Whose it is, in words. Ids never reach the screen.</summary>
        internal static string ZonePersonName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "?";

            string name;
            return ZonePeople.TryGetValue(id, out name) && name.Length > 0 ? name : "игрок";
        }

        internal static bool IsMyZone(Sorting.Zone zone)
        {
            return zone != null && (!_zonesFromServer || zone.Owner == _myZoneId || IsAdminUnlocked);
        }

        private static void OnZones(long sender, string text)
        {
            var net = ZNet.instance;
            if (net == null || net.IsServer()) return;

            _zonesFromServer = true;

            var split = (text ?? "").Split('\n');
            ReadZonePeople(split.Length > 0 ? split[0] : "");

            // The config is no longer the source, so the reader must not fall back to it.
            _sortZonesRead = true;
            SortZones.Clear();
            SortZones.AddRange(Sorting.Parse(split.Length > 1 ? split[1] : ""));

            SeedZonesIfNeeded();
            FollowEditedZone();

            if (Panel != null) RefreshMenu();
        }

        /// <summary>
        /// Points the open zone page back at its own zone after the list is replaced. A zone
        /// that is gone - taken away by its owner, or by an admin - closes the page rather
        /// than leaving it aimed at whatever moved into that place in the list.
        /// </summary>
        private static void FollowEditedZone()
        {
            if (_editingZoneId <= 0) return;

            for (var i = 0; i < SortZones.Count; i++)
            {
                if (SortZones[i].Id != _editingZoneId) continue;

                _editingSortZone = i;
                return;
            }

            _editingZoneId = 0;
            _editingSortZone = -1;
            if (MenuState == StateSortZone || MenuState == StateSortCrew
                || MenuState == StateSortInvite) MenuState = StateSortZones;
        }

        private static void ReadZonePeople(string line)
        {
            ZonePeople.Clear();
            if (string.IsNullOrEmpty(line)) return;

            foreach (var pair in line.Split('|'))
            {
                var at = pair.IndexOf('=');
                if (at <= 0) continue;

                var key = pair.Substring(0, at);
                var value = pair.Substring(at + 1);

                if (key == "me") _myZoneId = Sorting.CleanId(value);
                else ZonePeople[Sorting.CleanId(key)] = value;
            }
        }

        private static void OnZoneSay(long sender, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, text);
        }

        /// <summary>
        /// The zones this client drew before they had anywhere else to live. Sent once, and
        /// only while the server has none of mine - otherwise a zone deleted on purpose would
        /// come back at every login.
        /// </summary>
        private static void SeedZonesIfNeeded()
        {
            if (_zonesSeeded == null || _zonesSeeded.Value) return;
            if (SortZones.Count > 0 || _sortZones == null) return;

            var local = Sorting.Parse(_sortZones.Value);
            _zonesSeeded.Value = true;
            if (local.Count == 0) return;

            foreach (var zone in local)
            {
                zone.Id = 0;
                SendZone(zone);
            }

            Log.LogInfo($"[AstvardServerMod] Sorting zones: sent {local.Count} of my own to the server.");
        }

        /// <summary>
        /// Asks for the list. The server answers by forgetting what it last sent us, so the
        /// answer is the ordinary push and there is still only one place that sends zones.
        /// </summary>
        internal static void AskForZones()
        {
            var net = ZNet.instance;
            if (net == null || net.IsServer()) return;

            var now = Time.realtimeSinceStartup;
            if (now - _zonesAskedAt < 20f) return;
            _zonesAskedAt = now;

            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcZonesQuery);
        }

        internal static void SendZone(Sorting.Zone zone)
        {
            if (zone == null) return;

            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcZonePut, Sorting.Pack(new[] { zone }));
        }

        internal static void SendZoneDrop(int id)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcZoneDrop, id);
        }

        internal static void SendZoneInvite(int id, string name)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcZoneInvite, id, name);
        }

        internal static void SendZoneStrike(int id, string who)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcZoneStrike, id, Sorting.CleanId(who));
        }
    }
}
