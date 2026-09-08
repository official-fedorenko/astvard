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

        internal static GameObject Panel;

        internal static GameObject InfoButton;

        internal static GameObject InfoText;

        internal static GameObject ActivateButton;

        internal static GameObject RadiusInput;

        internal static GameObject HeightInput;

        internal static GameObject ApplyButton;

        internal static GameObject BackButton;

        internal static GameObject BuildButton;

        internal static GameObject PlatformsCategoryButton;

        internal static GameObject HousesCategoryButton;

        internal static GameObject StarterHousesButton;

        internal static GameObject StarterHouse1Button;

        internal static GameObject KitchensCategoryButton;

        internal static GameObject StarterKitchensButton;

        internal static GameObject KitchenFullButton;

        internal static GameObject ProcessingCategoryButton;

        internal static GameObject CharcoalKilnsButton;

        internal static GameObject CheatsButton;

        internal static GameObject FeaturesButton;

        internal static bool IsInfoShown;

        internal static int MenuState = StateRoot;

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
                UpdateInfoText();
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

        /// <summary>
        /// Appends what is only knowable in game — where the player stands and which
        /// world they stand in. Rebuilt on every open rather than cached, since both
        /// change under the panel while it is closed.
        /// </summary>
        private static void UpdateInfoText()
        {
            var label = InfoText != null ? InfoText.GetComponentInChildren<Text>() : null;
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
            SetActive(LevelGroundButton, admin && MenuState == StateBuild);
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
