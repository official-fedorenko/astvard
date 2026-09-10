using System.Collections.Generic;
using Jotunn.Managers;
using Splatform;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        internal static GameObject FenceButton;

        internal static GameObject FenceHint;

        internal static GameObject FenceRadiusInput;

        internal static GameObject FenceBuildButton;

        internal static GameObject FenceRemoveButton;

        private const string FencePrefab = "stake_wall";

        // The player's blueprints put stake_wall exactly 2.000 m apart.
        private const float StakeWidth = 2f;

        // «До 6 стен»: no side of the ring longer than six stakes, as in «30м.».
        private const int FenceMaxPerSide = 6;

        private const float FenceMinRadius = 4f;

        private const float FenceMaxRadius = 64f;

        // How far a stake goes into the ground below the lowest point under it, so a
        // stake across a slope leaves no daylight under its downhill end.
        private const float FenceSink = 0.3f;

        // Trees and rocks this close to the line of stakes go before the stakes do.
        private const float FenceClearRadius = 1.2f;

        // Where under a stake the ground is tried: both ends and the middle.
        private static readonly float[] StakeFeet = { -0.9f, 0f, 0.9f };

        private static List<ZDOID> _lastFence;

        private static int? _pieceLayer;

        /// <summary>Built pieces only - the ring must not stand inside somebody's house.</summary>
        private static int PieceLayer
        {
            get
            {
                if (_pieceLayer == null) _pieceLayer = LayerMask.GetMask("piece");
                return _pieceLayer.Value;
            }
        }

        private void CreateFenceWidgets(GUIManager gui)
        {
            FenceButton = MakeButton(gui, "Обнести забором", () =>
            {
                MenuState = StateFence;
                RefreshMenu();
            });

            FenceHint = MakeText(gui,
                $"Частокол кольцом вокруг тебя.{NEWLINE}Расстояние — от тебя до стены,{NEWLINE}"
                + $"от 4 до 64 м.{NEWLINE}Деревья и камни на линии снесёт —{NEWLINE}их уже не вернуть.");

            FenceRadiusInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "расстояние, напр. 20", 16, 160f, 32f);
            AddFixedSize(FenceRadiusInput, 160f, 32f);

            FenceBuildButton = MakeButton(gui, "Построить", () =>
            {
                BuildFence();
                RefreshMenu();
            });

            FenceRemoveButton = MakeButton(gui, "Убрать последний забор", () =>
            {
                RemoveLastFence();
                RefreshMenu();
            });
        }

        internal static bool HasLastFence
        {
            get { return _lastFence != null && _lastFence.Count > 0; }
        }

        /// <summary>
        /// Rings the player with palisade: stakes all the way round, at the asked
        /// distance, the way the player built «30м.» by hand - see Geometry.FenceRing.
        ///
        /// The line is cleared first, by the same rules a road clears by: trees, stumps,
        /// logs and bare rock go, ore, pieces and locations stay. Then every stake's
        /// footing is found before any goes up, and they all go up in one pass.
        ///
        /// A stake stands on the lowest ground under it and a little into it, so on a
        /// slope the ring steps with the land and nothing can crawl under. Deep water, a
        /// no-build location or somebody's building on the line leaves that stake out;
        /// the message says how many.
        /// </summary>
        private static void BuildFence()
        {
            var player = Player.m_localPlayer;
            var scene = ZNetScene.instance;
            var zones = ZoneSystem.instance;
            if (player == null || scene == null || zones == null) return;

            // The page is only drawn for an admin; this is the check where it counts,
            // since the stakes are free and the clearing cannot be undone.
            if (!IsAdminUnlocked) return;

            var prefab = scene.GetPrefab(FencePrefab);
            if (prefab == null)
            {
                Log.LogWarning($"[AstvardServerMod] No fence prefab '{FencePrefab}'.");
                return;
            }

            var radius = Mathf.Clamp(ParseField(FenceRadiusInput, 20f), FenceMinRadius, FenceMaxRadius);
            var centre = player.transform.position;
            var plan = Geometry.FenceRing(radius, FenceMaxPerSide, StakeWidth);

            var cleared = ClearAlongPath(FenceLine(plan, centre), FenceClearRadius);

            var footings = new List<Vector3>();
            var turns = new List<Quaternion>();
            var skipped = 0;
            foreach (var stake in plan.Stakes)
            {
                if (FindStakeFooting(centre, stake, out var footing))
                {
                    footings.Add(footing);
                    turns.Add(Quaternion.Euler(0f, stake.Yaw, 0f));
                }
                else
                {
                    skipped++;
                }
            }

            var lift = PivotAboveBase(prefab);
            var creator = player.GetPlayerID();
            var platform = PlatformManager.DistributionPlatform.LocalUser.PlatformUserID;
            var built = new List<ZDOID>();

            for (var i = 0; i < footings.Count; i++)
            {
                var go = Instantiate(prefab, footings[i] + Vector3.up * lift, turns[i]);
                var piece = go.GetComponent<Piece>();
                if (piece != null) piece.SetCreator(creator, platform);

                var view = go.GetComponent<ZNetView>();
                if (view != null && view.IsValid()) built.Add(view.GetZDO().m_uid);
            }

            _lastFence = built;

            player.Message(MessageHud.MessageType.Center,
                $"Забор {radius:0.#} м: кольев {built.Count}"
                + (cleared > 0 ? $", снесено: {cleared}" : "")
                + (skipped > 0 ? $", не встало: {skipped}" : ""));
            Log.LogInfo($"[AstvardServerMod] Fence r={radius:F1} sides={plan.Sides} perSide={plan.PerSide} " +
                        $"spacing={plan.Spacing:F2} stakes={plan.Stakes.Count} built={built.Count} " +
                        $"skipped={skipped} cleared={cleared} at {centre}");
        }

        /// <summary>
        /// Where one stake stands: under its middle, as low as the ground gets anywhere
        /// under it and a little below that. No footing in deep water - shallow water at
        /// a shore is fine, a ring round a lakeside base should not open onto the lake -
        /// nor in a no-build location, nor inside a piece already standing there.
        /// </summary>
        private static bool FindStakeFooting(Vector3 centre, Stake stake, out Vector3 footing)
        {
            footing = Vector3.zero;
            var zones = ZoneSystem.instance;

            var yaw = stake.Yaw * Mathf.Deg2Rad;
            // Along the wall is a quarter turn clockwise from its outward face.
            var along = new Vector3(Mathf.Cos(yaw), 0f, -Mathf.Sin(yaw));
            var middle = new Vector3(centre.x + stake.At.X, 0f, centre.z + stake.At.Z);

            var lowest = float.MaxValue;
            var underMiddle = 0f;
            foreach (var foot in StakeFeet)
            {
                if (!zones.GetGroundHeight(middle + along * foot, out var ground)) return false;
                lowest = Mathf.Min(lowest, ground);
                if (foot == 0f) underMiddle = ground;
            }

            if (underMiddle < zones.m_waterLevel - 1f) return false;

            footing = new Vector3(middle.x, lowest - FenceSink, middle.z);
            if (Location.IsInsideNoBuildLocation(footing)) return false;

            // Somebody's walls on the line: better a gap they close than a stake through them.
            return !Physics.CheckBox(footing + Vector3.up * (FenceSink + 1.5f),
                                     new Vector3(0.8f, 1f, 0.25f), Quaternion.Euler(0f, stake.Yaw, 0f),
                                     PieceLayer, QueryTriggerInteraction.Ignore);
        }

        /// <summary>The ring's line, corner to corner, a point every half metre for the clearing.</summary>
        private static List<Vector3> FenceLine(FencePlan plan, Vector3 centre)
        {
            var line = new List<Vector3>();
            var count = plan.Corners.Count;
            for (var i = 0; i < count; i++)
            {
                var a = plan.Corners[i];
                var b = plan.Corners[(i + 1) % count];
                var dx = b.X - a.X;
                var dz = b.Z - a.Z;
                var steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(dx * dx + dz * dz) / 0.5f));
                for (var s = 0; s < steps; s++)
                {
                    var t = (float)s / steps;
                    line.Add(new Vector3(centre.x + a.X + dx * t, centre.y, centre.z + a.Z + dz * t));
                }
            }

            return line;
        }

        /// <summary>
        /// Takes the last ring back down - the stakes, not what was cleared for them. As
        /// with undo, only what is still loaded here can go.
        /// </summary>
        private static void RemoveLastFence()
        {
            if (!HasLastFence) return;

            var total = _lastFence.Count;
            var removed = RemovePieces(_lastFence);
            _lastFence = null;

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                removed == total
                    ? $"Забор убран: кольев {removed}"
                    : $"Убрано кольев {removed} из {total}, остальные выгружены");
            Log.LogInfo($"[AstvardServerMod] Fence removed: {removed} of {total} stakes.");
        }
    }
}
