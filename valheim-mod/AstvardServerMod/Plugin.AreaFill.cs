using System.Collections;
using System.Collections.Generic;
using Jotunn.Managers;
using Splatform;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        internal static GameObject AreaFillButton;

        internal static GameObject AreaFillHint;

        internal static GameObject AreaFloorButton;

        private const string FloorPlate = "wood_floor";

        private const string FloorTile = "wood_floor_1x1";

        // Half a metre: fine enough to follow a wall set at any angle, coarse enough that
        // a closed room of a few hundred square metres is a few thousand questions.
        private const float FillCell = 0.5f;

        // How far the flood may go before a space counts as open, in cells: 64 m.
        private const int FillReach = 128;

        // 3000 m². Past this a space is either open, or more floor than one press should lay.
        private const int FillMaxCells = 12000;

        // Where the plate floats while it is being put to a wall: close, since it is one
        // plate and has to meet one.
        private const float FillSeedDistance = 4f;

        // Plates a frame while laying: a hall goes down within a second or so, and no
        // single frame stalls on it.
        private const int FillPerFrame = 64;

        private static readonly Color FillGhostTint = new Color(1f, 1f, 1f, 0.5f);

        private static readonly GhostPool FillGhosts = new GhostPool(FillGhostTint);

        // The cells under the plate the flood starts from: two metres, four cells a side.
        private static readonly GridCell[] PlateCells = MakePlateCells();

        private static readonly Collider[] FillHits = new Collider[32];

        private static readonly Dictionary<Collider, FloorCell> FillKinds = new Dictionary<Collider, FloorCell>();

        private static readonly List<PiecePlacement> FillPlanned = new List<PiecePlacement>();

        private static bool _fillSeeding;

        private static bool _fillLaying;

        private static FloorRegion _fillRegion;

        private static Vector3 _fillLookedAt;

        private static float _fillLookedYaw;

        private static float _fillLookedTime = -1f;

        private static string _fillSaid;

        private static GameObject _fillLeak;

        private static LineRenderer _fillLeakLine;

        private static GridCell[] MakePlateCells()
        {
            var cells = new List<GridCell>();
            for (var i = -2; i <= 1; i++)
                for (var j = -2; j <= 1; j++)
                    cells.Add(new GridCell(i, j));
            return cells.ToArray();
        }

        private void CreateAreaFillWidgets(GUIManager gui)
        {
            AreaFillButton = MakeButton(gui, "Заполнить", () =>
            {
                MenuState = StateAreaFill;
                RefreshMenu();
            });

            AreaFillHint = MakeText(gui,
                $"Пол: обнеси место стенами или{NEWLINE}балками и приставь плиту к стене{NEWLINE}"
                + $"изнутри — мод найдёт контур и{NEWLINE}покажет пол. Q/E — повернуть.{NEWLINE}"
                + $"Если контур не замкнут, красная{NEWLINE}линия пройдёт через дыру.{NEWLINE}"
                + $"Стена: встань на пол — стены{NEWLINE}встанут по его краю, где их нет.{NEWLINE}"
                + $"ЛКМ — поставить, Esc — отменить,{NEWLINE}P — закрепить.");

            AreaFloorButton = MakeButton(gui, "Пол", StartFloorSeed);

            CreateWallWidgets(gui);
        }

        /// <summary>A floor plate is being put to a wall, and the floor it would make is shown.</summary>
        internal static bool IsFillSeeding
        {
            get { return _fillSeeding; }
        }

        /// <summary>
        /// Hands the player one plate to put to a wall, the way a blueprint is placed - so
        /// it snaps, turns on Q/E, lifts on Shift+Q/E and pins on P - and from then on shows
        /// the whole floor that plate would start. The plate is the question: its height is
        /// the floor's height, its turn the floor's grid.
        /// </summary>
        private static void StartFloorSeed()
        {
            var scene = ZNetScene.instance;
            var player = Player.m_localPlayer;
            if (scene == null || player == null || _fillLaying || BuildInProgress) return;
            if (!RuleAllows("floor"))
            {
                player.Message(MessageHud.MessageType.Center, "Пол игрокам сейчас закрыт");
                return;
            }

            if (scene.GetPrefab(FloorPlate) == null || scene.GetPrefab(FloorTile) == null)
            {
                Log.LogWarning($"[AstvardServerMod] No floor prefabs '{FloorPlate}', '{FloorTile}'.");
                return;
            }

            Clipboard.Clear();
            Clipboard.Add(new CopiedPiece
            {
                Prefab = FloorPlate,
                LocalPos = Vector3.zero,
                LocalRot = Quaternion.identity,
            });

            StartPlacement();

            _fillSeeding = true;
            _fillRegion = null;
            _fillSaid = null;
            _fillLookedTime = -1f;
            FillPlanned.Clear();

            InventoryGui.instance?.Hide();
            player.Message(MessageHud.MessageType.Center, "Приставь плиту к стене изнутри, P — закрепить");
        }

        /// <summary>
        /// Looks again at the floor the plate would start, when the plate has moved or
        /// turned - five times a second at most - and once a second regardless, for
        /// whatever was built or taken down meanwhile.
        /// </summary>
        internal static void UpdateAreaFillPreview()
        {
            if (!_fillSeeding || GhostRoot == null) return;

            var seed = GhostRoot.transform;
            var yaw = seed.eulerAngles.y;
            var moved = (seed.position - _fillLookedAt).sqrMagnitude > 0.0025f
                        || Mathf.Abs(Mathf.DeltaAngle(yaw, _fillLookedYaw)) > 0.5f;

            // A big hall is tens of thousands of questions to the physics; a plate moving
            // in one looks again once a second rather than five times.
            var busy = _fillRegion != null && _fillRegion.Free.Count > 4000;
            var since = Time.time - _fillLookedTime;
            if (_fillLookedTime >= 0f && (moved ? since < (busy ? 1f : 0.2f) : since < 1f)) return;

            _fillLookedAt = seed.position;
            _fillLookedYaw = yaw;
            _fillLookedTime = Time.time;
            LookForFloor(seed.position, yaw);
        }

        /// <summary>
        /// Lays the floor that is shown - looked at once more first, since the last look
        /// can be a fifth of a second old. An open space lays nothing and keeps the plate
        /// in hand; the message has already said why.
        /// </summary>
        internal static void TryLayFloor()
        {
            if (!_fillSeeding || GhostRoot == null || _fillLaying) return;

            LookForFloor(GhostRoot.transform.position, GhostRoot.transform.eulerAngles.y);
            if (_fillRegion == null || !_fillRegion.Closed || FillPlanned.Count == 0) return;

            var placements = new List<PiecePlacement>(FillPlanned);
            var player = Player.m_localPlayer;

            if (!IsAdminUnlocked && player != null)
            {
                // Every plate's place asked of the wards, as the hammer asks it.
                var places = new List<Vector3>(placements.Count);
                foreach (var placement in placements) places.Add(placement.At);
                if (!WardsAllowPieces("floor", places))
                {
                    player.Message(MessageHud.MessageType.Center, "Пол задевает чужой оберег");
                    return;
                }

                if (!RuleAllows("floor"))
                {
                    player.Message(MessageHud.MessageType.Center, "Пол игрокам сейчас закрыт");
                    return;
                }

                if (RulePaid("floor"))
                {
                    var shortfall = BillShortfall(player, PlacementBill(placements));
                    if (shortfall != null)
                    {
                        player.Message(MessageHud.MessageType.Center, shortfall);
                        return;
                    }

                    LayFloorNow(placements, true);
                    return;
                }

                // Free: laid on the server's word, which keeps the pause between builds.
                AskToBuild("#floor", () => LayFloorNow(placements, false), ResumePlacement, () =>
                {
                    EndFloorSeed();
                    CancelPlacement();
                });
                return;
            }

            LayFloorNow(placements, false);
        }

        private static void LayFloorNow(List<PiecePlacement> placements, bool paid)
        {
            IsPlacing = false;
            _ghostPinned = false;
            ClearGhosts();
            EndFloorSeed();

            Instance?.StartCoroutine(LayFloor(placements, paid));
        }

        /// <summary>Puts the plate down without laying anything - Esc, or the placement giving up.</summary>
        internal static void EndFloorSeed()
        {
            _fillSeeding = false;
            _fillRegion = null;
            FillPlanned.Clear();
            FillGhosts.Clear();
            HideFillLeak();
        }

        /// <summary>
        /// Finds the space the plate stands in and plans its floor, or finds the way out
        /// of it, and shows whichever it was.
        /// </summary>
        private static void LookForFloor(Vector3 seedPos, float yaw)
        {
            var scene = ZNetScene.instance;
            var plate = scene != null ? scene.GetPrefab(FloorPlate) : null;
            var tile = scene != null ? scene.GetPrefab(FloorTile) : null;
            if (plate == null || tile == null) return;

            var top = seedPos.y + PivotBelowTop(plate);
            _fillRegion = FindFloorRegion(seedPos, yaw, top);

            FillPlanned.Clear();
            if (_fillRegion.Closed)
            {
                var big = new List<GridCell>();
                var small = new List<GridCell>();
                Geometry.FloorTiles(_fillRegion.Free, big, small);
                LayOutFloor(seedPos, yaw, top, plate, tile, big, small, FillPlanned);

                FillGhosts.Show(FillPlanned);
                HideFillLeak();
                SayFill(FillPlanned.Count == 0
                    ? "Здесь уже лежит пол"
                    : $"Пол: плит {big.Count} больших, {small.Count} малых. ЛКМ — уложить");
                return;
            }

            FillGhosts.Show(FillPlanned);
            ShowFillLeak(seedPos, yaw, top);
            SayFill(_fillRegion.Free.Count + _fillRegion.Floored.Count == 0
                ? "Плита стоит в стене — сдвинь её"
                : _fillRegion.TooBig
                    ? "Больше 3000 м² — контур, похоже, не замкнут"
                    : "Контур не замкнут — красная линия идёт через дыру");
        }

        private static void SayFill(string text)
        {
            // A plate slid along a wall looks again five times a second; the same news
            // once is enough.
            if (text == _fillSaid) return;
            _fillSaid = text;
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, text);
        }

        /// <summary>
        /// The space around the plate at the floor's height: a paint-bucket flood over
        /// half-metre cells on the plate's own grid - see Geometry.FloodFloor - asking the
        /// physics, from just under the new floor to head height, two things: whether a
        /// passage between neighbouring cells is shut, and what stands at a cell's middle.
        ///
        /// Walls, beams, fences, doors and furniture shut passages. A wall a floor was
        /// snapped to stands on the line between two cells, and both of those are floor -
        /// a plate goes under a wall to its middle, as the game lays them - so walls are
        /// looked for across the line between two cell middles, not in the cells. A
        /// cell's own middle is asked for a floor already lying at this height, which is
        /// crossed and left alone, and for anything thick enough to fill it.
        /// </summary>
        private static FloorRegion FindFloorRegion(Vector3 seedPos, float yaw, float top)
        {
            var turn = Quaternion.Euler(0f, yaw, 0f);
            var across = turn * Vector3.right;
            var along = turn * Vector3.forward;
            FillKinds.Clear();

            Vector3 Middle(int i, int j)
            {
                var point = seedPos + across * ((i + 0.5f) * FillCell) + along * ((j + 0.5f) * FillCell);
                point.y = top + 0.7f;
                return point;
            }

            FloorCell Classify(int i, int j)
            {
                var count = Physics.OverlapBoxNonAlloc(Middle(i, j), new Vector3(0.05f, 0.8f, 0.05f), FillHits, turn,
                                                       PieceLayer, QueryTriggerInteraction.Ignore);
                var floored = false;
                for (var k = 0; k < count; k++)
                {
                    var kind = KindOf(FillHits[k], top);
                    if (kind == FloorCell.Wall) return FloorCell.Wall;
                    if (kind == FloorCell.Floored) floored = true;
                }

                return floored ? FloorCell.Floored : FloorCell.Free;
            }

            // A thin slice from one middle to the next: it meets whatever stands on the
            // line between them, and nothing that only runs alongside.
            bool Shut(GridCell from, GridCell to)
            {
                var half = from.I != to.I
                    ? new Vector3(FillCell * 0.5f, 0.8f, 0.05f)
                    : new Vector3(0.05f, 0.8f, FillCell * 0.5f);
                var centre = (Middle(from.I, from.J) + Middle(to.I, to.J)) * 0.5f;

                var count = Physics.OverlapBoxNonAlloc(centre, half, FillHits, turn, PieceLayer,
                                                       QueryTriggerInteraction.Ignore);
                for (var k = 0; k < count; k++)
                    if (KindOf(FillHits[k], top) == FloorCell.Wall) return true;
                return false;
            }

            return Geometry.FloodFloor(PlateCells, Classify, Shut, FillReach, FillMaxCells);
        }

        /// <summary>
        /// What one built collider is to a floor at this height. A flat slab lying at it
        /// is a floor already there; any other flat slab - a table top, a loft over head -
        /// is not in the way; everything else is wall.
        ///
        /// A slab is told apart by its own shape, not its bounding box: one thin side and
        /// two wide ones, and the thin one pointing up. A wall is a slab too, standing on
        /// its edge; a beam has two thin sides. Asked once per collider per look.
        /// </summary>
        private static FloorCell KindOf(Collider collider, float top)
        {
            if (FillKinds.TryGetValue(collider, out var known)) return known;

            var kind = FloorCell.Wall;
            if (IsLyingSlab(collider))
                kind = Mathf.Abs(collider.bounds.max.y - top) < 0.3f ? FloorCell.Floored : FloorCell.Free;

            FillKinds[collider] = kind;
            return kind;
        }

        private static bool IsLyingSlab(Collider collider)
        {
            Vector3 size;
            if (collider is BoxCollider box) size = box.size;
            else if (collider is MeshCollider mesh && mesh.sharedMesh != null) size = mesh.sharedMesh.bounds.size;
            else return false;

            var scale = collider.transform.lossyScale;
            size = new Vector3(Mathf.Abs(size.x * scale.x), Mathf.Abs(size.y * scale.y), Mathf.Abs(size.z * scale.z));

            var thin = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
            if (size[thin] > 0.45f || size[(thin + 1) % 3] < 0.9f || size[(thin + 2) % 3] < 0.9f) return false;

            var axis = thin == 0 ? Vector3.right : thin == 1 ? Vector3.up : Vector3.forward;
            return Mathf.Abs((collider.transform.rotation * axis).y) > 0.8f;
        }

        /// <summary>
        /// The plates as they go down: on the start plate's grid and turn, their tops level
        /// with its top - the small plates measured for that, not assumed to share the big
        /// one's thickness.
        /// </summary>
        private static void LayOutFloor(Vector3 seedPos, float yaw, float top, GameObject plate, GameObject tile,
                                        List<GridCell> big, List<GridCell> small, List<PiecePlacement> into)
        {
            var turn = Quaternion.Euler(0f, yaw, 0f);
            var across = turn * Vector3.right;
            var along = turn * Vector3.forward;
            var plateY = top - PivotBelowTop(plate);
            var tileY = top - PivotBelowTop(tile);

            foreach (var cell in big)
            {
                var at = seedPos + across * (2f * cell.I) + along * (2f * cell.J);
                at.y = plateY;
                into.Add(new PiecePlacement(plate, at, turn));
            }

            foreach (var cell in small)
            {
                var at = seedPos + across * (cell.I + 0.5f) + along * (cell.J + 0.5f);
                at.y = tileY;
                into.Add(new PiecePlacement(tile, at, turn));
            }
        }

        private static IEnumerator LayFloor(List<PiecePlacement> placements, bool paid)
        {
            var player = Player.m_localPlayer;
            if (player == null) yield break;

            _fillLaying = true;
            var record = BeginBuild("пол");

            // Paid for whole, and what does not go down handed back at the end.
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
                // «Разрушать»: a trunk or a rock inside the outline would stand through the floor.
                var cleared = BuildClearingActive ? ClearUnderPieces(placements) : 0;

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
                    $"Пол уложен: плит {built.Count}" + (cleared > 0 ? $", снесено: {cleared}" : ""));
                Log.LogInfo($"[AstvardServerMod] Floor laid: {built.Count} plates at "
                            + $"{(placements.Count > 0 ? placements[0].At : Vector3.zero)}.");
            }
            finally
            {
                RefundBill(owed);
                _fillLaying = false;
                if (record.Running) EndBuild(record);
                RefreshMenu();
            }
        }

        /// <summary>
        /// Draws the way out of an open space: from the plate, the shortest way the flood
        /// took to get out, which runs through the gap - red, at knee height, so it reads
        /// against any floor.
        /// </summary>
        private static void ShowFillLeak(Vector3 seedPos, float yaw, float top)
        {
            if (_fillRegion == null || _fillRegion.WayOut.Count < 2)
            {
                HideFillLeak();
                return;
            }

            if (_fillLeak == null)
            {
                var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                if (shader == null) return;

                _fillLeak = new GameObject("AstvardFillLeak");
                _fillLeakLine = _fillLeak.AddComponent<LineRenderer>();
                _fillLeakLine.material = new Material(shader);
                _fillLeakLine.startColor = new Color(1f, 0.2f, 0.15f, 0.9f);
                _fillLeakLine.endColor = _fillLeakLine.startColor;
                _fillLeakLine.widthMultiplier = 0.2f;
                _fillLeakLine.useWorldSpace = true;
                _fillLeakLine.numCapVertices = 2;
                _fillLeakLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _fillLeakLine.receiveShadows = false;
            }

            var turn = Quaternion.Euler(0f, yaw, 0f);
            var across = turn * Vector3.right;
            var along = turn * Vector3.forward;

            var way = _fillRegion.WayOut;
            _fillLeakLine.positionCount = way.Count;
            for (var k = 0; k < way.Count; k++)
            {
                var point = seedPos + across * ((way[k].I + 0.5f) * FillCell) + along * ((way[k].J + 0.5f) * FillCell);
                point.y = top + 0.4f;
                _fillLeakLine.SetPosition(k, point);
            }

            _fillLeak.SetActive(true);
        }

        private static void HideFillLeak()
        {
            if (_fillLeak != null) _fillLeak.SetActive(false);
        }
    }
}
