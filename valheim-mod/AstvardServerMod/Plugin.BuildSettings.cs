using System.Collections.Generic;
using Jotunn.Managers;
using UnityEngine;

namespace AstvardServerMod
{
    /// <summary>
    /// «Постройки» → «Настройки»: how builds go down - snapping, levelling the ground under
    /// them, clearing what stands in their way, how far ahead the projection floats. Kept
    /// behind one button so the page itself holds only what gets built.
    /// </summary>
    public partial class Plugin
    {
        internal static GameObject BuildSettingsButton;

        internal static GameObject BuildSettingsHint;

        internal static GameObject BuildClearButton;

        internal static GameObject PlayerSettingsButton;

        /// <summary>
        /// Whether a build clears what stands where it goes - trees, stumps, logs, bare rocks,
        /// brush - the way a road clears its band. Off by default: nothing brings back what
        /// it takes, «Отменить постройку» included.
        /// </summary>
        internal static bool IsBuildClearing;

        // How far past a piece's own colliders the ground under it counts as its place: a
        // trunk standing flush against a wall is in the way of it.
        private const float BuildClearMargin = 0.3f;

        /// <summary>Clearing as it will actually happen: switched on, and open to whoever is building.</summary>
        private static bool BuildClearingActive
        {
            get { return IsBuildClearing && (IsAdminUnlocked || RuleAllows("clear")); }
        }

        private void CreateBuildSettingsWidgets(GUIManager gui)
        {
            BuildSettingsButton = MakeButton(gui, "Настройки", OpenBuildSettings);

            BuildSettingsHint = MakeText(gui, "");
        }

        private void CreateBuildClearWidget(GUIManager gui)
        {
            BuildClearButton = MakeButton(gui, "", () =>
            {
                IsBuildClearing = !IsBuildClearing;
                UpdateBuildClearButtonLabel();
                UpdateBuildSettingsHint();
                Log.LogInfo($"[AstvardServerMod] Build clearing: {IsBuildClearing}");
            });
            UpdateBuildClearButtonLabel();
        }

        private static void OpenBuildSettings()
        {
            MenuState = StateBuildSettings;
            RefreshMenu();
        }

        private static void UpdateBuildClearButtonLabel()
        {
            SetLabel(BuildClearButton, IsBuildClearing ? "Разрушать: вкл" : "Разрушать: выкл");
        }

        /// <summary>A line for each switch the page shows, and nothing about the ones it does not.</summary>
        private static void UpdateBuildSettingsHint()
        {
            var label = BuildSettingsHint != null ? BuildSettingsHint.GetComponentInChildren<UnityEngine.UI.Text>(true) : null;
            if (label == null) return;

            var admin = IsAdminUnlocked;
            var text = "Как ставятся постройки.";
            if (admin || RuleAllows("snap"))
                text += $"{NEWLINE}Прилипание — к деталям рядом.";
            if (admin)
                text += $"{NEWLINE}Выравнивание — земля под ней.";
            if (admin || RuleAllows("clear"))
                text += $"{NEWLINE}Разрушать — деревья и камни{NEWLINE}под ней; «Отменить постройку»{NEWLINE}их не вернёт.";
            if (admin)
                text += $"{NEWLINE}Поле — как далеко висит{NEWLINE}проекция, м.";
            label.text = text;
        }

        private static void RefreshBuildSettingsVisibility(bool admin)
        {
            var page = MenuState == StateBuildSettings;
            var snap = admin || RuleAllows("snap");
            var clear = admin || RuleAllows("clear");

            SetActive(BuildSettingsButton, admin && MenuState == StateBuild);
            SetActive(PlayerSettingsButton, !admin && MenuState == StatePlayerBuild && (snap || clear));

            if (page) UpdateBuildSettingsHint();
            SetActive(BuildSettingsHint, page && (admin || snap || clear));
            SetActive(SnapButton, page && snap);
            SetActive(LevelGroundButton, admin && page);
            SetActive(BuildClearButton, page && clear);
            SetActive(PlacementDistanceInput, admin && page);
        }

        /// <summary>
        /// Where one piece of a build stands on the ground, as the clearing sees it: the flat
        /// box of its colliders in its own frame, a little wider, and the height it spans.
        /// </summary>
        private struct Footprint
        {
            public Vector3 At;

            public Quaternion Unturn;

            public float MinX;

            public float MaxX;

            public float MinZ;

            public float MaxZ;

            public float Low;

            public float High;
        }

        /// <summary>
        /// Clears the ground a build is about to stand on: whatever the clearing takes, where
        /// it meets one of the pieces - only there, so a tree beside the house stays. The
        /// same judgement as a road's (<see cref="ClearWhere"/>), with the same regard for other
        /// players' wards. Asked before the first piece goes up.
        /// </summary>
        private static int ClearUnderPieces(List<PiecePlacement> placements)
        {
            if (placements == null || placements.Count == 0) return 0;

            var prints = new List<Footprint>(placements.Count);
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var placement in placements)
            {
                if (placement.Prefab == null) continue;

                var span = MeasurePiece(placement.Prefab);
                // Flat: the game turns pieces about the vertical only, and a box leaning with
                // the odd copied one would not stand on the ground any differently.
                var yaw = Quaternion.Euler(0f, placement.Turn.eulerAngles.y, 0f);
                prints.Add(new Footprint
                {
                    At = placement.At,
                    Unturn = Quaternion.Inverse(yaw),
                    MinX = span.MinX - BuildClearMargin,
                    MaxX = span.MaxX + BuildClearMargin,
                    MinZ = span.MinZ - BuildClearMargin,
                    MaxZ = span.MaxZ + BuildClearMargin,
                    // Half a metre either way: a tree whose trunk ends just under a raised
                    // floor, or a rock just over a low wall, is still in the way.
                    Low = placement.At.y - span.Below - 0.5f,
                    High = placement.At.y + span.Above + 0.5f,
                });

                // Round the pivot, as far as the box can reach turned any way.
                var reachX = Mathf.Max(Mathf.Abs(span.MinX), Mathf.Abs(span.MaxX)) + BuildClearMargin;
                var reachZ = Mathf.Max(Mathf.Abs(span.MinZ), Mathf.Abs(span.MaxZ)) + BuildClearMargin;
                var reach = Mathf.Sqrt(reachX * reachX + reachZ * reachZ);
                minX = Mathf.Min(minX, placement.At.x - reach);
                maxX = Mathf.Max(maxX, placement.At.x + reach);
                minZ = Mathf.Min(minZ, placement.At.z - reach);
                maxZ = Mathf.Max(maxZ, placement.At.z + reach);
            }

            if (prints.Count == 0) return 0;

            // Past the pieces' own box by the clearing's slack: a boulder's middle can stand
            // well outside the place it pushes into.
            var area = Rect.MinMaxRect(minX - ClearSlack, minZ - ClearSlack, maxX + ClearSlack, maxZ + ClearSlack);
            return ClearWhere(area, go => UnderAnyPiece(go, prints), IsAdminUnlocked);
        }

        /// <summary>Whether any solid part of a thing reaches into the place of any piece.</summary>
        private static bool UnderAnyPiece(GameObject go, List<Footprint> prints)
        {
            var solid = false;
            foreach (var col in go.GetComponentsInChildren<Collider>())
            {
                if (col == null || !col.enabled || col.isTrigger) continue;
                solid = true;
                if (MeetsAny(col.bounds, prints)) return true;
            }

            return !solid && MeetsAny(new Bounds(go.transform.position, Vector3.zero), prints);
        }

        /// <summary>
        /// A box against the pieces' places: its four flat corners turned into each piece's
        /// own frame, and the box round them tried against the piece's. That is generous at
        /// an angle - never short - which for "is it in the way" is the right side to err on.
        /// </summary>
        private static bool MeetsAny(Bounds box, List<Footprint> prints)
        {
            foreach (var print in prints)
            {
                if (box.max.y < print.Low || box.min.y > print.High) continue;

                float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
                for (var corner = 0; corner < 4; corner++)
                {
                    var x = (corner & 1) == 0 ? box.min.x : box.max.x;
                    var z = (corner & 2) == 0 ? box.min.z : box.max.z;
                    var local = print.Unturn * new Vector3(x - print.At.x, 0f, z - print.At.z);
                    x0 = Mathf.Min(x0, local.x);
                    x1 = Mathf.Max(x1, local.x);
                    z0 = Mathf.Min(z0, local.z);
                    z1 = Mathf.Max(z1, local.z);
                }

                if (x1 < print.MinX || x0 > print.MaxX || z1 < print.MinZ || z0 > print.MaxZ) continue;
                return true;
            }

            return false;
        }
    }
}
