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

            _zones = Config.Bind("Зона", "Zones", "",
                "Области, которые сервер держит загруженными: X,Z,радиус_в_метрах через ';'. "
                + "Радиус округляется наружу до целых зон по 64 м.");
            ParseZones(_zones.Value, Zones);

            _harmony = new Harmony("astvard.servermod");
            _harmony.PatchAll();
            Log.LogInfo("AstvardServerMod loaded");

            RegisterCommand(new HiCommand());
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

        private void OnDestroy()
        {
            // Leaving the keyboard captured would lock the player out of their own game.
            if (_inputBlocked) GUIManager.BlockInput(false);

            if (_roadPreview != null) Destroy(_roadPreview);
            _harmony?.UnpatchSelf();
        }
    }
}
