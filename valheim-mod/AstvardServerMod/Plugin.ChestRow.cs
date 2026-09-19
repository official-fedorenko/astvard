using System.Collections.Generic;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>
        /// «Поставить сундуки»: пара сундуков по середине той постройки, на которую смотришь.
        ///
        /// A wall of chests is built one at a time with the hammer, and it never comes out
        /// straight: the hammer places from wherever the crosshair lands, on its own grid,
        /// while the shelf under it lies on its own. Two chests on one plate end up either
        /// touching, or with a gap, or half a cell aside - and it only shows once the wall
        /// is up.
        ///
        /// So the pair is tied to the piece itself: the middle of the row is the middle of
        /// the piece under the crosshair, and its top is the floor the chests stand on. The
        /// width of the row is measured off the chest rather than remembered as a number:
        /// the game has several chests, and "touching" is a different distance for each.
        /// </summary>
        private static readonly string[] ChestPrefabs =
        {
            // From the cheapest up. The names come out of the game's own data
            // (StreamingAssets/SoftRef/manifest_extended), not out of memory, and every one
            // is still asked of the running game: what this build does not have is not in
            // the list either.
            "piece_chest_wood", "piece_chest", "piece_chest_private",
            "piece_chest_blackmetal", "piece_chest_grausten", "piece_chest_barrel"
        };

        /// <summary>Сколько сундуков в ряду. Хозяин просил два — столько и есть.</summary>
        private const int ChestRowCount = 2;

        /// <summary>
        /// Щель между сундуками.
        ///
        /// Nothing: they stand touching in the owner's base, and two of them then cover a
        /// two-metre plate exactly.
        /// </summary>
        private const float ChestRowGap = 0f;

        /// <summary>Ширина сундука, если померить его не вышло вовсе.</summary>
        private const float DefaultChestWidth = 1f;

        /// <summary>
        /// «Рядом» вместо «в ряд»: пара боком, вдвоём на одной плите, лицом к игроку.
        ///
        /// The measured wooden chest is 1.64 m across, so two of them shoulder to shoulder
        /// take 3.29 m and hang off a two-metre plate on both sides. Turned sideways the
        /// same two come to about two metres - one plate, which is how the owner's wall of
        /// chests is built. The turn is decided by the measurement, not by a number written
        /// here: whichever way round makes the pair narrower is the way it stands.
        ///
        /// The row keeps the piece's own turn, because a long wall has to line up with the
        /// building; the pair standing abreast takes the player's instead, so the chests
        /// look at whoever put them there.
        /// </summary>
        private static bool _chestsAbreast;

        /// <summary>Шаг, при котором сундуки стоят вплотную: ближе их не сводить.</summary>
        private static float _chestTouch = DefaultChestWidth;

        /// <summary>Шаг, на котором пара стоит сейчас; чтобы не пересобирать её каждый кадр.</summary>
        private static float _chestStep;

        /// <summary>The chests this build of the game has, gathered when the page opens.</summary>
        private static readonly List<GameObject> ChestKinds = new List<GameObject>();

        private static int _chestKind;

        /// <summary>
        /// True while a row of chests is in hand: the preview then sits on the piece under
        /// the crosshair instead of floating ahead of the player, and levels no ground.
        /// </summary>
        private static bool _onSurface;

        private static void RefreshChestKinds()
        {
            ChestKinds.Clear();
            if (ZNetScene.instance == null) return;

            foreach (var name in ChestPrefabs)
            {
                var prefab = ZNetScene.instance.GetPrefab(name);
                if (prefab == null) continue;

                // A piece with a container in it, not merely a name off the list: the game
                // knows the treasure chest too, and nothing builds it - it would be a button
                // leading nowhere.
                if (prefab.GetComponent<Piece>() == null) continue;
                if (prefab.GetComponentInChildren<Container>() == null) continue;

                ChestKinds.Add(prefab);
            }

            if (_chestKind >= ChestKinds.Count) _chestKind = 0;
        }

        private static GameObject CurrentChest()
        {
            if (ChestKinds.Count == 0) RefreshChestKinds();
            if (ChestKinds.Count == 0) return null;

            return ChestKinds[Mathf.Clamp(_chestKind, 0, ChestKinds.Count - 1)];
        }

        /// <summary>
        /// Имя детали словами.
        ///
        /// `Piece.m_name` is not a name but a translation key - `$piece_chestwood` - and
        /// shown to a player it reads as a broken mod. The same pit the item names have.
        /// </summary>
        private static string PieceTitle(GameObject prefab)
        {
            var piece = prefab != null ? prefab.GetComponent<Piece>() : null;
            var name = piece != null ? piece.m_name : null;
            if (string.IsNullOrEmpty(name)) return prefab != null ? prefab.name : "?";

            return Localization.instance != null ? Localization.instance.Localize(name) : name;
        }

        /// <summary>
        /// The piece's box in its own axes. Wanted twice over: the width, so the chests end
        /// up touching, and the bottom, so they sit on the shelf instead of sinking half way
        /// into it - a piece's origin is sometimes at its base and sometimes in its middle.
        /// </summary>
        private static bool LocalBox(GameObject prefab, out Bounds box)
        {
            box = new Bounds();
            if (prefab == null) return false;

            var root = prefab.transform;
            var have = false;

            foreach (var collider in prefab.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null || collider.isTrigger) continue;

                Vector3 centre, size;
                if (collider is BoxCollider slab)
                {
                    centre = slab.center;
                    size = slab.size;
                }
                else if (collider is MeshCollider mesh && mesh.sharedMesh != null)
                {
                    centre = mesh.sharedMesh.bounds.center;
                    size = mesh.sharedMesh.bounds.size;
                }
                else if (collider is CapsuleCollider capsule)
                {
                    centre = capsule.center;
                    size = new Vector3(capsule.radius * 2f, capsule.height, capsule.radius * 2f);
                }
                else if (collider is SphereCollider ball)
                {
                    centre = ball.center;
                    size = Vector3.one * (ball.radius * 2f);
                }
                else
                {
                    continue;
                }

                AddCorners(root, collider.transform, centre, size, ref box, ref have);
            }

            // The model, when there are no colliders at all: measuring the look beats
            // measuring nothing.
            if (!have)
                foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter == null || filter.sharedMesh == null) continue;
                    AddCorners(root, filter.transform, filter.sharedMesh.bounds.center,
                               filter.sharedMesh.bounds.size, ref box, ref have);
                }

            return have;
        }

        /// <summary>
        /// Eight corners of a box, brought into the piece's own axes. Corners rather than a
        /// centre and a size: a child object is sometimes turned, and its box in the piece's
        /// axes is then wider than its own.
        /// </summary>
        private static void AddCorners(Transform root, Transform where, Vector3 centre, Vector3 size,
                                       ref Bounds box, ref bool have)
        {
            var half = size * 0.5f;

            for (var i = 0; i < 8; i++)
            {
                var corner = centre + new Vector3(
                    (i & 1) == 0 ? -half.x : half.x,
                    (i & 2) == 0 ? -half.y : half.y,
                    (i & 4) == 0 ? -half.z : half.z);

                var local = root.InverseTransformPoint(where.TransformPoint(corner));
                if (!have)
                {
                    box = new Bounds(local, Vector3.zero);
                    have = true;
                }
                else
                {
                    box.Encapsulate(local);
                }
            }
        }

        /// <summary>
        /// Puts the pair into the preview. From there it is like any other build of the
        /// mod's: a click places, Esc takes it away, P pins it, the arrows nudge it, Q and E
        /// turn it.
        /// </summary>
        internal static void PlaceChestRow()
        {
            // The builder walks the clipboard across a yield, so swapping it out under a
            // build already going up would end that build wherever it had got to.
            if (BuildInProgress) return;

            var prefab = CurrentChest();
            if (prefab == null)
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Сундук не нашёлся — смотри лог");
                Log.LogWarning($"[AstvardServerMod] Chest row: none of {string.Join(", ", ChestPrefabs)} "
                               + "is a buildable chest.");
                return;
            }

            var width = DefaultChestWidth;
            var across = DefaultChestWidth;
            var lift = 0f;
            if (LocalBox(prefab, out var box))
            {
                width = Mathf.Max(0.1f, box.size.x);
                across = Mathf.Max(0.1f, box.size.z);
                // The bottom of the box onto the plane of the shelf: half a height for a
                // piece whose origin is in its middle, nothing for one standing on its own.
                // A turn about the upright leaves this alone, so it is measured once.
                lift = -box.min.y;
            }

            // Sideways only when it actually gains room; a chest that is already the
            // narrower way round would only be turned for the sake of turning.
            var sideways = _chestsAbreast && across < width;
            var step = sideways ? across : width;
            var turn = sideways ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;

            _chestTouch = step;
            _chestStep = step;

            var offsets = Geometry.RowOffsets(step, ChestRowGap, ChestRowCount);

            Clipboard.Clear();
            foreach (var offset in offsets)
                Clipboard.Add(new CopiedPiece
                {
                    Prefab = prefab.name,
                    LocalPos = new Vector3(offset, lift, 0f),
                    LocalRot = turn
                });

            StartPlacement("сундуки");

            // After StartPlacement, not before: it clears what the placement before it set,
            // and this would have been cleared with the rest.
            _onSurface = true;
            MarkPlayerChests();

            InventoryGui.instance?.Hide();
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                "Наводись на постройку: сундуки встанут по её середине. Q и E — поворот");
            Log.LogInfo($"[AstvardServerMod] Chest row: {ChestRowCount} x {prefab.name}, box "
                        + $"{width:F2} x {across:F2} m, {(sideways ? "sideways" : "square on")}, span "
                        + $"{Geometry.RowSpan(step, ChestRowGap, ChestRowCount):F2} m.");
        }

        /// <summary>
        /// What this is for a player: paid, and the bag is the whole price, asked before the
        /// build; free, and the click goes to the server, which holds it to the pause between
        /// builds. Says nothing for an admin - their «Ресурсы» decides that at the click.
        /// </summary>
        private static void MarkPlayerChests()
        {
            if (IsAdminUnlocked) return;

            if (RulePaid("chests"))
            {
                _playerPaidPlacement = true;
                return;
            }

            _playerPlacement = true;
            _playerPlacementName = "#chests";
        }

        // The same ray the hammer looks for a place with: its list of layers is already the
        // list of things a piece can be put on.
        private static int? _placeRayMask;

        private static int PlaceRayMask
        {
            get
            {
                if (_placeRayMask == null)
                    _placeRayMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece",
                                                      "piece_nonsolid", "terrain", "vehicle");
                return _placeRayMask.Value;
            }
        }

        private static readonly RaycastHit[] SurfaceHits = new RaycastHit[16];

        /// <summary>
        /// Where the crosshair points. A piece answers with its middle and its turn - that is
        /// the whole button - while the ground answers with the point under the crosshair: it
        /// has neither a middle nor a side, and inventing them would be a lie.
        /// </summary>
        private static bool AimAtSurface(Player player, out Vector3 centre, out float yaw, out Collider surface)
        {
            centre = Vector3.zero;
            yaw = 0f;
            surface = null;

            var eye = GameCamera.instance != null ? GameCamera.instance.transform : null;
            if (eye == null) return false;

            // The preview's colliders are switched off where it is made, so it can never
            // catch itself with this.
            var count = Physics.RaycastNonAlloc(eye.position, eye.forward, SurfaceHits,
                                                PlacementDistance, PlaceRayMask,
                                                QueryTriggerInteraction.Ignore);
            if (count <= 0) return false;

            var nearest = -1;
            for (var i = 0; i < count; i++)
            {
                if (SurfaceHits[i].collider == null) continue;
                if (nearest < 0 || SurfaceHits[i].distance < SurfaceHits[nearest].distance) nearest = i;
            }

            if (nearest < 0) return false;

            var hit = SurfaceHits[nearest];
            var piece = hit.collider.GetComponentInParent<Piece>();
            if (piece == null)
            {
                // Земля: у неё нет ни краёв, ни середины, раскладывать по ней нечего.
                centre = hit.point;
                yaw = player != null ? Quaternion.LookRotation(GhostForward(player)).eulerAngles.y : 0f;
                return true;
            }

            // The box of the part the ray actually hit, not of the whole piece: on a stair or
            // on something built of several parts the middle of "all of it" would be in the air.
            surface = hit.collider;
            var box = hit.collider.bounds;
            centre = new Vector3(box.center.x, box.max.y, box.center.z);
            yaw = piece.transform.eulerAngles.y;
            return true;
        }

        /// <summary>
        /// The row's preview: it sits on the piece under the crosshair, by that piece's middle
        /// and that piece's turn. Pinned (P) it stays where it was left - the crosshair is no
        /// longer its master, and the arrows move it. The turn (Q/E) and the height (Shift+Q/E)
        /// go on top: a shelf can hold two floors of chests, and turning them straight is what
        /// the owner asked for.
        ///
        /// The angle is not rounded to the game's grid here, unlike an ordinary preview: it is
        /// taken off the shelf itself, and rounding to 22.5° would set the chests across it.
        /// </summary>
        private static void UpdateSurfaceGhost(Player player)
        {
            if (GhostRoot == null) return;

            Vector3 position;
            float heading;

            if (_ghostPinned)
            {
                position = _ghostPinnedAt;
                heading = _ghostPinnedHeading;
            }
            else if (AimAtSurface(player, out var spot, out var surfaceYaw, out var surface))
            {
                position = spot;
                // Abreast looks at the player, a row looks the way the piece under it does.
                heading = _chestsAbreast ? PlayerHeading(player) : surfaceYaw;
                SpreadOver(surface, heading + _placeYaw);
            }
            else
            {
                // Aiming at the sky: let the pair hang ahead of the player like every other
                // preview, rather than vanish every time somebody looks up.
                var forward = GhostForward(player);
                position = player.transform.position + forward * PlacementDistance;
                if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(position, out var ground))
                    position.y = ground;
                heading = Quaternion.LookRotation(forward).eulerAngles.y;
            }

            position.y += _placeHeight;
            GhostRoot.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, heading + _placeYaw, 0f));
        }

        /// <summary>
        /// Раскладывает пару по самой плите: каждому сундуку своя половина, и он стоит
        /// посередине неё.
        ///
        /// Standing them touching in the middle of a plate leaves all the spare room at the
        /// two ends, and on a two-metre plate that is a quarter of a metre of nothing on
        /// either side of a pair that is stuck together. Dividing the plate instead puts the
        /// same spare room in three equal parts, which is what the eye reads as "evenly".
        ///
        /// The distance is taken off the piece being aimed at, across the row as it stands
        /// now, so a wider plate spreads them further and a narrow one brings them back to
        /// touching - closer than that they are never brought.
        /// </summary>
        private static void SpreadOver(Collider surface, float yaw)
        {
            if (surface == null || Clipboard.Count == 0) return;

            var across = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
            var half = HalfAcross(surface, across);
            if (half <= 0f) return;

            // Half the plate is the distance between two chests each standing in the middle
            // of its own half.
            LayRow(Mathf.Max(_chestTouch, half));
        }

        /// <summary>
        /// Сколько у детали поперёк этой стороны, от середины.
        ///
        /// The box's own half-sizes turned onto the direction asked about, rather than the
        /// world-aligned bounds: a plate laid at 22.5° has bounds a good deal wider than the
        /// plate, and the pair would be spread to the width of a box that is not there.
        /// </summary>
        private static float HalfAcross(Collider surface, Vector3 across)
        {
            Vector3 size;
            if (surface is BoxCollider box) size = box.size;
            else if (surface is MeshCollider mesh && mesh.sharedMesh != null) size = mesh.sharedMesh.bounds.size;
            else
            {
                var world = surface.bounds.size;
                return (Mathf.Abs(across.x) * world.x + Mathf.Abs(across.z) * world.z) * 0.5f;
            }

            var shape = surface.transform;
            var scale = shape.lossyScale;
            var half = new Vector3(Mathf.Abs(size.x * scale.x),
                                   Mathf.Abs(size.y * scale.y),
                                   Mathf.Abs(size.z * scale.z)) * 0.5f;

            return Mathf.Abs(Vector3.Dot(across, shape.right)) * half.x
                   + Mathf.Abs(Vector3.Dot(across, shape.up)) * half.y
                   + Mathf.Abs(Vector3.Dot(across, shape.forward)) * half.z;
        }

        /// <summary>
        /// Ставит пару на этот шаг - и в проекции, и в том, что из неё построится. Обе
        /// половины вместе: постройка читает буфер, а видит игрок призраков, и разъехаться
        /// им нельзя.
        /// </summary>
        private static void LayRow(float step)
        {
            // Меньше сантиметра - это дрожь прицела, а не новая раскладка.
            if (Mathf.Abs(step - _chestStep) < 0.01f) return;
            _chestStep = step;

            var offsets = Geometry.RowOffsets(step, 0f, Clipboard.Count);

            for (var i = 0; i < Clipboard.Count && i < offsets.Length; i++)
            {
                var entry = Clipboard[i];
                entry.LocalPos = new Vector3(offsets[i], entry.LocalPos.y, entry.LocalPos.z);
                Clipboard[i] = entry;

                if (i < Ghosts.Count && Ghosts[i] != null) Ghosts[i].transform.localPosition = entry.LocalPos;
            }
        }

        /// <summary>
        /// Куда смотрит игрок, по сетке игры.
        ///
        /// Rounded to the game's own 22.5° step, unlike the turn taken off a piece: there
        /// the angle is the shelf's and rounding would set the chests across it, while here
        /// it is a person standing on a floor, and a pair at 7° to everything else is not
        /// what anybody meant by "facing me".
        /// </summary>
        private static float PlayerHeading(Player player)
        {
            if (player == null) return 0f;

            var yaw = Quaternion.LookRotation(GhostForward(player)).eulerAngles.y;
            return Mathf.Round(yaw / RotationStep) * RotationStep;
        }

        // ---------------- страница ----------------

        internal static GameObject ChestRowButton;

        internal static GameObject ChestRowHint;

        internal static GameObject ChestRowKindButton;

        internal static GameObject ChestRowModeButton;

        internal static GameObject ChestRowPlaceButton;

        private void CreateChestRowWidgets(GUIManager gui)
        {
            ChestRowButton = MakeButton(gui, "Поставить сундуки", () =>
            {
                RefreshChestKinds();
                MenuState = StateChestRow;
                RefreshMenu();
            });

            ChestRowHint = MakeText(gui, "");

            ChestRowKindButton = MakeButton(gui, "", () =>
            {
                if (ChestKinds.Count == 0) RefreshChestKinds();
                if (ChestKinds.Count > 0) _chestKind = (_chestKind + 1) % ChestKinds.Count;
                RefreshMenu();
            });

            ChestRowModeButton = MakeButton(gui, "", () =>
            {
                _chestsAbreast = !_chestsAbreast;
                RefreshMenu();
            });

            ChestRowPlaceButton = MakeButton(gui, "Поставить пару", PlaceChestRow);
        }

        private static void RefreshChestRowVisibility()
        {
            var chest = CurrentChest();
            SetLabel(ChestRowKindButton, $"Сундук: {PieceTitle(chest)}");

            SetLabel(ChestRowModeButton, _chestsAbreast ? "Ставить: рядом" : "Ставить: в ряд");

            var hint = ChestRowHint != null ? ChestRowHint.GetComponentInChildren<Text>(true) : null;
            if (hint != null)
                hint.text = $"Два сундука встают по{NEWLINE}середине той постройки, на{NEWLINE}"
                            + $"которую смотришь, и на её верх.{NEWLINE}{NEWLINE}"
                            + $"«В ряд» — вдоль самой{NEWLINE}постройки, как стоит она.{NEWLINE}"
                            + $"«Рядом» — боком, вдвоём на{NEWLINE}одной плите и лицом к тебе.{NEWLINE}{NEWLINE}"
                            + $"ЛКМ — поставить, Shift+ЛКМ —{NEWLINE}поставить и ставить дальше.{NEWLINE}"
                            + $"Esc — отмена, P — закрепить,{NEWLINE}"
                            + $"стрелки — сдвиг.{NEWLINE}Q и E — поворот,{NEWLINE}Shift+Q/E — выше и ниже.";

            // A player while the admins keep it open; an admin always.
            SetActive(ChestRowButton, MenuState == StateChestWork && RuleAllows("chests"));

            var page = MenuState == StateChestRow;
            SetActive(ChestRowHint, page);
            SetActive(ChestRowKindButton, page && ChestKinds.Count > 1);
            SetActive(ChestRowModeButton, page);
            SetActive(ChestRowPlaceButton, page);
        }
    }
}
