using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
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

        internal static GameObject FenceWalkwayButton;

        internal static GameObject FenceRoofButton;

        internal static GameObject FenceLevelButton;

        internal static GameObject FenceBuildButton;

        internal static GameObject FenceRemoveButton;

        private const string FencePrefab = "stake_wall";

        private const string FloorPrefab = "wood_floor";

        private const string RoofPrefab = "wood_roof";

        private const string PostPrefab = "wood_pole2";

        // The player's blueprints put stake_wall exactly 2.000 m apart.
        private const float StakeWidth = 2f;

        // «До 6 стен»: no side of the ring longer than six stakes, as in «30м.».
        private const int FenceMaxPerSide = 6;

        private const float FenceMinRadius = 4f;

        private const float FenceMaxRadius = 64f;

        // How far a stake goes into the ground below the lowest point under it, so a
        // stake across a slope leaves no daylight under its downhill end.
        private const float FenceSink = 0.3f;

        // Trees and rocks this close to the line go before the fence does: the stakes
        // alone, or the whole section, from the walkway's inner edge to the eave.
        private const float FenceClearRadius = 1.2f;

        private const float SectionClearRadius = 2.5f;

        // The band levelled along the line, each side of it, before the ground eases
        // back over the blend: under the stakes, or under all of a section.
        private const float FenceLevelHalf = 1.5f;

        private const float SectionLevelHalf = 3f;

        private const float FenceLevelBlend = 3f;

        // The rest of «Секция 6шт.», measured from its stakes' pivot, which sits at -0.5
        // there: the floor at 1.5 one metre inside the line, so its outer edge meets the
        // stakes; the roof's low row at 4.5 a metre outside, its high row at 5.5 a metre
        // in, so it falls away from the walkway; the posts at 0.5, 2.5 and 4.5, three
        // two-metre poles with their pivots in the middle, standing in the line itself.
        private const float WalkwayRise = 2f;

        private const float WalkwayInset = 1f;

        private const float RoofLowRise = 5f;

        private const float RoofHighRise = 6f;

        private const float RoofReach = 1f;

        private static readonly float[] PostRises = { 1f, 3f, 5f };

        // Where under a stake the ground is tried: both ends and the middle.
        private static readonly float[] StakeFeet = { -0.9f, 0f, 0.9f };

        internal static bool IsFenceWalkway;

        internal static bool IsFenceRoof;

        internal static bool IsFenceLevel;

        private static bool _fenceBuilding;

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

            FenceHint = MakeText(gui, "");

            FenceRadiusInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "расстояние, напр. 20", 16, 160f, 32f);
            AddFixedSize(FenceRadiusInput, 160f, 32f);

            FenceWalkwayButton = MakeButton(gui, "", () =>
            {
                IsFenceWalkway = !IsFenceWalkway;
                UpdateFenceLabels();
            });

            FenceRoofButton = MakeButton(gui, "", () =>
            {
                IsFenceRoof = !IsFenceRoof;
                UpdateFenceLabels();
            });

            FenceLevelButton = MakeButton(gui, "", () =>
            {
                IsFenceLevel = !IsFenceLevel;
                UpdateFenceLabels();
            });

            FenceBuildButton = MakeButton(gui, "Построить", () =>
            {
                if (_fenceBuilding) return;
                Instance?.StartCoroutine(BuildFence());
            });

            FenceRemoveButton = MakeButton(gui, "Убрать последний забор", () =>
            {
                RemoveLastFence();
                RefreshMenu();
            });

            UpdateFenceLabels();
        }

        internal static bool HasLastFence
        {
            get { return _lastFence != null && _lastFence.Count > 0; }
        }

        private static void UpdateFenceLabels()
        {
            SetLabel(FenceWalkwayButton, IsFenceWalkway ? "Помост: вкл" : "Помост: выкл");
            SetLabel(FenceRoofButton, IsFenceRoof ? "Крыша: вкл" : "Крыша: выкл");
            SetLabel(FenceLevelButton, IsFenceLevel ? "Выравнивать: вкл" : "Выравнивать: выкл");

            var label = FenceHint != null ? FenceHint.GetComponentInChildren<Text>(true) : null;
            if (label == null) return;

            var sections = IsFenceWalkway || IsFenceRoof;
            label.text = $"Частокол кольцом вокруг тебя.{NEWLINE}Расстояние — от тебя до стены,{NEWLINE}"
                         + "от 4 до 64 м."
                         + (IsFenceLevel
                             ? $"{NEWLINE}Землю под ним выровняет{NEWLINE}по высоте, где ты стоишь."
                             : sections
                                 ? $"{NEWLINE}На склоне помост и крыша лягут{NEWLINE}по нижнему колу стороны."
                                 : "")
                         + $"{NEWLINE}Деревья и камни на линии снесёт —{NEWLINE}их уже не вернуть.";
        }

        /// <summary>
        /// Rings the player with palisade at the asked distance, the way the player built
        /// «30м.» by hand - see Geometry.FenceRing - and, if asked, with the walkway and
        /// the roof of «Секция 6шт.» along every side.
        ///
        /// Levelling is settled first, because it can be turned down - ground not loaded
        /// yet, or not loaded at all - and a turned-down fence should leave nothing
        /// behind, not a cleared ring. Then the line is cleared by the road's own rules,
        /// the ground levelled to where the player stands, and a frame later, once the
        /// terrain a footing is read from is the new one, the fence goes up a side at a
        /// time. A side stands complete in the frame it appears - a fresh piece starts at
        /// full support, so that is all a roof needs - and the ring grows round rather
        /// than stalling the game on a thousand pieces at once.
        ///
        /// On its own a stake stands on the lowest ground under it, so the ring steps
        /// with a slope. With a walkway or a roof the floors and panels have to line up,
        /// so a whole side takes its lowest footing - no stake may float - and on uneven
        /// ground that is what levelling is for. Deep water, a no-build location or a
        /// building on a stake's place leaves that place out, walkway and roof with it.
        ///
        /// Levelled, the fence is part of the undo step the levelling records, so undo
        /// puts the ground back and takes the fence down together.
        /// </summary>
        private static IEnumerator BuildFence()
        {
            var player = Player.m_localPlayer;
            var scene = ZNetScene.instance;
            var zones = ZoneSystem.instance;
            if (player == null || scene == null || zones == null) yield break;

            // The page is only drawn for an admin; this is the check where it counts,
            // since the pieces are free and the clearing cannot be undone.
            if (!IsAdminUnlocked) yield break;

            var stakePrefab = FencePiecePrefab(scene, FencePrefab);
            if (stakePrefab == null) yield break;

            var floorPrefab = IsFenceWalkway ? FencePiecePrefab(scene, FloorPrefab) : null;
            var roofPrefab = IsFenceRoof ? FencePiecePrefab(scene, RoofPrefab) : null;
            var postPrefab = IsFenceRoof ? FencePiecePrefab(scene, PostPrefab) : null;
            var walkway = floorPrefab != null;
            var roof = roofPrefab != null && postPrefab != null;
            var sections = walkway || roof;

            var radius = Mathf.Clamp(ParseField(FenceRadiusInput, 20f), FenceMinRadius, FenceMaxRadius);
            var centre = player.transform.position;
            var plan = Geometry.FenceRing(radius, FenceMaxPerSide, StakeWidth);

            var levelHalf = sections ? SectionLevelHalf : FenceLevelHalf;
            var levelReach = levelHalf + FenceLevelBlend;
            List<TerrainComp> comps = null;
            List<Vector3> levelLine = null;
            if (IsFenceLevel)
            {
                levelLine = FenceLine(plan, centre, 1f);
                if (!WardsAllowStroke("fence", levelLine, levelReach))
                {
                    player.Message(MessageHud.MessageType.Center, "Кольцо задевает чужой оберег");
                    yield break;
                }

                comps = CompsForStamps(levelLine, levelReach, centre.y, out var unloaded);
                if (comps == null)
                {
                    player.Message(MessageHud.MessageType.Center, unloaded
                        ? "Кольцо выходит за прогруженную землю — уменьши расстояние"
                        : "Земля вокруг ещё прогружается — подожди пару секунд и нажми снова");
                    yield break;
                }
            }

            _fenceBuilding = true;
            try
            {
                var cleared = ClearAlongPath(FenceLine(plan, centre, 0.5f),
                                             sections ? SectionClearRadius : FenceClearRadius);

                TerrainUndoStep undo = null;
                if (comps != null)
                {
                    undo = RecordTerrainUndo("забор", comps, centre, radius + levelReach);

                    var flat = new List<Vec2>(levelLine.Count);
                    foreach (var point in levelLine) flat.Add(new Vec2(point.x, point.z));
                    var profile = new float[flat.Count];
                    for (var i = 0; i < profile.Length; i++) profile[i] = centre.y;

                    foreach (var comp in comps) LevelAlong(comp, flat, profile, levelHalf, FenceLevelBlend);

                    // Save gained an optional paintOnly in 1.0; reflection does not fill it.
                    var save = AccessTools.Method(typeof(TerrainComp), "Save");
                    foreach (var comp in comps) save.Invoke(comp, new object[] { false });
                    RebuildHeightmaps(centre, radius + levelReach + 16f);

                    yield return null;
                }

                var stakeLift = PivotAboveBase(stakePrefab);
                var creator = player.GetPlayerID();
                var platform = PlatformManager.DistributionPlatform.LocalUser.PlatformUserID;
                var panels = Geometry.SectionPanels(plan.PerSide);
                var posts = Geometry.SectionPosts(plan.PerSide);
                var sideLength = plan.PerSide * plan.Spacing;
                var built = new List<ZDOID>();
                var skipped = 0;

                for (var side = 0; side < plan.Sides; side++)
                {
                    if (ZNetScene.instance == null || Player.m_localPlayer == null) break;

                    var yaw = plan.Stakes[side * plan.PerSide].Yaw;
                    var turn = Quaternion.Euler(0f, yaw, 0f);
                    var angle = yaw * Mathf.Deg2Rad;
                    var outward = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
                    var along = new Vector3(Mathf.Cos(angle), 0f, -Mathf.Sin(angle));

                    var feet = new Vector3[plan.PerSide];
                    var standing = new bool[plan.PerSide];
                    var roomy = new bool[plan.PerSide];
                    var lowest = float.MaxValue;
                    for (var j = 0; j < plan.PerSide; j++)
                    {
                        var stake = plan.Stakes[side * plan.PerSide + j];
                        standing[j] = FindStakeFooting(centre, stake, out feet[j]);
                        if (!standing[j])
                        {
                            skipped++;
                            continue;
                        }

                        lowest = Mathf.Min(lowest, feet[j].y);
                        roomy[j] = sections && SectionClear(feet[j], stake.Yaw, walkway, roof);
                    }

                    if (lowest < float.MaxValue)
                    {
                        for (var j = 0; j < plan.PerSide; j++)
                        {
                            if (!standing[j]) continue;
                            var baseY = sections ? lowest : feet[j].y;
                            PlaceFencePiece(stakePrefab, new Vector3(feet[j].x, baseY + stakeLift, feet[j].z),
                                            turn, creator, platform, built);
                        }

                        if (sections)
                        {
                            var middle = new Vector3(centre.x, 0f, centre.z) + outward * radius;
                            var pivot = Vector3.up * (lowest + stakeLift);

                            for (var j = 0; j < panels.Length; j++)
                            {
                                if (!roomy[j]) continue;
                                var at = middle + along * panels[j] + pivot;

                                if (walkway)
                                    PlaceFencePiece(floorPrefab, at + Vector3.up * WalkwayRise - outward * WalkwayInset,
                                                    turn, creator, platform, built);

                                if (roof)
                                {
                                    PlaceFencePiece(roofPrefab, at + Vector3.up * RoofLowRise + outward * RoofReach,
                                                    turn, creator, platform, built);
                                    PlaceFencePiece(roofPrefab, at + Vector3.up * RoofHighRise - outward * RoofReach,
                                                    turn, creator, platform, built);
                                }
                            }

                            if (roof)
                            {
                                foreach (var post in posts)
                                {
                                    // A post goes with the stake whose place it stands in.
                                    var slot = Mathf.Clamp(
                                        Mathf.RoundToInt((post + sideLength * 0.5f) / plan.Spacing - 0.5f),
                                        0, plan.PerSide - 1);
                                    if (!roomy[slot]) continue;

                                    foreach (var rise in PostRises)
                                        PlaceFencePiece(postPrefab, middle + along * post + pivot + Vector3.up * rise,
                                                        turn, creator, platform, built);
                                }
                            }
                        }
                    }

                    yield return null;
                }

                _lastFence = built;
                if (undo != null) undo.Pieces = built;

                var parts = "частокол" + (walkway ? ", помост" : "") + (roof ? ", крыша" : "");
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    $"Забор {radius:0.#} м ({parts}): деталей {built.Count}"
                    + (undo != null ? ", выровнен" : "")
                    + (cleared > 0 ? $", снесено: {cleared}" : "")
                    + (skipped > 0 ? $", мест пропущено: {skipped}" : ""));
                Log.LogInfo($"[AstvardServerMod] Fence r={radius:F1} sides={plan.Sides} perSide={plan.PerSide} " +
                            $"spacing={plan.Spacing:F2} walkway={walkway} roof={roof} levelled={undo != null} " +
                            $"pieces={built.Count} skipped={skipped} cleared={cleared} at {centre}");
            }
            finally
            {
                _fenceBuilding = false;
                RefreshMenu();
            }
        }

        private static GameObject FencePiecePrefab(ZNetScene scene, string name)
        {
            var prefab = scene.GetPrefab(name);
            if (prefab == null) Log.LogWarning($"[AstvardServerMod] No fence prefab '{name}'.");
            return prefab;
        }

        private static void PlaceFencePiece(GameObject prefab, Vector3 at, Quaternion turn, long creator,
                                            PlatformUserID platform, List<ZDOID> built)
        {
            var go = Instantiate(prefab, at, turn);
            var piece = go.GetComponent<Piece>();
            if (piece != null) piece.SetCreator(creator, platform);

            var view = go.GetComponent<ZNetView>();
            if (view != null && view.IsValid()) built.Add(view.GetZDO().m_uid);
        }

        /// <summary>
        /// Where one stake stands: under its middle, as low as the ground gets anywhere
        /// under it and a little below that. No footing in deep water - shallow water at
        /// a shore is fine, a ring round a lakeside base should not open onto the lake -
        /// nor in a no-build location, nor inside a building already standing on the line.
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

            return !BuiltWithin(footing, stake.Yaw, -0.25f, 0.25f, 2.5f);
        }

        /// <summary>
        /// Whether the walkway and roof over a stake have room: nothing built where the
        /// floor goes inside the line, nor where the roof and its eave go over both. A
        /// house against the wall inside loses its stretch of walkway, not its stake -
        /// the ring stays shut either way.
        /// </summary>
        private static bool SectionClear(Vector3 footing, float yaw, bool walkway, bool roof)
        {
            return !BuiltWithin(footing, yaw, walkway || roof ? -2f : -0.25f, roof ? 2f : 0.25f,
                                roof ? 6.5f : 2.5f);
        }

        /// <summary>
        /// Whether any built piece stands in a box over a stake's footing: half a metre
        /// short of the stake's own width along the line, from <paramref name="inner"/>
        /// to <paramref name="outer"/> across it, and from half a metre over the lowest
        /// ground under it up to <paramref name="top"/>.
        /// </summary>
        private static bool BuiltWithin(Vector3 footing, float yaw, float inner, float outer, float top)
        {
            var turn = Quaternion.Euler(0f, yaw, 0f);
            var box = footing + Vector3.up * (FenceSink + (0.5f + top) * 0.5f)
                      + turn * new Vector3(0f, 0f, (inner + outer) * 0.5f);

            return Physics.CheckBox(box, new Vector3(0.8f, (top - 0.5f) * 0.5f, (outer - inner) * 0.5f),
                                    turn, PieceLayer, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// The ring's line, corner to corner, a point every <paramref name="step"/> metres
        /// and closed back onto its first point, so a distance measured to it has no seam.
        /// </summary>
        private static List<Vector3> FenceLine(FencePlan plan, Vector3 centre, float step)
        {
            var line = new List<Vector3>();
            var count = plan.Corners.Count;
            for (var i = 0; i < count; i++)
            {
                var a = plan.Corners[i];
                var b = plan.Corners[(i + 1) % count];
                var dx = b.X - a.X;
                var dz = b.Z - a.Z;
                var steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(dx * dx + dz * dz) / step));
                for (var s = 0; s < steps; s++)
                {
                    var t = (float)s / steps;
                    line.Add(new Vector3(centre.x + a.X + dx * t, centre.y, centre.z + a.Z + dz * t));
                }
            }

            if (line.Count > 0) line.Add(line[0]);
            return line;
        }

        /// <summary>
        /// Takes the last ring back down - every piece of it, not what was cleared for it
        /// and not the ground under it; undo is what gives the ground back. As with undo,
        /// only what is still loaded here can go.
        /// </summary>
        private static void RemoveLastFence()
        {
            if (!HasLastFence) return;

            var total = _lastFence.Count;
            var removed = RemovePieces(_lastFence);
            _lastFence = null;

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                removed == total
                    ? $"Забор убран: деталей {removed}"
                    : $"Убрано деталей {removed} из {total}, остальные выгружены");
            Log.LogInfo($"[AstvardServerMod] Fence removed: {removed} of {total} pieces.");
        }
    }
}
