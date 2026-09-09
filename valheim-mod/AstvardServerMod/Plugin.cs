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
    [BepInPlugin("astvard.servermod", "AstvardServerMod", "1.0.0")]
    [BepInDependency(Jotunn.Main.ModGuid)]
    public partial class Plugin : BaseUnityPlugin
    {
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
            _autoCollect = Config.Bind("Функции", "AutoCollect", false,
                "Складывать готовый продукт в ближайший сундук.");
            _autoFill = Config.Bind("Функции", "AutoFill", false,
                "Подавать сырьё и топливо из ближайшего сундука.");

            _localAdminCommands = Config.Bind("Функции", "LocalAdminCommands",
                "debugmode fly nocost exploremap resetmap tod env resetenv wind resetwind",
                "Команды, которые мод разрешает админу выполнить на своём клиенте. "
                + "Игра запрещает их вне сервера, хотя действуют они только на того, кто ввёл. "
                + "Через пробел; пусто — не разрешать ничего; * — все, какие игра "
                + "отказывает клиенту. Команды, которые игра ретранслирует сама, не "
                + "затрагиваются никогда — иначе они выполнились бы у нас вместо сервера.");

            _zones = Config.Bind("Зона", "Zones", "",
                "Области, которые сервер держит загруженными: X,Z,радиус_в_метрах через ';'. "
                + "Радиус округляется наружу до целых зон по 64 м.");
            ParseZones(_zones.Value, Zones);

            _harmony = new Harmony("astvard.servermod");
            _harmony.PatchAll();
            Log.LogInfo("AstvardServerMod loaded");

            RegisterCommand(new AdminUnlockCommand());

            if (GUIManager.IsHeadless())
            {
                Log.LogInfo("Headless (server) — skipping UI setup.");
                return;
            }

            ReloadTemplates();
            GUIManager.OnCustomGUIAvailable += CreatePanel;
            StartCoroutine(AutomationLoop());
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

        internal static void RegisterAdminRpcs()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null) return;

            rpc.Register<long>(RpcAdminAsk, OnAdminAsk);
            rpc.Register<long>(RpcAdminGrant, OnAdminGrant);
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
            _harmony?.UnpatchSelf();
        }
    }
}
