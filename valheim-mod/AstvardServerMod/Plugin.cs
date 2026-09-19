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
    [BepInPlugin("astvard.servermod", "AstvardServerMod", Version)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    public partial class Plugin : BaseUnityPlugin
    {
        /// <summary>
        /// One place for the version, read by the attribute above and sent to clients.
        ///
        /// It stopped being decoration the day players started installing from Thunderstore
        /// and updating whenever they feel like it. Minor and patch are for anything a
        /// mismatched pair can live through; major is the promise that breaks - the ninth
        /// field of a template line, chest contents, is exactly that sort of change, and an
        /// old client drops it without a word. Nobody is turned away for either: see
        /// OnServerVersion, and note there is no NetworkCompatibility attribute on purpose.
        /// </summary>
        internal const string Version = "1.2.4";

        internal static bool IsAdminUnlocked;

        internal static Plugin Instance;

        internal static ManualLogSource Log;

        // Unlike the admin toggles this one belongs to the player, so it is
        // worth surviving a restart — hence a config entry rather than a field.
        private static ConfigEntry<bool> _autoCollect;

        private static ConfigEntry<bool> _autoFill;

        private static ConfigEntry<string> _localAdminCommands;

        // Only the server reads these; a client keeps its own copy of whatever the
        // server last reported, purely to show it in the menu.
        private static ConfigEntry<string> _zones;

        private const string ProjectDescription =
            "ASTVARD\n\n" +
            "Портал игровых серверов: сайт с личным кабинетом, " +
            "мониторингом статуса серверов и заявками на доступ.\n\n" +
            "На этом сервере:\n" +
            "• Вход по вайтлисту (Steam ID)\n" +
            "• Админ-панель прямо в игре (Tab)\n" +
            "• Серверные скрипты без модов у игроков\n\n" +
            "astvard.online";

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            Instance = this;
            // New keys, not the old AutoCollect/AutoFill: the switch no longer means what
            // it used to. It used to open a guess-the-nearest-chest pass on top of the
            // assigned chests; now it says whether that half runs at all. A stored «false»
            // read under the new meaning would quietly turn off automation somebody had
            // working, so the old value is left behind rather than reinterpreted.
            _autoCollect = Config.Bind("Наполнение и сбор", "Collect", true,
                "Складывать готовый продукт в назначенный сундук. Неназначенные сундуки "
                + "станции не трогают вовсе.");
            _autoFill = Config.Bind("Наполнение и сбор", "Fill", true,
                "Подавать сырьё и топливо из сундука подачи. Неназначенные сундуки "
                + "станции не трогают вовсе.");

            _localAdminCommands = Config.Bind("Функции", "LocalAdminCommands",
                "debugmode fly nocost exploremap resetmap tod env resetenv wind resetwind",
                "Команды, которые мод разрешает админу выполнить на своём клиенте. "
                + "Игра запрещает их вне сервера, хотя действуют они только на того, кто ввёл. "
                + "Через пробел; пусто — не разрешать ничего; * — все, какие игра "
                + "отказывает клиенту. Команды, которые игра ретранслирует сама, не "
                + "затрагиваются никогда — иначе они выполнились бы у нас вместо сервера.");

            _zones = Config.Bind("Зона", "Zones", "",
                "Области, которые сервер держит загруженными: X,Z,радиус_в_метрах,владелец через ';', "
                + "у зоны игрока ещё его id. Радиус округляется наружу до целых зон по 64 м.");
            ParseZones(_zones.Value, Zones);

            BindBuildPause(Config);
            BindPlayerRules(Config);
            BindCurrency(Config);
            BindSorting(Config);
            BindChestLabels(Config);
            BindChestZone(Config);
            BindWorldRates(Config);
            BindSiteLists(Config);

            _harmony = new Harmony("astvard.servermod");
            _harmony.PatchAll();
            Log.LogInfo("AstvardServerMod loaded");

            RegisterCommand(new AdminUnlockCommand());
            // ОПЫТ: помощник. Убрать вместе с Plugin.HelperTest.cs.
            RegisterCommand(new HelperTestCommand());

            if (GUIManager.IsHeadless())
            {
                Log.LogInfo("Headless (server) — skipping UI setup.");
                return;
            }

            ReloadTemplates();
            GUIManager.OnCustomGUIAvailable += CreatePanel;
            StartCoroutine(AutomationLoop());
            StartCoroutine(SortingLoop());
        }

        /// <summary>
        /// Hands a command to the game's console directly, instead of through Jotunn.
        ///
        /// Jotunn 2.29.2 looks up Terminal.ConsoleCommand's constructor by an exact
        /// signature, and 1.0 added parameters to it, so CommandManager quietly
        /// registers nothing and only says "No suitable constructor" in the log — the
        /// commands simply do not exist, astvardadmin among them. The constructor puts
        /// itself into Terminal's own static table, which is created once and never
        /// rebuilt, so calling it from here works whether the console has initialised
        /// yet or not, and needs no Jotunn release.
        ///
        /// Every flag the entity carries is passed on, so the command classes stay the
        /// one place its behaviour is described.
        /// </summary>
        private static void RegisterCommand(ConsoleCommand command)
        {
            _ = new Terminal.ConsoleCommand(
                command.Name,
                command.Help,
                args => command.Run(args.Args, args.Context),
                isCheat: command.IsCheat,
                isNetwork: command.IsNetwork,
                onlyServer: command.OnlyServer,
                isSecret: command.IsSecret,
                optionsFetcher: command.CommandOptionList);
        }

        // ---------------- admin unlock ----------------

        private const string RpcAdminAsk = "AstvardAdminAsk";

        private const string RpcAdminGrant = "AstvardAdminGrant";

        private const string RpcAdminSeen = "AstvardAdminSeen";

        /// <summary>The server's own version, sent with the panel's usual query.</summary>
        private const string RpcVersion = "AstvardVersion";

        /// <summary>
        /// The one outstanding unlock request, or 0. A client cannot authenticate an
        /// incoming routed RPC at all: ZRoutedRpc.RPC_RoutedRPC relays on the target id
        /// and never stamps the true sender over m_senderPeerID, so "is this from the
        /// server" is a question the receiver has no honest way to answer - any modded
        /// client can address a packet at any peer and name itself whatever it likes.
        ///
        /// So the client does not try to recognise the sender. It sends a secret with
        /// the question and only accepts an answer that carries the same secret back.
        /// The question goes to the server alone and is never relayed onward, so nobody
        /// else sees the value, and it is spent the moment it is used.
        /// </summary>
        private static long _adminNonce;

        /// <summary>
        /// Forgets that the server ever said yes.
        ///
        /// The flag was set once and never cleared, so it outlived the connection that
        /// earned it: leave for the main menu, join a different server, and the whole
        /// admin half of the panel is still open there - including the parts that never
        /// ask the server anything, like ForceDelete and terrain. It also meant that
        /// taking somebody out of adminlist.txt left their client-side powers standing
        /// until they restarted the game.
        ///
        /// The nonce goes with it as hygiene; it only ever travelled to the server that
        /// was asked, and a stale one would answer a grant nobody requested.
        /// </summary>
        internal static void ForgetAdmin()
        {
            _adminNonce = 0;
            IsAdminUnlocked = false;
            MayAskAdmin = false;
        }

        /// <summary>
        /// Hands the admin powers back without leaving the server.
        ///
        /// Only restarting the game used to do this. An admin who wanted to see the panel
        /// the way a player sees it - or just not to have force delete one misclick away -
        /// had no way out. The server is not told: it never keeps a grant, it only answers
        /// asks, so «Админка» on the root page is still the way back in.
        ///
        /// The flag is not the whole of it. God mode, debug fly and free building live on
        /// the Player and outlive it - m_debugMode only opens the Z and B keys, it does not
        /// close what they switched on - and so do a forced weather and wind. A projection
        /// already in hand was priced when it was picked up: MarkPlayerCopy says nothing for
        /// an admin, so a copy taken as admin and clicked down afterwards would go up free
        /// and past the players' rules. So everything being shown is dropped. A road already
        /// being laid is left to finish: it was allowed when it started, and stopping it
        /// would leave half a road behind.
        /// </summary>
        internal static void LeaveAdmin()
        {
            if (!IsAdminUnlocked) return;

            IsAdminUnlocked = false;
            _adminNonce = 0;
            // MayAskAdmin stays as the server last said: nothing about our right to ask has
            // changed, and it is what draws «Админка» for the way back.

            var player = Player.m_localPlayer;
            if (player != null)
            {
                if (player.InGodMode()) player.SetGodMode(false);
                if (player.IsDebugFlying()) player.ToggleDebugFly();
                player.SetNoPlacementCost(false);
            }
            Player.m_debugMode = false;

            var env = EnvMan.instance;
            if (env != null)
            {
                // Called directly rather than through ResetWeather and ResetWind: both of
                // those put a message up, and nothing may have been forced at all. The game
                // ignores an empty environment that is already empty.
                env.SetForceEnvironment("");
                if (env.m_debugWind) env.ResetDebugWind();
            }

            if (IsPlacing) CancelPlacement();
            ClearBuildAsk();
            CancelBridge();
            if (RoadAwaitingEnd && !_roadLaying) CancelRoad();
            CancelFencePreview();
            CancelAreaPreview();
            if (_wallPreviewing) CancelWallPreview();

            MenuState = StateRoot;
            RefreshMenu();

            Log.LogInfo("[AstvardServerMod] Admin mode left.");
            Chat.instance?.AddString("Astvard: админ-кнопки выключены.");
            player?.Message(MessageHud.MessageType.Center, "Админка выключена");
        }

        /// <summary>
        /// Client side: whether this server counts this player as an admin at all. Until it
        /// says so, «Админка» is not drawn - there is nothing behind it for a player, and a
        /// button that answers every press with silence is worse than no button.
        ///
        /// Only the drawing hangs on this. The grant itself is decided on the server, by the
        /// socket the request came in on, and a client saying otherwise changes nothing. The
        /// console command `astvardadmin` also still asks, whatever the panel shows: a real
        /// admin whose id in the list is written in another form gets no button, and that
        /// refusal - with the id the socket reported - is what the server's log needs to say.
        /// </summary>
        internal static bool MayAskAdmin;

        internal static void RegisterAdminRpcs()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null) return;

            rpc.Register<long>(RpcAdminAsk, OnAdminAsk);
            rpc.Register<long>(RpcAdminGrant, OnAdminGrant);
            rpc.Register<bool>(RpcAdminSeen, OnAdminSeen);
            rpc.Register<string>(RpcVersion, OnServerVersion);
        }

        /// <summary>Client side: ask the server whether we may have the admin buttons.</summary>
        internal static void RequestAdmin()
        {
            if (ZRoutedRpc.instance == null)
            {
                Log.LogWarning("[AstvardServerMod] astvardadmin: no routing yet.");
                return;
            }

            _adminNonce = System.BitConverter.ToInt64(System.Guid.NewGuid().ToByteArray(), 0);
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcAdminAsk, _adminNonce);
            Log.LogInfo("[AstvardServerMod] astvardadmin: asked the server.");
        }

        /// <summary>
        /// Server side. Decided against the socket the packet arrived on rather than the
        /// sender id in the packet, which the sender writes and can therefore forge -
        /// ServerAllows is the same gate every other server-side action here uses.
        /// The answer goes back to that same socket's peer for the same reason.
        /// </summary>
        private static void OnAdminAsk(long sender, long nonce)
        {
            if (!ServerAllows(sender) && !AdminFileAllows(sender))
            {
                // The id, not just the fact: a refusal is almost always the list holding
                // a different identity from the one the socket reports - a Steam id in
                // the file against a PlayFab id on the wire, say - and without printing
                // it there is nothing to compare the file against.
                Log.LogInfo($"[AstvardServerMod] Admin refused for {sender}, id \"{SenderHostName()}\".");
                return;
            }

            ZRoutedRpc.instance?.InvokeRoutedRPC(ReplyTarget(sender), RpcAdminGrant, nonce);
            Log.LogInfo($"[AstvardServerMod] Admin granted to {sender}.");
        }

        private static string _localAdminCommandsRaw;

        private static readonly HashSet<string> LocalAdminCommandSet = new HashSet<string>();

        /// <summary>
        /// Whether this command is one the admin may run on their own machine. Reparsed
        /// only when the setting's text changes, since it is asked once per command per
        /// keystroke while the console builds its suggestions.
        /// </summary>
        internal static bool IsLocalAdminCommand(string command)
        {
            if (_localAdminCommands == null || string.IsNullOrEmpty(command)) return false;

            var raw = (_localAdminCommands.Value ?? "").Trim();
            if (raw == "*") return true;

            if (raw != _localAdminCommandsRaw)
            {
                _localAdminCommandsRaw = raw;
                LocalAdminCommandSet.Clear();
                foreach (var name in raw.Split(new[] { ' ', ',', ';' },
                             System.StringSplitOptions.RemoveEmptyEntries))
                    LocalAdminCommandSet.Add(name.Trim().ToLowerInvariant());
            }

            return LocalAdminCommandSet.Contains(command.ToLowerInvariant());
        }

        /// <summary>Our own list, beside the game's adminlist.txt.</summary>
        private const string AdminFileName = "astvard-admins.txt";

        private static readonly string[] AdminFileHeader =
        {
            "# Кто может открыть админ-меню Astvard, помимо adminlist.txt самой игры.",
            "# Одна запись на строку, # — комментарий. Файл перечитывается на лету.",
            "#",
            "# SteamID64, например 76561198425108760 — надёжно. Сервер сверяет его с",
            "#   сокетом, подделать нельзя. Префикс V_ дописывать не нужно: мод",
            "#   сравнивает с тем, что сообщает сокет, а не с форматом списка игры.",
            "#",
            "# Имя игрока, например Meliowar — удобно, но НЕ ЗАЩИТА: имя игрок",
            "#   выбирает сам, и любой может назваться так же. Годится для своего",
            "#   круга, не годится для открытого сервера."
        };

        private static string AdminFilePath
        {
            get { return System.IO.Path.Combine(Paths.ConfigPath, AdminFileName); }
        }

        private static readonly List<string> AdminEntries = new List<string>();

        private static System.DateTime _adminFileStamp = System.DateTime.MinValue;

        /// <summary>
        /// Rereads the list when the file has changed, so an admin can be added without
        /// restarting the server. Creates it with its own explanation the first time,
        /// because a file nobody can find is a file nobody edits.
        /// </summary>
        private static void LoadAdminFile()
        {
            try
            {
                var path = AdminFilePath;
                if (!System.IO.File.Exists(path))
                {
                    System.IO.File.WriteAllLines(path, AdminFileHeader);
                    _adminFileStamp = System.IO.File.GetLastWriteTimeUtc(path);
                    AdminEntries.Clear();
                    return;
                }

                var stamp = System.IO.File.GetLastWriteTimeUtc(path);
                if (stamp == _adminFileStamp) return;
                _adminFileStamp = stamp;

                AdminEntries.Clear();
                foreach (var line in System.IO.File.ReadAllLines(path))
                {
                    var entry = line.Trim();
                    if (entry.Length == 0 || entry.StartsWith("#")) continue;
                    AdminEntries.Add(entry);
                }

                Log.LogInfo($"[AstvardServerMod] {AdminFileName}: {AdminEntries.Count} entries.");
            }
            catch (System.Exception ex)
            {
                Log.LogError($"[AstvardServerMod] Could not read {AdminFileName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Whether our own list lets this one in. The id comes off the socket and cannot
        /// be renamed by the person on the other end; the player name is whatever they
        /// typed at character creation, so a name entry is convenience and is documented
        /// as such in the file itself.
        /// </summary>
        private static bool AdminFileAllows(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return false;

            LoadAdminFile();
            if (AdminEntries.Count == 0) return false;

            var id = SenderHostName();
            var name = SenderName(sender);

            foreach (var entry in AdminEntries)
            {
                // A bare id, or the game's own V_-prefixed spelling, both match.
                if (entry == id || entry.EndsWith("_" + id)) return true;
                if (!string.IsNullOrEmpty(name) &&
                    string.Equals(entry, name, System.StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>
        /// What the server thinks the asker's identity is - the same string it looks up
        /// in adminlist.txt. Only for the log: nothing decides on it.
        /// </summary>
        private static string SenderHostName()
        {
            var rpc = RoutedSenderTracker.Current;
            if (rpc == null) return "(local)";
            if (MPeerByRpc == null || ZNet.instance == null) return "(no lookup)";

            var peer = MPeerByRpc.Invoke(ZNet.instance, new object[] { rpc }) as ZNetPeer;
            if (peer == null) return "(no peer)";
            if (peer.m_socket == null) return "(no socket)";

            return peer.m_socket.GetHostName();
        }

        /// <summary>
        /// Who to answer. The sender id travels inside the packet and is written by the
        /// sender, so an admin could otherwise have the server post a grant at somebody
        /// else. The socket cannot be renamed by the person on the other end of it.
        /// </summary>
        private static long ReplyTarget(long sender)
        {
            var rpc = RoutedSenderTracker.Current;
            if (rpc == null || MPeerByRpc == null || ZNet.instance == null) return sender;

            var peer = MPeerByRpc.Invoke(ZNet.instance, new object[] { rpc }) as ZNetPeer;
            return peer != null ? peer.m_uid : sender;
        }

        /// <summary>
        /// Server side. Says whether this player is in the admin list, by the same two gates
        /// the grant itself uses, so the button is drawn exactly where pressing it would work.
        /// Sent with the panel's usual query - see OnTemplateQuery - because the socket is
        /// only knowable while its own call is being handled.
        /// </summary>
        internal static void ReplyMayAdmin(long sender)
        {
            var may = ServerAllows(sender) || AdminFileAllows(sender);
            ZRoutedRpc.instance?.InvokeRoutedRPC(ReplyTarget(sender), RpcAdminSeen, may);
        }

        /// <summary>Client side: the server's word on whether to draw «Админка».</summary>
        private static void OnAdminSeen(long sender, bool may)
        {
            if (MayAskAdmin == may) return;

            MayAskAdmin = may;
            RefreshMenu();
        }

        // Said once per connection, like the panel's first query: a line on every panel
        // open would be nagging, and a reconnect brings a new ZNet.
        private static ZNet _versionToldOn;

        /// <summary>Server side: our version, for the client to compare against its own.</summary>
        internal static void ReplyVersion(long sender)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(ReplyTarget(sender), RpcVersion, Version);
        }

        /// <summary>
        /// Client side: says once that the versions differ, and does nothing else about it.
        /// Nobody is disconnected over this. The mod is not required to play here, and half
        /// the point of putting it on Thunderstore is that a player updates when they want
        /// to - so this is a line in the chat, not a gate.
        ///
        /// A player who never opens the panel never hears it. That is the price of hanging
        /// it on the panel's own query rather than on the handshake.
        /// </summary>
        private static void OnServerVersion(long sender, string version)
        {
            if (string.IsNullOrEmpty(version) || version == Version) return;
            if (ZNet.instance == null || ReferenceEquals(ZNet.instance, _versionToldOn)) return;
            _versionToldOn = ZNet.instance;

            Log.LogInfo($"[AstvardServerMod] Server runs {version}, we run {Version}.");

            Chat.instance?.AddString(Behind(Version, version)
                ? $"Astvard: на сервере мод {version}, у тебя {Version}. "
                  + "Обнови — часть кнопок может работать не так."
                : $"Astvard: у тебя мод {Version}, на сервере {version}. "
                  + "Заходить это не мешает, но нового сервер ещё не умеет.");
        }

        /// <summary>
        /// Whether <paramref name="mine"/> is behind <paramref name="theirs"/>, compared
        /// number by number: "1.10.0" is ahead of "1.9.0", which comparing the strings gets
        /// backwards. Anything that will not parse counts as equal - the version only ever
        /// decides which sentence to print, and a confidently wrong one is worse than none.
        /// </summary>
        private static bool Behind(string mine, string theirs)
        {
            var a = mine.Split('.');
            var b = theirs.Split('.');

            for (var i = 0; i < 3; i++)
            {
                if (i >= a.Length || i >= b.Length) return false;
                if (!int.TryParse(a[i], out var x) || !int.TryParse(b[i], out var y)) return false;
                if (x != y) return x < y;
            }

            return false;
        }

        /// <summary>Client side: an answer arrived. Only ours counts.</summary>
        private static void OnAdminGrant(long sender, long nonce)
        {
            if (_adminNonce == 0 || nonce != _adminNonce)
            {
                Log.LogWarning($"[AstvardServerMod] Unsolicited admin grant from {sender}; ignored.");
                return;
            }

            _adminNonce = 0;
            IsAdminUnlocked = true;
            RefreshMenu();
            Log.LogInfo("[AstvardServerMod] Admin unlocked by the server.");
            Chat.instance?.AddString("Astvard: админ-кнопки разблокированы.");
        }

        private void OnDestroy()
        {
            // Leaving the keyboard captured would lock the player out of their own game.
            if (_inputBlocked) GUIManager.BlockInput(false);

            if (_roadPreview != null) Destroy(_roadPreview);
            DestroyAreaPreview();
            DestroySortZonePreview();
            DestroyChestLabels();
            DestroyZonePreview();
            DestroyShownZone();
            DestroyChestZone();
            SavePurses();
            _harmony?.UnpatchSelf();
        }
    }
}
