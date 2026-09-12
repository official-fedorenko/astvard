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
        // Menu levels shown inside the Tab panel.
        private const int StateRoot = 0;      // Ознакомиться / Активировать
        private const int StateAdmin = 1;     // God / Debugmode / Рельеф
        private const int StateTerrain = 2;   // Выровнять круг / квадрат — открыт всем
        private const int StateTerrainForm = 3; // радиус + высота + применить
        private const int StateBuild = 4;       // Копировать / Вставить
        private const int StateCopyForm = 5;    // радиус копирования + применить
        private const int StateCheats = 6;      // God / Debugmode / Tod
        private const int StateTod = 7;         // время суток 1-10
        private const int StateTemplates = 8;      // категории, собранные из файлов
        private const int StateTemplateList = 9;   // шаблоны выбранной категории
        private const int StateTemplateEdit = 10;  // действия над одним шаблоном
        private const int StateSharedList = 11;    // шаблоны, лежащие на сервере
        private const int StateRepair = 12;     // радиус починки + применить
        private const int StateSharedItem = 13; // действия над серверным шаблоном
        private const int StateStarterKitchens = 14; // свободно
        private const int StateProcessing = 15; // свободно
        private const int StateForceDelete = 16; // радиус очистки + применить
        private const int StateFeatures = 17;   // общие функции, видны всем
        private const int StateFill = 18;       // наполнение станций из сундуков
        private const int StateCollect = 19;    // сбор продукта в сундуки
        private const int StateZone = 20;       // свои зоны + вход к чужим
        private const int StateZoneOthers = 21; // список тех, кто ставил зоны
        private const int StateZoneOwner = 22;  // зоны одного игрока
        private const int StateZoneEdit = 23;   // действия над одной зоной
        private const int StateRoad = 24;       // дорожки между двумя точками
        private const int StateWeather = 25;    // ветер и погода
        private const int StateWind = 26;       // куда и как сильно дует
        private const int StateEnv = 27;        // какую погоду держать
        private const int StateBridge = 28;     // мост между двумя точками
        private const int StateSpawners = 29;   // спавнеры, сгруппированные по биому
        private const int StateSpawnerList = 30; // спавнеры одного биома
        private const int StateFood = 31;       // готовые наборы еды по биомам
        private const int StateRoadKind = 32;   // кладка дорожки: каменная / земляная
        private const int StateRoadWidth = 33;  // ширина дорожки
        private const int StateRoadBend = 34;   // изгиб: насколько и в какую сторону
        private const int StateRoadTorches = 35; // факелы: какие и через сколько
        private const int StateRoadArea = 36;   // мощение вокруг игрока, из «Рельефа»
        private const int StateFence = 37;      // частокол кольцом вокруг игрока
        private const int StateAreaFill = 38;   // заполнить замкнутый контур: пол, потом остальное
        private const int StatePlayerBuild = 39; // постройки игрока: что админ открыл игрокам
        private const int StateSettings = 40;   // настройки админа: пауза построек, что открыто игрокам
        private const int StatePlayerCooldown = 41; // пауза между постройками игроков
        private const int StateTerrainRules = 42; // что игроки могут в «Рельефе»
        private const int StateRuleEdit = 43;     // одно правило для игроков: можно ли, до скольких метров, даром ли
        private const int StateBuildRules = 44;   // что игроки могут в своих «Постройках»
        private const int StateFeatureRules = 45; // что игроки могут в «Функциях»
        private const int StateAllowedList = 46;  // шаблоны, открытые игрокам
        private const int StatePlayerTemplates = 47; // у игрока: шаблоны сервера, открытые ему
        private const int StatePlayerZone = 48;   // у игрока: его зона автоматики
        private const int StateRuneMinutes = 49;  // сколько минут в игре даёт руну
        private const int StateWallHeight = 50;   // высота стены по краю пола, из «Заполнить»
        private const int StateBuildSettings = 51; // как ставятся постройки: прилипание, выравнивание, снос

        internal static GameObject Panel;

        internal static GameObject InfoButton;

        internal static GameObject InfoText;

        internal static GameObject SeedCopyButton;

        internal static GameObject ActivateButton;

        internal static GameObject AdminAskButton;

        internal static GameObject RadiusInput;

        internal static GameObject HeightInput;

        internal static GameObject ApplyButton;

        internal static GameObject BackButton;

        internal static GameObject BuildButton;

        private const int MaxTemplateButtons = 8;
        internal static readonly GameObject[] CategoryButtons = new GameObject[MaxTemplateButtons];
        internal static readonly GameObject[] TemplateButtons = new GameObject[MaxTemplateButtons];
        internal static GameObject TemplateHint;
        internal static GameObject TemplateEditHint;
        internal static GameObject TemplatePlaceButton;
        internal static GameObject TemplateRenameButton;
        internal static GameObject TemplateDeleteButton;
        internal static GameObject TemplateNameInput;
        internal static GameObject TemplateCategoryInput;
        internal static GameObject TemplateNameLabel;
        internal static GameObject TemplateCategoryLabel;
        internal static GameObject TemplateShareButton;
        internal static GameObject SharedButton;
        internal static GameObject SharedHint;
        internal static GameObject SharedTakeButton;
        internal static GameObject SharedDeleteButton;
        internal static readonly GameObject[] SharedButtons = new GameObject[MaxTemplateButtons];

        internal static GameObject CheatsButton;

        internal static GameObject FeaturesButton;

        internal static GameObject FoodHint;

        internal static readonly GameObject[] FoodSetButtons =
            new GameObject[MaxFoodButtons];

        internal static bool IsInfoShown;

        internal static int MenuState = StateRoot;

        private void CreatePanel()
        {
            // Jotunn raises OnCustomGUIAvailable again on every scene load, so this runs
            // more than once a session - the log shows two panels, one for the start
            // scene and one for the world.
            //
            // Both used to survive. The fields below point at whichever was built last,
            // but the earlier panel is still in the scene with its buttons still live,
            // so a click landed on two of them and the interface answered twice - which
            // is what a doubled click sound is. Every widget is a child of the panel, so
            // taking the panel takes them with it.
            if (Panel != null) Destroy(Panel);

            // And the text boxes cached for keyboard blocking belong to the panel that
            // has just gone, so the cache has to go too: held on to, every entry in it
            // is null, the block never engages, and letters typed into a field reach the
            // game as hotkeys instead.
            _panelInputs = null;
            _inputBlocked = false;

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

            // First on the first page: what a player has earned, above everything to do.
            CreateCurrencyWidget(gui);

            InfoButton = MakeButton(gui, "Ознакомиться", () =>
            {
                IsInfoShown = !IsInfoShown;
                UpdateInfoText();
                RefreshMenu();
            });

            InfoText = MakeText(gui, ProjectDescription);

            SeedCopyButton = MakeButton(gui, "Скопировать сид", CopySeed);

            ActivateButton = MakeButton(gui, "Админ-меню", () =>
            {
                MenuState = StateAdmin;
                RefreshMenu();
            });

            // Everyone sees this one, because whether you may press it is not a
            // question the client can answer - it asks the server, which decides from
            // adminlist.txt. It replaces the astvardadmin console command as the way
            // in: that command was marked OnlyServer, and the game refuses those on a
            // client outright, so it only ever worked while another mod relayed it.
            AdminAskButton = MakeButton(gui, "Админка", RequestAdmin);

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

            CreatePlayerFeatureWidgets(gui);

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
                    // The radius box is shared with the add page, and this page promises
                    // that leaving it empty keeps the zone's current radius. It can only
                    // keep that promise if it starts empty.
                    SetField(ZoneSizeInput, "");
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

            // Two presses, because one press wipes every admin's zones and there is
            // no undo: the coordinates only ever lived in the config string that this
            // clears. The wipe stays server-wide on purpose - zones are a shared
            // resource here and every other button on the page already edits anybody's -
            // so the label says so, and the arming says it again.
            ZoneClearButton = MakeButton(gui, "Удалить все зоны сервера", () =>
            {
                if (!ZoneWipeArmed)
                {
                    ArmZoneWipe();
                    return;
                }

                DisarmZoneWipe();
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

            FoodButton = MakeButton(gui, "Еда", () =>
            {
                MenuState = StateFood;
                RefreshMenu();
            });

            FoodHint = MakeText(gui, "Два блюда на здоровье и одно\nна выносливость — живот держит\nтри. По три порции каждого.\nС Мистленда есть второй набор,\nна эйтр: для посоха вместо меча.");

            for (var i = 0; i < MaxFoodButtons; i++)
            {
                var slot = i;
                FoodSetButtons[slot] = MakeButton(gui, FoodSetLabel(slot),
                                                  () => GiveFoodSet(slot));
            }

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


            SpawnerHint = MakeText(gui, "Выбери биом, потом тварь.\nЛКМ — поставить, Esc — отмена,\nP — закрепить, стрелки — сдвиг.\nСпавнер невидим — в проекции\nпоказан сам зверь.\nВ базе игрока (верстак, костёр)\nон молчит, и работает, только\nпока игрок ближе 60 м.\n«Убрать рядом» сносит все\nспавнеры в 8 м, и родные тоже.\nБуфер копирования будет занят.");

            for (var i = 0; i < MaxSpawnerButtons; i++)
            {
                var slot = i;
                SpawnerGroupButtons[slot] = MakeButton(gui, "", () => OpenSpawnerGroup(slot));
            }

            for (var i = 0; i < MaxSpawnerButtons; i++)
            {
                var slot = i;
                SpawnerKindButtons[slot] = MakeButton(gui, "", () => PlaceSpawner(slot));
            }

            SpawnerRemoveButton = MakeButton(gui, "Убрать рядом", RemoveNearbySpawners);

            WeatherButton = MakeButton(gui, "Настройка погоды", () =>
            {
                MenuState = StateWeather;
                RefreshMenu();
            });

            WindButton = MakeButton(gui, "Направление ветра", () =>
            {
                MenuState = StateWind;
                RefreshMenu();
            });

            EnvButton = MakeButton(gui, "Погода", () =>
            {
                MenuState = StateEnv;
                RefreshMenu();
            });

            WindHint = MakeText(gui, "Куда дует, в градусах:\n0 — север, 90 — восток,\n180 — юг, 270 — запад.\nСила 1-10.\n«По направлению» — куда смотришь.\nВидно только тебе.");

            WindAngleInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "градусы, напр. 90", 16, 160f, 32f);
            AddFixedSize(WindAngleInput, 160f, 32f);

            WindPowerInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "сила 1-10, напр. 5", 16, 160f, 32f);
            AddFixedSize(WindPowerInput, 160f, 32f);

            WindApplyButton = MakeButton(gui, "Установить", ApplyWind);

            WindFacingButton = MakeButton(gui, "По направлению", ApplyWindFromFacing);

            WindResetButton = MakeButton(gui, "Вернуть обычный", ResetWind);

            EnvHint = MakeText(gui, "Держит выбранную погоду,\nпока не вернёшь обычную.\nВидно только тебе.");

            // Built once, like every other list here: a button created inside a click
            // handler breeds a new one on every press.
            for (var i = 0; i < WeatherOptions.Length; i++)
            {
                var option = WeatherOptions[i];
                WeatherOptionButtons[i] = MakeButton(gui, option.Label, () => ApplyWeather(option));
            }

            EnvResetButton = MakeButton(gui, "Вернуть обычную", ResetWeather);

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

            CreatePlayerBuildWidgets(gui);

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

            TerrainHint = MakeText(gui, "Радиус (м) и высота над водой.\n0 или пусто — уровень игрока.\nКрая сшиваются автоматически.");

            RadiusInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "радиус, напр. 8", 16, 160f, 32f);
            AddFixedSize(RadiusInput, 160f, 32f);

            HeightInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "над водой, напр. 5", 16, 160f, 32f);
            AddFixedSize(HeightInput, 160f, 32f);


            ApplyButton = MakeButton(gui, "Применить", ApplyTerrainLevel);

            UndoButton = MakeButton(gui, "", () =>
            {
                if (RuleAllows("undo")) UndoTerrain();
                RefreshMenu();
            });

            RoadButton = MakeButton(gui, "Дорожка", () => OpenRoadPage(StateRoad));

            // One button for each setting, the choice itself behind it on a page of its
            // own - the way «Рельеф» opens onto «Выровнять круг» - so the road page keeps
            // only what is pressed every time. The page's widgets are made in the order
            // they stand on it; the pages behind them follow.
            RoadHint = MakeText(gui, "");

            RoadKindButton = MakeButton(gui, "", () => OpenPavingSubPage(StateRoadKind));

            RoadWidthButton = MakeButton(gui, "", () => OpenRoadPage(StateRoadWidth));

            RoadBendButton = MakeButton(gui, "", () => OpenRoadPage(StateRoadBend));

            RoadSmoothButton = MakeButton(gui, "", () =>
            {
                IsRoadSmoothing = !IsRoadSmoothing;
                UpdateRoadSmoothButtonLabel();
                UpdateRoadHint();
                Log.LogInfo($"[AstvardServerMod] Road smoothing: {IsRoadSmoothing}");
            });
            UpdateRoadSmoothButtonLabel();

            RoadClearButton = MakeButton(gui, "", () =>
            {
                IsRoadClearing = !IsRoadClearing;
                UpdateRoadClearButtonLabel();
                UpdateRoadHint();
                Log.LogInfo($"[AstvardServerMod] Road clearing: {IsRoadClearing}");
            });
            UpdateRoadClearButtonLabel();

            RoadTorchButton = MakeButton(gui, "", () => OpenPavingSubPage(StateRoadTorches));

            // On «Рельеф» itself, beside the road it shares its paving with: made here so it
            // stands right after «Дорожка» there.
            RoadAreaButton = MakeButton(gui, "Мощение вокруг", () => OpenRoadPage(StateRoadArea));

            RoadStartButton = MakeButton(gui, "Начать", () =>
            {
                var player = Player.m_localPlayer;
                if (player != null) MarkRoadStart(player, player.transform.position, "Начало отмечено");
            });

            // The next piece of a long road, from where the last one ended.
            RoadContinueButton = MakeButton(gui, "Продолжить", () =>
            {
                var player = Player.m_localPlayer;
                if (player == null || !_roadHasLastEnd) return;
                MarkRoadStart(player, _roadLastEnd, "Начало — в конце прошлого куска. Иди дальше и жми ЛКМ");
            });

            RoadEndButton = MakeButton(gui, "Закончить", BuildRoad);

            RoadCancelButton = MakeButton(gui, "Отменить", () =>
            {
                CancelRoad();
                RefreshMenu();
            });

            // A long road the server is laying. Not Escape: the admin is free to go about
            // other things meanwhile, and Escape there means the pause menu or another tool.
            RoadServerStopButton = MakeButton(gui, "Остановить укладку", StopServerRoad);

            // A choice made on one of these pages takes the player straight back to the
            // road or the paving it was opened from, where the button now says what was
            // chosen.
            RoadStoneButton = MakeButton(gui, "Каменная", () =>
            {
                _roadPaved = true;
                OpenRoadPage(_pavingBack);
            });

            RoadDirtButton = MakeButton(gui, "Земляная", () =>
            {
                _roadPaved = false;
                OpenRoadPage(_pavingBack);
            });

            RoadWidthInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "ширина, напр. 3", 16, 160f, 32f);
            AddFixedSize(RoadWidthInput, 160f, 32f);

            RoadCurveInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "изгиб 0-10, напр. 3", 16, 160f, 32f);
            AddFixedSize(RoadCurveInput, 160f, 32f);

            RoadLeftButton = MakeButton(gui, "Влево", () =>
            {
                _roadBendLeft = true;
                OpenRoadPage(StateRoad);
            });

            RoadRightButton = MakeButton(gui, "Вправо", () =>
            {
                _roadBendLeft = false;
                OpenRoadPage(StateRoad);
            });

            RoadTorchInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "шаг факелов, напр. 10", 16, 160f, 32f);
            AddFixedSize(RoadTorchInput, 160f, 32f);

            for (var i = 0; i < RoadTorchChoiceButtons.Length; i++)
            {
                var choice = i;
                RoadTorchChoiceButtons[i] = MakeButton(gui, TorchChoiceLabels[i], () =>
                {
                    ChooseRoadTorch(choice);
                    OpenRoadPage(_pavingBack);
                });
            }

            RoadAreaInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "радиус, напр. 8", 16, 160f, 32f);
            AddFixedSize(RoadAreaInput, 160f, 32f);

            RoadAreaMakeButton = MakeButton(gui, "Поставить", StartAreaPreview);

            BridgeButton = MakeButton(gui, "Мост", () =>
            {
                MenuState = StateBridge;
                RefreshMenu();
            });

            BridgeHint = MakeText(gui, "");
            UpdateBridgeHint();

            BridgeWidthInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "ширина 1-8, напр. 2", 16, 160f, 32f);
            AddFixedSize(BridgeWidthInput, 160f, 32f);

            BridgeLiftInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "подъём, напр. 0", 16, 160f, 32f);
            AddFixedSize(BridgeLiftInput, 160f, 32f);

            BridgeGapInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "отступ, м, напр. 4", 16, 160f, 32f);
            AddFixedSize(BridgeGapInput, 160f, 32f);

            BridgeCoverButton = MakeButton(gui, "", () =>
            {
                IsBridgeCovered = !IsBridgeCovered;
                UpdateBridgeCoverLabel();
                Log.LogInfo($"[AstvardServerMod] Bridge covered: {IsBridgeCovered}");
            });
            UpdateBridgeCoverLabel();

            BridgeStartButton = MakeButton(gui, "Начать", MarkBridgeStart);

            BridgeEndButton = MakeButton(gui, "Построить", BuildBridge);

            BridgeCancelButton = MakeButton(gui, "Отменить", () =>
            {
                CancelBridge();
                RefreshMenu();
            });

            BuildButton = MakeButton(gui, "Постройки", () =>
            {
                MenuState = StateBuild;
                RefreshMenu();
            });

            CreateSettingsWidgets(gui);

            // «Постройки» → «Настройки»: the button first on the build page, and behind it, in
            // this order, the hint and the switches made just below.
            CreateBuildSettingsWidgets(gui);

            SnapButton = MakeButton(gui, "Прилипание", () =>
            {
                IsSnapEnabled = !IsSnapEnabled;
                UpdateSnapButtonLabel();
                Log.LogInfo($"[AstvardServerMod] Snapping: {IsSnapEnabled}");
            });
            UpdateSnapButtonLabel();

            LevelGroundButton = MakeButton(gui, "Выравнивать землю", () =>
            {
                IsLevelGroundEnabled = !IsLevelGroundEnabled;
                UpdateLevelGroundButtonLabel();
                Log.LogInfo($"[AstvardServerMod] Level ground: {IsLevelGroundEnabled}");
            });
            UpdateLevelGroundButtonLabel();

            CreateBuildClearWidget(gui);

            CreateBuildResourcesWidget(gui);

            PlacementDistanceInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "дистанция, напр. 11", 16, 160f, 32f);
            AddFixedSize(PlacementDistanceInput, 160f, 32f);

            CopyButton = MakeButton(gui, "Копировать", () =>
            {
                _copyToFile = false;
                OpenCopyForm();
            });

            PasteButton = MakeButton(gui, "Скопировать", () =>
            {
                _copyToFile = true;
                OpenCopyForm();
            });

            TemplatesButton = MakeButton(gui, "Шаблоны", () =>
            {
                MenuState = StateTemplates;
                // Read the folder on the way in, so a file dropped there while the
                // game was running shows up without a restart.
                ReloadTemplates();
                // And ask the server which of them players may build, for the switch on
                // each template's page.
                AskSharedList();
                RefreshMenu();
            });

            SpawnerButton = MakeButton(gui, "Спавнеры", () =>
            {
                MenuState = StateSpawners;
                RefreshMenu();
            });

            CreateFenceWidgets(gui);

            CreateAreaFillWidgets(gui);

            TemplateHint = MakeText(gui, "");

            // One button per category and per template, filled in from whatever the
            // files say. Unity widgets are built once, so the pools stay put and the
            // lists move under them.
            for (var slot = 0; slot < MaxTemplateButtons; slot++)
            {
                var index = slot;
                CategoryButtons[slot] = MakeButton(gui, "", () =>
                {
                    var categories = TemplateCategories();
                    if (index >= categories.Count) return;

                    _templateCategory = categories[index];
                    MenuState = StateTemplateList;
                    RefreshMenu();
                });
            }

            for (var slot = 0; slot < MaxTemplateButtons; slot++)
            {
                var index = slot;
                TemplateButtons[slot] = MakeButton(gui, "", () =>
                {
                    var shown = TemplatesIn(_templateCategory);
                    if (index >= shown.Count) return;

                    _editingTemplate = shown[index];

                    // Filled in rather than left blank: renaming means editing what is
                    // there, and an empty box says nothing about what you are editing.
                    SetFieldText(TemplateNameInput, _editingTemplate.Name);
                    SetFieldText(TemplateCategoryInput, _editingTemplate.Category);

                    MenuState = StateTemplateEdit;
                    RefreshMenu();
                });
            }

            TemplateEditHint = MakeText(gui, "");

            TemplateNameLabel = MakeText(gui, "Имя:");

            TemplateNameInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.Standard, "имя шаблона", 16, 160f, 32f);
            AddFixedSize(TemplateNameInput, 160f, 32f);

            TemplateCategoryLabel = MakeText(gui, "Категория:");

            TemplateCategoryInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.Standard, "категория", 16, 160f, 32f);
            AddFixedSize(TemplateCategoryInput, 160f, 32f);

            TemplatePlaceButton = MakeButton(gui, "Поставить", () =>
            {
                if (_editingTemplate == null) return;
                // Loading a template rewrites the clipboard, and the builder is reading
                // it. Starting a second template mid-build used to splice the new one's
                // pieces into the old one's origin.
                if (BuildInProgress) return;
                if (!LoadTemplate(_editingTemplate.Lines, _editingTemplate.Name)) return;

                StartPlacement($"шаблон «{_editingTemplate.Name}»");
                // A player's own template is paid for when it goes up.
                if (!IsAdminUnlocked) _playerPaidPlacement = true;
                InventoryGui.instance?.Hide();
            });

            TemplateRenameButton = MakeButton(gui, "Переименовать", () =>
            {
                if (_editingTemplate == null) return;

                var name = FieldText(TemplateNameInput, _editingTemplate.Name);
                var category = FieldText(TemplateCategoryInput, _editingTemplate.Category);
                if (!RenameTemplate(_editingTemplate, name, category)) return;

                _templateCategory = category;
                _editingTemplate = null;
                MenuState = StateTemplateList;
                RefreshMenu();
            });

            TemplateShareButton = MakeButton(gui, "Выложить на сервер", () =>
            {
                if (_editingTemplate == null) return;
                PushTemplate(_editingTemplate);
            });

            CreateTemplatePlayersWidget(gui);

            SharedButton = MakeButton(gui, "Общие", () =>
            {
                MenuState = StateSharedList;
                RequestSharedList();
                RefreshMenu();
            });

            SharedHint = MakeText(gui, "");

            for (var slot = 0; slot < MaxTemplateButtons; slot++)
            {
                var index = slot;
                SharedButtons[slot] = MakeButton(gui, "", () =>
                {
                    if (index >= SharedTemplates.Count) return;

                    _selectedShared = SharedTemplates[index];
                    _sharedItemBack = StateSharedList;
                    MenuState = StateSharedItem;
                    RefreshMenu();
                });
            }

            SharedTakeButton = MakeButton(gui, "Забрать себе", () =>
            {
                TakeSharedTemplate(_selectedShared);
            });

            CreateSharedPlayersWidget(gui);

            SharedDeleteButton = MakeButton(gui, "Удалить с сервера", () =>
            {
                DeleteSharedTemplate(_selectedShared);
                _selectedShared = null;
                MenuState = _sharedItemBack;
                RefreshMenu();
            });

            TemplateDeleteButton = MakeButton(gui, "Удалить", () =>
            {
                if (_editingTemplate == null) return;
                if (!DeleteTemplate(_editingTemplate)) return;

                _editingTemplate = null;
                MenuState = StateTemplateList;
                RefreshMenu();
            });

            CopyHint = MakeText(gui, "Радиус (м).\nКопировать — проекция перед\nтобой, ЛКМ строит, Esc отменяет.\nQ/E — поворот, Shift+Q/E — высота,\nP — закрепить, стрелки — сдвиг.\nСкопировать — сохранить шаблоном,\nимя и категорию задай ниже.");

            CopyRadiusInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "радиус, напр. 10", 16, 160f, 32f);
            AddFixedSize(CopyRadiusInput, 160f, 32f);

            CopyApplyButton = MakeButton(gui, "Выполнить", () => { if (_copyToFile) RunCopyToFile(); else RunCopy(); });

            // Last but «Назад», so it sits in the same place on every page it shows on.
            CreateBuildUndoWidget(gui);

            BackButton = MakeButton(gui, "Назад", () =>
            {
                if (MenuState == StateTerrainForm) MenuState = StateTerrain;
                else if (MenuState == StateBridge) MenuState = StateTerrain;
                else if (MenuState == StateRoad) MenuState = StateTerrain;
                else if (MenuState == StateRoadArea) MenuState = StateTerrain;
                else if (MenuState == StateRoadKind || MenuState == StateRoadTorches) MenuState = _pavingBack;
                else if (IsRoadPage(MenuState)) MenuState = StateRoad;
                else if (MenuState == StateFence) MenuState = IsAdminUnlocked ? StateBuild : StatePlayerBuild;
                else if (MenuState == StateWallHeight) MenuState = StateAreaFill;
                else if (MenuState == StateAreaFill || MenuState == StateBuildSettings)
                    MenuState = IsAdminUnlocked ? StateBuild : StatePlayerBuild;
                else if (MenuState == StateCopyForm) MenuState = IsAdminUnlocked ? StateBuild : StatePlayerBuild;
                else if (MenuState == StateRepair && !IsAdminUnlocked) MenuState = StateFeatures;
                else if (MenuState == StateTod || MenuState == StateRepair ||
                         MenuState == StateForceDelete ||
                         MenuState == StateWeather) MenuState = StateCheats;
                else if (MenuState == StateWind || MenuState == StateEnv) MenuState = StateWeather;
                else if (MenuState == StateSpawners) MenuState = StateBuild;
                else if (MenuState == StateFood) MenuState = StateCheats;
                else if (MenuState == StateSpawnerList) MenuState = StateSpawners;
                else if (MenuState == StateTemplates) MenuState = IsAdminUnlocked ? StateBuild : StatePlayerBuild;
                else if (MenuState == StateTemplateList) MenuState = StateTemplates;
                else if (MenuState == StateTemplateEdit) MenuState = StateTemplateList;
                else if (MenuState == StateSharedList) MenuState = StateTemplates;
                else if (MenuState == StateSharedItem) MenuState = _sharedItemBack;
                else if (MenuState == StatePlayerCooldown || MenuState == StateAllowedList) MenuState = StateBuildRules;
                else if (MenuState == StateRuleEdit) MenuState = RulePage(PlayerRules[_editingRule].Group);
                else if (MenuState == StateTerrainRules || MenuState == StateBuildRules
                         || MenuState == StateFeatureRules) MenuState = StateSettings;
                else if (MenuState == StateRuneMinutes) MenuState = StateSettings;
                else if (MenuState == StateSettings) MenuState = StateAdmin;
                else if (MenuState == StatePlayerTemplates) MenuState = StatePlayerBuild;
                else if (MenuState == StatePlayerZone) MenuState = StateFeatures;
                else if (MenuState == StateFill || MenuState == StateCollect) MenuState = StateFeatures;
                else if (MenuState == StateZoneEdit) MenuState = StateZone;
                else if (MenuState == StateZoneOwner) MenuState = StateZoneOthers;
                else if (MenuState == StateZoneOthers) MenuState = StateZone;
                else if (MenuState == StatePlayerBuild) MenuState = StateRoot;
                else if (MenuState == StateFeatures) MenuState = StateRoot;
                else if (MenuState == StateTerrain) MenuState = StateRoot;
                else if (MenuState == StateAdmin) MenuState = StateRoot;
                else MenuState = StateAdmin;
                RefreshMenu();
            });

            RefreshMenu();
            Log.LogInfo("Astvard panel created.");
        }

        /// <summary>
        /// Takes back the second sound Jotunn puts on every button.
        ///
        /// One press was answered twice, and the two were not the same sound. Jotunn's
        /// ApplyButtonStyle fills two of ButtonSfx's slots - m_sfxPrefab with
        /// sfx_gui_button and m_selectSfxPrefab with sfx_gui_select - and 1.0's
        /// ButtonSfx plays the second one from ISelectHandler.OnSelect. A mouse press
        /// raises exactly that: Selectable.OnPointerDown makes the button the
        /// EventSystem's selection, so sfx_gui_select plays going down and
        /// sfx_gui_button coming back up.
        ///
        /// It reads as a doubled click because the two are milliseconds apart, and it
        /// survived the duplicate-panel fix because it never had anything to do with
        /// two panels - one button is enough.
        ///
        /// Jotunn is not wrong so much as out of date: this build moved the hover tick
        /// into m_enterSfxPrefab, a field that did not exist when that line was
        /// written, and left m_selectSfxPrefab meaning keyboard and gamepad selection.
        /// Clearing it silences the press and leaves the click, which is what a mouse
        /// user expects. Wiring the tick into m_enterSfxPrefab would give the panel
        /// vanilla's hover sound as well, and is a separate decision.
        /// </summary>
        private static void Silence(GameObject button)
        {
            var sfx = button != null ? button.GetComponent<ButtonSfx>() : null;
            if (sfx != null) sfx.m_selectSfxPrefab = null;
        }

        private GameObject MakeButton(GUIManager gui, string text, UnityEngine.Events.UnityAction onClick)
        {
            var go = gui.CreateButton(
                text, Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                180f, 40f);
            AddFixedSize(go, 180f, 40f);
            Silence(go);
            go.GetComponent<Button>().onClick.AddListener(onClick);
            go.SetActive(false);
            return go;
        }

        /// <summary>
        /// Appends what is only knowable in game — where the player stands and which
        /// world they stand in. Rebuilt on every open rather than cached, since both
        /// change under the panel while it is closed.
        /// </summary>
        /// <summary>
        /// Puts the world's seed on the system clipboard, for a map site or for the seed
        /// box of a new world.
        ///
        /// A client knows the seed even on a dedicated server: RPC_PeerInfo hands it over
        /// on connect, because the client generates the terrain itself. ZNet.World is a
        /// static that nothing ever clears, though, so after leaving a server it still
        /// holds the old world. The live ZNet instance is what says there is a world to
        /// copy from.
        ///
        /// systemCopyBuffer is what the game itself uses to copy text, which is the
        /// evidence it works in this runtime.
        /// </summary>
        private static void CopySeed()
        {
            var world = ZNet.instance != null ? ZNet.World : null;
            if (world == null || string.IsNullOrEmpty(world.m_seedName))
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Мир не загружен");
                return;
            }

            GUIUtility.systemCopyBuffer = world.m_seedName;
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                $"Сид скопирован: {world.m_seedName}");
        }

        private static void UpdateInfoText()
        {
            // The button calls this before RefreshMenu switches the text on, so the
            // lookup has to reach a hidden object. Without that it came back null and
            // the update was dropped - the page only refreshed when it was CLOSED, and
            // each open showed the snapshot from the last close. The first open of a
            // session showed no world, seed or position at all.
            var label = InfoText != null ? InfoText.GetComponentInChildren<Text>(true) : null;
            if (label == null) return;

            var text = new System.Text.StringBuilder(ProjectDescription);

            var world = ZNet.World;
            if (world != null)
            {
                text.Append($"{NEWLINE}{NEWLINE}Мир: {world.m_name}");
                text.Append($"{NEWLINE}Сид: {world.m_seedName} ({world.m_seed})");
            }

            var player = Player.m_localPlayer;
            if (player != null)
            {
                var pos = player.transform.position;
                text.Append($"{NEWLINE}Позиция: {pos.x:F0}, {pos.y:F0}, {pos.z:F0}");

                // Raw Y is measured from the world floor and the sea sits at 30 of it,
                // so the number in the line above says nothing a player can act on.
                // Against the water it does: negative means below the waves.
                var sea = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
                text.Append($"{NEWLINE}Высота: {pos.y - sea:F0}");

                var zone = ZoneSystem.GetZone(pos);
                text.Append($"{NEWLINE}Зона: {zone.x}, {zone.y}");

                var biome = Heightmap.FindBiome(pos);
                text.Append($"{NEWLINE}Биом: {biome}");
            }

            label.text = text.ToString();
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
        /// Writes a value into a text box the way a player typing it would, so a field
        /// and whatever a button just decided keep telling the same story.
        /// </summary>
        /// <summary>
        /// Reaches a text box whether or not it is on screen.
        ///
        /// GetComponentInChildren skips inactive objects unless told otherwise, and every
        /// box in this panel is inactive most of the time: RefreshMenu hides the ones
        /// that do not belong to the current page, and closing the inventory takes the
        /// whole panel down. So the plain overload returned null for a box the player had
        /// filled in, every caller quietly took its fallback, and nobody was told.
        ///
        /// It was doing real damage. The bridge and road tools read their numbers from an
        /// update that runs while the player walks - with the panel shut - so the preview
        /// was drawn at width 2 with no lift no matter what was typed, while «Построить»,
        /// pressed with the panel open, built the bridge that was actually asked for. The
        /// preview and the building never matched and could not. «Дистанция» was dead
        /// outright, since placement hides the inventory before the ghost ever moves.
        ///
        /// The one place that already passed true is UpdatePanelInputBlocking, which had
        /// to, or keyboard blocking would never have engaged.
        /// </summary>
        private static InputField FieldOf(GameObject inputGo)
        {
            return inputGo != null ? inputGo.GetComponentInChildren<InputField>(true) : null;
        }

        /// <summary>
        /// The name and category boxes are one pair of widgets serving both this page and
        /// the rename page, so a new save has to start from empty rather than from
        /// whatever the last rename left behind.
        /// </summary>
        private static void OpenCopyForm()
        {
            SetField(TemplateNameInput, "");
            SetField(TemplateCategoryInput, "");
            MenuState = StateCopyForm;
            RefreshMenu();
        }

        private static void SetField(GameObject inputGo, string text)
        {
            var field = FieldOf(inputGo);
            if (field != null) field.text = text;
        }

        private static float ParseField(GameObject inputGo, float fallback)
        {
            var field = FieldOf(inputGo);
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

            SetActive(CurrencyText, MenuState == StateRoot && _myRunes >= 0);
            SetActive(InfoButton, MenuState == StateRoot);
            SetActive(InfoText, MenuState == StateRoot && IsInfoShown);
            SetActive(SeedCopyButton, MenuState == StateRoot && IsInfoShown);
            SetActive(ActivateButton, admin && MenuState == StateRoot);
            SetActive(AdminAskButton, !admin && MenuState == StateRoot);

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
            // A player sees what the admins left open, and nothing of it until the server
            // has said what that is.
            SetActive(TerrainButton, MenuState == StateRoot && AnyTerrainAllowed);
            SetActive(BuildButton, admin && MenuState == StateAdmin);

            SetActive(GodButton, admin && MenuState == StateCheats);
            SetActive(DebugModeButton, admin && MenuState == StateCheats);
            SetActive(FoodButton, admin && MenuState == StateCheats);
            SetActive(FoodHint, admin && MenuState == StateFood);
            for (var i = 0; i < MaxFoodButtons; i++)
                SetActive(FoodSetButtons[i],
                          admin && MenuState == StateFood && i < FoodSetCount);
            SetActive(TodButton, admin && MenuState == StateCheats);
            SetActive(WeatherButton, admin && MenuState == StateCheats);
            SetActive(RepairButton, admin && MenuState == StateCheats);
            SetActive(ForceDeleteButton, admin && MenuState == StateCheats);

            SetActive(TodHint, admin && MenuState == StateTod);
            SetActive(TodInput, admin && MenuState == StateTod);
            SetActive(TodApplyButton, admin && MenuState == StateTod);

            // «Ремонт» is a player's too, from «Функции», while the admins keep it open.
            var repairPage = MenuState == StateRepair && RuleAllows("repair");
            if (repairPage) UpdateRepairHint();
            SetActive(RepairHint, repairPage);
            SetActive(RepairRadiusInput, repairPage);
            SetActive(RepairApplyButton, repairPage);
            RefreshPlayerFeatureVisibility(admin);
            SetActive(ForceDeleteHint, admin && MenuState == StateForceDelete);
            SetActive(ForceDeleteRadiusInput, admin && MenuState == StateForceDelete);
            SetActive(ForceDeleteApplyButton, admin && MenuState == StateForceDelete);

            SetActive(WindButton, admin && MenuState == StateWeather);
            SetActive(EnvButton, admin && MenuState == StateWeather);

            SetActive(WindHint, admin && MenuState == StateWind);
            SetActive(WindAngleInput, admin && MenuState == StateWind);
            SetActive(WindPowerInput, admin && MenuState == StateWind);
            SetActive(WindApplyButton, admin && MenuState == StateWind);
            SetActive(WindFacingButton, admin && MenuState == StateWind);
            SetActive(WindResetButton, admin && MenuState == StateWind);

            SetActive(EnvHint, admin && MenuState == StateEnv);
            for (var i = 0; i < WeatherOptions.Length; i++)
                SetActive(WeatherOptionButtons[i], admin && MenuState == StateEnv
                                                   && KnowsWeather(WeatherOptions[i].Env));
            SetActive(EnvResetButton, admin && MenuState == StateEnv);

            RebuildSpawnerViews();
            SetActive(SpawnerHint, admin && (MenuState == StateSpawners
                                            || MenuState == StateSpawnerList));
            for (var i = 0; i < MaxSpawnerButtons; i++)
            {
                SetActive(SpawnerGroupButtons[i], admin && MenuState == StateSpawners
                                                  && i < _shownSpawnerGroups);
                SetActive(SpawnerKindButtons[i], admin && MenuState == StateSpawnerList
                                                 && i < _shownSpawnerKinds);
            }
            SetActive(SpawnerRemoveButton, admin && (MenuState == StateSpawners
                                                    || MenuState == StateSpawnerList));

            SetActive(CopyButton, admin && MenuState == StateBuild);
            SetActive(PasteButton, admin && MenuState == StateBuild);
            SetActive(TemplatesButton, admin && MenuState == StateBuild);
            SetActive(SpawnerButton, admin && MenuState == StateBuild);
            SetActive(FenceButton, admin && MenuState == StateBuild);
            // The fence page is a player's too, while the admins keep fences open to them.
            var fencePage = MenuState == StateFence && RuleAllows("fence");
            if (fencePage) UpdateFenceLabels();
            SetActive(FenceHint, fencePage);
            SetActive(FenceRadiusInput, fencePage);
            SetActive(FenceWalkwayButton, fencePage);
            SetActive(FenceRoofButton, fencePage);
            SetActive(FenceLevelButton, fencePage && RuleAllows("level"));
            SetActive(FenceBuildButton, fencePage);
            SetActive(FenceCancelButton, fencePage && IsFencePreviewing);
            SetActive(FencePinButton, fencePage && IsFencePreviewing);
            SetActive(AreaFillButton, admin && MenuState == StateBuild);
            // A player's too, from their own «Постройки», each part behind its rule.
            var floorOpen = admin || RuleAllows("floor");
            var wallOpen = admin || RuleAllows("wall");
            var fillPage = MenuState == StateAreaFill;
            if (fillPage) UpdateWallLabels();
            SetActive(AreaFillHint, fillPage && (floorOpen || wallOpen));
            SetActive(AreaFloorButton, fillPage && floorOpen);
            SetActive(AreaWallButton, fillPage && wallOpen);
            SetActive(AreaWallHeightButton, fillPage && wallOpen);
            foreach (var choice in WallHeightButtons)
                SetActive(choice, MenuState == StateWallHeight && wallOpen);

            // On every page of «Постройки» - a player's own, for a player - and on any page
            // at all while a build is still going up: the panel always reopens at the root,
            // and stopping a base halfway should not take a walk through the menu first.
            DisarmBuildUndo();
            var buildPage = admin ? IsBuildPage(MenuState) : IsPlayerBuildPage(MenuState);
            SetActive(BuildUndoButton, CanUndoBuild && (BuildGoingUp || buildPage));

            RefreshPlayerBuildVisibility(admin);
            RefreshSettingsVisibility(admin);
            RefreshBuildSettingsVisibility(admin);
            RebuildTemplateViews();

            // A player's own templates, while copying is open to them: the same pages, less
            // what only an admin does - putting one on the server, or opening it to players.
            var copying = admin || RuleAllows("copy");
            SetActive(TemplateHint, copying && (MenuState == StateTemplates
                                                || MenuState == StateTemplateList));
            SetActive(TemplateEditHint, copying && MenuState == StateTemplateEdit);
            SetActive(TemplatePlaceButton, copying && MenuState == StateTemplateEdit);
            SetActive(TemplateRenameButton, copying && MenuState == StateTemplateEdit);
            SetActive(TemplateDeleteButton, copying && MenuState == StateTemplateEdit);
            SetActive(TemplateShareButton, admin && MenuState == StateTemplateEdit);
            SetActive(TemplateSubmitButton, !admin && copying && RuleAllows("share")
                                            && MenuState == StateTemplateEdit);
            SetActive(SharedButton, admin && MenuState == StateTemplates);
            SetActive(SharedHint, admin && (MenuState == StateSharedList
                                            || MenuState == StateSharedItem));
            SetActive(SharedTakeButton, admin && MenuState == StateSharedItem);
            SetActive(SharedDeleteButton, admin && MenuState == StateSharedItem);

            for (var i = 0; i < MaxTemplateButtons; i++)
                SetActive(SharedButtons[i], admin && MenuState == StateSharedList
                                            && i < SharedTemplates.Count);

            // A player's copy form names only what is saved, not what is placed.
            var naming = MenuState == StateTemplateEdit || (MenuState == StateCopyForm && (admin || _copyToFile));
            SetActive(TemplateNameLabel, copying && naming);
            SetActive(TemplateNameInput, copying && naming);
            SetActive(TemplateCategoryLabel, copying && naming);
            SetActive(TemplateCategoryInput, copying && naming);

            for (var i = 0; i < MaxTemplateButtons; i++)
            {
                SetActive(CategoryButtons[i], copying && MenuState == StateTemplates
                                              && i < _shownCategories);
                SetActive(TemplateButtons[i], copying && MenuState == StateTemplateList
                                              && i < _shownTemplates);
            }

            if (MenuState == StateCopyForm) UpdateCopyHint();
            SetActive(CopyHint, copying && MenuState == StateCopyForm);
            SetActive(CopyRadiusInput, copying && MenuState == StateCopyForm);
            SetActive(CopyApplyButton, copying && MenuState == StateCopyForm);

            SetActive(LevelCircleButton, MenuState == StateTerrain && RuleAllows("level"));
            SetActive(RoadButton, MenuState == StateTerrain && RuleAllows("road"));
            SetActive(BridgeButton, MenuState == StateTerrain && RuleAllows("bridge"));
            SetActive(UndoButton, MenuState == StateTerrain && CanUndoTerrain && RuleAllows("undo"));
            UpdateUndoButtonLabel();
            // The paving page has the road's paving, smoothing, clearing and torches on it
            // too - the same settings, shared - so it needs no trip to the road for them.
            var road = MenuState == StateRoad;
            var paving = MenuState == StateRoadArea;
            if (IsRoadPage(MenuState)) UpdateRoadHint();
            if (road || paving) UpdateRoadLabels();
            SetActive(RoadHint, IsRoadPage(MenuState));
            SetActive(RoadKindButton, road || paving);
            SetActive(RoadWidthButton, road);
            SetActive(RoadBendButton, road);
            SetActive(RoadSmoothButton, (road || paving) && RuleAllows("smooth"));
            SetActive(RoadClearButton, (road || paving) && RuleAllows("clear"));
            SetActive(RoadTorchButton, (road || paving) && RuleAllows("torches"));
            SetActive(RoadAreaButton, MenuState == StateTerrain && RuleAllows("area"));
            SetActive(RoadStartButton, road && RuleAllows("road"));
            SetActive(RoadContinueButton, road && _roadHasLastEnd && !RoadInProgress && RuleAllows("road"));
            SetActive(RoadEndButton, road && RoadAwaitingEnd);
            SetActive(RoadCancelButton, road && RoadInProgress);
            SetActive(RoadServerStopButton, road && _roadServerJob != 0 && _roadServerTaken);

            SetActive(RoadStoneButton, MenuState == StateRoadKind);
            SetActive(RoadDirtButton, MenuState == StateRoadKind);
            SetActive(RoadWidthInput, MenuState == StateRoadWidth);
            SetActive(RoadCurveInput, MenuState == StateRoadBend);
            SetActive(RoadLeftButton, MenuState == StateRoadBend);
            SetActive(RoadRightButton, MenuState == StateRoadBend);
            SetActive(RoadTorchInput, MenuState == StateRoadTorches);
            foreach (var choice in RoadTorchChoiceButtons)
                SetActive(choice, MenuState == StateRoadTorches);
            SetActive(RoadAreaInput, MenuState == StateRoadArea);
            SetActive(RoadAreaMakeButton, MenuState == StateRoadArea);
            SetActive(LevelSquareButton, MenuState == StateTerrain && RuleAllows("level"));

            if (MenuState == StateBridge) UpdateBridgeHint();
            SetActive(BridgeHint, MenuState == StateBridge);
            SetActive(BridgeWidthInput, MenuState == StateBridge);
            SetActive(BridgeCoverButton, MenuState == StateBridge);
            SetActive(BridgeLiftInput, MenuState == StateBridge);
            SetActive(BridgeGapInput, MenuState == StateBridge);
            SetActive(BridgeStartButton, MenuState == StateBridge);
            SetActive(BridgeEndButton, MenuState == StateBridge);
            SetActive(BridgeCancelButton, MenuState == StateBridge && BridgeInProgress);

            if (MenuState == StateTerrainForm)
                SetLabel(TerrainHint, "Радиус (м) и высота над водой.\n0 или пусто — уровень игрока.\nКрая сшиваются автоматически."
                                      + RuleLimitNote("level", MaxLevelRadius));
            SetActive(TerrainHint, MenuState == StateTerrainForm);
            SetActive(RadiusInput, MenuState == StateTerrainForm);
            SetActive(HeightInput, MenuState == StateTerrainForm);
            SetActive(ApplyButton, MenuState == StateTerrainForm);

            SetActive(BackButton, (admin || IsPlayerSection(MenuState)) && MenuState != StateRoot);

            KeepPanelOnScreen();
        }

        /// <summary>
        /// Drags the panel back inside the screen after its size changes.
        ///
        /// The panel is draggable and resizes itself to whatever page is showing, and
        /// those two together can lose it: drag it low, open a tall page, and the panel
        /// grows off the bottom — including the strip you grab it by, so there is no way
        /// left to drag it back. Clamping after every page change means it can always be
        /// reached, and a panel taller than the screen pins to the top rather than
        /// hanging past both edges.
        /// </summary>
        private static void KeepPanelOnScreen()
        {
            if (Panel == null || !Panel.activeInHierarchy) return;

            var rect = Panel.GetComponent<RectTransform>();
            var canvas = rect != null ? rect.parent as RectTransform : null;
            if (rect == null || canvas == null) return;

            // The size fitter only settles during layout, so ask for it now rather than
            // clamping against the size the panel had on the previous page.
            LayoutRebuilder.ForceRebuildLayoutImmediate(rect);

            var size = rect.rect.size;
            var half = canvas.rect.size * 0.5f;
            var position = rect.anchoredPosition;

            // Pivot sits at the top edge, so anchoredPosition.y is the top of the panel.
            var top = half.y;
            var bottom = -half.y + size.y;
            position.y = Mathf.Clamp(position.y, Mathf.Min(bottom, top), top);

            var margin = size.x * 0.5f;
            var left = -half.x + margin;
            var right = half.x - margin;
            position.x = Mathf.Clamp(position.x, Mathf.Min(left, right), Mathf.Max(left, right));

            rect.anchoredPosition = position;
        }

        // Pages every player can reach, admin or not.
        private static bool IsPlayerSection(int state)
        {
            return state == StateFeatures || state == StateFill || state == StateCollect
                   || state == StateTerrain || state == StateTerrainForm
                   || state == StateRoad || state == StateBridge || IsRoadPage(state)
                   || IsPlayerBuildPage(state) || state == StateRepair || state == StatePlayerZone;
        }

        private static readonly string NEWLINE = "\n";

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
    }
}
