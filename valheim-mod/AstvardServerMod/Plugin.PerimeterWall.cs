using System.Collections;
using System.Collections.Generic;
using Jotunn.Managers;
using Splatform;
using UnityEngine;

namespace AstvardServerMod
{
    /// <summary>
    /// «Заполнить» → «Стена по краю пола»: walls along the outer edge of the floor the player
    /// stands on, wherever none stands yet. The house goes up the way the owner builds one by
    /// hand - an outline, the floor inside it, then the walls round the floor - and it works as
    /// well for the floor of a second storey.
    ///
    /// The edge is found the way the floor fill finds its space: half-metre cells on the grid
    /// of the plate underfoot, asked of the physics. Here the flood keeps to cells with floor
    /// under them at that height, whatever stands on it, so an inner wall does not cut a room
    /// off. Which of its edges face the outside, and how an edge is cut into panels, is
    /// Geometry's; a hole in the middle, a stairwell, gets no walls.
    /// </summary>
    public partial class Plugin
    {
        internal static GameObject AreaWallButton;

        internal static GameObject AreaWallHeightButton;

        internal static readonly GameObject[] WallHeightButtons = new GameObject[MaxWallHeight];

        // The widths and heights the game's wooden walls come in, as the owner's own houses
        // use them: a two by two metre wall, a two by one half wall, a one by one quarter.
        private const string WallFull = "woodwall";

        private const string WallHalf = "wood_wall_half";

        private const string WallQuarter = "wood_wall_quarter";

        private const int MaxWallHeight = 4;

        // Three metres out of the box: a half wall under a whole one, the owner's
        // «Дом (Модуль)» to the centimetre.
        private static int _wallHeight = 3;

        private static bool _wallPreviewing;

        private static bool _wallPinned;

        private static bool _wallLaying;

        // The grid the plan was drawn on, kept while the player walks about on the same floor:
        // a new grid from every plate stepped on could shift the panels by a metre and back.
        private static bool _wallHaveGrid;

        private static Vector3 _wallOrigin;

        private static Vector3 _wallAcross;

        private static Vector3 _wallAlong;

        private static float _wallTop;

        private static readonly HashSet<long> WallFloor = new HashSet<long>();

        private static Vector3 _wallLookedAt;

        private static float _wallLookedTime = -1f;

        // When the plan was last drawn, and from where the flood started.
        private static float _wallPlannedAt = float.NegativeInfinity;

        private static Vector3 _wallFeet;

        private static string _wallSaid;

        private static readonly List<PiecePlacement> WallPlanned = new List<PiecePlacement>();

        private static readonly GhostPool WallGhosts = new GhostPool(new Color(1f, 1f, 1f, 0.5f));

        private static readonly RaycastHit[] WallRayHits = new RaycastHit[16];

        internal static bool IsWallPreviewing
        {
            get { return _wallPreviewing; }
        }

        private void CreateWallWidgets(GUIManager gui)
        {
            AreaWallButton = MakeButton(gui, "Стена по краю пола", StartWallPreview);

            AreaWallHeightButton = MakeButton(gui, "", () =>
            {
                MenuState = StateWallHeight;
                RefreshMenu();
            });

            string[] made = { "1 м — полустена", "2 м — стена", "3 м — полустена и стена", "4 м — две стены" };
            for (var i = 0; i < MaxWallHeight; i++)
            {
                var height = i + 1;
                WallHeightButtons[i] = MakeButton(gui, made[i], () =>
                {
                    _wallHeight = height;
                    MenuState = StateAreaFill;
                    RefreshMenu();
                    // A preview up since before shows the new height at once, pinned or not.
                    if (_wallPreviewing && _wallHaveGrid) PlanWalls(_wallFeet);
                });
            }
        }

        private static void UpdateWallLabels()
        {
            SetLabel(AreaWallHeightButton, $"Высота стены: {_wallHeight} м");
        }

        /// <summary>
        /// Puts the walls up as a projection over the floor underfoot. It follows the player
        /// from floor to floor - onto the next storey, say - and stays put while the player
        /// steps off to look at it from outside.
        /// </summary>
        private static void StartWallPreview()
        {
            var player = Player.m_localPlayer;
            var scene = ZNetScene.instance;
            if (player == null || scene == null || _wallLaying || RefuseWhileBuilding()) return;

            if (!RuleAllows("wall"))
            {
                player.Message(MessageHud.MessageType.Center, "Стены игрокам сейчас закрыты");
                return;
            }

            if (scene.GetPrefab(WallFull) == null || scene.GetPrefab(WallHalf) == null
                || scene.GetPrefab(WallQuarter) == null)
            {
                Log.LogWarning($"[AstvardServerMod] No wall prefabs '{WallFull}', '{WallHalf}', '{WallQuarter}'.");
                return;
            }

            // A marked road or bridge start answers the same click, so one of them has to go first.
            if (RoadAwaitingEnd || BridgeInProgress)
            {
                player.Message(MessageHud.MessageType.Center, "Сначала закончи или отмени дорожку или мост");
                return;
            }

            // One projection at a time, or one click would answer two of them.
            if (IsPlacing) CancelPlacement();
            if (IsFencePreviewing) CancelFencePreview();
            CancelAreaPreview();

            _wallPreviewing = true;
            _wallPinned = false;
            _wallHaveGrid = false;
            _wallLookedTime = -1f;
            _wallPlannedAt = float.NegativeInfinity;
            _wallSaid = null;
            WallFloor.Clear();
            WallPlanned.Clear();

            // The click on the button closes the panel; without a deaf moment it would carry
            // on into the world and build what it had only just asked to see.
            NoteToolStart();
            InventoryGui.instance?.Hide();

            LookForWalls(player);
            if (!_wallHaveGrid) SayWall("Встань на пол — стены встанут по его краю. Esc — отменить");
        }

        internal static void CancelWallPreview()
        {
            _wallPreviewing = false;
            _wallPinned = false;
            _wallHaveGrid = false;
            WallFloor.Clear();
            WallPlanned.Clear();
            WallGhosts.Clear();
        }

        /// <summary>
        /// Looks again, from Update: at once when the player has moved, every two seconds
        /// otherwise - for walls built or taken down meanwhile. Pinned, never.
        /// </summary>
        internal static void UpdateWallPreview()
        {
            if (!_wallPreviewing || _wallPinned) return;

            var player = Player.m_localPlayer;
            if (player == null)
            {
                CancelWallPreview();
                return;
            }

            var moved = (player.transform.position - _wallLookedAt).sqrMagnitude > 0.25f;
            var since = Time.time - _wallLookedTime;
            if (_wallLookedTime >= 0f && since < (moved ? 0.3f : 2f)) return;

            LookForWalls(player);
        }

        internal static bool HandleWallPreviewInput()
        {
            if (!_wallPreviewing) return false;

            var player = Player.m_localPlayer;
            if (player == null)
            {
                CancelWallPreview();
                return false;
            }

            if (InventoryGui.IsVisible() || Chat.instance?.HasFocus() == true) return false;

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                NoteEscapeUsed();
                ClearBuildAsk();
                CancelWallPreview();
                player.Message(MessageHud.MessageType.Center, "Отменено");
                RefreshMenu();
                return true;
            }

            // P keeps the walls where they are shown while the player walks off the floor and
            // round it; there is nothing to nudge - they stand where the floor ends.
            if (Input.GetKeyDown(PinKey))
            {
                _wallPinned = !_wallPinned;
                if (!_wallPinned) _wallLookedTime = -1f;
                SayPinned(_wallPinned);
                return true;
            }

            if (Input.GetMouseButtonDown(0) && Time.time - _toolMarkedAt > MarkDeafSeconds)
            {
                // The window every projection's click uses: this click must not also be a swing.
                _inputHeldUntil = Time.time + 0.3f;

                // One question to the server at a time; the answer builds or gives it back.
                if (_buildAsk == null) TryBuildWalls(player);
                return true;
            }

            return false;
        }

        private static void SayWall(string text)
        {
            // The plan is looked at again every so often; the same news once is enough.
            if (text == _wallSaid) return;
            _wallSaid = text;
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, text);
        }

        /// <summary>
        /// Finds the floor underfoot and plans its walls. Off any floor, the last plan stays.
        /// On the floor already planned, the grid is kept and the plan only drawn again every
        /// couple of seconds - walking about on it changes nothing - and a new floor, a storey
        /// up, is planned the moment the player stands on it.
        /// </summary>
        private static void LookForWalls(Player player)
        {
            var feet = player.transform.position;
            _wallLookedAt = feet;
            _wallLookedTime = Time.time;

            if (!FindFloorUnder(feet, out var plate, out var top)) return;

            var sameFloor = _wallHaveGrid && Mathf.Abs(top - _wallTop) <= 0.15f && WallFloor.Contains(GridKeyOf(feet));
            if (sameFloor && Time.time - _wallPlannedAt < 2f) return;

            if (!sameFloor)
            {
                if (!GridFromPlate(plate, out _wallOrigin, out _wallAcross, out _wallAlong)) return;
                _wallTop = top;
                _wallHaveGrid = true;
            }

            PlanWalls(feet);
        }

        /// <summary>The cell under a point, on the grid of the current plan.</summary>
        private static long GridKeyOf(Vector3 at)
        {
            var local = at - _wallOrigin;
            return Geometry.CellKey(Mathf.FloorToInt(Vector3.Dot(local, _wallAcross) / FillCell),
                                    Mathf.FloorToInt(Vector3.Dot(local, _wallAlong) / FillCell));
        }

        /// <summary>
        /// The floor piece under the feet: a flat slab that is a piece, the nearest one down.
        /// A rug lies as flat on a floor as the floor itself, so a piece called a floor wins.
        /// </summary>
        private static bool FindFloorUnder(Vector3 feet, out Collider floor, out float top)
        {
            floor = null;
            top = 0f;

            var count = Physics.RaycastNonAlloc(feet + Vector3.up * 0.5f, Vector3.down, WallRayHits, 2.5f,
                                                PieceLayer, QueryTriggerInteraction.Ignore);
            var best = float.MaxValue;
            for (var k = 0; k < count; k++)
            {
                var hit = WallRayHits[k];
                if (hit.collider == null || !IsLyingSlab(hit.collider)) continue;

                var piece = hit.collider.GetComponentInParent<Piece>();
                if (piece == null) continue;

                var named = Utils.GetPrefabName(piece.gameObject).IndexOf("floor", System.StringComparison.OrdinalIgnoreCase) >= 0;
                var score = hit.distance - (named ? 10f : 0f);
                if (score >= best) continue;

                best = score;
                floor = hit.collider;
                top = hit.point.y;
            }

            return floor != null;
        }

        /// <summary>
        /// The half-metre grid a plate lies on, from one of its corners: then its own edges,
        /// and those of every plate laid on from it, fall on whole metres, and the two-metre
        /// joints of a floor of two-metre plates on every second one. Its two axes are the
        /// plate's own, as seen from above, in the order right and forward come in.
        /// </summary>
        private static bool GridFromPlate(Collider plate, out Vector3 origin, out Vector3 across, out Vector3 along)
        {
            origin = across = along = Vector3.zero;

            Vector3 size, middle;
            if (plate is BoxCollider box)
            {
                size = box.size;
                middle = box.center;
            }
            else if (plate is MeshCollider mesh && mesh.sharedMesh != null)
            {
                size = mesh.sharedMesh.bounds.size;
                middle = mesh.sharedMesh.bounds.center;
            }
            else return false;

            var t = plate.transform;
            var scale = t.lossyScale;
            size = new Vector3(Mathf.Abs(size.x * scale.x), Mathf.Abs(size.y * scale.y), Mathf.Abs(size.z * scale.z));

            var thin = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
            var a = (thin + 1) % 3;
            var b = (thin + 2) % 3;

            across = Flat(a == 0 ? t.right : a == 1 ? t.up : t.forward);
            along = Flat(b == 0 ? t.right : b == 1 ? t.up : t.forward);
            if (across == Vector3.zero || along == Vector3.zero) return false;

            // Right then forward turns clockwise seen from above; a pair the other way round
            // would mirror the grid, and "out" with it.
            if (Vector3.Cross(across, along).y > 0f) along = -along;

            // Whole half metres: a plate is one or two metres a side, and a hair of rounding
            // in its collider must not shift the grid off its edges.
            var halfAcross = Mathf.Round(size[a]) * 0.5f;
            var halfAlong = Mathf.Round(size[b]) * 0.5f;

            origin = t.TransformPoint(middle) - across * halfAcross - along * halfAlong;
            return true;
        }

        private static Vector3 Flat(Vector3 direction)
        {
            direction.y = 0f;
            return direction.sqrMagnitude > 0.01f ? direction.normalized : Vector3.zero;
        }

        /// <summary>
        /// The walls for the floor under <paramref name="feet"/> on the current grid: the
        /// floor flooded, its outer edge cut into panels, and each panel stacked to the height
        /// asked, less every piece of it already standing there.
        /// </summary>
        private static void PlanWalls(Vector3 feet)
        {
            var scene = ZNetScene.instance;
            if (scene == null) return;

            _wallFeet = feet;
            _wallPlannedAt = Time.time;

            var origin = _wallOrigin;
            var across = _wallAcross;
            var along = _wallAlong;
            var top = _wallTop;
            var turn = Quaternion.LookRotation(along, Vector3.up);

            bool Floored(int i, int j)
            {
                var middle = origin + across * ((i + 0.5f) * FillCell) + along * ((j + 0.5f) * FillCell);
                middle.y = top - 0.05f;

                var count = Physics.OverlapBoxNonAlloc(middle, new Vector3(0.05f, 0.08f, 0.05f), FillHits, turn,
                                                       PieceLayer, QueryTriggerInteraction.Ignore);
                for (var k = 0; k < count; k++)
                {
                    var hit = FillHits[k];
                    if (IsLyingSlab(hit) && Mathf.Abs(hit.bounds.max.y - top) < 0.15f) return true;
                }

                return false;
            }

            // The flood: from the cell underfoot - with the feet on a seam, the cells round it -
            // over every cell with floor under it at this height.
            WallFloor.Clear();
            var seen = new HashSet<long>();
            var queue = new Queue<GridCell>();
            var local = feet - origin;
            var fi = Mathf.FloorToInt(Vector3.Dot(local, across) / FillCell);
            var fj = Mathf.FloorToInt(Vector3.Dot(local, along) / FillCell);
            for (var di = -1; di <= 1; di++)
                for (var dj = -1; dj <= 1; dj++)
                {
                    var key = Geometry.CellKey(fi + di, fj + dj);
                    if (!seen.Add(key) || !Floored(fi + di, fj + dj)) continue;
                    WallFloor.Add(key);
                    queue.Enqueue(new GridCell(fi + di, fj + dj));
                }

            var tooBig = false;
            while (queue.Count > 0 && !tooBig)
            {
                var cell = queue.Dequeue();
                for (var s = 0; s < 4; s++)
                {
                    var ni = cell.I + (s == 0 ? 1 : s == 1 ? -1 : 0);
                    var nj = cell.J + (s == 2 ? 1 : s == 3 ? -1 : 0);
                    var key = Geometry.CellKey(ni, nj);
                    if (!seen.Add(key) || !Floored(ni, nj)) continue;

                    WallFloor.Add(key);
                    queue.Enqueue(new GridCell(ni, nj));
                    if (WallFloor.Count > FillMaxCells)
                    {
                        tooBig = true;
                        break;
                    }
                }
            }

            WallPlanned.Clear();
            if (tooBig || WallFloor.Count == 0)
            {
                WallGhosts.Show(WallPlanned);
                SayWall(tooBig ? "Пол больше 3000 м² — стены по нему не ставлю" : "Встань на пол — стены встанут по его краю");
                return;
            }

            var full = scene.GetPrefab(WallFull);
            var halfWall = scene.GetPrefab(WallHalf);
            var quarter = scene.GetPrefab(WallQuarter);

            var columns = 0;
            var skipped = 0;
            var gapCells = 0;
            foreach (var run in Geometry.PerimeterRuns(WallFloor))
            {
                var outward = (run.ConstantI ? across : along) * run.Outward;
                var alongRun = run.ConstantI ? along : across;
                var line = (run.ConstantI ? across : along) * (run.Line * FillCell);
                var rotation = Quaternion.LookRotation(outward, Vector3.up);

                foreach (var panel in Geometry.WallPanels(run.From, run.To, out var gap))
                {
                    var foot = origin + line + alongRun * ((panel.Start + panel.Width * 0.5f) * FillCell);
                    foot.y = top;

                    var width = panel.Width * FillCell;
                    var placedHere = false;
                    foreach (var row in WallRows(width, _wallHeight, full, halfWall, quarter))
                    {
                        var at = foot + Vector3.up * (row.Bottom + PivotAboveWallBase(row.Prefab, row.Height));
                        if (WallSpotTaken(at, rotation, width, row.Height))
                        {
                            skipped++;
                            continue;
                        }

                        WallPlanned.Add(new PiecePlacement(row.Prefab, at, rotation));
                        placedHere = true;
                    }

                    if (placedHere) columns++;
                }

                gapCells += run.To - run.From - SumWidths(run);
            }

            WallGhosts.Show(WallPlanned);

            var gaps = gapCells > 0 ? $", {gapCells * FillCell:0.#} м не закрыть — пол не по сетке" : "";
            if (WallPlanned.Count == 0)
            {
                SayWall(skipped > 0 ? "По краю этого пола стены уже стоят" : "Здесь стене негде встать" + gaps);
                return;
            }

            SayWall($"Стена {_wallHeight} м: {columns} пролётов, деталей {WallPlanned.Count}"
                    + (skipped > 0 ? $", {skipped} уже стоят" : "") + gaps
                    + ". ЛКМ — поставить, Esc — отменить, P — закрепить");
        }

        private static int SumWidths(EdgeRun run)
        {
            var sum = 0;
            foreach (var panel in Geometry.WallPanels(run.From, run.To, out _)) sum += panel.Width;
            return sum;
        }

        /// <summary>One piece of one panel: which wall, and where its base stands above the floor.</summary>
        private struct WallRow
        {
            public GameObject Prefab;

            public float Bottom;

            public float Height;

            public WallRow(GameObject prefab, float bottom, float height)
            {
                Prefab = prefab;
                Bottom = bottom;
                Height = height;
            }
        }

        /// <summary>
        /// How a panel of this width is stacked to this height. Two metres wide: a half wall
        /// for an odd metre, whole walls for the rest, the half at the bottom as the owner's
        /// houses have it. One metre wide: quarters, one a metre.
        /// </summary>
        private static IEnumerable<WallRow> WallRows(float width, int height, GameObject full, GameObject half,
                                                     GameObject quarter)
        {
            if (width < 1.5f)
            {
                for (var k = 0; k < height; k++) yield return new WallRow(quarter, k, 1f);
                yield break;
            }

            var bottom = 0f;
            if (height % 2 == 1)
            {
                yield return new WallRow(half, 0f, 1f);
                bottom = 1f;
            }

            for (; bottom + 2f <= height + 0.01f; bottom += 2f) yield return new WallRow(full, bottom, 2f);
        }

        /// <summary>
        /// How far above its base a wall keeps its pivot. The owner's blueprints have it in
        /// the middle - a half wall on a floor at 0 stands at 0.5, a whole one above it at 2 -
        /// and the measured colliders are taken only when they agree with that.
        /// </summary>
        private static float PivotAboveWallBase(GameObject prefab, float height)
        {
            var measured = PivotAboveBase(prefab);
            return Mathf.Abs(measured - height * 0.5f) < 0.15f ? measured : height * 0.5f;
        }

        /// <summary>
        /// Whether something already stands where this wall piece would: a wall, a door, a
        /// floor above it - anything solid but a post or a beam, which walls are built round
        /// and against. The box stays clear of the floor under it and a little short of the
        /// ends, where the neighbouring walls and the corner posts meet it.
        /// </summary>
        private static bool WallSpotTaken(Vector3 at, Quaternion rotation, float width, float height)
        {
            var half = new Vector3(Mathf.Max(0.1f, width * 0.5f - 0.3f), Mathf.Max(0.1f, height * 0.5f - 0.15f), 0.05f);
            var count = Physics.OverlapBoxNonAlloc(at, half, FillHits, rotation, PieceLayer,
                                                   QueryTriggerInteraction.Ignore);
            for (var k = 0; k < count; k++)
                if (!IsPostOrBeam(FillHits[k])) return true;
            return false;
        }

        /// <summary>A collider thin two ways: a pole standing, a beam lying.</summary>
        private static bool IsPostOrBeam(Collider collider)
        {
            Vector3 size;
            if (collider is BoxCollider box) size = box.size;
            else if (collider is MeshCollider mesh && mesh.sharedMesh != null) size = mesh.sharedMesh.bounds.size;
            else if (collider is CapsuleCollider capsule) return capsule.radius * 2f < 0.45f;
            else return false;

            var scale = collider.transform.lossyScale;
            var x = Mathf.Abs(size.x * scale.x);
            var y = Mathf.Abs(size.y * scale.y);
            var z = Mathf.Abs(size.z * scale.z);
            var thin = (x < 0.45f ? 1 : 0) + (y < 0.45f ? 1 : 0) + (z < 0.45f ? 1 : 0);
            return thin >= 2;
        }

        /// <summary>Builds the walls shown, a player's by the same rules as their floor.</summary>
        private static void TryBuildWalls(Player player)
        {
            if (WallPlanned.Count == 0)
            {
                // Said again even if said before: the click wants an answer.
                var said = _wallSaid;
                _wallSaid = null;
                SayWall(said ?? "Здесь нечего ставить");
                return;
            }

            if (_wallLaying || RefuseWhileBuilding()) return;

            var placements = new List<PiecePlacement>(WallPlanned);

            if (!IsAdminUnlocked)
            {
                // Every piece's place asked of the wards, as the hammer asks it.
                var places = new List<Vector3>(placements.Count);
                foreach (var placement in placements) places.Add(placement.At);
                if (!WardsAllowPieces("wall", places))
                {
                    player.Message(MessageHud.MessageType.Center, "Стена задевает чужой оберег");
                    return;
                }

                if (!RuleAllows("wall"))
                {
                    player.Message(MessageHud.MessageType.Center, "Стены игрокам сейчас закрыты");
                    return;
                }

                if (RulePaid("wall"))
                {
                    var shortfall = BillShortfall(player, PlacementBill(placements));
                    if (shortfall != null)
                    {
                        player.Message(MessageHud.MessageType.Center, shortfall);
                        return;
                    }

                    LayWallsNow(placements, true);
                    return;
                }

                // Free: built on the server's word, which keeps the pause between builds. The
                // projection stays up meanwhile; closed, it goes.
                AskToBuild("#wall", () => LayWallsNow(placements, false), null, CancelWallPreview);
                return;
            }

            LayWallsNow(placements, false);
        }

        private static void LayWallsNow(List<PiecePlacement> placements, bool paid)
        {
            CancelWallPreview();
            Instance?.StartCoroutine(LayWalls(placements, paid));
        }

        private static IEnumerator LayWalls(List<PiecePlacement> placements, bool paid)
        {
            var player = Player.m_localPlayer;
            if (player == null) yield break;

            _wallLaying = true;
            var record = BeginBuild("стена");

            // Paid for whole, and what does not go up handed back at the end.
            Bill owed = null;
            if (paid)
            {
                var bill = PlacementBill(placements);
                if (PayBill(player, bill))
                {
                    owed = bill;
                    record.Paid = true;
                }
            }

            try
            {
                var cleared = BuildClearingActive ? ClearUnderPieces(placements) : 0;

                // Bottom row first, so every piece has what holds it up by the time it is
                // judged - and all of it well inside the half minute a new piece is left alone.
                placements.Sort((x, y) => x.At.y.CompareTo(y.At.y));

                var creator = player.GetPlayerID();
                var platform = PlatformManager.DistributionPlatform.LocalUser.PlatformUserID;
                var built = record.Pieces;

                for (var i = 0; i < placements.Count; i++)
                {
                    if (ZNetScene.instance == null || record.Cancelled) break;
                    var placement = placements[i];
                    var before = built.Count;
                    PlacePiece(placement.Prefab, placement.At, placement.Turn, creator, platform, built);
                    if (built.Count > before) owed?.Add(placement.Prefab, -1);
                    if ((i + 1) % FillPerFrame == 0) yield return null;
                }

                if (record.Cancelled)
                {
                    record.Running = false;
                    TakeDownBuild(record);
                    yield break;
                }

                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    $"Стены поставлены: деталей {built.Count}" + (cleared > 0 ? $", снесено: {cleared}" : ""));
                Log.LogInfo($"[AstvardServerMod] Walls built: {built.Count} pieces, {_wallHeight} m, "
                            + $"pivots {PivotAboveBase(placements[0].Prefab):F3} above base for {placements[0].Prefab.name}.");
            }
            finally
            {
                RefundBill(owed);
                _wallLaying = false;
                if (record.Running) EndBuild(record);
                RefreshMenu();
            }
        }
    }
}
