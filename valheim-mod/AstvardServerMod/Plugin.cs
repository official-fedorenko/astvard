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

        internal static bool IsAdminUnlocked;
        internal static bool IsInfoShown;
        internal static int MenuState = StateRoot;
        private static bool _terrainSquare;

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

            ActivateButton = MakeButton(gui, "Активировать", () =>
            {
                MenuState = StateAdmin;
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

            BackButton = MakeButton(gui, "Назад", () =>
            {
                MenuState = MenuState == StateTerrainForm ? StateTerrain : StateAdmin;
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

            SetActive(GodButton, admin && MenuState == StateAdmin);
            SetActive(DebugModeButton, admin && MenuState == StateAdmin);
            SetActive(TerrainButton, admin && MenuState == StateAdmin);

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
