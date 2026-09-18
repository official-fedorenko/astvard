using System.Collections.Generic;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>
        /// Что на самом деле встанет: клетки зоны, показанные до установки.
        ///
        /// A kept zone is not the circle its radius suggests. The game loads whole zones of
        /// 64 m, and ZoneCells takes the corners of the box around the radius and turns them
        /// into cells - so a zone of 40 m keeps four cells, 128 m of ground, and one of 33 m
        /// may keep four or one depending on where in a cell the player happens to stand.
        /// Nothing in the panel used to say so; the radius was typed, the zone appeared, and
        /// how much of the world it was holding awake was anybody's guess.
        ///
        /// So the outline drawn here is the cells, not the radius. The radius is drawn too,
        /// faintly, because seeing the two apart is the whole point: this is what you asked
        /// for, that is what you get. Standing one step to the side and watching the grid
        /// snap is a better explanation than any hint text.
        /// </summary>
        private static bool _zonePreviewing;

        /// <summary>Whose zone is being placed: a player's own, or an admin's from the zone page.</summary>
        private static bool _zonePreviewMine;

        private static bool _zonePinned;

        private static Vector3 _zonePinnedAt;

        private static GameObject _zonePreview;

        private static LineRenderer _zoneRing;

        private static readonly List<LineRenderer> ZoneGridLines = new List<LineRenderer>();

        // Which cells the grid currently shows, so it is rebuilt when they change and not
        // once a frame: the cells only move when the player crosses a cell edge, and the
        // ground under every line has to be sampled each time they do.
        private static int _gridMinX, _gridMinY, _gridMaxX, _gridMaxY;

        private static bool _gridDrawn;

        // A point every so many metres along a grid line, so it follows the ground instead
        // of hanging over the dips.
        private const float GridStep = 16f;

        private static readonly Color ZoneCellColour = new Color(0.45f, 0.75f, 1f);

        internal static bool IsZonePreviewing
        {
            get { return _zonePreviewing; }
        }

        /// <summary>The radius as this page means it, held to what the server would allow.</summary>
        private static int ZonePreviewRadius()
        {
            var typed = _zonePreviewMine
                ? ParseField(PlayerZoneRadiusInput, MinZoneRadius)
                : ParseField(ZoneSizeInput, MinZoneRadius);

            var top = _zonePreviewMine && !IsAdminUnlocked
                ? Mathf.RoundToInt(RuleLimit("zone", MaxZoneRadius))
                : MaxZoneRadius;

            return Mathf.Clamp(Mathf.RoundToInt(typed), MinZoneRadius, Mathf.Max(MinZoneRadius, top));
        }

        private static Vector3 ZonePreviewCentre(Player player)
        {
            if (!_zonePinned) return player.transform.position;

            var centre = _zonePinnedAt;
            var system = ZoneSystem.instance;
            if (system != null && system.GetGroundHeight(centre, out var ground)) centre.y = ground;
            return centre;
        }

        internal static void StartZonePreview(bool mine)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            if (mine && !RuleAllows("zone"))
            {
                player.Message(MessageHud.MessageType.Center, "Зоны игрокам сейчас закрыты");
                return;
            }

            // One projection at a time, or one click would answer two of them.
            if (IsPlacing) CancelPlacement();
            if (IsFencePreviewing) CancelFencePreview();
            if (IsAreaPreviewing) CancelAreaPreview();
            if (IsSortZonePreviewing) CancelSortZonePreview();
            CancelWallPreview();

            _zonePreviewing = true;
            _zonePreviewMine = mine;
            _zonePinned = false;
            _gridDrawn = false;
            NoteToolStart();
            InventoryGui.instance?.Hide();
            player.Message(MessageHud.MessageType.Center, "ЛКМ — поставить зону, Esc — отменить, P — закрепить");
        }

        internal static void CancelZonePreview()
        {
            if (!_zonePreviewing) return;

            _zonePreviewing = false;
            _zonePinned = false;
            _gridDrawn = false;
            if (_zonePreview != null) _zonePreview.SetActive(false);
        }

        /// <summary>LMB, Esc, P and the arrows while the cells are shown.</summary>
        internal static bool HandleZonePreviewInput()
        {
            if (!_zonePreviewing) return false;

            var player = Player.m_localPlayer;
            if (player == null)
            {
                CancelZonePreview();
                return false;
            }

            if (InventoryGui.IsVisible() || Chat.instance?.HasFocus() == true) return false;

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                NoteEscapeUsed();
                CancelZonePreview();
                player.Message(MessageHud.MessageType.Center, "Отменено");
                RefreshMenu();
                return true;
            }

            if (Input.GetKeyDown(PinKey))
            {
                _zonePinned = !_zonePinned;
                if (_zonePinned) _zonePinnedAt = player.transform.position;
                SayPinned(_zonePinned);
                return true;
            }

            if (_zonePinned && PinNudgeThisFrame(out var step))
            {
                _zonePinnedAt += step;
                return true;
            }

            if (Input.GetMouseButtonDown(0) && Time.time - _toolMarkedAt > MarkDeafSeconds)
            {
                _inputHeldUntil = Time.time + 0.3f;

                var centre = ZonePreviewCentre(player);
                var radius = ZonePreviewRadius();

                // Cut to the players' limit without a word, a zone smaller than the number
                // typed reads as the button being broken.
                if (_zonePreviewMine)
                    SayHeldToLimit("Зона", ParseField(PlayerZoneRadiusInput, MinZoneRadius), radius);

                ZRoutedRpc.instance?.InvokeRoutedRPC(_zonePreviewMine ? RpcZoneMine : RpcZoneAdd,
                    centre.x, centre.z, radius);

                CancelZonePreview();
                RefreshMenu();
                return true;
            }

            return false;
        }

        /// <summary>Draws the cells and the radius from Update, while the preview is up.</summary>
        internal static void UpdateZonePreview()
        {
            var player = Player.m_localPlayer;
            if (!_zonePreviewing || player == null)
            {
                if (_zonePreview != null) _zonePreview.SetActive(false);
                return;
            }

            if (_zoneRing == null && !CreateZonePreview()) return;

            var centre = ZonePreviewCentre(player);
            var radius = ZonePreviewRadius();

            _zonePreview.SetActive(true);

            // What was asked for, faint, under what will actually be kept.
            DrawGroundRing(_zoneRing, centre, radius);

            // The same arithmetic the server uses, called rather than copied: a preview that
            // worked it out for itself would be right until one of the two was changed.
            CellsFor(centre.x, centre.z, radius, out var min, out var max);
            DrawZoneGrid(centre, min, max);
        }

        private static void DrawZoneGrid(Vector3 centre, Vector2s min, Vector2s max)
        {
            if (_gridDrawn && min.x == _gridMinX && min.y == _gridMinY
                && max.x == _gridMaxX && max.y == _gridMaxY)
                return;

            _gridMinX = min.x;
            _gridMinY = min.y;
            _gridMaxX = max.x;
            _gridMaxY = max.y;
            _gridDrawn = true;

            var system = ZoneSystem.instance;
            var size = system != null ? system.m_zoneSize : 64f;
            var half = size * 0.5f;

            // The outer edges of the block of cells, in world metres.
            var west = ZoneSystem.GetZonePos(new Vector2s(min.x, min.y)).x - half;
            var south = ZoneSystem.GetZonePos(new Vector2s(min.x, min.y)).z - half;
            var east = ZoneSystem.GetZonePos(new Vector2s(max.x, max.y)).x + half;
            var north = ZoneSystem.GetZonePos(new Vector2s(max.x, max.y)).z + half;

            var down = max.x - min.x + 2;   // lines running north-south, one past each cell
            var across = max.y - min.y + 2;
            var used = 0;

            for (var i = 0; i < down; i++)
            {
                var x = west + i * size;
                DrawGroundLine(GridLine(used++), new Vector3(x, centre.y, south), new Vector3(x, centre.y, north));
            }

            for (var i = 0; i < across; i++)
            {
                var z = south + i * size;
                DrawGroundLine(GridLine(used++), new Vector3(west, centre.y, z), new Vector3(east, centre.y, z));
            }

            for (var i = used; i < ZoneGridLines.Count; i++) ZoneGridLines[i].enabled = false;
        }

        /// <summary>A line of the grid, made the first time it is needed and kept after.</summary>
        private static LineRenderer GridLine(int index)
        {
            while (ZoneGridLines.Count <= index)
            {
                var line = MakeGroundLine(_zonePreview.transform, "Cell" + ZoneGridLines.Count, false);
                line.widthMultiplier = 0.4f;
                line.startColor = Faded(ZoneCellColour, 0.85f);
                line.endColor = line.startColor;
                ZoneGridLines.Add(line);
            }

            ZoneGridLines[index].enabled = true;
            return ZoneGridLines[index];
        }

        private static void DrawGroundLine(LineRenderer line, Vector3 from, Vector3 to)
        {
            var system = ZoneSystem.instance;
            var length = Vector3.Distance(from, to);
            var points = Mathf.Max(2, Mathf.CeilToInt(length / GridStep) + 1);

            line.positionCount = points;
            for (var i = 0; i < points; i++)
            {
                var point = Vector3.Lerp(from, to, i / (float)(points - 1));
                if (system != null && system.GetGroundHeight(point, out var ground)) point.y = ground;
                point.y += 0.15f;
                line.SetPosition(i, point);
            }
        }

        private static bool CreateZonePreview()
        {
            if (PreviewMaterial() == null) return false;

            _zonePreview = new GameObject("AstvardZonePreview");
            _zoneRing = MakeGroundLine(_zonePreview.transform, "Radius", true);
            _zoneRing.widthMultiplier = 0.3f;
            _zoneRing.startColor = Faded(PreviewGood, 0.45f);
            _zoneRing.endColor = _zoneRing.startColor;
            return true;
        }

        internal static void DestroyZonePreview()
        {
            if (_zonePreview != null) Destroy(_zonePreview);
            ZoneGridLines.Clear();
        }

    }
}
