using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using Splatform;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        internal static GameObject CopyButton;

        internal static GameObject PasteButton;

        internal static GameObject CopyHint;

        internal static GameObject CopyRadiusInput;

        internal static GameObject CopyApplyButton;

        internal static GameObject TemplatesButton;


        internal static GameObject SnapButton;

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

        /// <summary>True while a ghost is following the player, waiting to be placed.</summary>
        internal static bool IsPlacing;

        // Placement ends the instant the click is read, but the game's own input may
        // still run later in that same frame. Holding the block a moment longer is what
        // stops the committing click from also being a swing.
        private static float _inputHeldUntil;

        // The button that marks a start also closes the panel, so without a deaf moment
        // the very same click carries on into the world and finishes the road it just
        // began. Long enough to outlive the frame, short enough not to be felt.
        private const float MarkDeafSeconds = 0.25f;

        private static float _toolMarkedAt;

        /// <summary>Called when a road or bridge start is marked, to hold off that click.</summary>
        internal static void NoteToolStart()
        {
            _toolMarkedAt = Time.time;
        }

        internal static bool PlacementHoldsInput
        {
            get { return IsPlacing || Time.time < _inputHeldUntil; }
        }

        // Placement adjustments driven by Q/E and shift+Q/E.
        private static float _placeYaw;

        private static float _placeHeight;

        // How far ahead of the player the preview floats, unless the field says
        // otherwise. Eleven metres clears an average build; a long hall wants more,
        // and a single piece is easier to place close in.
        private const float DefaultPlacementDistance = 11f;

        internal static GameObject PlacementDistanceInput;

        private static float PlacementDistance
        {
            get
            {
                return Mathf.Clamp(ParseField(PlacementDistanceInput, DefaultPlacementDistance),
                    2f, 40f);
            }
        }

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

        private static void UpdateSnapButtonLabel()
        {
            var label = SnapButton != null ? SnapButton.GetComponentInChildren<Text>() : null;
            if (label != null) label.text = IsSnapEnabled ? "Прилипание: вкл" : "Прилипание: выкл";
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
        /// <summary>
        /// Copies what is around the player and stores it as a template. Squaring up
        /// happens on the way in, so what lands in the folder is already aligned —
        /// that used to be a hand-run script between the game and the mod.
        /// </summary>
        private static void RunCopyToFile()
        {
            var player = Player.m_localPlayer;
            if (player == null || _building) return;

            var radius = Mathf.Clamp(ParseField(CopyRadiusInput, 10f), 1f, 64f);
            if (!FillClipboard(player, radius)) return;

            var name = FieldText(TemplateNameInput, "");
            var category = FieldText(TemplateCategoryInput, "");

            if (!SaveClipboardAsTemplate(name, category, player.GetPlayerName()))
            {
                player.Message(MessageHud.MessageType.Center, "Не удалось сохранить шаблон");
                return;
            }

            player.Message(MessageHud.MessageType.Center,
                $"Шаблон сохранён: {Clipboard.Count} деталей");

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
        private static UnityEngine.UI.InputField[] _panelInputs;
        private static bool _inputBlocked;

        /// <summary>
        /// Hands the keyboard to a focused text box. Without this the game keeps every
        /// letter as a hotkey, so typing a template name did nothing visible — the
        /// numeric fields only seemed to work because digits are not bound to anything.
        /// </summary>
        internal static void UpdatePanelInputBlocking()
        {
            if (Panel == null) return;

            if (_panelInputs == null)
                _panelInputs = Panel.GetComponentsInChildren<UnityEngine.UI.InputField>(true);

            var focused = false;
            if (Panel.activeInHierarchy)
            {
                foreach (var field in _panelInputs)
                {
                    if (field == null || !field.isFocused) continue;
                    focused = true;
                    break;
                }
            }

            if (focused == _inputBlocked) return;

            _inputBlocked = focused;
            GUIManager.BlockInput(focused);
        }

        private void Update()
        {
            UpdatePanelInputBlocking();
            UpdateRoadPreview();
            UpdateBridgePreview();

            // Escape gets the road and the bridge out of the way too, and it has to be
            // read before the placement guard below — a marked start is not a placement.
            if ((RoadInProgress || BridgeInProgress) && Input.GetKeyDown(KeyCode.Escape)
                && !InventoryGui.IsVisible() && Chat.instance?.HasFocus() != true)
            {
                // Asked separately: CancelRoad answers "Отменено" whether or not a road
                // was going, so calling both would answer twice for one press.
                if (RoadInProgress) CancelRoad();
                if (BridgeInProgress) CancelBridge();
                RefreshMenu();
                return;
            }

            // A marked road or bridge finishes on a click as well as on its own button.
            // The panel is shut while you walk to the far end, and opening it to press
            // one button is a step nobody wants. Marking stays on the button: a click
            // that both began and ended a road would leave no way to change your mind.
            if ((RoadAwaitingEnd || BridgeInProgress) && Input.GetMouseButtonDown(0)
                && !InventoryGui.IsVisible() && Chat.instance?.HasFocus() != true
                && Time.time - _toolMarkedAt > MarkDeafSeconds)
            {
                // The same window the committing placement click uses: this click must
                // not also be a swing, and the game's own input can still run later in
                // the very same frame.
                _inputHeldUntil = Time.time + 0.3f;

                if (BridgeInProgress) BuildBridge();
                else BuildRoad();

                RefreshMenu();
                return;
            }

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
                _inputHeldUntil = Time.time + 0.3f;
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
            // Read once rather than per piece: this is a property chain through the
            // distribution platform. It does NOT avoid the linear scan of the world's
            // player history that SetCreator itself runs for every piece - that cost
            // is inside the call and stays.
            var creatorPlatform = PlatformManager.DistributionPlatform.LocalUser.PlatformUserID;

            // Before the pieces, not after: a floor dropped onto a slope and then
            // levelled underneath would already have decided what it was resting on.
            if (IsLevelGroundEnabled && ClipboardHasBuildPieces())
                LevelUnderBuild(origin, rotation);

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
                    if (piece != null) piece.SetCreator(creator, creatorPlatform);
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

                    if (ghost.GetComponentsInChildren<Renderer>().Length == 0)
                        AddGhostMarker(ghost, prefab);

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

        /// <summary>The stand-in for a modelless piece that is not a creature spawner.</summary>
        private const string GhostMarkerPrefab = "wood_pole";

        /// <summary>
        /// Gives a piece with no model something to look at. A spawner is a bare
        /// transform, so its preview drew nothing and there was no way to aim one. The
        /// spawner knows which creature it releases, and that model is the honest
        /// marker: it stands on the spot and says what is being planted there.
        /// </summary>
        private static void AddGhostMarker(GameObject ghost, GameObject prefab)
        {
            GameObject markerPrefab = null;

            var spawner = prefab.GetComponent<CreatureSpawner>();
            if (spawner != null) markerPrefab = spawner.m_creaturePrefab;

            if (markerPrefab == null && ZNetScene.instance != null)
                markerPrefab = ZNetScene.instance.GetPrefab(GhostMarkerPrefab);

            if (markerPrefab == null) return;

            var marker = Instantiate(markerPrefab, ghost.transform);
            marker.transform.localPosition = Vector3.zero;
            marker.transform.localRotation = Quaternion.identity;

            foreach (var collider in marker.GetComponentsInChildren<Collider>())
                collider.enabled = false;
            foreach (var behaviour in marker.GetComponentsInChildren<MonoBehaviour>())
                behaviour.enabled = false;

            // A creature carries a Rigidbody, and a Rigidbody with its scripts switched
            // off still answers to gravity - the marker would sink out of the preview.
            foreach (var body in marker.GetComponentsInChildren<Rigidbody>())
                body.isKinematic = true;
        }

        /// <summary>
        /// Whether this placement actually stands on the ground. Levelling is there to
        /// give a building a flat pad; a spawner is an empty point resting on nothing,
        /// and flattening a circle of terrain under it changes the world for no reason.
        /// </summary>
        private static bool ClipboardHasBuildPieces()
        {
            if (ZNetScene.instance == null) return false;

            foreach (var entry in Clipboard)
            {
                var prefab = ZNetScene.instance.GetPrefab(entry.Prefab);
                if (prefab != null && prefab.GetComponent<Piece>() != null) return true;
            }

            return false;
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
    }
}
