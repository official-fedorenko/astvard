using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>
        /// Насколько далеко от станции ищется назначенный сундук — и как эту даль увидеть.
        ///
        /// Число было зашито в код: двадцать четыре метра, о которых знал только тот, кто их
        /// туда вписал. На тесной базе этого много - сундук соседней мастерской оказывается
        /// «рядом», - а на просторной мало, и понять, почему печь не отдаёт слитки в сундук
        /// через двор, снаружи нечем.
        ///
        /// Круг рисуется вокруг игрока, а не вокруг станции: станций много, и обводить их все
        /// значит закрыть базу кольцами. Радиус один и тот же, так что круг у ног показывает
        /// именно ту даль, с которой работает каждая из них.
        /// </summary>
        private static BepInEx.Configuration.ConfigEntry<float> _chestZoneRadius;

        internal static void BindChestZone(BepInEx.Configuration.ConfigFile config)
        {
            _chestZoneRadius = config.Bind("Наполнение и сбор", "ChestRadius", 24f,
                "На сколько метров от печи, плавильни или улья ищется назначенный сундук, "
                + "от 8 до 64. Меняется в игре: «Функции» → «Зона сундуков».");

            _chestZoneSquare = config.Bind("Наполнение и сбор", "ChestSquare", false,
                "Считать эту зону квадратом. false — кругом. Дома у людей прямоугольные, "
                + "и квадрат ложится на них ровнее.");
        }

        /// <summary>
        /// An assigned chest is a deliberate choice, so it reaches further than the
        /// "whatever is closest" fallback.
        /// </summary>
        internal static float AssignedChestRadius
        {
            get { return Mathf.Clamp(_chestZoneRadius != null ? _chestZoneRadius.Value : 24f, 8f, 64f); }
        }

        private static BepInEx.Configuration.ConfigEntry<bool> _chestZoneSquare;

        /// <summary>Квадратом или кругом. У домов углы, у круга их нет.</summary>
        internal static bool ChestZoneSquare
        {
            get { return _chestZoneSquare != null && _chestZoneSquare.Value; }
        }

        internal static void SetChestZoneSquare(bool square)
        {
            if (_chestZoneSquare != null) _chestZoneSquare.Value = square;
        }

        /// <summary>
        /// Попадает ли сундук в зону этой станции. Высота не спрашивается, как и у зоны
        /// сортировки: сундук этажом ниже стоит в том же доме.
        /// </summary>
        internal static bool InChestZone(Vector3 origin, Vector3 spot)
        {
            var reach = AssignedChestRadius;
            var dx = spot.x - origin.x;
            var dz = spot.z - origin.z;

            if (ChestZoneSquare) return Mathf.Abs(dx) <= reach && Mathf.Abs(dz) <= reach;
            return dx * dx + dz * dz <= reach * reach;
        }

        internal static void SetAssignedChestRadius(float metres)
        {
            if (_chestZoneRadius != null) _chestZoneRadius.Value = Mathf.Clamp(metres, 8f, 64f);
        }

        // ---------------- подсветка ----------------

        private static bool _chestZoneShown;

        private static GameObject _chestZoneObject;

        private static LineRenderer _chestZoneLine;

        internal static void ToggleChestZone()
        {
            _chestZoneShown = !_chestZoneShown;
            if (!_chestZoneShown && _chestZoneObject != null) _chestZoneObject.SetActive(false);
        }

        /// <summary>
        /// Круг у ног, пока подсветка включена. Рисуется каждый кадр, в отличие от контура
        /// поставленной зоны: этот ходит вместе с игроком, и замри он на месте - показывал бы
        /// не ту даль, о которой рассказывает.
        /// </summary>
        internal static void UpdateChestZone()
        {
            var player = Player.m_localPlayer;
            if (!_chestZoneShown || player == null || GUIManager.IsHeadless())
            {
                if (_chestZoneObject != null) _chestZoneObject.SetActive(false);
                return;
            }

            if (_chestZoneLine == null)
            {
                if (PreviewMaterial() == null) return;

                _chestZoneObject = new GameObject("AstvardChestZone");
                _chestZoneLine = MakeGroundLine(_chestZoneObject.transform, "Ring", true);
                _chestZoneLine.widthMultiplier = 0.4f;
                _chestZoneLine.startColor = Faded(ChestZoneColour, 0.7f);
                _chestZoneLine.endColor = _chestZoneLine.startColor;
            }

            _chestZoneObject.SetActive(true);
            if (ChestZoneSquare)
                DrawGroundBox(_chestZoneLine, player.transform.position, AssignedChestRadius, 0f);
            else
                DrawGroundRing(_chestZoneLine, player.transform.position, AssignedChestRadius);
        }

        internal static void DestroyChestZone()
        {
            if (_chestZoneObject != null) Destroy(_chestZoneObject);
            _chestZoneLine = null;
            _chestZoneShown = false;
        }

        // Синий: чтобы не спутать с зелёной зоной сортировки и жёлтой площадкой рельефа.
        private static readonly Color ChestZoneColour = new Color(0.45f, 0.7f, 1f);

        // ---------------- страница ----------------

        internal static GameObject ChestZoneButton;

        internal static GameObject ChestZoneHint;

        internal static GameObject ChestZoneInput;

        internal static GameObject ChestZoneApply;

        internal static GameObject ChestZoneShowButton;

        internal static GameObject ChestZoneShapeButton;

        private void CreateChestZoneWidgets(GUIManager gui)
        {
            ChestZoneButton = MakeButton(gui, "", () =>
            {
                SetFieldText(ChestZoneInput,
                    AssignedChestRadius.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
                MenuState = StateChestZone;
                RefreshMenu();
            });

            ChestZoneHint = MakeText(gui, "");

            ChestZoneInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "метры, напр. 24", 16, 160f, 32f);
            AddFixedSize(ChestZoneInput, 160f, 32f);

            ChestZoneApply = MakeButton(gui, "Применить", () =>
            {
                SetAssignedChestRadius(ParseField(ChestZoneInput, AssignedChestRadius));
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    $"Сундуки ищутся в {AssignedChestRadius:0.#} м от станции");
                MenuState = StateFeatures;
                RefreshMenu();
            });

            ChestZoneShapeButton = MakeButton(gui, "", () =>
            {
                SetChestZoneSquare(!ChestZoneSquare);
                RefreshMenu();
            });

            ChestZoneShowButton = MakeButton(gui, "", () =>
            {
                ToggleChestZone();
                RefreshMenu();
            });
        }

        private static void RefreshChestZoneVisibility()
        {
            SetLabel(ChestZoneButton, $"Зона сундуков: {AssignedChestRadius:0.#} м");
            SetLabel(ChestZoneShowButton, _chestZoneShown ? "Подсветка: вкл" : "Подсветка: выкл");
            SetLabel(ChestZoneShapeButton, ChestZoneSquare ? "Форма: квадрат" : "Форма: круг");

            var hint = ChestZoneHint != null ? ChestZoneHint.GetComponentInChildren<Text>(true) : null;
            if (hint != null)
                hint.text = $"На сколько метров от печи,{NEWLINE}плавильни или улья ищется{NEWLINE}"
                            + $"назначенный сундук, от 8{NEWLINE}до 64.{NEWLINE}{NEWLINE}"
                            + $"Круг рисуется вокруг тебя —{NEWLINE}станций много, и обводить{NEWLINE}"
                            + $"каждую значило бы закрыть{NEWLINE}базу кольцами.";

            SetActive(ChestZoneButton, MenuState == StateFeatures);

            var page = MenuState == StateChestZone;
            SetActive(ChestZoneHint, page);
            SetActive(ChestZoneInput, page);
            SetActive(ChestZoneApply, page);
            SetActive(ChestZoneShowButton, page);
            SetActive(ChestZoneShapeButton, page);
        }
    }
}
