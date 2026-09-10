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

        internal static GameObject FenceCancelButton;

        internal static GameObject FencePinButton;

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

        // As the bridge's projection is tinted: the pieces themselves, see-through.
        private static readonly Color FenceGhostTint = new Color(1f, 1f, 1f, 0.5f);

        internal static bool IsFenceWalkway;

        internal static bool IsFenceRoof;

        internal static bool IsFenceLevel;

        private static bool _fenceBuilding;

        private static bool _fencePreviewing;

        // Pinned, the projection stays where it was left instead of following the player,
        // who can walk round it and nudge it with the arrows before building.
        private static bool _fencePinned;

        private static Vector3 _fencePinnedAt;


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

            FenceBuildButton = MakeButton(gui, "Поставить", StartFencePreview);

            FenceCancelButton = MakeButton(gui, "Отменить", () =>
            {
                CancelFencePreview();
                RefreshMenu();
            });

            FencePinButton = MakeButton(gui, "", () =>
            {
                ToggleFencePin();
                RefreshMenu();
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

        /// <summary>A projection of the fence is following the player, waiting for a click.</summary>
        internal static bool IsFencePreviewing
        {
            get { return _fencePreviewing; }
        }

        private static void UpdateFenceLabels()
        {
            SetLabel(FencePinButton, _fencePinned ? "Открепить проекцию" : "Закрепить проекцию");
            SetLabel(FenceWalkwayButton, IsFenceWalkway ? "Помост: вкл" : "Помост: выкл");
            SetLabel(FenceRoofButton, IsFenceRoof ? "Крыша: вкл" : "Крыша: выкл");
            SetLabel(FenceLevelButton, IsFenceLevel ? "Выравнивать: вкл" : "Выравнивать: выкл");

            var label = FenceHint != null ? FenceHint.GetComponentInChildren<Text>(true) : null;
            if (label == null) return;

            var sections = IsFenceWalkway || IsFenceRoof;
            label.text = $"Частокол кольцом вокруг тебя.{NEWLINE}Расстояние — от тебя до стены,{NEWLINE}"
                         + $"от 4 до 64 м. «Поставить» покажет{NEWLINE}проекцию: ЛКМ — построить,{NEWLINE}"
                         + $"Esc — отменить. P — закрепить её{NEWLINE}на месте, стрелки — сдвинуть."
                         + (IsFenceLevel
                             ? $"{NEWLINE}Землю под ним выровняет{NEWLINE}по земле в середине кольца."
                             : sections
                                 ? $"{NEWLINE}На склоне помост и крыша лягут{NEWLINE}по нижнему колу стороны."
                                 : "")
                         + $"{NEWLINE}Деревья и камни на линии снесёт —{NEWLINE}их уже не вернуть.";
        }

        // ---------------- projection ----------------

        // Ghosts by prefab name, kept for the life of a projection and handed out again
        // on every fresh look: a walk round the ring moves them rather than making new
        // ones, which at a thousand pieces is the difference between smooth and not.
        private static readonly Dictionary<string, List<GameObject>> FenceGhostPool =
            new Dictionary<string, List<GameObject>>();

        private static readonly Dictionary<Material, Material> FenceGhostTints = new Dictionary<Material, Material>();

        private static readonly List<FencePlacement> FenceShown = new List<FencePlacement>();

        private static readonly HashSet<string> FenceWarned = new HashSet<string>();

        private static string _fenceGhostKey;

        private static float _fenceLookedAt;

        private static Vector3 _fenceLookedFrom;

        /// <summary>
        /// Shows the ring about to be built, round the player and following them, as the
        /// pieces themselves. Nothing is built until the click; Esc takes it away.
        /// </summary>
        private static void StartFencePreview()
        {
            if (_fenceBuilding || Player.m_localPlayer == null || !IsAdminUnlocked) return;

            _fencePreviewing = true;
            _fencePinned = false;
            _fenceGhostKey = null;
            UpdateFenceLabels();

            // The press that opened the projection is still going down; without a deaf
            // moment it would carry on into the world and build straight away.
            NoteToolStart();
            InventoryGui.instance?.Hide();
            Player.m_localPlayer.Message(MessageHud.MessageType.Center,
                "ЛКМ — построить, Esc — отменить, P — закрепить");
        }

        /// <summary>
        /// Leaves the projection where it stands, to be walked round and nudged, or hands
        /// it back to the player. Pinned where the player is standing, so it does not
        /// jump; unpinned, it goes back round the player at once.
        /// </summary>
        private static void ToggleFencePin()
        {
            var player = Player.m_localPlayer;
            if (!_fencePreviewing || player == null) return;

            _fencePinned = !_fencePinned;
            if (_fencePinned) _fencePinnedAt = _fenceLookedFrom != Vector3.zero ? _fenceLookedFrom : player.transform.position;
            _fenceGhostKey = null;
            UpdateFenceLabels();

            SayPinned(_fencePinned);
        }

        private static void CancelFencePreview()
        {
            if (!_fencePreviewing) return;

            _fencePreviewing = false;
            _fencePinned = false;
            ClearFenceGhost();
            UpdateFenceLabels();
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Отменено");
        }

        /// <summary>
        /// Keeps the projection on the player. It is looked at afresh - footings, the
        /// water, anything built in the way - when the player has moved, when a setting
        /// on the page has changed, and once a second regardless; the same judging and
        /// the same layout as the build, so what the click puts up is what was shown.
        /// Open the panel and change the distance or a switch, and the projection changes
        /// with it behind the panel.
        ///
        /// With levelling on the ground is shown as it will be, flat at the height of the
        /// ground in the ring's middle, since that is where the build will find it.
        /// </summary>
        internal static void UpdateFencePreview()
        {
            if (!_fencePreviewing)
            {
                if (FenceShown.Count > 0 || FenceGhostPool.Count > 0) ClearFenceGhost();
                return;
            }

            var player = Player.m_localPlayer;
            var scene = ZNetScene.instance;
            if (player == null || scene == null || ZoneSystem.instance == null)
            {
                _fencePreviewing = false;
                ClearFenceGhost();
                return;
            }

            var radius = Mathf.Clamp(ParseField(FenceRadiusInput, 20f), FenceMinRadius, FenceMaxRadius);
            var key = $"{radius:F2} {IsFenceWalkway} {IsFenceRoof} {IsFenceLevel}";
            var centre = _fencePinned ? _fencePinnedAt : player.transform.position;

            // A big ring with a roof is a thousand pieces to move and as many questions
            // to ask the physics; it can follow a walking player a little less eagerly.
            var changed = key != _fenceGhostKey;
            var moved = (centre - _fenceLookedFrom).sqrMagnitude > 0.0625f
                        && Time.time - _fenceLookedAt > (FenceShown.Count > 300 ? 0.25f : 0.1f);
            if (!changed && !moved && Time.time - _fenceLookedAt < 1f) return;

            _fenceGhostKey = key;
            _fenceLookedAt = Time.time;
            _fenceLookedFrom = centre;

            var kit = FenceKitFor(scene);
            if (kit == null) return;

            var plan = Geometry.FenceRing(radius, FenceMaxPerSide, StakeWidth);
            var panels = Geometry.SectionPanels(plan.PerSide);
            var posts = Geometry.SectionPosts(plan.PerSide);
            float? flat = IsFenceLevel ? FenceLevelHeight(centre) : (float?)null;
            var stakeLift = PivotAboveBase(kit.Stake);

            var placements = new List<FencePlacement>();
            for (var side = 0; side < plan.Sides; side++)
            {
                var fs = JudgeFenceSide(plan, side, centre, radius, panels, posts, kit.Walkway, kit.Roofed, flat);
                LayOutSide(fs, plan, kit, stakeLift, panels, posts, placements);
            }

            ShowFenceGhost(placements);
        }

        /// <summary>
        /// The two keys a projection answers, read before anything else in the frame:
        /// Esc takes it away, a click builds it where it stands. A click on the panel is
        /// not a click in the world, and neither is one typed into the chat.
        /// </summary>
        internal static bool HandleFencePreviewInput()
        {
            if (!_fencePreviewing) return false;
            if (InventoryGui.IsVisible() || Chat.instance?.HasFocus() == true) return false;

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                NoteEscapeUsed();
                CancelFencePreview();
                RefreshMenu();
                return true;
            }

            if (Input.GetKeyDown(PinKey))
            {
                ToggleFencePin();
                return true;
            }

            // Levelling follows a nudged ring: the height it works to is the ground
            // wherever the ring's middle has been moved to.
            if (_fencePinned && PinNudgeThisFrame(out var step))
            {
                _fencePinnedAt += step;
                _fenceGhostKey = null;
                return true;
            }

            if (Input.GetMouseButtonDown(0) && Time.time - _toolMarkedAt > MarkDeafSeconds)
            {
                // The same window a placed blueprint uses: this click must not also be a
                // swing, and the game's own input can still run later in the same frame.
                _inputHeldUntil = Time.time + 0.3f;
                if (_fenceBuilding) return true;

                var centre = _fencePinned ? _fencePinnedAt : Player.m_localPlayer.transform.position;
                _fencePreviewing = false;
                _fencePinned = false;
                ClearFenceGhost();
                UpdateFenceLabels();
                Instance?.StartCoroutine(BuildFence(centre));
                return true;
            }

            return false;
        }

        private static void ShowFenceGhost(List<FencePlacement> placements)
        {
            var used = new Dictionary<string, int>();
            foreach (var placement in placements)
            {
                var name = placement.Prefab.name;
                if (!FenceGhostPool.TryGetValue(name, out var pool))
                {
                    pool = new List<GameObject>();
                    FenceGhostPool[name] = pool;
                }

                used.TryGetValue(name, out var next);

                // A ghost can go with its scene; a fresh one takes its place.
                if (next < pool.Count && pool[next] == null) pool[next] = MakeFenceGhost(placement.Prefab);
                if (next == pool.Count) pool.Add(MakeFenceGhost(placement.Prefab));

                var ghost = pool[next];
                used[name] = next + 1;
                if (ghost == null) continue;

                ghost.transform.SetPositionAndRotation(placement.At, placement.Turn);
                if (!ghost.activeSelf) ghost.SetActive(true);
            }

            foreach (var entry in FenceGhostPool)
            {
                used.TryGetValue(entry.Key, out var shown);
                for (var i = shown; i < entry.Value.Count; i++)
                    if (entry.Value[i] != null && entry.Value[i].activeSelf) entry.Value[i].SetActive(false);
            }

            FenceShown.Clear();
            FenceShown.AddRange(placements);
        }

        /// <summary>
        /// One piece of the projection: the real prefab, made without joining the world,
        /// with nothing in it left to collide or to run, and see-through. The tinted
        /// materials are shared - one copy for each material the fence's four prefabs use
        /// - rather than a copy for every ghost.
        /// </summary>
        private static GameObject MakeFenceGhost(GameObject prefab)
        {
            ZNetView.m_forceDisableInit = true;
            try
            {
                var ghost = Instantiate(prefab);

                foreach (var collider in ghost.GetComponentsInChildren<Collider>())
                    collider.enabled = false;
                foreach (var behaviour in ghost.GetComponentsInChildren<MonoBehaviour>())
                    behaviour.enabled = false;

                foreach (var renderer in ghost.GetComponentsInChildren<Renderer>())
                {
                    var materials = renderer.sharedMaterials;
                    for (var i = 0; i < materials.Length; i++)
                    {
                        var original = materials[i];
                        if (original == null) continue;

                        if (!FenceGhostTints.TryGetValue(original, out var tinted))
                        {
                            tinted = new Material(original);
                            if (tinted.HasProperty("_Color")) tinted.color = FenceGhostTint;
                            FenceGhostTints[original] = tinted;
                        }

                        materials[i] = tinted;
                    }

                    renderer.sharedMaterials = materials;
                }

                return ghost;
            }
            finally
            {
                ZNetView.m_forceDisableInit = false;
            }
        }

        private static void ClearFenceGhost()
        {
            foreach (var pool in FenceGhostPool.Values)
                foreach (var ghost in pool)
                    if (ghost != null) Destroy(ghost);
            FenceGhostPool.Clear();

            foreach (var tinted in FenceGhostTints.Values)
                if (tinted != null) Destroy(tinted);
            FenceGhostTints.Clear();

            FenceShown.Clear();
            _fenceGhostKey = null;
        }

        // ---------------- building ----------------

        /// <summary>The prefabs a ring is built from, as the switches on the page ask for.</summary>
        private sealed class FenceKit
        {
            public GameObject Stake;

            public GameObject Floor;

            public GameObject Roof;

            public GameObject Post;

            public bool Walkway
            {
                get { return Floor != null; }
            }

            public bool Roofed
            {
                get { return Roof != null && Post != null; }
            }

            public bool Sections
            {
                get { return Walkway || Roofed; }
            }
        }

        private struct FencePlacement
        {
            public GameObject Prefab;

            public Vector3 At;

            public Quaternion Turn;

            public FencePlacement(GameObject prefab, Vector3 at, Quaternion turn)
            {
                Prefab = prefab;
                At = at;
                Turn = turn;
            }
        }

        private static FenceKit FenceKitFor(ZNetScene scene)
        {
            var kit = new FenceKit
            {
                Stake = FencePiecePrefab(scene, FencePrefab),
                Floor = IsFenceWalkway ? FencePiecePrefab(scene, FloorPrefab) : null,
                Roof = IsFenceRoof ? FencePiecePrefab(scene, RoofPrefab) : null,
                Post = IsFenceRoof ? FencePiecePrefab(scene, PostPrefab) : null,
            };
            return kit.Stake != null ? kit : null;
        }

        private static GameObject FencePiecePrefab(ZNetScene scene, string name)
        {
            var prefab = scene.GetPrefab(name);

            // Once, not at every look the projection takes.
            if (prefab == null && FenceWarned.Add(name))
                Log.LogWarning($"[AstvardServerMod] No fence prefab '{name}'.");
            return prefab;
        }

        /// <summary>
        /// Rings the player with palisade at the asked distance, the way the player built
        /// «30м.» by hand - see Geometry.FenceRing - and, if asked, with the walkway and
        /// the roof of «Секция 6шт.» along every side.
        ///
        /// Levelling is settled first, because it can be turned down - ground not loaded
        /// yet, or not loaded at all - and a turned-down fence should leave nothing
        /// behind, not a cleared ring. Then the line is cleared by the road's own rules,
        /// the ground levelled to the ground at the ring's middle, and a frame later, once the
        /// terrain a footing is read from is the new one, every place round the ring is
        /// judged while none of it stands, and it goes up a side at a time. A side stands
        /// complete in the frame it appears - a fresh piece starts at full support, so
        /// that is all a roof needs - and the ring grows round rather than stalling the
        /// game on a thousand pieces at once.
        ///
        /// Levelled, the fence is part of the undo step the levelling records, so undo
        /// puts the ground back and takes the fence down together.
        /// </summary>
        private static IEnumerator BuildFence(Vector3 centre)
        {
            var player = Player.m_localPlayer;
            var scene = ZNetScene.instance;
            var zones = ZoneSystem.instance;
            if (player == null || scene == null || zones == null) yield break;

            // The page is only drawn for an admin; this is the check where it counts,
            // since the pieces are free and the clearing cannot be undone.
            if (!IsAdminUnlocked) yield break;

            var kit = FenceKitFor(scene);
            if (kit == null) yield break;

            var radius = Mathf.Clamp(ParseField(FenceRadiusInput, 20f), FenceMinRadius, FenceMaxRadius);
            var plan = Geometry.FenceRing(radius, FenceMaxPerSide, StakeWidth);

            var levelHalf = kit.Sections ? SectionLevelHalf : FenceLevelHalf;
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
                                             kit.Sections ? SectionClearRadius : FenceClearRadius);

                TerrainUndoStep undo = null;
                if (comps != null)
                {
                    undo = RecordTerrainUndo("забор", comps, centre, radius + levelReach);

                    var flat = new List<Vec2>(levelLine.Count);
                    foreach (var point in levelLine) flat.Add(new Vec2(point.x, point.z));
                    var profile = new float[flat.Count];
                    var height = FenceLevelHeight(centre);
                    for (var i = 0; i < profile.Length; i++) profile[i] = height;

                    foreach (var comp in comps) LevelAlong(comp, flat, profile, levelHalf, FenceLevelBlend);

                    // Save gained an optional paintOnly in 1.0; reflection does not fill it.
                    var save = AccessTools.Method(typeof(TerrainComp), "Save");
                    foreach (var comp in comps) save.Invoke(comp, new object[] { false });
                    RebuildHeightmaps(centre, radius + levelReach + 16f);

                    yield return null;
                }

                var stakeLift = PivotAboveBase(kit.Stake);
                var panels = Geometry.SectionPanels(plan.PerSide);
                var posts = Geometry.SectionPosts(plan.PerSide);

                // Every place is judged before any piece goes up. Judged a side at a time
                // as they were built, each side's first span took the side before it - its
                // floor and panels run past the corner - for somebody else's building and
                // gave up its walkway, its roof and its post; the last side lost both its
                // posts, and its roof came down.
                var sides = new List<FenceSide>(plan.Sides);
                for (var side = 0; side < plan.Sides; side++)
                    sides.Add(JudgeFenceSide(plan, side, centre, radius, panels, posts,
                                             kit.Walkway, kit.Roofed, null));

                var creator = player.GetPlayerID();
                var platform = PlatformManager.DistributionPlatform.LocalUser.PlatformUserID;
                var built = new List<ZDOID>();
                var placements = new List<FencePlacement>();
                var skipped = 0;
                var roofless = 0;

                foreach (var fs in sides)
                {
                    if (ZNetScene.instance == null || Player.m_localPlayer == null) break;

                    skipped += fs.Skipped;
                    if (kit.Roofed && !fs.Roofed) roofless++;

                    placements.Clear();
                    LayOutSide(fs, plan, kit, stakeLift, panels, posts, placements);
                    foreach (var placement in placements)
                        PlaceFencePiece(placement.Prefab, placement.At, placement.Turn, creator, platform, built);

                    yield return null;
                }

                _lastFence = built;
                if (undo != null) undo.Pieces = built;

                var parts = "частокол" + (kit.Walkway ? ", помост" : "") + (kit.Roofed ? ", крыша" : "");
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    $"Забор {radius:0.#} м ({parts}): деталей {built.Count}"
                    + (undo != null ? ", выровнен" : "")
                    + (cleared > 0 ? $", снесено: {cleared}" : "")
                    + (skipped > 0 ? $", мест пропущено: {skipped}" : "")
                    + (roofless > 0 ? $", сторон без крыши: {roofless}" : ""));
                Log.LogInfo($"[AstvardServerMod] Fence r={radius:F1} sides={plan.Sides} perSide={plan.PerSide} " +
                            $"spacing={plan.Spacing:F2} walkway={kit.Walkway} roof={kit.Roofed} levelled={undo != null} " +
                            $"pieces={built.Count} skipped={skipped} roofless={roofless} cleared={cleared} at {centre}");
            }
            finally
            {
                _fenceBuilding = false;
                RefreshMenu();
            }
        }

        /// <summary>
        /// Every piece one judged side puts up, and how each stands - the build and the
        /// projection both lay a side out through here, so they cannot disagree.
        ///
        /// On its own a stake stands on the lowest ground under it, so the ring steps with
        /// a slope. With a walkway or a roof the floors and panels have to line up, so the
        /// whole side takes its lowest footing and no stake floats; on uneven ground that
        /// is what levelling is for.
        /// </summary>
        private static void LayOutSide(FenceSide fs, FencePlan plan, FenceKit kit, float stakeLift,
                                       float[] panels, float[] posts, List<FencePlacement> into)
        {
            if (fs.Lowest == float.MaxValue) return;

            var turn = Quaternion.Euler(0f, fs.Yaw, 0f);
            for (var j = 0; j < plan.PerSide; j++)
            {
                if (!fs.Standing[j]) continue;
                var baseY = kit.Sections ? fs.Lowest : fs.Feet[j].y;
                into.Add(new FencePlacement(kit.Stake,
                    new Vector3(fs.Feet[j].x, baseY + stakeLift, fs.Feet[j].z), turn));
            }

            var pivot = Vector3.up * (fs.Lowest + stakeLift);
            for (var j = 0; j < panels.Length; j++)
            {
                var at = fs.Middle + fs.Along * panels[j] + pivot;

                if (fs.Floor[j])
                    into.Add(new FencePlacement(kit.Floor,
                        at + Vector3.up * WalkwayRise - fs.Outward * WalkwayInset, turn));

                if (fs.Roofed && fs.Roof[j])
                {
                    into.Add(new FencePlacement(kit.Roof, at + Vector3.up * RoofLowRise + fs.Outward * RoofReach, turn));
                    into.Add(new FencePlacement(kit.Roof, at + Vector3.up * RoofHighRise - fs.Outward * RoofReach, turn));
                }
            }

            if (!fs.Roofed) return;

            foreach (var post in posts)
                foreach (var rise in PostRises)
                    into.Add(new FencePlacement(kit.Post, fs.Middle + fs.Along * post + pivot + Vector3.up * rise, turn));
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
        ///
        /// <paramref name="flatGround"/>, when given, is the ground as levelling will
        /// leave it, for a projection shown before the levelling has happened: the stake
        /// is shown standing on that, and water it will fill is no reason to leave it out.
        /// </summary>
        private static bool FindStakeFooting(Vector3 centre, Stake stake, float? flatGround, out Vector3 footing)
        {
            footing = Vector3.zero;
            var zones = ZoneSystem.instance;

            var yaw = stake.Yaw * Mathf.Deg2Rad;
            // Along the wall is a quarter turn clockwise from its outward face.
            var along = new Vector3(Mathf.Cos(yaw), 0f, -Mathf.Sin(yaw));
            var middle = new Vector3(centre.x + stake.At.X, 0f, centre.z + stake.At.Z);

            float lowest;
            if (flatGround.HasValue)
            {
                lowest = flatGround.Value;
            }
            else
            {
                lowest = float.MaxValue;
                var underMiddle = 0f;
                foreach (var foot in StakeFeet)
                {
                    if (!zones.GetGroundHeight(middle + along * foot, out var ground)) return false;
                    lowest = Mathf.Min(lowest, ground);
                    if (foot == 0f) underMiddle = ground;
                }

                if (underMiddle < zones.m_waterLevel - 1f) return false;
            }

            footing = new Vector3(middle.x, lowest - FenceSink, middle.z);
            if (Location.IsInsideNoBuildLocation(footing)) return false;

            return !BuiltWithin(footing, stake.Yaw, -0.25f, 0.25f, 2.5f);
        }

        /// <summary>One side of a ring, judged before anything of the ring is built.</summary>
        private sealed class FenceSide
        {
            public float Yaw;

            /// <summary>On the line, halfway along the side, at height 0.</summary>
            public Vector3 Middle;

            public Vector3 Along;

            public Vector3 Outward;

            public Vector3[] Feet;

            public bool[] Standing;

            public int Skipped;

            /// <summary>
            /// The lowest footing among the side's stakes. A walkway and a roof take it for
            /// the whole side, so that they line up and no stake floats.
            /// </summary>
            public float Lowest = float.MaxValue;

            public bool[] Floor;

            public bool[] Roof;

            /// <summary>
            /// Every post of the side has its place, so its roof may go up. Panels hang off
            /// the posts, and a roof short of one comes down within the minute.
            /// </summary>
            public bool Roofed;
        }

        /// <summary>
        /// Where one side's pieces can go, each part asked about its own room: the
        /// stake by FindStakeFooting, the floor where it lies inside the line, the
        /// panels over both and out to the eave, each post up the line itself. A house
        /// against the wall inside costs its stretch of walkway, not its stake - the ring
        /// stays shut either way.
        /// </summary>
        private static FenceSide JudgeFenceSide(FencePlan plan, int side, Vector3 centre, float radius,
                                                float[] panels, float[] posts, bool walkway, bool roof,
                                                float? flatGround)
        {
            var yaw = plan.Stakes[side * plan.PerSide].Yaw;
            var angle = yaw * Mathf.Deg2Rad;
            var fs = new FenceSide
            {
                Yaw = yaw,
                Outward = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)),
                // A quarter turn clockwise from the outward face, the way the stakes run.
                Along = new Vector3(Mathf.Cos(angle), 0f, -Mathf.Sin(angle)),
                Feet = new Vector3[plan.PerSide],
                Standing = new bool[plan.PerSide],
                Floor = new bool[plan.PerSide],
                Roof = new bool[plan.PerSide],
            };
            fs.Middle = new Vector3(centre.x, 0f, centre.z) + fs.Outward * radius;

            for (var j = 0; j < plan.PerSide; j++)
            {
                fs.Standing[j] = FindStakeFooting(centre, plan.Stakes[side * plan.PerSide + j], flatGround,
                                                  out fs.Feet[j]);
                if (fs.Standing[j]) fs.Lowest = Mathf.Min(fs.Lowest, fs.Feet[j].y);
                else fs.Skipped++;
            }

            if (fs.Lowest == float.MaxValue || !(walkway || roof)) return fs;

            // Heights from the lowest ground under the side's stakes: the floor lies at
            // about 1.9 m over it, the two panel rows at about 4.9 and 5.9.
            var ground = fs.Lowest + FenceSink;
            for (var j = 0; j < panels.Length; j++)
            {
                if (!fs.Standing[j]) continue;
                fs.Floor[j] = walkway && !SideBuiltWithin(fs, panels[j], 0.9f, -1.9f, -0.1f, ground + 1.4f, ground + 2.4f);
                fs.Roof[j] = roof && !SideBuiltWithin(fs, panels[j], 0.9f, -1.9f, 1.9f, ground + 4.2f, ground + 6.8f);
            }

            if (!roof) return fs;

            fs.Roofed = true;
            var sideLength = plan.PerSide * plan.Spacing;
            foreach (var post in posts)
            {
                // A post stands in the line, in the span of one stake; where that stake
                // could not stand, neither can the post.
                var slot = Mathf.Clamp(Mathf.FloorToInt((post + sideLength * 0.5f) / plan.Spacing),
                                       0, plan.PerSide - 1);
                if (!fs.Standing[slot] || SideBuiltWithin(fs, post, 0.3f, -0.3f, 0.3f, ground + 0.5f, ground + 5.8f))
                    fs.Roofed = false;
            }

            return fs;
        }

        /// <summary>
        /// Whether any built piece stands in a box on one side of the ring: centred
        /// <paramref name="at"/> metres along it, from <paramref name="inner"/> to
        /// <paramref name="outer"/> across the line, between two heights.
        /// </summary>
        private static bool SideBuiltWithin(FenceSide fs, float at, float halfAlong, float inner, float outer,
                                            float bottom, float top)
        {
            var box = fs.Middle + fs.Along * at + fs.Outward * ((inner + outer) * 0.5f)
                      + Vector3.up * ((bottom + top) * 0.5f);

            return Physics.CheckBox(box, new Vector3(halfAlong, (top - bottom) * 0.5f, (outer - inner) * 0.5f),
                                    Quaternion.Euler(0f, fs.Yaw, 0f), PieceLayer, QueryTriggerInteraction.Ignore);
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
        /// The height a ring is levelled to: the ground at its middle, not the player.
        /// The two are the same for somebody standing there, but an admin sizing up a
        /// ring from above in debug flight - one of the first rings was put down from
        /// 85 m over 51 m ground - would otherwise have the band raised the full eight
        /// metres the terrain allows; so would anybody standing on a floor of their own.
        /// </summary>
        private static float FenceLevelHeight(Vector3 centre)
        {
            var zones = ZoneSystem.instance;
            return zones != null && zones.GetGroundHeight(new Vector3(centre.x, 0f, centre.z), out var ground)
                ? ground
                : centre.y;
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
