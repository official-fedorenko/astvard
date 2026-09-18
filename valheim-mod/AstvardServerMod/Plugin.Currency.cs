using System.Collections.Generic;
using BepInEx;
using Jotunn.Managers;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        private const string RpcCurrency = "AstvardCurrency";
        private const string RpcRuneMinutesSet = "AstvardRuneMinutesSet";

        // The ledger and the grant: an admin asks, the server answers, an admin pays.
        private const string RpcRosterQuery = "AstvardRosterQuery";
        private const string RpcRoster = "AstvardRoster";
        private const string RpcRuneGrant = "AstvardRuneGrant";

        // A day: past that a rune is not something anyone would see arrive.
        private const int MaxMinutesPerRune = 1440;

        // Server side: how long in the world one rune takes. A config entry, so a restart
        // keeps it; an admin sets it from «Настройки», and it takes at the next count.
        private static BepInEx.Configuration.ConfigEntry<int> _minutesPerRune;

        internal static void BindCurrency(BepInEx.Configuration.ConfigFile config)
        {
            _minutesPerRune = config.Bind("Руны", "MinutesPerRune", 60,
                "Сколько минут в игре даёт одну руну, от 1 до 1440. Читает только сервер; "
                + "админ меняет это в игре, в «Настройках».");

            _idleMinutes = config.Bind("Руны", "IdleMinutes", 20,
                "Через сколько минут без единого действия игрок перестаёт получать руны, "
                + "0 — не проверять. Баланс и начатый час остаются; никого не отключает.");
        }

        private static int MinutesPerRune
        {
            get { return Mathf.Clamp(_minutesPerRune != null ? _minutesPerRune.Value : 60, 1, MaxMinutesPerRune); }
        }

        private static int SecondsPerRune
        {
            get { return MinutesPerRune * 60; }
        }

        // Server side: how long a player may do nothing at all before the hour stops
        // counting for them. Generous on purpose - see StillHere.
        private static BepInEx.Configuration.ConfigEntry<int> _idleMinutes;

        private static int IdleMinutes
        {
            get { return Mathf.Clamp(_idleMinutes != null ? _idleMinutes.Value : 20, 0, MaxMinutesPerRune); }
        }

        // Jitter in the position a client keeps sending must not read as a walk; half a
        // metre is under one step and over the noise.
        private const float IdleStep = 0.5f;

        // Client side: the rate as the server last said, for the admin's button.
        private static int _runeMinutes = 60;

        // How often the server counts who is on: often enough that leaving costs at most
        // this much of the hour, rarely enough to cost nothing.
        private const float CurrencyTick = 10f;

        // A hitch longer than this is not played time; a frozen server counts nobody.
        private const float CurrencyMaxStep = 60f;

        private const float CurrencySaveEvery = 60f;

        private const string CurrencyFile = "astvard-currency.txt";

        internal static GameObject CurrencyText;

        /// <summary>
        /// One player's runes on the server, by platform id: the balance, and the part of
        /// an hour already played towards the next one.
        /// </summary>
        private sealed class Purse
        {
            public string Id;

            public string Name;

            public int Balance;

            public float Seconds;

            // Only while the server runs: the last moment this player did anything, and
            // what «anything» was measured against. Nothing here is written to the file -
            // a restart starts everyone off as present, which is the kind side to err on.
            public float LastActive = -1f;

            public Vector3 LastPos;

            public uint LastRevision;

            // Which connection the three above were measured on: a purse outlives the
            // session, and a new one is somebody arriving, which is doing something.
            public long LastPeer;

            public bool Away;
        }

        // Server side. Kept in a file of its own beside the other server data, and never
        // sent out whole: each player hears only their own.
        private static readonly Dictionary<string, Purse> Purses = new Dictionary<string, Purse>();

        // Server side: the nicknames people chose on the site, by the same SteamID64 the
        // purses are keyed by. The site fills this through the exchange it already runs;
        // until it has, the ledger is simply the purses, which is what the server knows.
        private static readonly Dictionary<string, string> RosterAccounts =
            new Dictionary<string, string>();

        private static bool _pursesLoaded;

        private static bool _pursesDirty;

        private static float _currencyLastTick = -1f;

        private static float _currencySavedAt;

        // Client side: this player's, as the server last said; -1 until it has.
        private static int _myRunes = -1;

        private static float _myNextRuneAt;

        private static float _currencyLabelTickAt;

        private static string CurrencyPath
        {
            get { return System.IO.Path.Combine(Paths.ConfigPath, CurrencyFile); }
        }

        private void CreateCurrencyWidget(GUIManager gui)
        {
            CurrencyText = MakeText(gui, "");
        }

        internal static void RegisterCurrencyRpcs(ZRoutedRpc rpc)
        {
            rpc.Register<int, int, bool, int>(RpcCurrency, OnCurrency);
            rpc.Register<int>(RpcRuneMinutesSet, OnRuneMinutesSet);
            rpc.Register(RpcRosterQuery, OnRosterQuery);
            rpc.Register<string>(RpcRoster, OnRoster);
            rpc.Register<string, int>(RpcRuneGrant, OnRuneGrant);
        }

        // ---------------- server side ----------------

        /// <summary>
        /// Counts who is on, from Update on the server. Played time is kept as a remainder,
        /// so half an hour today and half tomorrow make one rune. Only a player in the
        /// world counts - not one still choosing a character.
        /// </summary>
        internal static void TickCurrency()
        {
            var net = ZNet.instance;
            if (net == null || !net.IsServer()) return;

            var now = Time.realtimeSinceStartup;
            if (_currencyLastTick < 0f)
            {
                _currencyLastTick = now;
                return;
            }

            if (now - _currencyLastTick < CurrencyTick) return;

            var step = Mathf.Min(now - _currencyLastTick, CurrencyMaxStep);
            _currencyLastTick = now;

            foreach (var peer in net.GetPeers())
            {
                if (peer == null || !peer.IsReady() || peer.m_socket == null) continue;
                if (peer.m_characterID.IsNone()) continue;

                var id = peer.m_socket.GetHostName();
                if (string.IsNullOrEmpty(id)) continue;

                var purse = PurseFor(id, peer.m_playerName);

                // An hour is for playing it, not for being logged in through it.
                if (!StillHere(peer, purse, now)) continue;

                purse.Seconds += step;
                _pursesDirty = true;

                var earned = 0;
                while (purse.Seconds >= SecondsPerRune)
                {
                    purse.Seconds -= SecondsPerRune;
                    purse.Balance++;
                    earned++;
                }

                if (earned == 0) continue;

                SendPurse(peer.m_uid, purse, true);
                Log.LogInfo($"[AstvardServerMod] Runes: {purse.Name} ({id}) +{earned}, now {purse.Balance}.");
            }

            if (_pursesDirty && now - _currencySavedAt >= CurrencySaveEvery) SavePurses();
        }

        private static Purse PurseFor(string id, string name)
        {
            if (!_pursesLoaded) LoadPurses();

            if (!Purses.TryGetValue(id, out var purse))
            {
                purse = new Purse { Id = id, Name = "" };
                Purses[id] = purse;
            }

            if (!string.IsNullOrEmpty(name)) purse.Name = CleanName(name);
            return purse;
        }

        private static void SendPurse(long target, Purse purse, bool earned)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(target, RpcCurrency, purse.Balance,
                Mathf.Max(0, Mathf.CeilToInt(SecondsPerRune - purse.Seconds)), earned, MinutesPerRune);
        }

        /// <summary>
        /// An admin's new rate. It takes at the next count - the purses keep their seconds, so
        /// a shorter hour pays out what is already past it - and everyone on hears their own
        /// purse again, so the minutes they are shown to the next rune are the new ones.
        /// </summary>
        private static void OnRuneMinutesSet(long sender, int minutes)
        {
            if (!ServerAllows(sender) || _minutesPerRune == null) return;

            _minutesPerRune.Value = Mathf.Clamp(minutes, 1, MaxMinutesPerRune);
            Log.LogInfo($"[AstvardServerMod] Runes: one per {MinutesPerRune} min, set by {SenderName(sender)}.");

            var net = ZNet.instance;
            if (net == null) return;

            foreach (var peer in net.GetPeers())
            {
                if (peer == null || !peer.IsReady() || peer.m_socket == null) continue;

                var id = peer.m_socket.GetHostName();
                if (!string.IsNullOrEmpty(id)) SendPurse(peer.m_uid, PurseFor(id, peer.m_playerName), false);
            }
        }

        /// <summary>
        /// Is this player doing anything at all. Two signals, either one enough:
        ///
        ///   - the reference position the client keeps sending, which is how the game decides
        ///     what to load around them, and
        ///   - the data revision of their character, which the game raises on any field whose
        ///     value actually changed - so crafting, eating, taking a hit, opening a chest all
        ///     count while standing perfectly still does not.
        ///
        /// The second one is why the threshold can be about afk and not about walking: a
        /// player sorting chests or waiting on a smelter never moves a metre and is still
        /// plainly here. Even so the default is generous, because the cost of judging wrongly
        /// is somebody's evening of runes.
        ///
        /// Nobody is ever thrown out. A kick has to be right every single time; this only has
        /// to be right often enough to make an afk night worth nothing. A player judged away
        /// keeps their balance and their part of an hour and starts earning again the moment
        /// they do something.
        ///
        /// If it turns out some field of a player's character changes on its own every few
        /// seconds, this never fires and everyone is paid as before - the harmless way round.
        /// The log says who was judged away and when, which is how that gets found out.
        /// </summary>
        private static bool StillHere(ZNetPeer peer, Purse purse, float now)
        {
            var minutes = IdleMinutes;
            if (minutes <= 0) return true;

            var pos = peer.m_refPos;
            var zdo = peer.m_characterID.IsNone() ? null : ZDOMan.instance?.GetZDO(peer.m_characterID);
            var revision = zdo != null ? zdo.DataRevision : 0u;

            // Measured against the last moment they were counted here, not against the last
            // tick: a slow drift down a slope adds up, a shiver in the numbers does not.
            var moved = (pos - purse.LastPos).sqrMagnitude > IdleStep * IdleStep;

            if (purse.LastActive < 0f || peer.m_uid != purse.LastPeer
                || moved || revision != purse.LastRevision)
            {
                if (purse.Away)
                    Log.LogInfo($"[AstvardServerMod] Runes: {purse.Name} ({purse.Id}) is back, earning again.");

                purse.LastActive = now;
                purse.LastPos = pos;
                purse.LastRevision = revision;
                purse.LastPeer = peer.m_uid;
                purse.Away = false;
                return true;
            }

            if (now - purse.LastActive < minutes * 60f) return true;

            if (!purse.Away)
            {
                purse.Away = true;
                Log.LogInfo($"[AstvardServerMod] Runes: {purse.Name} ({purse.Id}) has done nothing "
                            + $"for {minutes} min, not earning until they do.");
            }

            return false;
        }

        /// <summary>
        /// The nicknames from the site, as the exchange last brought them. Kept whole rather
        /// than merged into the purses: a purse's name is the character the server saw, and
        /// the two answer different questions.
        /// </summary>
        internal static void SetRosterAccounts(Dictionary<string, string> accounts)
        {
            RosterAccounts.Clear();
            if (accounts == null) return;

            foreach (var pair in accounts)
            {
                var id = Roster.Clean(pair.Key);
                if (id.Length > 0) RosterAccounts[id] = Roster.Clean(pair.Value);
            }
        }

        /// <summary>The whole ledger, as it goes to an admin's panel.</summary>
        private static string PackRoster()
        {
            if (!_pursesLoaded) LoadPurses();

            var purses = new List<Roster.Row>();
            foreach (var purse in Purses.Values)
                purses.Add(new Roster.Row
                {
                    Id = purse.Id,
                    Character = purse.Name,
                    Balance = purse.Balance,
                });

            var accounts = new List<Roster.Row>();
            foreach (var pair in RosterAccounts)
                accounts.Add(new Roster.Row { Id = pair.Key, Site = pair.Value });

            var online = new List<string>();
            var net = ZNet.instance;
            if (net != null)
                foreach (var peer in net.GetPeers())
                {
                    if (peer == null || !peer.IsReady() || peer.m_socket == null) continue;
                    var id = peer.m_socket.GetHostName();
                    if (!string.IsNullOrEmpty(id)) online.Add(id);
                }

            return Roster.Pack(Roster.Merge(purses, accounts, online));
        }

        /// <summary>
        /// Only to an admin, and only to the one who asked. The ledger carries every player's
        /// platform id, and the panel hangs in front of everybody.
        /// </summary>
        private static void OnRosterQuery(long sender)
        {
            if (!ServerAllows(sender)) return;

            ZRoutedRpc.instance?.InvokeRoutedRPC(sender, RpcRoster, PackRoster());
        }

        /// <summary>
        /// An admin hands runes over, or takes them back. The purse is written to disk at once
        /// rather than at the next minute: this is a rare, deliberate thing, and «I gave you
        /// fifty» surviving a crash matters more than one more file write.
        /// </summary>
        private static void OnRuneGrant(long sender, string id, int amount)
        {
            if (!ServerAllows(sender)) return;

            // Ids come from a list the admin was shown, but they arrive over the network like
            // everything else; anything that is not a platform id is not paid.
            var ids = SiteSync.ParseIds(Roster.Clean(id));
            amount = Roster.ClampGrant(amount);
            if (ids.Count != 1 || amount == 0) return;

            var target = ids[0];

            // An empty name on purpose: a purse's name is the character the server saw, and
            // somebody who has never been here has none to show.
            var purse = PurseFor(target, "");
            var before = purse.Balance;
            purse.Balance = Roster.ApplyGrant(before, amount);
            _pursesDirty = true;
            SavePurses();

            var admin = SenderName(sender);
            var moved = purse.Balance - before;
            var who = string.IsNullOrEmpty(purse.Name) ? "?" : purse.Name;
            Log.LogInfo($"[AstvardServerMod] Runes: {admin} gave {moved:+#;-#;0} to "
                        + $"{who} ({target}), now {purse.Balance}.");

            var peer = PeerByPlatformId(target);

            // A grant that moved nothing - the balance was already at the ceiling, or at
            // zero with runes being taken - says nothing to the player: «забрал 0 рун» is
            // worse than silence.
            if (peer != null && moved != 0)
            {
                SendPurse(peer.m_uid, purse, false);
                Say(peer.m_uid, moved > 0
                    ? $"Админ выдал {moved} рун. Всего: {purse.Balance}"
                    : $"Админ забрал {-moved} рун. Всего: {purse.Balance}");
            }

            // The admin sees the list they are working from, freshly counted.
            ZRoutedRpc.instance?.InvokeRoutedRPC(sender, RpcRoster, PackRoster());
        }

        private static ZNetPeer PeerByPlatformId(string id)
        {
            var net = ZNet.instance;
            if (net == null || string.IsNullOrEmpty(id)) return null;

            foreach (var peer in net.GetPeers())
            {
                if (peer == null || peer.m_socket == null) continue;
                if (peer.m_socket.GetHostName() == id) return peer;
            }

            return null;
        }


        /// <summary>The asker's own runes; goes out with everything else a panel asks for.</summary>
        private static void ReplyCurrency(long target)
        {
            var id = SenderId();
            if (id == "local" || id == "?") return;

            SendPurse(target, PurseFor(id, SenderName(target)), false);
        }

        private static void LoadPurses()
        {
            _pursesLoaded = true;
            Purses.Clear();

            try
            {
                if (!System.IO.File.Exists(CurrencyPath)) return;

                foreach (var line in System.IO.File.ReadAllLines(CurrencyPath))
                {
                    if (line.StartsWith("#")) continue;

                    var parts = line.Split('\t');
                    if (parts.Length < 3 || parts[0].Length == 0) continue;

                    int.TryParse(parts[1], out var balance);
                    float.TryParse(parts[2], System.Globalization.NumberStyles.Float, Invariant, out var seconds);
                    Purses[parts[0]] = new Purse
                    {
                        Id = parts[0],
                        Balance = Mathf.Max(0, balance),
                        Seconds = Mathf.Clamp(seconds, 0f, SecondsPerRune),
                        Name = parts.Length > 3 ? parts[3] : "",
                    };
                }

                Log.LogInfo($"[AstvardServerMod] Runes: {Purses.Count} purses loaded.");
            }
            catch (System.Exception ex)
            {
                Log.LogError($"[AstvardServerMod] Could not read {CurrencyFile}: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes every purse, a minute at most after a change and on the way out. Through a
        /// temporary file, so a server killed halfway leaves the old file whole.
        /// </summary>
        internal static void SavePurses()
        {
            if (!_pursesLoaded || !_pursesDirty) return;
            _currencySavedAt = Time.realtimeSinceStartup;

            try
            {
                var lines = new List<string> { "# astvard runes: id, balance, seconds towards the next, name" };
                foreach (var purse in Purses.Values)
                    lines.Add($"{purse.Id}\t{purse.Balance}\t{purse.Seconds.ToString("F0", Invariant)}\t{purse.Name}");

                var temp = CurrencyPath + ".tmp";
                System.IO.File.WriteAllLines(temp, lines);
                if (System.IO.File.Exists(CurrencyPath)) System.IO.File.Replace(temp, CurrencyPath, null);
                else System.IO.File.Move(temp, CurrencyPath);

                _pursesDirty = false;
            }
            catch (System.Exception ex)
            {
                Log.LogError($"[AstvardServerMod] Could not write {CurrencyFile}: {ex.Message}");
            }
        }

        // ---------------- client side ----------------

        // ---------------- the ledger, client side ----------------

        /// <summary>
        /// The ledger as the server last sent it, and what the admin typed into the search.
        /// Kept here rather than in the page so that reopening the panel shows the list at
        /// once, with the numbers from the last answer, instead of an empty page.
        /// </summary>
        internal static readonly List<Roster.Row> RosterRows = new List<Roster.Row>();

        internal static string RosterQuery = "";

        internal static bool RosterAsked;

        private static void OnRoster(long sender, string packed)
        {
            RosterRows.Clear();
            RosterRows.AddRange(Roster.Parse(packed));
            RosterAsked = true;
            RefreshMenu();
        }

        /// <summary>Ask for the ledger. The server answers admins only.</summary>
        internal static void RequestRoster()
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcRosterQuery);
        }

        /// <summary>Hand runes over, or take them back with a negative number.</summary>
        internal static void GrantRunes(string id, int amount)
        {
            amount = Roster.ClampGrant(amount);
            if (string.IsNullOrEmpty(id) || amount == 0) return;

            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcRuneGrant, id, amount);
        }


        private static void OnCurrency(long sender, int balance, int secondsToNext, bool earned, int minutesPerRune)
        {
            _runeMinutes = Mathf.Clamp(minutesPerRune, 1, MaxMinutesPerRune);
            _myRunes = Mathf.Max(0, balance);
            _myNextRuneAt = Time.realtimeSinceStartup + Mathf.Max(0, secondsToNext);

            if (earned)
                Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft,
                    $"+1 руна за {RuneTime(_runeMinutes)} в игре. Всего: {_myRunes}");

            UpdateCurrencyLabel();
            UpdateRuneHud();
            RefreshMenu();
        }

        /// <summary>The rate as the message says it: «час», «30 минут», «2 часа».</summary>
        private static string RuneTime(int minutes)
        {
            if (minutes == 60) return "час";
            if (minutes % 60 == 0) return $"{minutes / 60} ч";
            return $"{minutes} мин";
        }

        /// <summary>The admin's «Применить»: the server keeps it and tells everyone on.</summary>
        private static void SetRuneMinutes(int minutes)
        {
            minutes = Mathf.Clamp(minutes, 1, MaxMinutesPerRune);
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcRuneMinutesSet, minutes);
            _runeMinutes = minutes;
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                $"Руна игроку — за {RuneTime(minutes)} в игре");
        }

        private static void UpdateCurrencyLabel()
        {
            if (_myRunes < 0) return;

            var minutes = Mathf.CeilToInt((_myNextRuneAt - Time.realtimeSinceStartup) / 60f);
            SetLabel(CurrencyText, $"Руны: {_myRunes}{NEWLINE}"
                                   + (minutes > 0 ? $"следующая через {minutes} мин" : "следующая вот-вот"));
        }

        /// <summary>
        /// Keeps the minutes to the next rune going while the panel's first page is open, and
        /// hangs the count under the hotbar once there is a HUD to hang it on.
        /// </summary>
        internal static void TickCurrencyLabel()
        {
            if (Time.realtimeSinceStartup < _currencyLabelTickAt) return;
            _currencyLabelTickAt = Time.realtimeSinceStartup + 1f;

            // Asked as soon as the player is in the world, not at the first opening of the
            // panel: the count under the hotbar should be there from the start.
            if (Player.m_localPlayer != null && !GUIManager.IsHeadless()) AskSharedListOnce();

            if (_myRunes < 0) return;
            if (_runeHud == null) UpdateRuneHud();
            if (MenuState == StateRoot && Panel != null && Panel.activeInHierarchy) UpdateCurrencyLabel();
        }

        private static UnityEngine.UI.Text _runeHud;

        /// <summary>
        /// «Руны: N» under the hotbar, where it is always in sight. Made the first time the HUD
        /// is there, and made again after the main menu takes the HUD, and it, away.
        /// </summary>
        private static void UpdateRuneHud()
        {
            if (GUIManager.IsHeadless()) return;

            if (_runeHud == null)
            {
                if (_myRunes < 0 || Hud.instance == null) return;

                var bar = Hud.instance.GetComponentInChildren<HotkeyBar>(true);
                if (bar == null) return;

                _runeHud = MakeRuneHud(bar);
                if (_runeHud == null) return;
            }

            var shown = _myRunes >= 0;
            if (_runeHud.gameObject.activeSelf != shown) _runeHud.gameObject.SetActive(shown);
            if (shown) _runeHud.text = $"Руны: {_myRunes}";
        }

        private static UnityEngine.UI.Text MakeRuneHud(HotkeyBar bar)
        {
            var gui = GUIManager.Instance;
            if (gui == null) return null;

            // A child of the bar, so it keeps to it at any UI scale. The bar lays its slots
            // out from its own origin, each at its pivot, so the first slot's lower left
            // corner comes from the slot prefab's rect.
            var slot = bar.m_elementPrefab != null ? bar.m_elementPrefab.GetComponent<RectTransform>() : null;
            var size = slot != null ? slot.rect.size : new Vector2(64f, 64f);
            var pivot = slot != null ? slot.pivot : new Vector2(0.5f, 0.5f);

            var go = gui.CreateText("", bar.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero,
                gui.AveriaSerifBold, 18, gui.ValheimBeige, true, Color.black,
                240f, 28f, false);

            var rect = go.GetComponent<RectTransform>();
            rect.pivot = new Vector2(0f, 1f);
            rect.localPosition = new Vector3(-pivot.x * size.x, -pivot.y * size.y - 6f, 0f);

            var text = go.GetComponent<UnityEngine.UI.Text>();
            text.alignment = TextAnchor.UpperLeft;
            text.raycastTarget = false;
            return text;
        }
    }
}
