using System.Collections.Generic;
using Jotunn.Managers;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>
        /// Подписи над помеченными сундуками — чтобы видеть раскладку, а не вспоминать её.
        ///
        /// The mark has been readable all along, but only by looking at one chest at a time,
        /// and the whole point of marking a wall of them is to take it in at a glance. The
        /// game itself cannot help here: a chest has no name to give it, which is why people
        /// stand signs beside their storage. So the mod hangs the word over the chest.
        ///
        /// Only inside the zone the player is standing in, and only within sight, because a
        /// base of thirty chests would otherwise be a wall of text seen through the wall.
        /// </summary>
        private static BepInEx.Configuration.ConfigEntry<bool> _chestLabels;

        private static readonly List<TextMesh> ChestLabelPool = new List<TextMesh>();

        private static GameObject _chestLabelRoot;

        private static float _chestLabelScanAt;

        private static int _chestLabelsShown;

        // Far enough to read a wall of chests from the doorway, near enough that the
        // neighbour's storage is not labelled through the hill.
        private const float ChestLabelRange = 40f;

        private const float ChestLabelScan = 0.5f;

        // Reused rather than made afresh every scan: the list holds every piece of the
        // base, and a new one twice a second is rubbish for somebody else to sweep up.
        private static readonly List<Piece> ChestLabelPieces = new List<Piece>();

        // More than this over one zone is not a label, it is fog.
        private const int MaxChestLabels = 40;

        internal static void BindChestLabels(BepInEx.Configuration.ConfigFile config)
        {
            _chestLabels = config.Bind("Сортировка", "ChestLabels", true,
                "Показывать ли подпись над помеченными сундуками, пока ты в своей зоне. "
                + "Переключается в игре: «Функции» → «Сортировка».");

            _chestLabelLift = config.Bind("Сортировка", "LabelHeight", 0.5f,
                "На сколько метров подпись поднята над сундуком, от 0 до 5. Отсчёт от дна "
                + "сундука. Меняется на ходу: правь файл и смотри, перезапуск не нужен.");
        }

        internal static bool ChestLabelsOn
        {
            get { return _chestLabels == null || _chestLabels.Value; }
        }

        /// <summary>Из игры: высота подписи над сундуком.</summary>
        internal static void SetChestLabelLift(float metres)
        {
            if (_chestLabelLift != null) _chestLabelLift.Value = Mathf.Clamp(metres, 0f, 5f);
        }

        internal static void SetChestLabelsOn(bool on)
        {
            if (_chestLabels != null) _chestLabels.Value = on;
            if (!on) HideChestLabels();
        }

        /// <summary>
        /// From Update. The list of chests is rebuilt a few times a second - chests do not
        /// move - while the turn towards the camera is done every frame, because the player
        /// does.
        /// </summary>
        internal static void TickChestLabels()
        {
            if (GUIManager.IsHeadless()) return;

            var player = Player.m_localPlayer;
            if (player == null || !ChestLabelsOn || !SortingOn)
            {
                HideChestLabels();
                return;
            }

            if (Time.realtimeSinceStartup >= _chestLabelScanAt)
            {
                _chestLabelScanAt = Time.realtimeSinceStartup + ChestLabelScan;
                ScanChestLabels(player);
            }

            FaceChestLabels();
        }

        private static void ScanChestLabels(Player player)
        {
            var where = player.transform.position;
            var zones = SortingZones();
            var at = Sorting.ZoneAt(zones, where.x, where.z);

            // Outside every zone there is nothing to explain.
            if (at < 0)
            {
                HideChestLabels();
                return;
            }

            var zone = zones[at];
            var reach = Sorting.ClampRadius(zone.Radius) * (zone.Square ? SquareDiagonal : 1f);

            ChestLabelPieces.Clear();
            Piece.GetAllPiecesInRadius(new Vector3(zone.X, where.y, zone.Z), reach, ChestLabelPieces);

            var shown = 0;

            foreach (var piece in ChestLabelPieces)
            {
                if (shown >= MaxChestLabels) break;
                if (piece == null) continue;

                var container = piece.GetComponentInChildren<Container>();
                if (container == null) continue;

                var spot = container.transform.position;
                if (!Sorting.Inside(zone, spot.x, spot.z)) continue;
                if (Vector3.Distance(spot, where) > ChestLabelRange) continue;

                var note = SortMarkNote(container);
                if (note.Length == 0) continue;

                var label = ChestLabel(shown++);
                if (label == null) break;

                if (label.text != note) label.text = note;

                var colour = IsPrivateChest(container) ? PrivateLabelColour : MarkLabelColour;
                if (label.color != colour) label.color = colour;
                label.transform.position = spot + Vector3.up * ChestLabelLift;
                label.gameObject.SetActive(true);
            }

            for (var i = shown; i < ChestLabelPool.Count; i++)
                if (ChestLabelPool[i] != null) ChestLabelPool[i].gameObject.SetActive(false);

            _chestLabelsShown = shown;
        }

        /// <summary>Turns every shown label towards the camera, so none of them is read edge on.</summary>
        private static void FaceChestLabels()
        {
            if (_chestLabelsShown == 0) return;

            var camera = GameCamera.instance != null ? GameCamera.instance.transform : null;
            if (camera == null) return;

            for (var i = 0; i < _chestLabelsShown && i < ChestLabelPool.Count; i++)
            {
                var label = ChestLabelPool[i];
                if (label == null || !label.gameObject.activeSelf) continue;

                label.transform.rotation = camera.rotation;
            }
        }

        private static void HideChestLabels()
        {
            if (_chestLabelsShown == 0) return;

            foreach (var label in ChestLabelPool)
                if (label != null && label.gameObject.activeSelf) label.gameObject.SetActive(false);

            _chestLabelsShown = 0;
        }

        // Over the chest's own point, which is its base. A setting rather than a number in
        // the code because the right height is a matter of looking at it, and looking at it
        // means being in the game - where a rebuild costs a restart.
        private static BepInEx.Configuration.ConfigEntry<float> _chestLabelLift;

        private static float ChestLabelLift
        {
            get { return Mathf.Clamp(_chestLabelLift != null ? _chestLabelLift.Value : 0.5f, 0f, 5f); }
        }

        private static readonly Color MarkLabelColour = new Color(1f, 0.85f, 0.45f);

        private static readonly Color PrivateLabelColour = new Color(0.65f, 0.8f, 1f);

        /// <summary>One label, made the first time it is needed and kept from then on.</summary>
        private static TextMesh ChestLabel(int index)
        {
            while (ChestLabelPool.Count <= index)
            {
                var made = MakeChestLabel();
                if (made == null) return null;
                ChestLabelPool.Add(made);
            }

            return ChestLabelPool[index];
        }

        private static TextMesh MakeChestLabel()
        {
            var gui = GUIManager.Instance;
            var font = gui != null ? gui.AveriaSerifBold : null;

            // Without the game's own font there is nothing to draw with, and a font of our
            // own is not worth shipping for a label.
            if (font == null) return null;

            if (_chestLabelRoot == null) _chestLabelRoot = new GameObject("AstvardChestLabels");

            var go = new GameObject("Label");
            go.transform.SetParent(_chestLabelRoot.transform, false);

            var text = go.AddComponent<TextMesh>();
            text.font = font;
            text.fontSize = 48;
            text.anchor = TextAnchor.LowerCenter;
            text.alignment = TextAlignment.Center;

            // A TextMesh draws through the font's own material; without this it is invisible.
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.material = font.material;

            // The size above is in font pixels: shrunk to something that reads as a hand's
            // width over a chest rather than a billboard over the base.
            go.transform.localScale = Vector3.one * 0.03f;

            go.SetActive(false);
            return text;
        }

        internal static void DestroyChestLabels()
        {
            if (_chestLabelRoot != null) Destroy(_chestLabelRoot);
            ChestLabelPool.Clear();
            _chestLabelsShown = 0;
        }
    }
}
