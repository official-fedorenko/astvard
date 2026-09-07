using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
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

            TemplatesButton = MakeButton(gui, "Шаблоны", () =>
            {
                MenuState = StateTemplates;
                RefreshMenu();
            });

            SnapButton = MakeButton(gui, "Прилипание", () =>
            {
                IsSnapEnabled = !IsSnapEnabled;
                UpdateSnapButtonLabel();
                Log.LogInfo($"[AstvardServerMod] Snapping: {IsSnapEnabled}");
            });
            UpdateSnapButtonLabel();

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
                else if (MenuState == StateCopyForm) MenuState = StateBuild;
                else if (MenuState == StateTod || MenuState == StateRepair) MenuState = StateCheats;
                else if (MenuState == StateTemplates) MenuState = StateBuild;
                else if (MenuState == StatePlatforms || MenuState == StateHouses ||
                         MenuState == StateKitchens || MenuState == StateProcessing) MenuState = StateTemplates;
                else if (MenuState == StateStarterHouses) MenuState = StateHouses;
                else if (MenuState == StateStarterKitchens) MenuState = StateKitchens;
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

            SetActive(CheatsButton, admin && MenuState == StateAdmin);
            SetActive(TerrainButton, admin && MenuState == StateAdmin);
            SetActive(BuildButton, admin && MenuState == StateAdmin);

            SetActive(GodButton, admin && MenuState == StateCheats);
            SetActive(DebugModeButton, admin && MenuState == StateCheats);
            SetActive(TodButton, admin && MenuState == StateCheats);
            SetActive(RepairButton, admin && MenuState == StateCheats);

            SetActive(TodHint, admin && MenuState == StateTod);
            SetActive(TodInput, admin && MenuState == StateTod);
            SetActive(TodApplyButton, admin && MenuState == StateTod);

            SetActive(RepairHint, admin && MenuState == StateRepair);
            SetActive(RepairRadiusInput, admin && MenuState == StateRepair);
            SetActive(RepairApplyButton, admin && MenuState == StateRepair);

            SetActive(CopyButton, admin && MenuState == StateBuild);
            SetActive(PasteButton, admin && MenuState == StateBuild);
            SetActive(TemplatesButton, admin && MenuState == StateBuild);
            SetActive(SnapButton, admin && MenuState == StateBuild);
            SetActive(PlatformsCategoryButton, admin && MenuState == StateTemplates);
            SetActive(HousesCategoryButton, admin && MenuState == StateTemplates);
            SetActive(KitchensCategoryButton, admin && MenuState == StateTemplates);
            SetActive(ProcessingCategoryButton, admin && MenuState == StateTemplates);
            SetActive(SmelterHallButton, admin && MenuState == StateProcessing);
            SetActive(PlatformTemplateButton, admin && MenuState == StatePlatforms);
            SetActive(StarterHousesButton, admin && MenuState == StateHouses);
            SetActive(StarterHouse1Button, admin && MenuState == StateStarterHouses);
            SetActive(StarterKitchensButton, admin && MenuState == StateKitchens);
            SetActive(KitchenFullButton, admin && MenuState == StateStarterKitchens);

            SetActive(CopyHint, admin && MenuState == StateCopyForm);
            SetActive(CopyRadiusInput, admin && MenuState == StateCopyForm);
            SetActive(CopyApplyButton, admin && MenuState == StateCopyForm);

            SetActive(LevelCircleButton, admin && MenuState == StateTerrain);
            SetActive(LevelSquareButton, admin && MenuState == StateTerrain);

            SetActive(TerrainHint, admin && MenuState == StateTerrainForm);
            SetActive(RadiusInput, admin && MenuState == StateTerrainForm);
            SetActive(HeightInput, admin && MenuState == StateTerrainForm);
            SetActive(ApplyButton, admin && MenuState == StateTerrainForm);

            SetActive(BackButton, admin && MenuState >= StateTerrain);
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
