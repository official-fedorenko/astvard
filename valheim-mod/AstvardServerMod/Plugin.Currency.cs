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
        }

        private static int MinutesPerRune
        {
            get { return Mathf.Clamp(_minutesPerRune != null ? _minutesPerRune.Value : 60, 1, MaxMinutesPerRune); }
        }

        private static int SecondsPerRune
        {
            get { return MinutesPerRune * 60; }
        }

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
        }

        // Server side. Kept in a file of its own beside the other server data, and never
        // sent out whole: each player hears only their own.
        private static readonly Dictionary<string, Purse> Purses = new Dictionary<string, Purse>();

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
                purse = new Purse { Id = id };
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
