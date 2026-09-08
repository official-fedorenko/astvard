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
    public class Plugin : BaseUnityPlugin
    {
        // Menu levels shown inside the Tab panel.
        private const int StateRoot = 0;      // Ознакомиться / Активировать
        private const int StateAdmin = 1;     // God / Debugmode / Рельеф
        private const int StateTerrain = 2;   // Выровнять круг / квадрат
        private const int StateTerrainForm = 3; // радиус + высота + применить
        private const int StateBuild = 4;       // Копировать / Вставить
        private const int StateCopyForm = 5;    // радиус копирования + применить
        private const int StateCheats = 6;      // God / Debugmode / Tod
        private const int StateTod = 7;         // время суток 1-10
        private const int StateTemplates = 8;   // Платформы / Дома
        private const int StatePlatforms = 9;   // готовые платформы
        private const int StateHouses = 10;     // категории домов
        private const int StateStarterHouses = 11; // стартовые дома
        private const int StateRepair = 12;     // радиус починки + применить
        private const int StateKitchens = 13;   // категории кухонь
        private const int StateStarterKitchens = 14; // стартовые кухни
        private const int StateProcessing = 15; // переработка
        private const int StateForceDelete = 16; // радиус очистки + применить
        private const int StateFeatures = 17;   // общие функции, видны всем
        private const int StateFill = 18;       // наполнение станций из сундуков
        private const int StateCollect = 19;    // сбор продукта в сундуки
        private const int StateZone = 20;       // свои зоны + вход к чужим
        private const int StateZoneOthers = 21; // список тех, кто ставил зоны
        private const int StateZoneOwner = 22;  // зоны одного игрока
        private const int StateZoneEdit = 23;   // действия над одной зоной
        private const int StateRoad = 24;       // дорожки между двумя точками

        internal static ManualLogSource Log;
        internal static GameObject Panel;

        internal static GameObject InfoButton;
        internal static GameObject InfoText;
        internal static GameObject ActivateButton;
        internal static GameObject GodButton;
        internal static GameObject DebugModeButton;
        internal static GameObject TerrainButton;
        internal static GameObject LevelCircleButton;
        internal static GameObject LevelSquareButton;
        internal static GameObject TerrainHint;
        internal static GameObject RadiusInput;
        internal static GameObject HeightInput;
        internal static GameObject ApplyButton;
        internal static GameObject BackButton;
        internal static GameObject BuildButton;
        internal static GameObject CopyButton;
        internal static GameObject PasteButton;
        internal static GameObject CopyHint;
        internal static GameObject CopyRadiusInput;
        internal static GameObject CopyApplyButton;
        internal static GameObject TemplatesButton;
        internal static GameObject PlatformsCategoryButton;
        internal static GameObject HousesCategoryButton;
        internal static GameObject StarterHousesButton;
        internal static GameObject PlatformTemplateButton;
        internal static GameObject StarterHouse1Button;
        internal static GameObject KitchensCategoryButton;
        internal static GameObject StarterKitchensButton;
        internal static GameObject KitchenFullButton;
        internal static GameObject ProcessingCategoryButton;
        internal static GameObject SmelterHallButton;
        internal static GameObject CharcoalKilnsButton;
        internal static GameObject SnapButton;
        internal static GameObject CheatsButton;
        internal static GameObject TodButton;
        internal static GameObject TodHint;
        internal static GameObject TodInput;
        internal static GameObject TodApplyButton;
        internal static GameObject RepairButton;
        internal static GameObject RepairHint;
        internal static GameObject RepairRadiusInput;
        internal static GameObject RepairApplyButton;
        internal static GameObject ForceDeleteButton;
        internal static GameObject ForceDeleteHint;
        internal static GameObject ForceDeleteRadiusInput;
        internal static GameObject ForceDeleteApplyButton;
        internal static GameObject FeaturesButton;
        internal static GameObject AutoCollectHint;
        internal static GameObject AutoCollectButton;
        internal static GameObject AssignChestButton;
        internal static GameObject UnassignChestButton;
        internal static GameObject FillCategoryButton;
        internal static GameObject CollectCategoryButton;
        internal static GameObject FillHint;
        internal static GameObject FillButton;
        internal static GameObject FillAssignButton;
        internal static GameObject FillUnassignButton;
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
        internal static GameObject RoadButton;
        internal static GameObject RoadHint;
        internal static GameObject RoadWidthInput;
        internal static GameObject RoadCurveInput;
        internal static GameObject RoadAreaInput;
        internal static GameObject RoadAreaButton;
        internal static GameObject RoadStoneButton;
        internal static GameObject RoadDirtButton;
        internal static GameObject RoadStartButton;
        internal static GameObject RoadEndButton;

        private const int MaxZoneButtons = 8;
        internal static readonly GameObject[] ZoneButtons = new GameObject[MaxZoneButtons];
        internal static readonly GameObject[] OwnerButtons = new GameObject[MaxZoneButtons];

        internal static bool IsAdminUnlocked;
        internal static bool IsInfoShown;
        internal static int MenuState = StateRoot;
        private static bool _terrainSquare;

        /// <summary>One copied piece, stored relative to where the player stood.</summary>
        private struct CopiedPiece
        {
            public string Prefab;
            public Vector3 LocalPos;
            public Quaternion LocalRot;
        }

        private static readonly List<CopiedPiece> Clipboard = new List<CopiedPiece>();
        private static readonly List<GameObject> Ghosts = new List<GameObject>();
        private static GameObject GhostRoot;
        private static bool _building;
        private static bool _copyToFile;
        internal static Plugin Instance;

        /// <summary>True while a ghost is following the player, waiting to be placed.</summary>
        internal static bool IsPlacing;

        // Placement adjustments driven by Q/E and shift+Q/E.
        private static float _placeYaw;
        private static float _placeHeight;

        // How far ahead of the player the preview floats.
        private const float PlacementDistance = 11f;
        private const float RotationStep = 22.5f;
        private const float HeightStep = 0.5f;

        /// <summary>Whether the preview latches onto nearby built pieces.</summary>
        internal static bool IsSnapEnabled;
        // null = not armed, true = arm assign, false = arm unassign.
        internal static bool? PendingChestAssign;
        // Which role the armed button is about: supply chest or collection chest.
        internal static bool PendingChestSupply;

        // Unlike the admin toggles this one belongs to the player, so it is
        // worth surviving a restart — hence a config entry rather than a field.
        private static ConfigEntry<bool> _autoCollect;
        private static ConfigEntry<bool> _autoFill;

        // Only the server reads these; a client keeps its own copy of whatever the
        // server last reported, purely to show it in the menu.
        private static ConfigEntry<string> _zones;

        private static float _zoneReportTime = float.NegativeInfinity;
        private static float _pokeTime = float.NegativeInfinity;
        private static float _sectorCacheTime = float.NegativeInfinity;
        private static float _claimTime = float.NegativeInfinity;
        private static int _shownZoneLoaded;
        internal static bool IsAutoCollectEnabled
        {
            get { return _autoCollect != null && _autoCollect.Value; }
            set { if (_autoCollect != null) _autoCollect.Value = value; }
        }

        internal static bool IsAutoFillEnabled
        {
            get { return _autoFill != null && _autoFill.Value; }
            set { if (_autoFill != null) _autoFill.Value = value; }
        }

        // How close two snap points must come before the preview jumps to meet
        // them, how often the surrounding points are re-gathered, and the cell
        // size used to fold coincident points together.
        private const float SnapDistance = 2f;
        private const float SnapCacheInterval = 0.25f;
        private const float SnapDedupeCell = 0.05f;

        /// <summary>Preview snap points in root-local space, gathered once per preview.</summary>
        private static readonly List<Vector3> GhostSnapLocal = new List<Vector3>();

        /// <summary>Built snap points around the preview, bucketed by grid cell.</summary>
        private static readonly Dictionary<long, List<Vector3>> SnapGrid = new Dictionary<long, List<Vector3>>();

        private static readonly List<Transform> SnapTransforms = new List<Transform>();
        private static readonly List<Piece> SnapPieces = new List<Piece>();
        private static float _snapCacheTime = float.NegativeInfinity;
        private static float _ghostRadius = 1f;

        // How many pieces go up per tick, and how long a tick lasts.
        private const int PiecesPerBatch = 5;
        private const float BatchDelay = 0.4f;
        // Time the full ghost outline stays up before the first piece lands.
        private const float GhostPreviewDelay = 1.5f;

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

            CommandManager.Instance.AddConsoleCommand(new HiCommand());
            CommandManager.Instance.AddConsoleCommand(new AdminUnlockCommand());

            if (GUIManager.IsHeadless())
            {
                Log.LogInfo("Headless (server) — skipping UI setup.");
                return;
            }

            GUIManager.OnCustomGUIAvailable += CreatePanel;
            StartCoroutine(AutomationLoop());
        }

        private void CreatePanel()
        {
            var gui = GUIManager.Instance;
            if (gui == null || GUIManager.CustomGUIFront == null)
            {
                Log.LogWarning("GUIManager not ready, cannot create panel.");
                return;
            }

            Panel = gui.CreateWoodpanel(
                GUIManager.CustomGUIFront.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-130f, 500f),
                300f, 200f, true);
            Panel.SetActive(false);

            // Pivot at top-center so growth from added content extends the panel
            // downward instead of expanding symmetrically around the center.
            Panel.GetComponent<RectTransform>().pivot = new Vector2(0.5f, 1f);

            var layout = Panel.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(20, 20, 20, 20);
            layout.spacing = 10f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            var fitter = Panel.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            InfoButton = MakeButton(gui, "Ознакомиться", () =>
            {
                IsInfoShown = !IsInfoShown;
                RefreshMenu();
            });

            InfoText = MakeText(gui, ProjectDescription);

            ActivateButton = MakeButton(gui, "Админ-меню", () =>
            {
                MenuState = StateAdmin;
                RefreshMenu();
            });

            FeaturesButton = MakeButton(gui, "Функции", () =>
            {
                MenuState = StateFeatures;
                RefreshMenu();
            });

            FillCategoryButton = MakeButton(gui, "Наполнение", () =>
            {
                MenuState = StateFill;
                RefreshMenu();
            });

            CollectCategoryButton = MakeButton(gui, "Сбор", () =>
            {
                MenuState = StateCollect;
                RefreshMenu();
            });

            FillHint = MakeText(gui, "Назначь сундук — из него берётся\nруда, топливо, мясо и заготовки\nдля браги. Переключатель —\nзапасной режим: ближайший сундук.");

            FillButton = MakeButton(gui, "Наполнение", () =>
            {
                IsAutoFillEnabled = !IsAutoFillEnabled;
                UpdateFillButtonLabel();
                Log.LogInfo($"[AstvardServerMod] AutoFill: {IsAutoFillEnabled}");
            });
            UpdateFillButtonLabel();

            FillAssignButton = MakeButton(gui, "Назначить сундук", () =>
            {
                PendingChestAssign = true;
                PendingChestSupply = true;
                InventoryGui.instance?.Hide();
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    "Открой сундук, чтобы брать сырьё из него");
            });

            FillUnassignButton = MakeButton(gui, "Отвязать сундук", () =>
            {
                PendingChestAssign = false;
                PendingChestSupply = true;
                InventoryGui.instance?.Hide();
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    "Открой сундук, чтобы отвязать подачу");
            });

            AutoCollectHint = MakeText(gui, "Назначь сундук — в него идут\nслитки, уголь, готовая еда,\nмёд и брага. Переключатель —\nзапасной режим: ближайший сундук.");

            AutoCollectButton = MakeButton(gui, "Сбор в сундук", () =>
            {
                IsAutoCollectEnabled = !IsAutoCollectEnabled;
                UpdateAutoCollectButtonLabel();
                Log.LogInfo($"[AstvardServerMod] AutoCollect: {IsAutoCollectEnabled}");
            });
            UpdateAutoCollectButtonLabel();

            AssignChestButton = MakeButton(gui, "Назначить сундук", () =>
            {
                PendingChestAssign = true;
                PendingChestSupply = false;
                InventoryGui.instance?.Hide();
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    "Открой сундук, чтобы складывать продукт в него");
            });

            UnassignChestButton = MakeButton(gui, "Отвязать сундук", () =>
            {
                PendingChestAssign = false;
                PendingChestSupply = false;
                InventoryGui.instance?.Hide();
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    "Открой сундук, чтобы отвязать сбор");
            });

            ZoneCategoryButton = MakeButton(gui, "Зона автоматики", () =>
            {
                MenuState = StateZone;
                RequestZoneList();
                RefreshMenu();
            });

            ZoneHint = MakeText(gui, "");
            UpdateZoneHint();

            ZoneSizeInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "радиус 32-256 м", 16, 160f, 32f);
            AddFixedSize(ZoneSizeInput, 160f, 32f);

            ZoneAddButton = MakeButton(gui, "Добавить здесь", () =>
            {
                var player = Player.m_localPlayer;
                if (player == null) return;
                var pos = player.transform.position;
                var radius = Mathf.Clamp((int)ParseField(ZoneSizeInput, MinZoneRadius),
                    MinZoneRadius, MaxZoneRadius);
                ZRoutedRpc.instance?.InvokeRoutedRPC(RpcZoneAdd, pos.x, pos.z, radius);
            });

            // One button per zone, filled in from whichever list the current page shows.
            // Unity widgets are built once, so the pool is fixed and the labels move.
            for (var slot = 0; slot < MaxZoneButtons; slot++)
            {
                var index = slot;
                ZoneButtons[slot] = MakeButton(gui, "", () =>
                {
                    if (index >= VisibleZones.Count) return;
                    _editingZone = VisibleZones[index];
                    MenuState = StateZoneEdit;
                    RefreshMenu();
                });
            }

            for (var slot = 0; slot < MaxZoneButtons; slot++)
            {
                var index = slot;
                OwnerButtons[slot] = MakeButton(gui, "", () =>
                {
                    if (index >= ZoneOwners.Count) return;
                    _selectedOwner = ZoneOwners[index];
                    MenuState = StateZoneOwner;
                    RefreshMenu();
                });
            }

            ZoneOthersButton = MakeButton(gui, "Зоны других", () =>
            {
                MenuState = StateZoneOthers;
                RefreshMenu();
            });

            ZoneClearButton = MakeButton(gui, "Удалить все", () =>
            {
                ZRoutedRpc.instance?.InvokeRoutedRPC(RpcZoneDel, 0f, 0f, true);
            });

            ZoneEditHint = MakeText(gui, "");

            ZoneMoveButton = MakeButton(gui, "Переместить сюда", () =>
            {
                var zone = EditedZone();
                var player = Player.m_localPlayer;
                if (zone == null || player == null) return;

                var pos = player.transform.position;
                ZRoutedRpc.instance?.InvokeRoutedRPC(RpcZoneMove,
                    zone.Value.X, zone.Value.Z, pos.x, pos.z, zone.Value.Radius);
            });

            ZoneRadiusButton = MakeButton(gui, "Задать радиус", () =>
            {
                var zone = EditedZone();
                if (zone == null) return;

                var radius = Mathf.Clamp((int)ParseField(ZoneSizeInput, zone.Value.Radius),
                    MinZoneRadius, MaxZoneRadius);
                ZRoutedRpc.instance?.InvokeRoutedRPC(RpcZoneMove,
                    zone.Value.X, zone.Value.Z, zone.Value.X, zone.Value.Z, radius);
            });

            ZoneDeleteButton = MakeButton(gui, "Удалить", () =>
            {
                var zone = EditedZone();
                if (zone == null) return;

                ZRoutedRpc.instance?.InvokeRoutedRPC(RpcZoneDel, zone.Value.X, zone.Value.Z, false);
                MenuState = StateZone;
                RefreshMenu();
            });

            CheatsButton = MakeButton(gui, "Читы", () =>
            {
                MenuState = StateCheats;
                RefreshMenu();
            });

            GodButton = MakeButton(gui, "God", () =>
            {
                if (Player.m_localPlayer == null) return;
                var newState = !Player.m_localPlayer.InGodMode();
                Player.m_localPlayer.SetGodMode(newState);
                Log.LogInfo($"[AstvardServerMod] God mode: {newState}");
            });

            DebugModeButton = MakeButton(gui, "Debugmode", () =>
            {
                Player.m_debugMode = !Player.m_debugMode;
                Log.LogInfo($"[AstvardServerMod] Debugmode: {Player.m_debugMode}");
            });

            TodButton = MakeButton(gui, "Tod", () =>
            {
                MenuState = StateTod;
                RefreshMenu();
            });

            TodHint = MakeText(gui, "Время суток 1-10.\n2.5 — рассвет, 5 — полдень,\n7.5 — закат, 10 — полночь.");

            TodInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "1-10, напр. 5", 16, 160f, 32f);
            AddFixedSize(TodInput, 160f, 32f);

            TodApplyButton = MakeButton(gui, "Установить", ApplyTimeOfDay);

            RepairButton = MakeButton(gui, "Починить всё", () =>
            {
                MenuState = StateRepair;
                RefreshMenu();
            });

            RepairHint = MakeText(gui, "Радиус (м).\nЧинит все постройки вокруг\nи заправляет костры, факелы,\nпечи и плавильни.");

            RepairRadiusInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "радиус, напр. 20", 16, 160f, 32f);
            AddFixedSize(RepairRadiusInput, 160f, 32f);

            RepairApplyButton = MakeButton(gui, "Починить", RunRepair);

            ForceDeleteButton = MakeButton(gui, "Forcedelete", () =>
            {
                MenuState = StateForceDelete;
                RefreshMenu();
            });

            ForceDeleteHint = MakeText(gui, "Радиус (м), максимум 50.\nСносит ВСЁ вокруг: постройки,\nдеревья, камни, мобов, предметы.\nОтменить нельзя — сохранись заранее.");

            ForceDeleteRadiusInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "радиус, напр. 5", 16, 160f, 32f);
            AddFixedSize(ForceDeleteRadiusInput, 160f, 32f);

            ForceDeleteApplyButton = MakeButton(gui, "Снести", RunForceDelete);

            TerrainButton = MakeButton(gui, "Рельеф", () =>
            {
                MenuState = StateTerrain;
                RefreshMenu();
            });

            LevelCircleButton = MakeButton(gui, "Выровнять круг", () =>
            {
                _terrainSquare = false;
                MenuState = StateTerrainForm;
                RefreshMenu();
            });

            LevelSquareButton = MakeButton(gui, "Выровнять квадрат", () =>
            {
                _terrainSquare = true;
                MenuState = StateTerrainForm;
                RefreshMenu();
            });

            TerrainHint = MakeText(gui, "Радиус (м) и высота.\n0 = уровень игрока.\nКрая сшиваются автоматически.");

            RadiusInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "радиус, напр. 8", 16, 160f, 32f);
            AddFixedSize(RadiusInput, 160f, 32f);

            HeightInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "высота, напр. 0", 16, 160f, 32f);
            AddFixedSize(HeightInput, 160f, 32f);


            ApplyButton = MakeButton(gui, "Применить", ApplyTerrainLevel);

            RoadButton = MakeButton(gui, "Дорожка", () =>
            {
                MenuState = StateRoad;
                RefreshMenu();
            });

            RoadHint = MakeText(gui, "");
            UpdateRoadHint();

            RoadWidthInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "ширина, напр. 3", 16, 160f, 32f);
            AddFixedSize(RoadWidthInput, 160f, 32f);

            RoadCurveInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "изгиб 0-10, минус — влево", 16, 160f, 32f);
            AddFixedSize(RoadCurveInput, 160f, 32f);

            RoadAreaInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "радиус площадки, напр. 8", 16, 160f, 32f);
            AddFixedSize(RoadAreaInput, 160f, 32f);

            RoadStoneButton = MakeButton(gui, "Каменная", () =>
            {
                _roadPaved = true;
                UpdateRoadHint();
            });

            RoadDirtButton = MakeButton(gui, "Земляная", () =>
            {
                _roadPaved = false;
                UpdateRoadHint();
            });

            RoadStartButton = MakeButton(gui, "Начать", () =>
            {
                var player = Player.m_localPlayer;
                if (player == null) return;

                _roadStart = player.transform.position;
                _roadStarted = true;
                UpdateRoadHint();
                InventoryGui.instance?.Hide();
                player.Message(MessageHud.MessageType.Center, "Начало отмечено");
            });

            RoadEndButton = MakeButton(gui, "Закончить", BuildRoad);

            RoadAreaButton = MakeButton(gui, "Вокруг меня", BuildArea);

            BuildButton = MakeButton(gui, "Постройки", () =>
            {
                MenuState = StateBuild;
                RefreshMenu();
            });

            CopyButton = MakeButton(gui, "Копировать", () =>
            {
                _copyToFile = false;
                MenuState = StateCopyForm;
                RefreshMenu();
            });

            PasteButton = MakeButton(gui, "Скопировать", () =>
            {
                _copyToFile = true;
                MenuState = StateCopyForm;
                RefreshMenu();
            });

            SnapButton = MakeButton(gui, "Прилипание", () =>
            {
                IsSnapEnabled = !IsSnapEnabled;
                UpdateSnapButtonLabel();
                Log.LogInfo($"[AstvardServerMod] Snapping: {IsSnapEnabled}");
            });
            UpdateSnapButtonLabel();

            TemplatesButton = MakeButton(gui, "Шаблоны", () =>
            {
                MenuState = StateTemplates;
                RefreshMenu();
            });

            PlatformsCategoryButton = MakeButton(gui, "Платформы", () =>
            {
                MenuState = StatePlatforms;
                RefreshMenu();
            });

            HousesCategoryButton = MakeButton(gui, "Дома", () =>
            {
                MenuState = StateHouses;
                RefreshMenu();
            });

            StarterHousesButton = MakeButton(gui, "Стартовые", () =>
            {
                MenuState = StateStarterHouses;
                RefreshMenu();
            });

            PlatformTemplateButton = MakeButton(gui, "Платформа 4х4", () =>
            {
                LoadPlatformTemplate();
                StartPlacement();
                InventoryGui.instance?.Hide();
            });

            StarterHouse1Button = MakeButton(gui, "Стартовый дом №1", () =>
            {
                if (!LoadTemplate(Templates.StarterHouse1, "Стартовый дом №1")) return;
                StartPlacement();
                InventoryGui.instance?.Hide();
            });

            KitchensCategoryButton = MakeButton(gui, "Кухни", () =>
            {
                MenuState = StateKitchens;
                RefreshMenu();
            });

            StarterKitchensButton = MakeButton(gui, "Стартовые", () =>
            {
                MenuState = StateStarterKitchens;
                RefreshMenu();
            });

            KitchenFullButton = MakeButton(gui, "Полная", () =>
            {
                if (!LoadTemplate(Templates.KitchenFull, "Полная кухня")) return;
                StartPlacement();
                InventoryGui.instance?.Hide();
            });

            ProcessingCategoryButton = MakeButton(gui, "Переработка", () =>
            {
                MenuState = StateProcessing;
                RefreshMenu();
            });

            SmelterHallButton = MakeButton(gui, "Плавильня", () =>
            {
                if (!LoadTemplate(Templates.SmelterHall, "Плавильня")) return;
                StartPlacement();
                InventoryGui.instance?.Hide();
            });

            CharcoalKilnsButton = MakeButton(gui, "Угольные печи", () =>
            {
                if (!LoadTemplate(Templates.CharcoalKilns, "Угольные печи")) return;
                StartPlacement();
                InventoryGui.instance?.Hide();
            });

            CopyHint = MakeText(gui, "Радиус (м).\nКопировать — проекция перед\nтобой, ЛКМ строит, Esc отменяет.\nQ/E — поворот, Shift+Q/E — высота.\nСкопировать — чертёж в файл.");

            CopyRadiusInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "радиус, напр. 10", 16, 160f, 32f);
            AddFixedSize(CopyRadiusInput, 160f, 32f);

            CopyApplyButton = MakeButton(gui, "Выполнить", () => { if (_copyToFile) RunCopyToFile(); else RunCopy(); });

            BackButton = MakeButton(gui, "Назад", () =>
            {
                if (MenuState == StateTerrainForm) MenuState = StateTerrain;
                else if (MenuState == StateRoad) MenuState = StateTerrain;
                else if (MenuState == StateCopyForm) MenuState = StateBuild;
                else if (MenuState == StateTod || MenuState == StateRepair ||
                         MenuState == StateForceDelete) MenuState = StateCheats;
                else if (MenuState == StateTemplates) MenuState = StateBuild;
                else if (MenuState == StatePlatforms || MenuState == StateHouses ||
                         MenuState == StateKitchens || MenuState == StateProcessing) MenuState = StateTemplates;
                else if (MenuState == StateStarterHouses) MenuState = StateHouses;
                else if (MenuState == StateStarterKitchens) MenuState = StateKitchens;
                else if (MenuState == StateFill || MenuState == StateCollect) MenuState = StateFeatures;
                else if (MenuState == StateZoneEdit) MenuState = StateZone;
                else if (MenuState == StateZoneOwner) MenuState = StateZoneOthers;
                else if (MenuState == StateZoneOthers) MenuState = StateZone;
                else if (MenuState == StateFeatures) MenuState = StateRoot;
                else MenuState = StateAdmin;
                RefreshMenu();
            });

            RefreshMenu();
            Log.LogInfo("Astvard panel created.");
        }

        private GameObject MakeButton(GUIManager gui, string text, UnityEngine.Events.UnityAction onClick)
        {
            var go = gui.CreateButton(
                text, Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                180f, 40f);
            AddFixedSize(go, 180f, 40f);
            go.GetComponent<Button>().onClick.AddListener(onClick);
            go.SetActive(false);
            return go;
        }

        private static void UpdateSnapButtonLabel()
        {
            var label = SnapButton != null ? SnapButton.GetComponentInChildren<Text>() : null;
            if (label != null) label.text = IsSnapEnabled ? "Прилипание: вкл" : "Прилипание: выкл";
        }

        private static void UpdateAutoCollectButtonLabel()
        {
            var label = AutoCollectButton != null ? AutoCollectButton.GetComponentInChildren<Text>() : null;
            if (label != null) label.text = IsAutoCollectEnabled ? "Сбор в сундук: вкл" : "Сбор в сундук: выкл";
        }

        private static void UpdateFillButtonLabel()
        {
            var label = FillButton != null ? FillButton.GetComponentInChildren<Text>() : null;
            if (label != null) label.text = IsAutoFillEnabled ? "Наполнение: вкл" : "Наполнение: выкл";
        }

        private GameObject MakeText(GUIManager gui, string text)
        {
            var go = gui.CreateText(
                text, Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                gui.AveriaSerif, 16, gui.ValheimBeige, true, Color.black,
                260f, 0f, true);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = 260f;
            go.SetActive(false);
            return go;
        }

        /// <summary>
        /// Freezes the world clock at the given time, the same way the console's
        /// "tod" command does. Input is 1-10 for convenience; the game wants 0-1.
        /// </summary>
        private static void ApplyTimeOfDay()
        {
            var env = EnvMan.instance;
            if (env == null)
            {
                Log.LogWarning("[AstvardServerMod] EnvMan not ready.");
                return;
            }

            var value = Mathf.Clamp(ParseField(TodInput, 5f), 1f, 10f);
            env.m_debugTimeOfDay = true;
            env.m_debugTime = value / 10f;

            Log.LogInfo($"[AstvardServerMod] Time of day set to {value} ({env.m_debugTime:F2}).");
        }

        // CookingStation and Smelter keep their fuel setter private, unlike
        // Fireplace — both just write the ZDO, so calling them is safe.
        private static readonly System.Reflection.MethodInfo MCookingSetFuel =
            AccessTools.Method(typeof(CookingStation), "SetFuel");
        private static readonly System.Reflection.MethodInfo MSmelterSetFuel =
            AccessTools.Method(typeof(Smelter), "SetFuel");

        private const float AutoCollectRadius = 8f;

        // An assigned chest is a deliberate choice, so it reaches further than the
        // "whatever is closest" fallback.
        private const float AssignedChestRadius = 24f;

        // Lives in the chest's own ZDO, so the assignment is part of the world: it
        // survives a relog and every player sees the same chest, not only whoever set it.
        private const string CollectChestKey = "astvard_collect";
        private const string SupplyChestKey = "astvard_supply";

        private const float HarvestScanRadius = 64f;

        internal static bool IsCollectChest(Container container)
        {
            return HasChestFlag(container, CollectChestKey);
        }

        internal static bool IsSupplyChest(Container container)
        {
            return HasChestFlag(container, SupplyChestKey);
        }

        private static bool HasChestFlag(Container container, string key)
        {
            if (container == null) return false;
            var view = container.GetComponent<ZNetView>();
            return view != null && view.IsValid() && view.GetZDO().GetBool(key);
        }

        /// <summary>
        /// Gives a chest one of its two roles, or takes it away. Returns false when the
        /// chest cannot be written to, so the caller can let it open as usual.
        /// A chest may hold both roles at once — nothing stops a barrel of coal from
        /// also being where the coal ends up.
        /// </summary>
        internal static bool SetChestRole(Container container, bool supply, bool enabled)
        {
            PendingChestAssign = null;

            var view = container != null ? container.GetComponent<ZNetView>() : null;
            if (view == null || !view.IsValid()) return false;

            // The flag only sticks if the owner writes it, same as the inventory itself.
            if (!view.IsOwner()) view.ClaimOwnership();

            var key = supply ? SupplyChestKey : CollectChestKey;
            var was = view.GetZDO().GetBool(key);
            view.GetZDO().Set(key, enabled);

            string message;
            if (was == enabled)
                message = supply
                    ? (enabled ? "Из этого сундука уже берётся сырьё" : "Из этого сундука и так не берут")
                    : (enabled ? "Этот сундук уже для сбора" : "Этот сундук и так не для сбора");
            else
                message = supply
                    ? (enabled ? "Сундук назначен на подачу" : "Подача с сундука снята")
                    : (enabled ? "Сундук назначен для сбора" : "Сундук отвязан");

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, message);
            Log.LogInfo("[AstvardServerMod] Chest " + (supply ? "supply " : "collect ")
                        + (enabled ? "set" : "cleared") + ".");
            return true;
        }


        /// <summary>
        /// Puts a produced item into a chest near <paramref name="origin"/>. Returns
        /// false when nothing could take it, so every caller can fall back to the
        /// vanilla behaviour of dropping it on the ground.
        /// </summary>
        internal static bool TryStoreNearby(Vector3 origin, GameObject prefab, int amount)
        {
            if (prefab == null || amount <= 0) return false;

            // An assigned chest is a decision made in the world, so it works for
            // whoever happens to be nearby. The personal toggle only governs the
            // guess-the-nearest-chest fallback.
            var target = FindChest(origin, AssignedChestRadius, prefab, amount, true)
                         ?? (IsAutoCollectEnabled
                             ? FindChest(origin, AutoCollectRadius, prefab, amount, false)
                             : null);
            if (target == null) return false;

            // A container saves itself to its ZDO only from the owner's side, so adding
            // to one we do not own would live in local memory and vanish on reload.
            var targetView = target.GetComponent<ZNetView>();
            if (!targetView.IsOwner()) targetView.ClaimOwnership();

            return target.GetInventory().AddItem(prefab, amount);
        }

        /// <summary>
        /// Routes a smelter's finished product into a nearby chest. Covers smelters,
        /// blast furnaces and charcoal kilns alike — the game gives them all the same
        /// Smelter component.
        /// </summary>
        internal static bool TryCollectToChest(Smelter smelter, string ore, int stack)
        {
            if (smelter == null || stack <= 0) return false;

            var product = FindProduct(smelter, ore);
            if (!TryStoreNearby(smelter.transform.position, product, stack)) return false;

            // Spawn() plays this before dropping the item; keep the cue so the player
            // still gets the usual feedback that something came out.
            smelter.m_produceEffects.Create(smelter.transform.position, smelter.transform.rotation);
            return true;
        }

        private static Container FindChest(Vector3 origin, float radius, GameObject product,
                                           int stack, bool assignedOnly)
        {
            var pieces = new List<Piece>();
            Piece.GetAllPiecesInRadius(origin, radius, pieces);

            Container best = null;
            var bestSqr = float.MaxValue;

            foreach (var piece in pieces)
            {
                if (piece == null) continue;

                var container = piece.GetComponentInChildren<Container>();
                if (container == null) continue;
                if (assignedOnly != IsCollectChest(container)) continue;

                // Writing into a chest somebody has open is a reliable way to desync it.
                if (container.IsInUse()) continue;

                var view = container.GetComponent<ZNetView>();
                if (view == null || !view.IsValid()) continue;
                if (!container.GetInventory().CanAddItem(product, stack)) continue;

                // Sorting the same goods together beats raw proximity: with a chest
                // by the kiln and another by the kitchen, coal and food stop mixing.
                var sqr = (container.transform.position - origin).sqrMagnitude;
                if (!AlreadyHolds(container, product)) sqr += SortingBias;
                if (sqr >= bestSqr) continue;

                best = container;
                bestSqr = sqr;
            }

            return best;
        }

        // Far enough to outweigh any distance inside the search radius.
        private const float SortingBias = 1e6f;

        private static bool AlreadyHolds(Container container, GameObject prefab)
        {
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null) return false;
            return container.GetInventory().HaveItem(drop.m_itemData.m_shared.m_name);
        }

        private static readonly System.Reflection.MethodInfo MCookingFreeSlot =
            AccessTools.Method(typeof(CookingStation), "GetFreeSlot");

        private static readonly List<string> AcceptScratch = new List<string>();

        /// <summary>
        /// One sweep a second drives both halves: producers that only hand their goods
        /// over when asked, and stations waiting to be fed. Each vanilla "take" routine
        /// already refuses to act when nothing is ready, so they can be asked blindly.
        /// </summary>
        private static IEnumerator AutomationLoop()
        {
            var pieces = new List<Piece>();
            var collectSpots = new List<Vector3>();
            var supplyChests = new List<Container>();
            var anyChests = new List<Container>();

            while (true)
            {
                yield return new WaitForSeconds(1f);

                var player = Player.m_localPlayer;
                if (player == null) continue;

                pieces.Clear();
                Piece.GetAllPiecesInRadius(player.transform.position, HarvestScanRadius, pieces);

                // Sort the chests out of the same sweep. Asking per station would mean
                // re-walking every loaded piece once for each of them.
                collectSpots.Clear();
                supplyChests.Clear();
                anyChests.Clear();
                foreach (var piece in pieces)
                {
                    if (piece == null) continue;
                    var container = piece.GetComponentInChildren<Container>();
                    if (container == null) continue;

                    anyChests.Add(container);
                    if (IsCollectChest(container)) collectSpots.Add(container.transform.position);
                    if (IsSupplyChest(container)) supplyChests.Add(container);
                }

                // With no supply chest and the toggle off there is nothing to feed
                // from, so skip the reads and reflection the feeding half would do.
                var feeding = IsAutoFillEnabled || supplyChests.Count > 0;

                foreach (var piece in pieces)
                {
                    if (piece == null) continue;

                    var cooking = piece.GetComponentInChildren<CookingStation>();
                    if (cooking != null)
                    {
                        if (MayHarvest(cooking, collectSpots))
                            cooking.GetComponent<ZNetView>()
                                .InvokeRPC("RPC_RemoveDoneItem", player.transform.position, 1);
                        if (feeding) FillCooking(cooking, supplyChests, anyChests);
                    }

                    var beehive = piece.GetComponentInChildren<Beehive>();
                    if (beehive != null && MayHarvest(beehive, collectSpots))
                        beehive.GetComponent<ZNetView>().InvokeRPC("RPC_Extract");

                    var fermenter = piece.GetComponentInChildren<Fermenter>();
                    if (fermenter != null)
                    {
                        if (MayHarvest(fermenter, collectSpots))
                            fermenter.GetComponent<ZNetView>().InvokeRPC("RPC_Tap");
                        if (feeding) FillFermenter(fermenter, supplyChests, anyChests);
                    }

                    var smelter = piece.GetComponentInChildren<Smelter>();
                    if (smelter != null && feeding) FillSmelter(smelter, supplyChests, anyChests);

                    var fireplace = piece.GetComponentInChildren<Fireplace>();
                    if (fireplace != null && feeding) FillFireplace(fireplace, supplyChests, anyChests);
                }
            }
        }

        /// <summary>
        /// Only the owner runs a producer's logic. The chest check matters too: without
        /// somewhere to put the goods, harvesting unattended would just tip them onto
        /// the ground, which is worse than leaving them where they are.
        /// </summary>
        private static bool MayHarvest(Component producer, List<Vector3> assignedChests)
        {
            if (!OwnedAndValid(producer)) return false;
            if (IsAutoCollectEnabled) return true;

            var origin = producer.transform.position;
            var range = AssignedChestRadius * AssignedChestRadius;
            foreach (var chest in assignedChests)
                if ((chest - origin).sqrMagnitude <= range) return true;

            return false;
        }

        private static bool OwnedAndValid(Component producer)
        {
            var view = producer.GetComponent<ZNetView>();
            return view != null && view.IsValid() && view.IsOwner();
        }

        // ---------------- feeding ----------------

        /// <summary>
        /// Removes one unit of anything the station accepts from a chest, and reports
        /// which prefab it was. A chest explicitly put on supply duty is tried first and
        /// reaches further; the personal toggle only enables the guess-the-nearest pass.
        /// </summary>
        private static string TakeSupply(Vector3 origin, List<Container> supplyChests,
                                         List<Container> anyChests, List<string> accepted)
        {
            if (accepted.Count == 0) return null;

            return TakeFrom(origin, supplyChests, AssignedChestRadius, accepted)
                   ?? (IsAutoFillEnabled ? TakeFrom(origin, anyChests, AutoCollectRadius, accepted) : null);
        }

        private static string TakeFrom(Vector3 origin, List<Container> chests,
                                       float radius, List<string> accepted)
        {
            var range = radius * radius;
            Container bestChest = null;
            ItemDrop.ItemData bestItem = null;
            var bestSqr = float.MaxValue;

            foreach (var container in chests)
            {
                if (container == null || container.IsInUse()) continue;

                var sqr = (container.transform.position - origin).sqrMagnitude;
                if (sqr > range || sqr >= bestSqr) continue;

                var view = container.GetComponent<ZNetView>();
                if (view == null || !view.IsValid()) continue;

                foreach (var item in container.GetInventory().GetAllItems())
                {
                    if (item == null || item.m_dropPrefab == null) continue;
                    if (!accepted.Contains(item.m_dropPrefab.name)) continue;

                    bestChest = container;
                    bestItem = item;
                    bestSqr = sqr;
                    break;
                }
            }

            if (bestChest == null) return null;

            // Same rule as storing: only the owner's write reaches the ZDO.
            var bestView = bestChest.GetComponent<ZNetView>();
            if (!bestView.IsOwner()) bestView.ClaimOwnership();

            var name = bestItem.m_dropPrefab.name;
            return bestChest.GetInventory().RemoveItem(bestItem, 1) ? name : null;
        }

        private static void FillSmelter(Smelter smelter, List<Container> supply, List<Container> any)
        {
            if (!OwnedAndValid(smelter)) return;

            var view = smelter.GetComponent<ZNetView>();
            var zdo = view.GetZDO();
            var origin = smelter.transform.position;

            if (smelter.m_maxFuel > 0 && smelter.m_fuelItem != null &&
                zdo.GetFloat(ZDOVars.s_fuel) <= smelter.m_maxFuel - 1)
            {
                AcceptScratch.Clear();
                AcceptScratch.Add(smelter.m_fuelItem.gameObject.name);
                if (TakeSupply(origin, supply, any, AcceptScratch) != null)
                    view.InvokeRPC("RPC_AddFuel");
            }

            if (zdo.GetInt(ZDOVars.s_queued) < smelter.m_maxOre)
            {
                AcceptScratch.Clear();
                foreach (var conversion in smelter.m_conversion)
                    if (conversion != null && conversion.m_from != null)
                        AcceptScratch.Add(conversion.m_from.gameObject.name);

                var ore = TakeSupply(origin, supply, any, AcceptScratch);
                if (ore != null) view.InvokeRPC("RPC_AddOre", ore);
            }
        }

        private static void FillCooking(CookingStation cooking, List<Container> supply, List<Container> any)
        {
            if (!OwnedAndValid(cooking)) return;

            var view = cooking.GetComponent<ZNetView>();
            var origin = cooking.transform.position;

            if (cooking.m_useFuel && cooking.m_fuelItem != null &&
                view.GetZDO().GetFloat(ZDOVars.s_fuel) <= cooking.m_maxFuel - 1)
            {
                AcceptScratch.Clear();
                AcceptScratch.Add(cooking.m_fuelItem.gameObject.name);
                if (TakeSupply(origin, supply, any, AcceptScratch) != null)
                    view.InvokeRPC("RPC_AddFuel");
            }

            if (MCookingFreeSlot == null) return;
            if ((int)MCookingFreeSlot.Invoke(cooking, null) == -1) return;

            AcceptScratch.Clear();
            foreach (var conversion in cooking.m_conversion)
                if (conversion != null && conversion.m_from != null)
                    AcceptScratch.Add(conversion.m_from.gameObject.name);

            var raw = TakeSupply(origin, supply, any, AcceptScratch);
            if (raw != null) view.InvokeRPC("RPC_AddItem", raw);
        }

        private static void FillFermenter(Fermenter fermenter, List<Container> supply, List<Container> any)
        {
            if (!OwnedAndValid(fermenter)) return;

            var view = fermenter.GetComponent<ZNetView>();
            // Status.Empty is exactly "no content stored", so the ZDO answers this
            // without reaching for the private enum.
            if (!string.IsNullOrEmpty(view.GetZDO().GetString(ZDOVars.s_content))) return;

            AcceptScratch.Clear();
            foreach (var conversion in fermenter.m_conversion)
                if (conversion != null && conversion.m_from != null)
                    AcceptScratch.Add(conversion.m_from.gameObject.name);

            var brew = TakeSupply(fermenter.transform.position, supply, any, AcceptScratch);
            if (brew != null) view.InvokeRPC("RPC_AddItem", brew);
        }

        private static void FillFireplace(Fireplace fireplace, List<Container> supply, List<Container> any)
        {
            if (!OwnedAndValid(fireplace) || fireplace.m_fuelItem == null) return;
            if (fireplace.m_infiniteFuel) return;

            var view = fireplace.GetComponent<ZNetView>();
            if (view.GetZDO().GetFloat(ZDOVars.s_fuel) > fireplace.m_maxFuel - 1f) return;

            AcceptScratch.Clear();
            AcceptScratch.Add(fireplace.m_fuelItem.gameObject.name);
            if (TakeSupply(fireplace.transform.position, supply, any, AcceptScratch) != null)
                view.InvokeRPC("RPC_AddFuel");
        }

        private static GameObject FindProduct(Smelter smelter, string ore)
        {
            foreach (var conversion in smelter.m_conversion)
            {
                if (conversion == null || conversion.m_from == null) continue;
                if (conversion.m_from.gameObject.name != ore) continue;
                return conversion.m_to != null ? conversion.m_to.gameObject : null;
            }
            return null;
        }

        /// <summary>
        /// Mirrors the game's own "forcedelete" console command, including its list of
        /// protected objects, so the button does exactly what the command does.
        /// </summary>
        private static void RunForceDelete()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            // The command caps at 50 and so do we: the sweep walks every GameObject
            // in the scene, and a bigger bite is more damage than anyone can undo.
            var radius = Mathf.Clamp(ParseField(ForceDeleteRadiusInput, 5f), 1f, 50f);
            var origin = player.transform.position;
            var sqrRadius = radius * radius;

            var removed = 0;

            // Sorting the whole scene by instance id would be wasted work here.
            foreach (var obj in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
            {
                // Destroying one object can take its children with it, so re-check.
                if (obj == null) continue;
                if ((obj.transform.position - origin).sqrMagnitude > sqrRadius) continue;
                if (IsProtectedFromDelete(obj)) continue;

                var destructible = obj.GetComponent<Destructible>();
                if (destructible != null)
                {
                    destructible.DestroyNow();
                    removed++;
                }
                else if (obj.GetComponent<ZNetView>() != null && ZNetScene.instance != null)
                {
                    ZNetScene.instance.Destroy(obj);
                    removed++;
                }
            }

            player.Message(MessageHud.MessageType.Center,
                removed == 0 ? "Сносить нечего" : $"Снесено объектов: {removed}");
            Log.LogInfo($"[AstvardServerMod] ForceDelete r={radius} -> {removed} removed.");
        }

        private static bool IsProtectedFromDelete(GameObject obj)
        {
            // A placement preview is not part of the world, so it must survive the
            // sweep — its pieces sit in the same scene as real ones.
            if (GhostRoot != null && obj.transform.IsChildOf(GhostRoot.transform)) return true;

            if (obj.GetComponentInParent<Game>() != null) return true;
            if (obj.GetComponentInParent<Player>() != null) return true;
            if (obj.GetComponentInParent<Valkyrie>() != null) return true;
            if (obj.GetComponentInParent<LocationProxy>() != null) return true;
            if (obj.GetComponentInParent<Room>() != null) return true;
            if (obj.GetComponentInParent<Vegvisir>() != null) return true;
            if (obj.GetComponentInParent<DungeonGenerator>() != null) return true;

            var path = TransformPath(obj.transform);
            return path.Contains("StartTemple") || path.Contains("BossStone");
        }

        /// <summary>
        /// The command checks the full scene path for a couple of names. The game's own
        /// GetPath() extension lives outside assembly_valheim, so build the path here.
        /// </summary>
        private static string TransformPath(Transform t)
        {
            var path = new System.Text.StringBuilder(t.name);
            for (var parent = t.parent; parent != null; parent = parent.parent)
                path.Insert(0, parent.name + "/");
            return path.ToString();
        }

        /// <summary>
        /// Repairs every damaged structure around the player and tops up
        /// everything that burns fuel. Repair goes through WearNTear's own
        /// Repair(), so the health change is replicated the same way a hammer
        /// swing would do it — no ZDO is written behind the game's back.
        /// Ownership is claimed first: Repair() fires an RPC at the owner, and a
        /// piece nobody owns would otherwise swallow it silently.
        /// </summary>
        private static void RunRepair()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            var radius = Mathf.Clamp(ParseField(RepairRadiusInput, 20f), 1f, 200f);
            var origin = player.transform.position;
            var sqrRadius = radius * radius;

            var repaired = 0;
            var skipped = 0;

            // The list is live and Repair() can spawn effects, so iterate a copy.
            foreach (var wear in WearNTear.GetAllInstances().ToList())
            {
                if (wear == null) continue;
                if ((wear.transform.position - origin).sqrMagnitude > sqrRadius) continue;

                var nview = wear.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;
                if (wear.GetHealthPercentage() >= 1f) continue;

                if (!nview.IsOwner()) nview.ClaimOwnership();

                if (wear.Repair()) repaired++;
                else skipped++;
            }

            var filled = RefuelAround(origin, radius);

            string message;
            if (repaired == 0 && filled == 0) message = "Всё целое и заправлено";
            else if (filled == 0) message = $"Починено построек: {repaired}";
            else if (repaired == 0) message = $"Заправлено: {filled}";
            else message = $"Починено: {repaired}, заправлено: {filled}";

            player.Message(MessageHud.MessageType.Center, message);

            Log.LogInfo($"[AstvardServerMod] Repair r={radius} -> {repaired} repaired, " +
                        $"{skipped} skipped, {filled} refuelled.");
        }

        /// <summary>
        /// Tops up everything burning fuel nearby: fireplaces (torches, campfires,
        /// hearths, braziers), fuelled cooking stations and smelters. Only the
        /// fuel is filled — a smelter still needs its own ore, since deciding what
        /// it should be smelting is not ours to make.
        /// </summary>
        private static int RefuelAround(Vector3 origin, float radius)
        {
            var pieces = new List<Piece>();
            Piece.GetAllPiecesInRadius(origin, radius, pieces);

            var filled = 0;
            foreach (var piece in pieces)
            {
                if (piece == null) continue;

                var fireplace = piece.GetComponentInChildren<Fireplace>();
                if (fireplace != null && !fireplace.m_infiniteFuel &&
                    ClaimForRefuel(fireplace, fireplace.m_maxFuel) != null)
                {
                    // Fireplace replicates the change itself, no ZDO poking needed.
                    fireplace.SetFuel(fireplace.m_maxFuel);
                    filled++;
                }

                var cooking = piece.GetComponentInChildren<CookingStation>();
                if (cooking != null && cooking.m_useFuel &&
                    SetFuelDirect(cooking, MCookingSetFuel, cooking.m_maxFuel)) filled++;

                var smelter = piece.GetComponentInChildren<Smelter>();
                if (smelter != null && smelter.m_maxFuel > 0 &&
                    SetFuelDirect(smelter, MSmelterSetFuel, smelter.m_maxFuel)) filled++;
            }

            return filled;
        }

        /// <summary>
        /// Returns the station's view once it is owned locally and actually short
        /// on fuel, or null when there is nothing to do.
        /// </summary>
        private static ZNetView ClaimForRefuel(Component station, float max)
        {
            var nview = station.GetComponentInParent<ZNetView>();
            if (nview == null || !nview.IsValid()) return null;
            if (nview.GetZDO().GetFloat(ZDOVars.s_fuel, 0f) >= max) return null;

            if (!nview.IsOwner()) nview.ClaimOwnership();
            return nview;
        }

        /// <summary>Fills a station whose own SetFuel is private and owner-only.</summary>
        private static bool SetFuelDirect(Component station, System.Reflection.MethodInfo setFuel, float max)
        {
            if (setFuel == null || ClaimForRefuel(station, max) == null) return false;
            setFuel.Invoke(station, new object[] { max });
            return true;
        }

        /// <summary>Parses stored blueprint lines into the clipboard.</summary>
        private static bool LoadTemplate(string[] lines, string name)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            Clipboard.Clear();

            foreach (var line in lines)
            {
                var parts = line.Split(';');
                if (parts.Length != 8) continue;

                if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float, culture, out var px) ||
                    !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, culture, out var py) ||
                    !float.TryParse(parts[3], System.Globalization.NumberStyles.Float, culture, out var pz) ||
                    !float.TryParse(parts[4], System.Globalization.NumberStyles.Float, culture, out var rx) ||
                    !float.TryParse(parts[5], System.Globalization.NumberStyles.Float, culture, out var ry) ||
                    !float.TryParse(parts[6], System.Globalization.NumberStyles.Float, culture, out var rz) ||
                    !float.TryParse(parts[7], System.Globalization.NumberStyles.Float, culture, out var rw))
                    continue;

                Clipboard.Add(new CopiedPiece
                {
                    Prefab = parts[0],
                    LocalPos = new Vector3(px, py, pz),
                    LocalRot = new Quaternion(rx, ry, rz, rw),
                });
            }

            if (Clipboard.Count == 0)
            {
                Log.LogWarning($"[AstvardServerMod] Template '{name}' is empty.");
                return false;
            }

            Clipboard.Sort((a, b) => a.LocalPos.y.CompareTo(b.LocalPos.y));
            Log.LogInfo($"[AstvardServerMod] Template loaded: {name} ({Clipboard.Count} pieces).");
            return true;
        }

        /// <summary>
        /// Builds a 4x4 floor platform on a 3x3 grid of posts straight into the
        /// clipboard. Generated rather than stored as data so the grid comes out
        /// perfectly aligned, unlike a copy taken from a hand-built structure.
        /// </summary>
        private static void LoadPlatformTemplate()
        {
            const float tile = 2f;   // one wood_floor is 2x2 m
            const float floorY = 1f; // posts are 1 m tall

            Clipboard.Clear();

            // Posts: 3x3 grid at -2 / 0 / +2.
            for (var x = -1; x <= 1; x++)
            {
                for (var z = -1; z <= 1; z++)
                {
                    Clipboard.Add(new CopiedPiece
                    {
                        Prefab = "wood_pole2",
                        LocalPos = new Vector3(x * tile, 0f, z * tile),
                        LocalRot = Quaternion.identity,
                    });
                }
            }

            // Floor: 4x4 tiles centred on the same origin, so at -3 / -1 / 1 / 3.
            for (var x = 0; x < 4; x++)
            {
                for (var z = 0; z < 4; z++)
                {
                    Clipboard.Add(new CopiedPiece
                    {
                        Prefab = "wood_floor",
                        LocalPos = new Vector3((x - 1.5f) * tile, floorY, (z - 1.5f) * tile),
                        LocalRot = Quaternion.identity,
                    });
                }
            }

            Clipboard.Sort((a, b) => a.LocalPos.y.CompareTo(b.LocalPos.y));
            Log.LogInfo($"[AstvardServerMod] Template loaded: platform 4x4 ({Clipboard.Count} pieces).");
        }

        /// <summary>Fills the clipboard from everything around the player.</summary>
        private static bool FillClipboard(Player player, float radius)
        {
            var origin = player.transform.position;
            var inverse = Quaternion.Inverse(player.transform.rotation);

            var pieces = new List<Piece>();
            Piece.GetAllPiecesInRadius(origin, radius, pieces);

            Clipboard.Clear();
            foreach (var piece in pieces)
            {
                if (piece == null) continue;

                // A preview's pieces register themselves in the same global list
                // as real ones, so without this a copy taken while a ghost is up
                // would swallow the ghost along with the building.
                if (GhostRoot != null && piece.transform.IsChildOf(GhostRoot.transform)) continue;

                var prefabName = Utils.GetPrefabName(piece.gameObject);
                if (string.IsNullOrEmpty(prefabName)) continue;

                Clipboard.Add(new CopiedPiece
                {
                    Prefab = prefabName,
                    LocalPos = inverse * (piece.transform.position - origin),
                    LocalRot = inverse * piece.transform.rotation,
                });
            }

            if (Clipboard.Count == 0)
            {
                Log.LogWarning("[AstvardServerMod] Nothing to copy in radius.");
                return false;
            }

            // Re-centre horizontally on the building itself. Coordinates start out
            // relative to wherever the player happened to stand, which would make
            // the ghost pivot around that offset spot instead of its own middle.
            var min = Clipboard[0].LocalPos;
            var max = min;
            foreach (var p in Clipboard)
            {
                min = Vector3.Min(min, p.LocalPos);
                max = Vector3.Max(max, p.LocalPos);
            }

            var centre = new Vector3((min.x + max.x) * 0.5f, 0f, (min.z + max.z) * 0.5f);
            for (var i = 0; i < Clipboard.Count; i++)
            {
                var entry = Clipboard[i];
                entry.LocalPos -= centre;
                Clipboard[i] = entry;
            }

            // Sorting once here means both the file and the build order run
            // bottom-up, so nothing is ever placed before what holds it up.
            Clipboard.Sort((a, b) => a.LocalPos.y.CompareTo(b.LocalPos.y));
            return true;
        }

        /// <summary>Copies and immediately enters placement mode with a live ghost.</summary>
        private static void RunCopy()
        {
            var player = Player.m_localPlayer;
            if (player == null || _building) return;

            var radius = Mathf.Clamp(ParseField(CopyRadiusInput, 10f), 1f, 64f);
            if (!FillClipboard(player, radius)) return;

            Log.LogInfo($"[AstvardServerMod] Copied {Clipboard.Count} pieces (r={radius}), placing.");

            StartPlacement();
            InventoryGui.instance?.Hide();
        }

        /// <summary>Copies to the clipboard and writes a blueprint file, no placement.</summary>
        private static void RunCopyToFile()
        {
            var player = Player.m_localPlayer;
            if (player == null || _building) return;

            var radius = Mathf.Clamp(ParseField(CopyRadiusInput, 10f), 1f, 64f);
            if (!FillClipboard(player, radius)) return;

            var path = SaveBlueprint(radius);
            Log.LogInfo($"[AstvardServerMod] Saved {Clipboard.Count} pieces (r={radius}) -> {path}");

            MenuState = StateBuild;
            RefreshMenu();
        }

        private static void StartPlacement()
        {
            SpawnGhosts();
            _placeYaw = 0f;
            _placeHeight = 0f;
            IsPlacing = true;
        }

        private static void CancelPlacement()
        {
            IsPlacing = false;
            ClearGhosts();
            Log.LogInfo("[AstvardServerMod] Placement cancelled.");
        }

        /// <summary>
        /// Drives placement mode: the ghost follows the player until left click
        /// commits it, or escape drops it.
        /// </summary>
        private void Update()
        {
            UpdateRoadPreview();

            if (!IsPlacing) return;

            var player = Player.m_localPlayer;
            if (player == null) { CancelPlacement(); return; }

            // Keep the ghost parked while a menu is open, so clicking UI buttons
            // doesn't drop a building behind them.
            if (InventoryGui.IsVisible() || Chat.instance?.HasFocus() == true) return;

            var shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            if (Input.GetKeyDown(KeyCode.Q))
            {
                if (shift) _placeHeight -= HeightStep;
                else _placeYaw -= RotationStep;
            }

            if (Input.GetKeyDown(KeyCode.E))
            {
                if (shift) _placeHeight += HeightStep;
                else _placeYaw += RotationStep;
            }

            UpdateGhostTransform(player);

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                CancelPlacement();
                return;
            }

            if (Input.GetMouseButtonDown(0) && !_building)
            {
                IsPlacing = false;
                StartCoroutine(BuildFromGhost(player));
            }
        }

        /// <summary>Parks the ghost a few metres ahead of the player, on the ground.</summary>
        private static void UpdateGhostTransform(Player player)
        {
            if (GhostRoot == null) return;

            var forward = player.transform.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 0.001f ? forward.normalized : Vector3.forward;

            var position = player.transform.position + forward * PlacementDistance;
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(position, out var ground))
                position.y = ground;
            position.y += _placeHeight;

            // Snap the heading to the same 22.5° steps the game builds on. Using the
            // raw look direction would drop the structure at an arbitrary angle, and
            // anything added by hand afterwards then refuses to line up with it.
            var yaw = Quaternion.LookRotation(forward).eulerAngles.y + _placeYaw;
            yaw = Mathf.Round(yaw / RotationStep) * RotationStep;

            GhostRoot.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));

            if (!IsSnapEnabled) return;

            // What is built nearby barely changes between frames, so it is
            // gathered on a timer while the pair search runs every frame.
            if (Time.time - _snapCacheTime > SnapCacheInterval)
            {
                _snapCacheTime = Time.time;
                RefreshSnapGrid(position);
            }

            if (TryFindSnapOffset(out var snapOffset))
                GhostRoot.transform.position += snapOffset;
        }

        /// <summary>
        /// Collects the snap points of everything built around the preview into a
        /// grid, so the per-frame search only looks at the handful of points that
        /// could possibly be in range instead of all of them.
        /// </summary>
        private static void RefreshSnapGrid(Vector3 centre)
        {
            SnapGrid.Clear();
            SnapTransforms.Clear();
            SnapPieces.Clear();

            // This goes through an overlap test, and the preview's colliders are
            // disabled, so the preview can never find and latch onto itself.
            Piece.GetSnapPoints(centre, _ghostRadius + SnapDistance, SnapTransforms, SnapPieces);

            foreach (var point in SnapTransforms)
            {
                if (point == null) continue;
                var world = point.position;
                var key = CellKey(world, SnapDistance);
                if (!SnapGrid.TryGetValue(key, out var bucket))
                {
                    bucket = new List<Vector3>();
                    SnapGrid[key] = bucket;
                }
                bucket.Add(world);
            }
        }

        /// <summary>
        /// Finds the shortest jump that brings one of the preview's snap points
        /// onto a built one. Returns false when nothing is within reach, which
        /// leaves placement free-hand.
        /// </summary>
        private static bool TryFindSnapOffset(out Vector3 offset)
        {
            offset = Vector3.zero;
            if (SnapGrid.Count == 0 || GhostSnapLocal.Count == 0) return false;

            var root = GhostRoot.transform;
            var bestSqr = SnapDistance * SnapDistance;
            var found = false;

            foreach (var local in GhostSnapLocal)
            {
                var point = root.TransformPoint(local);
                var cx = Mathf.FloorToInt(point.x / SnapDistance);
                var cy = Mathf.FloorToInt(point.y / SnapDistance);
                var cz = Mathf.FloorToInt(point.z / SnapDistance);

                // A cell is one snap distance across, so a match can only sit in
                // this cell or one of its 26 neighbours.
                for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (!SnapGrid.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out var bucket)) continue;

                    foreach (var world in bucket)
                    {
                        var delta = world - point;
                        var sqr = delta.sqrMagnitude;
                        if (sqr >= bestSqr) continue;

                        bestSqr = sqr;
                        offset = delta;
                        found = true;
                    }
                }
            }

            return found;
        }

        /// <summary>Reads the preview's own snap points, in root-local space.</summary>
        private static void CollectGhostSnapPoints()
        {
            GhostSnapLocal.Clear();
            SnapGrid.Clear();
            _snapCacheTime = float.NegativeInfinity;
            _ghostRadius = 1f;
            if (GhostRoot == null) return;

            var root = GhostRoot.transform;
            var points = new List<Transform>();

            foreach (var ghost in Ghosts)
            {
                if (ghost == null) continue;

                // Piece.GetSnapPoints only walks tagged child transforms, so it
                // still works on a preview whose components are all switched off.
                var piece = ghost.GetComponent<Piece>();
                if (piece != null)
                {
                    points.Clear();
                    piece.GetSnapPoints(points);
                    foreach (var point in points)
                        if (point != null) GhostSnapLocal.Add(root.InverseTransformPoint(point.position));
                }

                _ghostRadius = Mathf.Max(_ghostRadius, ghost.transform.localPosition.magnitude);
            }

            DedupePoints(GhostSnapLocal);
            Log.LogInfo($"[AstvardServerMod] Preview snap points: {GhostSnapLocal.Count}, radius {_ghostRadius:F1}");
        }

        /// <summary>
        /// Folds points that land in the same tiny cell into one. Neighbouring
        /// pieces share their corners, so without this a big blueprint carries
        /// several times more snap points than it has distinct positions.
        /// </summary>
        private static void DedupePoints(List<Vector3> points)
        {
            var seen = new HashSet<long>();
            var write = 0;

            for (var read = 0; read < points.Count; read++)
            {
                var point = points[read];
                if (!seen.Add(CellKey(point, SnapDedupeCell))) continue;
                points[write++] = point;
            }

            points.RemoveRange(write, points.Count - write);
        }

        private static long CellKey(Vector3 point, float cell)
        {
            return Key(Mathf.FloorToInt(point.x / cell),
                       Mathf.FloorToInt(point.y / cell),
                       Mathf.FloorToInt(point.z / cell));
        }

        /// <summary>Packs a cell coordinate into one key; 21 bits an axis is far
        /// more than Valheim's world ever needs.</summary>
        private static long Key(int x, int y, int z)
        {
            return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
        }

        private static string SaveBlueprint(float radius)
        {
            try
            {
                var dir = System.IO.Path.Combine(Paths.ConfigPath, "astvard-blueprints");
                System.IO.Directory.CreateDirectory(dir);
                var path = System.IO.Path.Combine(dir,
                    $"blueprint_{System.DateTime.Now:yyyyMMdd_HHmmss}.txt");

                var lines = new List<string>
                {
                    "# astvard blueprint",
                    $"# pieces={Clipboard.Count} radius={radius}",
                    "# prefab;posX;posY;posZ;rotX;rotY;rotZ;rotW",
                };

                var culture = System.Globalization.CultureInfo.InvariantCulture;
                foreach (var p in Clipboard)
                {
                    lines.Add(string.Format(culture, "{0};{1};{2};{3};{4};{5};{6};{7}",
                        p.Prefab,
                        p.LocalPos.x, p.LocalPos.y, p.LocalPos.z,
                        p.LocalRot.x, p.LocalRot.y, p.LocalRot.z, p.LocalRot.w));
                }

                System.IO.File.WriteAllLines(path, lines);
                return path;
            }
            catch (System.Exception ex)
            {
                Log.LogError($"[AstvardServerMod] Could not write blueprint: {ex.Message}");
                return "(not saved)";
            }
        }

        /// <summary>
        /// Shows the whole thing as a ghost, then materialises it a few pieces at a
        /// time from the ground up, clearing each ghost as its real piece lands.
        /// </summary>
        private static IEnumerator BuildFromGhost(Player player)
        {
            _building = true;

            // The ghost is already sitting exactly where the build should land.
            var origin = GhostRoot != null ? GhostRoot.transform.position : player.transform.position;
            var rotation = GhostRoot != null ? GhostRoot.transform.rotation : player.transform.rotation;
            var creator = player.GetPlayerID();

            for (var i = 0; i < Clipboard.Count; i++)
            {
                var entry = Clipboard[i];
                var prefab = ZNetScene.instance != null
                    ? ZNetScene.instance.GetPrefab(entry.Prefab)
                    : null;

                if (prefab != null)
                {
                    var go = Instantiate(prefab,
                        origin + rotation * entry.LocalPos,
                        rotation * entry.LocalRot);

                    var piece = go.GetComponent<Piece>();
                    if (piece != null) piece.SetCreator(creator);
                }
                else
                {
                    Log.LogWarning($"[AstvardServerMod] Unknown prefab '{entry.Prefab}', skipped.");
                }

                if (i < Ghosts.Count && Ghosts[i] != null) Destroy(Ghosts[i]);

                // Pause after every batch, and after the last partial one too, so
                // small blueprints don't finish within a single frame.
                if ((i + 1) % PiecesPerBatch == 0 || i == Clipboard.Count - 1)
                    yield return new WaitForSeconds(BatchDelay);
            }

            ClearGhosts();
            _building = false;
            Log.LogInfo($"[AstvardServerMod] Built {Clipboard.Count} pieces.");
        }

        /// <summary>
        /// Builds the ghost under one parent object, so moving the whole preview is
        /// a single transform update instead of touching every piece each frame.
        /// </summary>
        private static void SpawnGhosts()
        {
            ClearGhosts();
            if (ZNetScene.instance == null) return;

            GhostRoot = new GameObject("AstvardGhostRoot");

            // Without this the ghosts would register themselves as real networked
            // objects the moment they are instantiated.
            ZNetView.m_forceDisableInit = true;
            try
            {
                foreach (var entry in Clipboard)
                {
                    var prefab = ZNetScene.instance.GetPrefab(entry.Prefab);
                    if (prefab == null) { Ghosts.Add(null); continue; }

                    var ghost = Instantiate(prefab, GhostRoot.transform);
                    ghost.transform.localPosition = entry.LocalPos;
                    ghost.transform.localRotation = entry.LocalRot;

                    foreach (var collider in ghost.GetComponentsInChildren<Collider>())
                        collider.enabled = false;
                    foreach (var behaviour in ghost.GetComponentsInChildren<MonoBehaviour>())
                        behaviour.enabled = false;

                    Ghosts.Add(ghost);
                }
            }
            finally
            {
                ZNetView.m_forceDisableInit = false;
            }

            CollectGhostSnapPoints();

            Log.LogInfo($"[AstvardServerMod] Ghost preview: {Ghosts.Count(g => g != null)}/{Clipboard.Count} pieces");
        }

        private static void ClearGhosts()
        {
            foreach (var ghost in Ghosts)
                if (ghost != null) Destroy(ghost);
            Ghosts.Clear();

            GhostSnapLocal.Clear();
            SnapGrid.Clear();

            if (GhostRoot != null) Destroy(GhostRoot);
            GhostRoot = null;
        }

        /// <summary>Applies the level operation at the player's position.</summary>
        private static readonly System.Reflection.MethodInfo MPaintCleared =
            AccessTools.Method(typeof(TerrainComp), "PaintCleared");

        // Long enough for a real stretch of road, short enough that one press does not
        // rewrite the terrain of a dozen zones at once.
        private const float MaxRoadLength = 200f;

        private static bool _roadPaved = true;
        private static bool _roadStarted;
        private static Vector3 _roadStart;

        private static void UpdateRoadHint()
        {
            var label = RoadHint != null ? RoadHint.GetComponentInChildren<Text>() : null;
            if (label == null) return;

            var kind = _roadPaved ? "каменная" : "земляная";
            label.text = _roadStarted
                ? $"Кладка: {kind}.{NEWLINE}Начало отмечено — иди в конец{NEWLINE}и нажми «Закончить»."
                : $"Кладка: {kind}.{NEWLINE}Встань в начало дорожки{NEWLINE}и нажми «Начать».";
        }

        private static readonly List<Vector3> RoadPath = new List<Vector3>();
        private static readonly List<Vector3> PreviewStamps = new List<Vector3>();

        private static GameObject _roadPreview;
        private static LineRenderer _roadLine;
        private static bool _roadPreviewFailed;

        private static readonly System.Reflection.FieldInfo FModifiedPaint =
            AccessTools.Field(typeof(TerrainComp), "m_modifiedPaint");
        private static readonly System.Reflection.FieldInfo FPaintMask =
            AccessTools.Field(typeof(TerrainComp), "m_paintMask");

        private static float RoadWidth()
        {
            return Mathf.Clamp(ParseField(RoadWidthInput, 3f), 1f, 8f);
        }

        /// <summary>
        /// How far the road bows out at its middle, in metres. Scaling it by the length
        /// means the typed number describes the shape rather than an absolute distance:
        /// 1 is a gentle bend and 10 puts the bulge at half the chord, a semicircle.
        /// </summary>
        private static float RoadSagitta(float length)
        {
            var curve = Mathf.Clamp(ParseField(RoadCurveInput, 0f), -10f, 10f);
            return curve * length * 0.05f;
        }

        /// <summary>
        /// Samples the centreline. A quadratic Bezier only reaches half of its control
        /// offset, so the control point is pushed out twice the bulge we want.
        /// </summary>
        private static void RoadPoints(Vector3 from, Vector3 to, float sagitta,
                                       float step, List<Vector3> into)
        {
            into.Clear();

            var chord = new Vector3(to.x - from.x, 0f, to.z - from.z);
            var length = chord.magnitude;
            if (length < 0.01f) return;

            var side = new Vector3(-chord.z, 0f, chord.x).normalized;
            var control = Vector3.Lerp(from, to, 0.5f) + side * (sagitta * 2f);

            var span = length + Mathf.Abs(sagitta) * 2f;
            var count = Mathf.Max(1, Mathf.CeilToInt(span / Mathf.Max(step, 0.1f)));

            for (var i = 0; i <= count; i++)
            {
                var t = (float)i / count;
                var inv = 1f - t;
                into.Add(inv * inv * from + 2f * inv * t * control + t * t * to);
            }
        }

        // ---------------- painting ----------------

        /// <summary>
        /// One zone's paint mask, with the scratch a single operation needs. The base
        /// colour is remembered per vertex so that a later, better-covering pass blends
        /// from the ground's original colour instead of compounding its own earlier work.
        /// </summary>
        private sealed class PaintTarget
        {
            public TerrainComp Comp;
            public Heightmap Hmap;
            public bool[] Modified;
            public Color[] Mask;
            public float[] Best;
            public Color[] Base;
            public bool[] Touched;
            public int Size;
            public int Half;
            public float Scale;
            public Vector3 Origin;
        }

        private static PaintTarget MakeTarget(TerrainComp comp)
        {
            var hmap = FHmap != null ? FHmap.GetValue(comp) as Heightmap : null;
            var modified = FModifiedPaint != null ? FModifiedPaint.GetValue(comp) as bool[] : null;
            var mask = FPaintMask != null ? FPaintMask.GetValue(comp) as Color[] : null;
            if (hmap == null || modified == null || mask == null || hmap.m_scale <= 0f) return null;

            var size = hmap.m_width + 1;
            if (modified.Length < size * size || mask.Length < size * size) return null;

            return new PaintTarget
            {
                Comp = comp,
                Hmap = hmap,
                Modified = modified,
                Mask = mask,
                Best = new float[size * size],
                Base = new Color[size * size],
                Touched = new bool[size * size],
                Size = size,
                Half = size / 2,
                Scale = hmap.m_scale,
                Origin = hmap.transform.position
            };
        }

        // Mirrors Heightmap.WorldToVertexMask, and its inverse. Keeping both here means
        // the two can be read against each other instead of trusted separately.
        private static int VertexAt(PaintTarget t, float world, float origin)
        {
            return Mathf.FloorToInt((world - origin) / t.Scale + 0.5f) + t.Half;
        }

        private static float WorldAt(PaintTarget t, int vertex, float origin)
        {
            return origin + (vertex - t.Half) * t.Scale;
        }

        private static float DistanceToPath(List<Vector3> path, int first, int last, float x, float z)
        {
            if (last <= first)
            {
                var only = path[first];
                return Mathf.Sqrt((x - only.x) * (x - only.x) + (z - only.z) * (z - only.z));
            }

            var best = float.MaxValue;
            for (var k = first; k < last; k++)
            {
                var a = path[k];
                var b = path[k + 1];

                var abx = b.x - a.x;
                var abz = b.z - a.z;
                var lenSq = abx * abx + abz * abz;

                var t = lenSq > 1e-6f
                    ? Mathf.Clamp01(((x - a.x) * abx + (z - a.z) * abz) / lenSq)
                    : 0f;

                var dx = x - (a.x + abx * t);
                var dz = z - (a.z + abz * t);
                var d = dx * dx + dz * dz;
                if (d < best) best = d;
            }
            return Mathf.Sqrt(best);
        }

        /// <summary>
        /// Paints every vertex within <paramref name="radius"/> of a stretch of the path,
        /// once, using its true distance. The game's own PaintCleared cannot be used for
        /// this: it stamps circles that overwrite each other, and since it reads its base
        /// colour from the rendered heightmap — which only refreshes between batches —
        /// the last stamp to graze a vertex wins with its weakest edge value. That is
        /// what turned a road into a row of blotches.
        /// </summary>
        private static void PaintStretch(PaintTarget t, List<Vector3> path, int first, int last,
                                         float radius, Color paint)
        {
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (var k = first; k <= last; k++)
            {
                var point = path[k];
                if (point.x < minX) minX = point.x;
                if (point.x > maxX) maxX = point.x;
                if (point.z < minZ) minZ = point.z;
                if (point.z > maxZ) maxZ = point.z;
            }

            var j0 = Mathf.Max(0, VertexAt(t, minX - radius, t.Origin.x));
            var j1 = Mathf.Min(t.Size - 1, VertexAt(t, maxX + radius, t.Origin.x));
            var i0 = Mathf.Max(0, VertexAt(t, minZ - radius, t.Origin.z));
            var i1 = Mathf.Min(t.Size - 1, VertexAt(t, maxZ + radius, t.Origin.z));

            for (var i = i0; i <= i1; i++)
            {
                var wz = WorldAt(t, i, t.Origin.z);
                for (var j = j0; j <= j1; j++)
                {
                    var wx = WorldAt(t, j, t.Origin.x);

                    var distance = DistanceToPath(path, first, last, wx, wz);
                    if (distance > radius) continue;

                    // Same shape as the game's brush: solid across most of the width
                    // with the fade squeezed into the last sliver.
                    var f = Mathf.Pow(1f - Mathf.Clamp01(distance / radius), 0.1f);

                    var index = i * t.Size + j;
                    if (f <= t.Best[index]) continue;
                    t.Best[index] = f;

                    if (!t.Touched[index])
                    {
                        t.Touched[index] = true;
                        t.Base[index] = t.Modified[index] ? t.Mask[index] : t.Hmap.GetPaintMask(j, i);
                    }

                    var baseColor = t.Base[index];
                    var blended = Color.Lerp(baseColor, paint, f);
                    // Alpha carries the terrain's own data, not our colour.
                    blended.a = baseColor.a;

                    t.Modified[index] = true;
                    t.Mask[index] = blended;
                }
            }
        }

        private static Color PaintColor()
        {
            return _roadPaved ? Heightmap.m_paintMaskPaved : Heightmap.m_paintMaskDirt;
        }

        // ---------------- preview ----------------

        private static void UpdateRoadPreview()
        {
            var player = Player.m_localPlayer;
            if (!_roadStarted || player == null || _roadPreviewFailed)
            {
                if (_roadPreview != null) _roadPreview.SetActive(false);
                return;
            }

            if (_roadLine == null && !CreateRoadPreview()) return;

            var to = player.transform.position;
            var length = new Vector3(to.x - _roadStart.x, 0f, to.z - _roadStart.z).magnitude;
            var width = RoadWidth();

            RoadPoints(_roadStart, to, RoadSagitta(length), Mathf.Max(width * 0.5f, 1f), PreviewStamps);
            if (PreviewStamps.Count < 2)
            {
                _roadPreview.SetActive(false);
                return;
            }

            _roadPreview.SetActive(true);
            _roadLine.widthMultiplier = width;
            _roadLine.positionCount = PreviewStamps.Count;

            var system = ZoneSystem.instance;
            for (var i = 0; i < PreviewStamps.Count; i++)
            {
                var point = PreviewStamps[i];
                if (system != null && system.GetGroundHeight(point, out var ground)) point.y = ground;
                point.y += 0.15f;
                _roadLine.SetPosition(i, point);
            }
        }

        private static bool CreateRoadPreview()
        {
            var shader = Shader.Find("Sprites/Default")
                         ?? Shader.Find("Particles/Standard Unlit")
                         ?? Shader.Find("Unlit/Color");
            if (shader == null)
            {
                // Better a road tool with no preview than one that throws every frame.
                _roadPreviewFailed = true;
                Log.LogWarning("[AstvardServerMod] No shader for the road preview.");
                return false;
            }

            _roadPreview = new GameObject("AstvardRoadPreview");
            _roadPreview.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            _roadLine = _roadPreview.AddComponent<LineRenderer>();
            _roadLine.material = new Material(shader);
            _roadLine.startColor = new Color(1f, 0.8f, 0.27f, 0.55f);
            _roadLine.endColor = _roadLine.startColor;
            _roadLine.useWorldSpace = true;
            _roadLine.numCapVertices = 2;
            _roadLine.alignment = LineAlignment.TransformZ;
            _roadLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _roadLine.receiveShadows = false;
            return true;
        }

        /// <summary>
        /// Metres between neighbouring vertices of the paint mask — the finest detail
        /// the terrain can hold.
        /// </summary>
        private static float PaintGridScale(Vector3 near)
        {
            var comp = TerrainComp.FindTerrainCompiler(near);
            var hmap = comp != null && FHmap != null ? FHmap.GetValue(comp) as Heightmap : null;
            if (hmap == null) hmap = Heightmap.FindHeightmap(near);
            return hmap != null && hmap.m_scale > 0f ? hmap.m_scale : 1f;
        }

        /// <summary>
        /// Every zone the work can reach, resolved once. Asking find-or-create per point
        /// would query the same zone dozens of times, and a creation landing on a zone
        /// that already has a compiler makes the new one destroy the old — taking the
        /// paint already laid into it along with it.
        /// </summary>
        private static List<TerrainComp> CompsForStamps(List<Vector3> points, float radius, float y)
        {
            var zones = new HashSet<Vector2i>();
            foreach (var point in points)
            {
                zones.Add(ZoneSystem.GetZone(point + new Vector3(-radius, 0f, -radius)));
                zones.Add(ZoneSystem.GetZone(point + new Vector3(radius, 0f, -radius)));
                zones.Add(ZoneSystem.GetZone(point + new Vector3(-radius, 0f, radius)));
                zones.Add(ZoneSystem.GetZone(point + new Vector3(radius, 0f, radius)));
            }

            var comps = new List<TerrainComp>();
            foreach (var zone in zones)
            {
                var zoneCenter = ZoneSystem.GetZonePos(zone);
                var at = new Vector3(zoneCenter.x, y, zoneCenter.z);

                var comp = TerrainComp.FindTerrainCompiler(at) ?? CreateTerrainCompiler(at);
                if (comp == null) continue;

                var nview = comp.GetComponent<ZNetView>();
                if (nview != null && !nview.IsOwner()) nview.ClaimOwnership();
                comps.Add(comp);
            }

            return comps;
        }

        // ---------------- the two tools ----------------

        private static void BuildRoad()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            if (!_roadStarted)
            {
                player.Message(MessageHud.MessageType.Center, "Сначала нажми «Начать»");
                return;
            }

            var from = _roadStart;
            var to = player.transform.position;
            var length = new Vector3(to.x - from.x, 0f, to.z - from.z).magnitude;

            if (length < 1f)
            {
                player.Message(MessageHud.MessageType.Center, "Точки слишком близко");
                return;
            }

            if (length > MaxRoadLength)
            {
                player.Message(MessageHud.MessageType.Center,
                    $"Далеко: {length:F0} м, максимум {MaxRoadLength:F0}");
                return;
            }

            var width = RoadWidth();
            var scale = PaintGridScale(from);

            // The mask has a vertex every scale metres, so a band narrower than about
            // three quarters of a cell can miss whole rows and come out dotted however
            // carefully it is drawn. The asked-for width still widens it beyond that.
            var radius = Mathf.Max(width * 0.5f, scale * 0.75f);

            // A metre between samples is plenty: the distance is measured to the line
            // itself, so sampling only has to follow the curve, not cover it.
            RoadPoints(from, to, RoadSagitta(length), 1f, RoadPath);
            if (RoadPath.Count == 0) return;

            var comps = CompsForStamps(RoadPath, radius, from.y);
            if (comps.Count == 0)
            {
                Log.LogWarning("[AstvardServerMod] No TerrainComp for the road.");
                return;
            }

            _roadStarted = false;
            UpdateRoadHint();

            Instance?.StartCoroutine(LayPaint(new List<Vector3>(RoadPath), comps, radius,
                PaintColor(), length, width, scale, "road"));
        }

        /// <summary>
        /// A filled circle around the player — a square or a yard rather than a path.
        /// It is the same painter with a one-point path, so the distance test alone
        /// fills the disc; no ring of stamps is needed.
        /// </summary>
        private static void BuildArea()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            var area = Mathf.Clamp(ParseField(RoadAreaInput, 8f), 2f, 32f);
            var centre = player.transform.position;
            var scale = PaintGridScale(centre);

            RoadPath.Clear();
            RoadPath.Add(centre);

            var comps = CompsForStamps(RoadPath, area, centre.y);
            if (comps.Count == 0)
            {
                Log.LogWarning("[AstvardServerMod] No TerrainComp for the area.");
                return;
            }

            Instance?.StartCoroutine(LayPaint(new List<Vector3>(RoadPath), comps, area,
                PaintColor(), area, area * 2f, scale, "area"));
        }

        /// <summary>
        /// Lays the paint a stretch at a time so it grows from the marked point onwards.
        /// Each stretch is saved and poked, which is also what makes it visible — the
        /// mask only reaches the rendered ground when its heightmap regenerates.
        /// </summary>
        private static IEnumerator LayPaint(List<Vector3> path, List<TerrainComp> comps,
                                            float radius, Color paint, float length,
                                            float width, float scale, string kind)
        {
            var targets = new List<PaintTarget>();
            foreach (var comp in comps)
            {
                var target = MakeTarget(comp);
                if (target != null) targets.Add(target);
            }

            if (targets.Count == 0)
            {
                Log.LogWarning("[AstvardServerMod] Could not reach the paint mask.");
                yield break;
            }

            var save = AccessTools.Method(typeof(TerrainComp), "Save");
            var owned = 0;
            foreach (var comp in comps)
            {
                var view = comp.GetComponent<ZNetView>();
                if (view != null && view.IsOwner()) owned++;
            }

            // Twenty stretches at most: each one costs a full serialise of the zone and
            // a heightmap rebuild, so it is the number of them that matters.
            var segments = Mathf.Max(1, path.Count - 1);
            var perStretch = Mathf.Max(1, Mathf.CeilToInt(segments / 20f));

            for (var first = 0; first < Mathf.Max(1, segments); first += perStretch)
            {
                var last = Mathf.Min(first + perStretch, path.Count - 1);

                foreach (var target in targets)
                    PaintStretch(target, path, first, last, radius, paint);

                foreach (var comp in comps) save.Invoke(comp, null);
                RebuildHeightmaps(path[last], radius + 16f);
                yield return null;
            }

            // One last sweep over the whole run: a stretch near a zone border can leave
            // the neighbour's heightmap holding a version from before the last write.
            RebuildHeightmaps(path[path.Count / 2], length * 0.5f + radius + 16f);

            var painted = 0;
            foreach (var target in targets)
                foreach (var touched in target.Touched)
                    if (touched) painted++;

            if (_roadPreview != null) _roadPreview.SetActive(false);

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                kind == "area"
                    ? $"Площадка радиусом {length:F0} м"
                    : $"Дорожка {length:F0} м, ширина {width:F1} м");

            Log.LogInfo($"[AstvardServerMod] {kind} {(_roadPaved ? "paved" : "dirt")} " +
                        $"{length:F1} m width {width:F1} brush={radius:F2} grid={scale:F2} " +
                        $"nodes={path.Count} zones={comps.Count} owned={owned} verts={painted}");
        }

        private static void ApplyTerrainLevel()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            var radius = ParseField(RadiusInput, 8f);
            var heightOffset = ParseField(HeightInput, 0f);
            // The heightmap only covers one 64 m zone, so anything past its edge is
            // silently clipped — the cap is generous rather than exact.
            radius = Mathf.Clamp(radius, 1f, 64f);

            var playerPos = player.transform.position;
            var target = new Vector3(playerPos.x, playerPos.y + heightOffset, playerPos.z);

            // The blend band is derived from the radius — a bigger platform gets a
            // longer run-out, so the user only has to pick radius and height.
            var blend = Mathf.Clamp(radius * 0.75f, 4f, 24f);
            var reach = radius + blend;

            // Each zone keeps its own heightmap, so an operation spilling over a
            // zone border has to be handed to every TerrainComp it touches —
            // otherwise the neighbour keeps its old heights and the seam tears open.
            var comps = CollectTerrainComps(target, reach);
            if (comps.Count == 0)
            {
                Log.LogWarning("[AstvardServerMod] No TerrainComp available for the area.");
                return;
            }

            var save = AccessTools.Method(typeof(TerrainComp), "Save");
            foreach (var c in comps) BlendLevel(c, target, radius, blend);
            foreach (var c in comps) save.Invoke(c, null);

            RebuildHeightmaps(target, reach);

            Log.LogInfo($"[AstvardServerMod] Level {(_terrainSquare ? "square" : "circle")} " +
                        $"r={radius} h={heightOffset:F1} blend={blend:F1} " +
                        $"zones={comps.Count} at {target}");
        }

        private static readonly System.Reflection.FieldInfo FHmap = AccessTools.Field(typeof(TerrainComp), "m_hmap");
        private static readonly System.Reflection.FieldInfo FLevelDelta = AccessTools.Field(typeof(TerrainComp), "m_levelDelta");
        private static readonly System.Reflection.FieldInfo FSmoothDelta = AccessTools.Field(typeof(TerrainComp), "m_smoothDelta");
        private static readonly System.Reflection.FieldInfo FModified = AccessTools.Field(typeof(TerrainComp), "m_modifiedHeight");
        private static readonly System.Reflection.FieldInfo FWidth = AccessTools.Field(typeof(TerrainComp), "m_width");
        private static readonly System.Reflection.FieldInfo FOperations = AccessTools.Field(typeof(TerrainComp), "m_operations");
        private static readonly System.Reflection.FieldInfo FLastOpPoint = AccessTools.Field(typeof(TerrainComp), "m_lastOpPoint");
        private static readonly System.Reflection.FieldInfo FLastOpRadius = AccessTools.Field(typeof(TerrainComp), "m_lastOpRadius");

        /// <summary>
        /// Levels the inner disc and blends outwards per vertex: every point in the
        /// outer band is pulled towards the flat height only as far as its distance
        /// allows, ending at the terrain's own height. The engine's LevelTerrain can
        /// only stamp one flat height over a whole radius, which is why the border
        /// had to be faked with rings before — writing the height deltas ourselves
        /// gives a continuous slope that meets whatever is already there.
        /// </summary>
        private static void BlendLevel(TerrainComp comp, Vector3 worldTarget, float radius, float blend)
        {
            var hmap = FHmap.GetValue(comp) as Heightmap;
            var levelDelta = FLevelDelta.GetValue(comp) as float[];
            var smoothDelta = FSmoothDelta.GetValue(comp) as float[];
            var modified = FModified.GetValue(comp) as bool[];
            if (hmap == null || levelDelta == null || smoothDelta == null || modified == null) return;

            var width = (int)FWidth.GetValue(comp);
            var size = width + 1;
            var scale = hmap.m_scale;
            if (scale <= 0f) return;

            hmap.WorldToVertex(worldTarget, out var cx, out var cy);
            // Heights inside a heightmap are relative to its own transform.
            var localTargetY = worldTarget.y - comp.transform.position.y;

            var reach = radius + blend;
            var reachVerts = Mathf.CeilToInt(reach / scale);

            for (var i = cy - reachVerts; i <= cy + reachVerts; i++)
            {
                if (i < 0 || i >= size) continue;
                for (var j = cx - reachVerts; j <= cx + reachVerts; j++)
                {
                    if (j < 0 || j >= size) continue;

                    var dx = j - cx;
                    var dz = i - cy;
                    // Chebyshev distance gives square platforms, Euclidean round ones.
                    var distance = (_terrainSquare
                        ? Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz))
                        : Mathf.Sqrt(dx * dx + dz * dz)) * scale;

                    if (distance > reach) continue;

                    var index = i * size + j;
                    var current = hmap.GetHeight(j, i);

                    float desired;
                    if (distance <= radius)
                    {
                        desired = localTargetY;
                    }
                    else
                    {
                        var t = Mathf.SmoothStep(0f, 1f, (distance - radius) / blend);
                        desired = Mathf.Lerp(localTargetY, current, t);
                    }

                    // Same bookkeeping LevelTerrain does: fold in and clear any
                    // pending smooth delta, then clamp to the engine's ±8 m budget.
                    var delta = desired - current + smoothDelta[index];
                    smoothDelta[index] = 0f;
                    levelDelta[index] = Mathf.Clamp(levelDelta[index] + delta, -8f, 8f);
                    modified[index] = true;
                }
            }

            FOperations.SetValue(comp, (int)FOperations.GetValue(comp) + 1);
            FLastOpPoint.SetValue(comp, worldTarget);
            FLastOpRadius.SetValue(comp, reach);
        }


        /// <summary>Every TerrainComp whose zone is touched by the given reach, created if missing.</summary>
        private static List<TerrainComp> CollectTerrainComps(Vector3 center, float reach)
        {
            var comps = new List<TerrainComp>();
            var seen = new HashSet<Vector2i>();

            // Sample a grid across the affected square; a half-zone step is fine
            // since one sample per 32 m cannot skip over a 64 m zone.
            const float step = 32f;
            for (var dx = -reach; dx <= reach + step; dx += step)
            {
                for (var dz = -reach; dz <= reach + step; dz += step)
                {
                    var probe = center + new Vector3(Mathf.Clamp(dx, -reach, reach), 0f,
                                                     Mathf.Clamp(dz, -reach, reach));
                    var zone = ZoneSystem.GetZone(probe);
                    if (!seen.Add(zone)) continue;

                    var zoneCenter = ZoneSystem.GetZonePos(zone);
                    var probeAtZone = new Vector3(zoneCenter.x, center.y, zoneCenter.z);

                    var comp = TerrainComp.FindTerrainCompiler(probeAtZone)
                               ?? CreateTerrainCompiler(probeAtZone);
                    if (comp == null) continue;

                    var nview = comp.GetComponent<ZNetView>();
                    if (nview != null && !nview.IsOwner()) nview.ClaimOwnership();
                    comps.Add(comp);
                }
            }

            return comps;
        }

        /// <summary>
        /// Rebuilds every heightmap in range. TerrainComp.DoOperation only pokes its
        /// own, which leaves neighbouring zones rendering stale geometry — the visible
        /// gaps at zone seams. The game does the same sweep in TerrainModifier.PokeHeightmaps.
        /// </summary>
        private static void RebuildHeightmaps(Vector3 center, float reach)
        {
            foreach (var hmap in Heightmap.GetAllHeightmaps())
            {
                if (hmap != null && hmap.IsPointInside(center, reach))
                    hmap.Poke(delayed: false);
            }

            if (ClutterSystem.instance != null)
                ClutterSystem.instance.ResetGrass(center, reach);
        }

        private static TerrainComp CreateTerrainCompiler(Vector3 pos)
        {
            if (ZNetScene.instance == null)
            {
                Log.LogError("[AstvardServerMod] ZNetScene not ready.");
                return null;
            }

            var prefab = ZNetScene.instance.GetPrefab("_TerrainCompiler");
            if (prefab == null)
            {
                var candidates = ZNetScene.instance.GetPrefabNames()
                    .Where(n => n.IndexOf("terrain", System.StringComparison.OrdinalIgnoreCase) >= 0
                             || n.IndexOf("compiler", System.StringComparison.OrdinalIgnoreCase) >= 0);
                Log.LogError("[AstvardServerMod] '_TerrainCompiler' prefab not found. Candidates: "
                             + string.Join(", ", candidates));
                return null;
            }

            var zonePos = ZoneSystem.GetZonePos(ZoneSystem.GetZone(pos));
            var go = UnityEngine.Object.Instantiate(prefab, zonePos, Quaternion.identity);
            var comp = go.GetComponent<TerrainComp>();
            if (comp == null)
                Log.LogError("[AstvardServerMod] Spawned _TerrainCompiler has no TerrainComp component.");
            else
                Log.LogInfo($"[AstvardServerMod] Created TerrainComp for zone at {zonePos}");
            return comp;
        }

        private static float ParseField(GameObject inputGo, float fallback)
        {
            var field = inputGo != null ? inputGo.GetComponentInChildren<InputField>() : null;
            if (field == null || string.IsNullOrEmpty(field.text)) return fallback;
            return float.TryParse(field.text.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
        }

        /// <summary>Single place deciding what is visible for the current menu state.</summary>
        internal static void RefreshMenu()
        {
            var admin = IsAdminUnlocked;

            SetActive(InfoButton, MenuState == StateRoot);
            SetActive(InfoText, MenuState == StateRoot && IsInfoShown);
            SetActive(ActivateButton, admin && MenuState == StateRoot);

            // Everything below is open to every player, not just admins.
            SetActive(FeaturesButton, MenuState == StateRoot);
            SetActive(FillCategoryButton, MenuState == StateFeatures);
            SetActive(CollectCategoryButton, MenuState == StateFeatures);

            SetActive(FillHint, MenuState == StateFill);
            SetActive(FillButton, MenuState == StateFill);
            SetActive(FillAssignButton, MenuState == StateFill);
            SetActive(FillUnassignButton, MenuState == StateFill);

            SetActive(AutoCollectHint, MenuState == StateCollect);
            SetActive(AutoCollectButton, MenuState == StateCollect);
            SetActive(AssignChestButton, MenuState == StateCollect);
            SetActive(UnassignChestButton, MenuState == StateCollect);

            SetActive(CheatsButton, admin && MenuState == StateAdmin);
            SetActive(ZoneCategoryButton, admin && MenuState == StateAdmin);
            RebuildZoneViews();

            SetActive(ZoneHint, admin && (MenuState == StateZone || MenuState == StateZoneOwner));
            SetActive(ZoneSizeInput, admin && (MenuState == StateZone || MenuState == StateZoneEdit));
            SetActive(ZoneAddButton, admin && MenuState == StateZone);
            SetActive(ZoneOthersButton, admin && MenuState == StateZone && ZoneOwners.Count > 0);
            SetActive(ZoneClearButton, admin && MenuState == StateZone);

            SetActive(ZoneEditHint, admin && MenuState == StateZoneEdit);
            SetActive(ZoneMoveButton, admin && MenuState == StateZoneEdit);
            SetActive(ZoneRadiusButton, admin && MenuState == StateZoneEdit);
            SetActive(ZoneDeleteButton, admin && MenuState == StateZoneEdit);

            var listing = MenuState == StateZone || MenuState == StateZoneOwner;
            for (var i = 0; i < MaxZoneButtons; i++)
            {
                SetActive(ZoneButtons[i], admin && listing && i < VisibleZones.Count);
                SetActive(OwnerButtons[i], admin && MenuState == StateZoneOthers && i < ZoneOwners.Count);
            }
            SetActive(TerrainButton, admin && MenuState == StateAdmin);
            SetActive(BuildButton, admin && MenuState == StateAdmin);

            SetActive(GodButton, admin && MenuState == StateCheats);
            SetActive(DebugModeButton, admin && MenuState == StateCheats);
            SetActive(TodButton, admin && MenuState == StateCheats);
            SetActive(RepairButton, admin && MenuState == StateCheats);
            SetActive(ForceDeleteButton, admin && MenuState == StateCheats);

            SetActive(TodHint, admin && MenuState == StateTod);
            SetActive(TodInput, admin && MenuState == StateTod);
            SetActive(TodApplyButton, admin && MenuState == StateTod);

            SetActive(RepairHint, admin && MenuState == StateRepair);
            SetActive(RepairRadiusInput, admin && MenuState == StateRepair);
            SetActive(RepairApplyButton, admin && MenuState == StateRepair);
            SetActive(ForceDeleteHint, admin && MenuState == StateForceDelete);
            SetActive(ForceDeleteRadiusInput, admin && MenuState == StateForceDelete);
            SetActive(ForceDeleteApplyButton, admin && MenuState == StateForceDelete);

            SetActive(CopyButton, admin && MenuState == StateBuild);
            SetActive(PasteButton, admin && MenuState == StateBuild);
            SetActive(TemplatesButton, admin && MenuState == StateBuild);
            SetActive(SnapButton, admin && MenuState == StateBuild);
            SetActive(PlatformsCategoryButton, admin && MenuState == StateTemplates);
            SetActive(HousesCategoryButton, admin && MenuState == StateTemplates);
            SetActive(KitchensCategoryButton, admin && MenuState == StateTemplates);
            SetActive(ProcessingCategoryButton, admin && MenuState == StateTemplates);
            SetActive(SmelterHallButton, admin && MenuState == StateProcessing);
            SetActive(CharcoalKilnsButton, admin && MenuState == StateProcessing);
            SetActive(PlatformTemplateButton, admin && MenuState == StatePlatforms);
            SetActive(StarterHousesButton, admin && MenuState == StateHouses);
            SetActive(StarterHouse1Button, admin && MenuState == StateStarterHouses);
            SetActive(StarterKitchensButton, admin && MenuState == StateKitchens);
            SetActive(KitchenFullButton, admin && MenuState == StateStarterKitchens);

            SetActive(CopyHint, admin && MenuState == StateCopyForm);
            SetActive(CopyRadiusInput, admin && MenuState == StateCopyForm);
            SetActive(CopyApplyButton, admin && MenuState == StateCopyForm);

            SetActive(LevelCircleButton, admin && MenuState == StateTerrain);
            SetActive(RoadButton, admin && MenuState == StateTerrain);
            SetActive(RoadHint, admin && MenuState == StateRoad);
            SetActive(RoadWidthInput, admin && MenuState == StateRoad);
            SetActive(RoadCurveInput, admin && MenuState == StateRoad);
            SetActive(RoadAreaInput, admin && MenuState == StateRoad);
            SetActive(RoadAreaButton, admin && MenuState == StateRoad);
            SetActive(RoadStoneButton, admin && MenuState == StateRoad);
            SetActive(RoadDirtButton, admin && MenuState == StateRoad);
            SetActive(RoadStartButton, admin && MenuState == StateRoad);
            SetActive(RoadEndButton, admin && MenuState == StateRoad);
            SetActive(LevelSquareButton, admin && MenuState == StateTerrain);

            SetActive(TerrainHint, admin && MenuState == StateTerrainForm);
            SetActive(RadiusInput, admin && MenuState == StateTerrainForm);
            SetActive(HeightInput, admin && MenuState == StateTerrainForm);
            SetActive(ApplyButton, admin && MenuState == StateTerrainForm);

            SetActive(BackButton, (admin || IsPlayerSection(MenuState)) && MenuState >= StateTerrain);
        }

        // Pages every player can reach, admin or not.
        private static bool IsPlayerSection(int state)
        {
            return state == StateFeatures || state == StateFill || state == StateCollect;
        }

        private static readonly string NEWLINE = "\n";

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
        }

        // Server: the authoritative list. Client: left empty, see ShownZones.
        private static readonly List<KeptZone> Zones = new List<KeptZone>();
        private static readonly List<KeptZone> ShownZones = new List<KeptZone>();
        private static readonly List<Minimap.PinData> ZonePins = new List<Minimap.PinData>();
        private static readonly List<ZDO> KeptZoneObjects = new List<ZDO>();

        private static string PackZones(List<KeptZone> zones)
        {
            var packed = new System.Text.StringBuilder();
            foreach (var zone in zones)
            {
                if (packed.Length > 0) packed.Append(';');
                packed.Append(zone.X.ToString("F1", Invariant)).Append(',')
                      .Append(zone.Z.ToString("F1", Invariant)).Append(',')
                      .Append(zone.Radius.ToString(Invariant)).Append(',')
                      .Append(CleanName(zone.Owner));
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
                    Owner = parts.Length > 3 ? parts[3] : "?"
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

        private static void ZoneCells(KeptZone zone, out Vector2i min, out Vector2i max)
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

            rpc.Register<float, float, int>(RpcZoneAdd, OnZoneAdd);
            rpc.Register<float, float, float, float, int>(RpcZoneMove, OnZoneMove);
            rpc.Register<float, float, bool>(RpcZoneDel, OnZoneDel);
            rpc.Register(RpcZoneQuery, OnZoneQuery);
            rpc.Register<string, int>(RpcZoneList, OnZoneList);
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
            _zones.Value = PackZones(Zones);
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

            ZRoutedRpc.instance?.InvokeRoutedRPC(target, RpcZoneList, PackZones(Zones), loaded);
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
        private static void RebuildZoneViews()
        {
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
                var zoneLabel = ZoneButtons[i] != null ? ZoneButtons[i].GetComponentInChildren<Text>() : null;
                if (zoneLabel != null)
                    zoneLabel.text = i < VisibleZones.Count
                        ? ZoneLabel(ShownZones[VisibleZones[i]])
                        : "";

                var ownerLabel = OwnerButtons[i] != null ? OwnerButtons[i].GetComponentInChildren<Text>() : null;
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
            var label = ZoneEditHint != null ? ZoneEditHint.GetComponentInChildren<Text>() : null;
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

        private static void PaintRect(Texture2D tex, int x0, int y0, int x1, int y1,
                                      Color fill, Color edge)
        {
            for (var y = y0; y <= y1; y++)
            {
                if (y < 0 || y >= tex.height) continue;
                for (var x = x0; x <= x1; x++)
                {
                    if (x < 0 || x >= tex.width) continue;
                    var onEdge = x == x0 || x == x1 || y == y0 || y == y1;
                    tex.SetPixel(x, y, onEdge ? edge : fill);
                }
            }
        }

        private static void UpdateZoneHint()
        {
            var label = ZoneHint != null ? ZoneHint.GetComponentInChildren<Text>() : null;
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

            foreach (var zone in Zones)
            {
                ZoneCells(zone, out var min, out var max);
                for (var y = min.y; y <= max.y; y++)
                for (var x = min.x; x <= max.x; x++)
                    MPokeLocalZone.Invoke(system, new object[] { new Vector2i(x, y) });
            }
        }

        /// <summary>
        /// The same list this appends to is handed to RemoveObjects straight afterwards,
        /// so adding here both creates the objects and spares them from being culled.
        /// </summary>
        internal static void AppendKeptZoneObjects(List<ZDO> currentNearObjects)
        {
            if (!KeptZonesActive() || currentNearObjects == null) return;

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
                        // area 0 is exactly the one centre cell, so walking the range
                        // cell by cell gives the same set the square would, no more.
                        for (var y = min.y; y <= max.y; y++)
                        for (var x = min.x; x <= max.x; x++)
                            man.FindSectorObjects(new Vector2i(x, y), 0, 0, KeptZoneObjects);
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
                    if (system != null && system.IsZoneLoaded(new Vector2i(x, y))) loadedCells++;
                }
            }

            Log.LogInfo($"[AstvardServerMod] Kept zones: {Zones.Count}, "
                        + $"terrain {loadedCells}/{cells} cells, "
                        + $"{KeptZoneObjects.Count} zdos, {scene.NrOfInstances()} objects loaded.");
        }

        private static void SetActive(GameObject go, bool state)
        {
            if (go != null) go.SetActive(state);
        }

        private static void AddFixedSize(GameObject go, float width, float height)
        {
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.preferredHeight = height;
        }

        private void OnDestroy()
        {
            if (_roadPreview != null) Destroy(_roadPreview);
            _harmony?.UnpatchSelf();
        }
    }

    public class HiCommand : ConsoleCommand
    {
        public override string Name => "hi";
        public override string Help => "Astvard: печатает приветствие";
        public override bool OnlyServer => true;

        public override void Run(string[] args)
        {
            Plugin.Log.LogInfo("[AstvardServerMod] hi command executed");
            Chat.instance?.AddString("Приветас мир");
        }
    }

    public class AdminUnlockCommand : ConsoleCommand
    {
        public override string Name => "astvardadmin";
        public override string Help => "Astvard: разблокировать админ-кнопки в меню (только для админов сервера)";
        public override bool OnlyServer => true;

        public override void Run(string[] args)
        {
            // OnlyServer commands only reach here if the server's own admin check
            // (adminlist.txt) let it through — same gate as devcommands/kick.
            Plugin.IsAdminUnlocked = true;
            Plugin.RefreshMenu();
            Plugin.Log.LogInfo("[AstvardServerMod] Admin unlocked via astvardadmin command");
            Chat.instance?.AddString("Astvard: админ-кнопки разблокированы.");
        }
    }

    /// <summary>
    /// Swallows the attack input while a ghost is being placed — otherwise the same
    /// left click that commits the building also swings whatever is in hand.
    /// </summary>
    /// <summary>
    /// Smelter.Spawn is the single point where a finished product materialises, for
    /// smelters, blast furnaces and charcoal kilns alike. Skipping it means the item
    /// never hits the ground, so there is nothing to clean up afterwards.
    /// </summary>
    [HarmonyPatch(typeof(Smelter), "Spawn")]
    public static class SmelterSpawnToChest
    {
        private static bool Prefix(Smelter __instance, string ore, int stack)
        {
            return !Plugin.TryCollectToChest(__instance, ore, stack);
        }
    }

    /// <summary>
    /// While assignment is armed, opening a chest marks it instead. Returning false
    /// keeps the chest closed, so the click that assigns does not also open the UI.
    /// </summary>
    [HarmonyPatch(typeof(Container), nameof(Container.Interact))]
    public static class ContainerAssignPatch
    {
        private static bool Prefix(Container __instance, Humanoid character, bool hold, ref bool __result)
        {
            if (Plugin.PendingChestAssign == null || hold) return true;
            if (character == null || character != Player.m_localPlayer) return true;

            __result = Plugin.SetChestRole(__instance, Plugin.PendingChestSupply,
                Plugin.PendingChestAssign.Value);
            return false;
        }
    }

    /// <summary>
    /// Marks a collection chest right where the player already looks, instead of
    /// floating a label in the world. The vanilla text is already localised and uses
    /// rich text, so the marker just rides along on the name line.
    /// </summary>
    [HarmonyPatch(typeof(Container), nameof(Container.GetHoverText))]
    public static class ContainerCollectHoverPatch
    {
        private static void Postfix(Container __instance, ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;

            var collect = Plugin.IsCollectChest(__instance);
            var supply = Plugin.IsSupplyChest(__instance);
            if (!collect && !supply) return;

            var roles = collect && supply ? "сбор · подача" : (collect ? "сбор" : "подача");
            var marker = " <color=#FFCC44>· " + roles + "</color>";
            var lineEnd = __result.IndexOf('\n');
            __result = lineEnd < 0
                ? __result + marker
                : __result.Substring(0, lineEnd) + marker + __result.Substring(lineEnd);
        }
    }

    /// <summary>
    /// Cooked food, burnt included: taking the burnt piece too keeps the slot free so
    /// the station carries on working while nobody is watching.
    /// </summary>
    [HarmonyPatch(typeof(CookingStation), "SpawnItem")]
    public static class CookingStationSpawnToChest
    {
        private static bool Prefix(CookingStation __instance, string name)
        {
            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(name) : null;
            return !Plugin.TryStoreNearby(__instance.transform.position, prefab, 1);
        }
    }

    /// <summary>
    /// Honey. The vanilla routine spawns the jars inline rather than through a helper,
    /// so this replaces it wholesale and clears the hive itself.
    /// </summary>
    [HarmonyPatch(typeof(Beehive), "RPC_Extract")]
    public static class BeehiveExtractToChest
    {
        private static bool Prefix(Beehive __instance)
        {
            var view = __instance.GetComponent<ZNetView>();
            if (view == null || !view.IsValid() || __instance.m_honeyItem == null) return true;

            var level = view.GetZDO().GetInt(ZDOVars.s_level);
            if (level <= 0) return true;

            if (!Plugin.TryStoreNearby(__instance.transform.position,
                    __instance.m_honeyItem.gameObject, level)) return true;

            __instance.m_spawnEffect.Create(__instance.m_spawnPoint.position, Quaternion.identity);
            view.GetZDO().Set(ZDOVars.s_level, 0);
            return false;
        }
    }

    /// <summary>
    /// Mead. DelayedTap is the pour that follows the tap animation, and it is the only
    /// place the bottles come into existence.
    /// </summary>
    [HarmonyPatch(typeof(Fermenter), "DelayedTap")]
    public static class FermenterTapToChest
    {
        private static readonly AccessTools.FieldRef<Fermenter, string> TapItem =
            AccessTools.FieldRefAccess<Fermenter, string>("m_delayedTapItem");

        private static bool Prefix(Fermenter __instance)
        {
            var content = TapItem(__instance);
            if (string.IsNullOrEmpty(content)) return true;

            Fermenter.ItemConversion conversion = null;
            foreach (var candidate in __instance.m_conversion)
            {
                if (candidate == null || candidate.m_from == null) continue;
                if (candidate.m_from.gameObject.name != content) continue;
                conversion = candidate;
                break;
            }

            if (conversion == null || conversion.m_to == null) return true;
            if (!Plugin.TryStoreNearby(__instance.transform.position,
                    conversion.m_to.gameObject, conversion.m_producedItems)) return true;

            __instance.m_spawnEffects.Create(__instance.m_outputPoint.position, Quaternion.identity);
            return false;
        }
    }

    [HarmonyPatch(typeof(Game), "Start")]
    public static class RegisterZoneRpcsOnStart
    {
        private static void Postfix() => Plugin.RegisterZoneRpcs();
    }

    /// <summary>
    /// Records which socket a routed RPC physically arrived on. The sender id carried
    /// inside the packet is written by the sender and can claim to be anyone, including
    /// the server; the socket cannot be forged. A null value means the call was raised
    /// on this machine rather than received.
    /// </summary>
    [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
    public static class RoutedSenderTracker
    {
        internal static ZRpc Current;

        private static void Prefix(ZRpc rpc) => Current = rpc;

        // A finalizer rather than a postfix: a handler that throws would otherwise
        // leave the last sender's socket standing as the answer for local calls.
        private static void Finalizer() => Current = null;
    }

    [HarmonyPatch(typeof(ZoneSystem), "Update")]
    public static class KeepZoneTerrainAlive
    {
        private static void Postfix() => Plugin.PokeKeptZones();
    }

    [HarmonyPatch(typeof(ZNetScene), "CreateObjects")]
    public static class KeepZoneObjectsLoaded
    {
        private static void Prefix(List<ZDO> currentNearObjects)
            => Plugin.AppendKeptZoneObjects(currentNearObjects);
    }

    [HarmonyPatch(typeof(Player), "PlayerAttackInput")]
    public static class SuppressAttackInputWhilePlacing
    {
        private static bool Prefix() => !Plugin.IsPlacing;
    }

    /// <summary>
    /// Backstop for the above: blocking the input handler alone still let attacks
    /// through, so refuse the attack itself as well while placing.
    /// </summary>
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
    public static class SuppressStartAttackWhilePlacing
    {
        private static bool Prefix(Humanoid __instance, ref bool __result)
        {
            if (!Plugin.IsPlacing || __instance != Player.m_localPlayer) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "Show")]
    public static class InventoryShowPatch
    {
        private static void Postfix()
        {
            if (Plugin.Panel != null) Plugin.Panel.SetActive(true);
            Plugin.RefreshMenu();
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "Hide")]
    public static class InventoryHidePatch
    {
        private static void Postfix()
        {
            if (Plugin.Panel != null) Plugin.Panel.SetActive(false);
            // next open starts collapsed at the root menu
            Plugin.MenuState = 0;
            Plugin.IsInfoShown = false;
            Plugin.RefreshMenu();
        }
    }
}
