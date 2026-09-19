using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        internal static GameObject SortingButton;

        internal static GameObject SortHint;

        internal static GameObject SortSetupButton;

        internal static GameObject SortOnButton;

        internal static GameObject SortLabelsButton;

        internal static GameObject SortPlaceButton;

        internal static GameObject SortZonesButton;

        internal static GameObject SortMarkButton;

        internal static GameObject SortRecheckButton;

        internal static GameObject SortAutoFillButton;

        internal static GameObject SortKilnsButton;

        internal static GameObject SortCoalButton;

        internal static GameObject SortCoalHint;

        internal static GameObject SortCoalInput;

        internal static GameObject SortCoalApply;

        internal static GameObject SortCookButton;

        internal static GameObject SortFoodButton;

        internal static GameObject SortFoodHint;

        internal static GameObject SortFoodInput;

        internal static GameObject SortFoodApply;

        internal static GameObject SortPlaceHint;

        internal static GameObject SortShapeButton;

        internal static GameObject SortRadiusInput;

        internal static GameObject SortPlaceStartButton;

        internal static GameObject SortZonesHint;

        internal static readonly GameObject[] SortZoneButtons = new GameObject[MaxTemplateButtons];

        internal static GameObject SortMarkHint;

        internal static readonly GameObject[] SortMarkButtons = new GameObject[Sorting.Count];

        internal static GameObject SortPrivateButton;

        internal static GameObject SortClearButton;

        private static int _shownSortZones;

        /// <summary>Round or square, chosen before the zone goes down and kept for the next one.</summary>
        private static bool _sortSquare;

        // How far the square is turned. Kept between zones like the shape is: a hall
        // laid across the world usually has more than one room along it.
        private static float _sortAngle;

        /// <summary>
        /// «Функции» → «Сортировка»: the switch, the zones, and the mark a chest is given.
        ///
        /// Apart from the automation zone on purpose. That one exists so the server keeps a
        /// patch of the world awake; this one exists so a person can say «мои сундуки стоят
        /// вот здесь», and it only ever does anything while they are standing in it.
        /// </summary>
        private void CreateSortPageWidgets(GUIManager gui)
        {
            SortingButton = MakeButton(gui, "Сортировка", () =>
            {
                MenuState = StateSorting;
                RefreshMenu();
            });

            // Первой на странице - виджеты ложатся в порядке создания, а «Назад»
            // поднимается над ними сама. Настройки нужны раз в неделю, зоны и пометка
            // сундука - каждый вечер, поэтому настройки уехали за одну кнопку.
            SortSetupButton = MakeButton(gui, "Настройки", () =>
            {
                MenuState = StateSortSetup;
                RefreshMenu();
            });

            SortHint = MakeText(gui, "");

            SortOnButton = MakeButton(gui, "", () =>
            {
                SetSortingOn(!SortingOn);
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    SortingOn ? "Сортировка включена" : "Сортировка выключена");
                RefreshMenu();
            });

            SortAutoFillButton = MakeButton(gui, "", () =>
            {
                SetZoneStations(!ZoneStationsOn);
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    ZoneStationsOn
                        ? "Станции в зоне берут из помеченных сундуков и сдают в них"
                        : "Станции работают только с назначенными сундуками");
                RefreshMenu();
            });

            CreateSortSettingWidgets(gui);
            CreateSortZonePageWidgets(gui);
            CreateSortCrewWidgets(gui);

            SortLabelsButton = MakeButton(gui, "", () =>
            {
                SetChestLabelsOn(!ChestLabelsOn);
                RefreshMenu();
            });

            SortPlaceButton = MakeButton(gui, "Поставить зону", () =>
            {
                SetFieldText(SortRadiusInput, "24");
                MenuState = StateSortPlace;
                RefreshMenu();
            });

            SortZonesButton = MakeButton(gui, "", () =>
            {
                _itemOffset = 0;
                AskForZones();
                MenuState = StateSortZones;
                RefreshMenu();
            });

            SortRecheckButton = MakeButton(gui, "", () =>
            {
                StartRecheck();
                RefreshMenu();
            });

            SortMarkButton = MakeButton(gui, "Пометить сундук", () =>
            {
                MenuState = StateSortMark;
                RefreshMenu();
            });

            SortPlaceHint = MakeText(gui, "");

            SortShapeButton = MakeButton(gui, "", () =>
            {
                _sortSquare = !_sortSquare;
                RefreshMenu();
            });

            SortRadiusInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "метры, напр. 24", 16, 160f, 32f);
            AddFixedSize(SortRadiusInput, 160f, 32f);

            SortPlaceStartButton = MakeButton(gui, "Показать", StartSortZonePreview);

            SortZonesHint = MakeText(gui, "");

            for (var slot = 0; slot < MaxTemplateButtons; slot++)
            {
                var index = slot;
                SortZoneButtons[slot] = MakeButton(gui, "", () =>
                {
                    var zones = SortingZones();
                    var at = MenuPaging.Clamp(_itemOffset, zones.Count, MaxTemplateButtons) + index;
                    if (at >= zones.Count) return;

                    OpenSortZone(at);
                });
            }

            SortMarkHint = MakeText(gui, "");

            for (var i = 0; i < Sorting.Count; i++)
            {
                var category = i;
                SortMarkButtons[i] = MakeButton(gui, Sorting.Title(i), () => ArmSortMark(category));
            }

            SortPrivateButton = MakeButton(gui, "Личный", () => ArmSortMark(MarkPrivate));
            SortClearButton = MakeButton(gui, "Снять пометку", () => ArmSortMark(MarkNone));
        }

        /// <summary>
        /// Arms the mark and sends the player to the chest: the next one they open takes it
        /// instead of opening. The same way a collection chest is assigned, because it is
        /// the same question - «который из них» - and pointing at it is the only answer.
        /// </summary>
        private static void ArmSortMark(int mark)
        {
            PendingSortMark = mark;
            PendingChestAssign = null;

            var what = mark == MarkPrivate ? "личным"
                : mark == MarkNone ? "без пометки"
                : $"под «{Sorting.Title(mark)}»";

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                $"Открой сундук — он станет {what}");
            InventoryGui.instance?.Hide();
        }

        private static float SortZoneRadius()
        {
            return Sorting.ClampRadius(ParseField(SortRadiusInput, 24f));
        }

        // ---------------- the zone about to be put down ----------------

        private static bool _sortPreviewing;

        private static bool _sortPinned;

        private static Vector3 _sortPinnedAt;

        private static GameObject _sortPreview;

        private static LineRenderer _sortLine;

        /// <summary>Колечко в середине зоны: по нему и видно, куда её сдвинули.</summary>
        private static LineRenderer _sortCentreLine;

        private const float CentreMark = 1f;

        private const int SortBoxPerSide = 12;

        internal static bool IsSortZonePreviewing
        {
            get { return _sortPreviewing; }
        }

        /// <summary>
        /// Шаг стрелки, положенный на оси самой зоны.
        ///
        /// The arrows step away from the camera and to its right, as they do for every other
        /// projection, and for a circle or a square standing straight that is the whole
        /// story. A square turned by Q and E is the exception: the step would carry it
        /// across its own sides, so a zone lined up with the wall of a house slid off the
        /// wall diagonally, a little sideways with every press. Here the step keeps its
        /// length and its meaning - «away» is still away - but goes along the side of the
        /// square nearest to that direction.
        /// </summary>
        private static Vector3 AlongZone(Vector3 step)
        {
            if (!_sortSquare || Mathf.Abs(_sortAngle) < 0.01f) return step;

            var turn = Quaternion.Euler(0f, _sortAngle, 0f);
            var forward = turn * Vector3.forward;
            var right = turn * Vector3.right;

            var alongForward = Vector3.Dot(step, forward);
            var alongRight = Vector3.Dot(step, right);
            var size = step.magnitude;

            return Mathf.Abs(alongForward) >= Mathf.Abs(alongRight)
                ? forward * (size * Mathf.Sign(alongForward))
                : right * (size * Mathf.Sign(alongRight));
        }

        private static Vector3 SortZoneCentre(Player player)
        {
            if (!_sortPinned) return player.transform.position;

            var centre = _sortPinnedAt;
            var system = ZoneSystem.instance;
            if (system != null && system.GetGroundHeight(centre, out var ground)) centre.y = ground;
            return centre;
        }

        /// <summary>
        /// Несём уже поставленную зону. Проекция та же и отвечает тем же: ЛКМ ставит,
        /// Esc отменяет, P закрепляет, стрелки двигают, Q и E вращают квадрат. Форма и
        /// размер берутся у самой зоны — переносим её, а не ставим новую.
        /// </summary>
        private static void StartSortZoneMove(int at)
        {
            var zones = SortingZones();
            if (at < 0 || at >= zones.Count) return;

            _movingZone = at;
            _sortSquare = zones[at].Square;
            _sortAngle = zones[at].Angle;
            SetFieldText(SortRadiusInput,
                zones[at].Radius.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));

            StartSortZonePreview();
        }

        private static void StartSortZonePreview()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            if (!RuleAllows("sort"))
            {
                player.Message(MessageHud.MessageType.Center, "Сортировка игрокам сейчас закрыта");
                return;
            }

            // One projection at a time, or one click would answer two of them.
            if (IsPlacing) CancelPlacement();
            if (IsFencePreviewing) CancelFencePreview();
            if (IsAreaPreviewing) CancelAreaPreview();
            CancelWallPreview();

            _sortPreviewing = true;
            _sortPinned = false;
            NoteToolStart();

            // Панель намеренно не закрывается, как у забора и дорожки: радиус и форма
            // читаются из полей каждый кадр, и пока панель открыта, круг идёт за тем, что
            // в них написано. Закрытая панель превращала бы подбор радиуса в «показать -
            // отменить - поправить - показать», а это ровно та работа, которой проекция и
            // должна избавлять. Tab прячет панель, когда radius подобран.
            player.Message(MessageHud.MessageType.Center,
                _sortSquare
                    ? "Радиус — в поле, круг идёт за ним. ЛКМ — поставить, Esc — отмена, P — закрепить, Q/E — поворот"
                    : "Радиус — в поле, круг идёт за ним. ЛКМ — поставить, Esc — отмена, P — закрепить");
        }

        internal static void CancelSortZonePreview()
        {
            if (!_sortPreviewing) return;

            _sortPreviewing = false;
            _sortPinned = false;
            _movingZone = -1;
            if (_sortPreview != null) _sortPreview.SetActive(false);
        }

        /// <summary>LMB, Esc, P and the arrows while the zone is shown, as every projection has them.</summary>
        internal static bool HandleSortZonePreviewInput()
        {
            if (!_sortPreviewing) return false;

            var player = Player.m_localPlayer;
            if (player == null)
            {
                CancelSortZonePreview();
                return false;
            }

            if (InventoryGui.IsVisible() || Chat.instance?.HasFocus() == true) return false;

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                NoteEscapeUsed();
                CancelSortZonePreview();
                player.Message(MessageHud.MessageType.Center, "Отменено");
                RefreshMenu();
                return true;
            }

            if (Input.GetKeyDown(PinKey))
            {
                _sortPinned = !_sortPinned;
                if (_sortPinned) _sortPinnedAt = player.transform.position;
                SayPinned(_sortPinned);
                return true;
            }

            if (_sortPinned && PinNudgeThisFrame(out var step))
            {
                _sortPinnedAt += AlongZone(step);

                var away = _sortPinnedAt - player.transform.position;
                away.y = 0f;
                player.Message(MessageHud.MessageType.TopLeft,
                    $"Середина зоны: {away.magnitude:0.#} м от тебя");
                return true;
            }

            // Q and E turn it, as they turn a blueprint's ghost, by the game's own
            // 22.5° - sixteen clicks to the circle. With Shift, a quarter of that, so
            // four fine presses still land back on the coarse grid.
            var turnLeft = Input.GetKeyDown(KeyCode.Q);
            if (turnLeft || Input.GetKeyDown(KeyCode.E))
            {
                if (!_sortSquare)
                {
                    player.Message(MessageHud.MessageType.Center,
                        "Круг вращать незачем — переключи форму на квадрат");
                    return true;
                }

                var shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                var turn = shift ? RotationStep * 0.25f : RotationStep;
                _sortAngle = Sorting.NormaliseAngle(_sortAngle + (turnLeft ? -turn : turn));

                // Q is the game's auto-run: without this the character jogs away while
                // the outline turns behind them.
                _inputHeldUntil = Time.time + 0.3f;
                player.Message(MessageHud.MessageType.TopLeft, $"Поворот: {_sortAngle:0.#}°");
                return true;
            }

            if (Input.GetMouseButtonDown(0) && Time.time - _toolMarkedAt > MarkDeafSeconds)
            {
                _inputHeldUntil = Time.time + 0.3f;

                var centre = SortZoneCentre(player);

                // Turned down - it would share ground with a zone already there - the outline
                // stays up to be moved rather than vanishing with no way back.
                if (_movingZone >= 0)
                {
                    if (MoveSortingZone(_movingZone, centre.x, centre.z, _sortAngle))
                    {
                        CancelSortZonePreview();
                        MenuState = StateSortZone;
                    }

                    RefreshMenu();
                    return true;
                }

                var zone = new Sorting.Zone
                {
                    X = centre.x,
                    Z = centre.z,
                    Radius = SortZoneRadius(),
                    Square = _sortSquare,
                    Angle = _sortSquare ? _sortAngle : 0f,
                };

                if (AddSortingZone(zone))
                {
                    CancelSortZonePreview();
                    player.Message(MessageHud.MessageType.Center,
                        $"Зона поставлена: {(zone.Square ? "квадрат" : "круг")} {zone.Radius:0} м");
                    MenuState = StateSorting;
                }

                RefreshMenu();
                return true;
            }

            return false;
        }

        /// <summary>Draws the zone where it would go, on the ground, from Update.</summary>
        internal static void UpdateSortZonePreview()
        {
            var player = Player.m_localPlayer;
            if (!_sortPreviewing || player == null)
            {
                if (_sortPreview != null) _sortPreview.SetActive(false);
                return;
            }

            if (_sortLine == null && !CreateSortZonePreview()) return;

            var centre = SortZoneCentre(player);
            var reach = SortZoneRadius();

            _sortPreview.SetActive(true);
            if (_sortSquare) DrawGroundBox(_sortLine, centre, reach, _sortAngle);
            else DrawGroundRing(_sortLine, centre, reach);

            // Середина - то, что двигают стрелки, и без метки её не видно вовсе: контур
            // зоны в двадцать метров уходит за спину, и понять, куда уехал центр, нечем.
            if (_sortCentreLine != null) DrawGroundRing(_sortCentreLine, centre, CentreMark);
        }

        /// <summary>
        /// The same outline as the paving ring, with corners. Sampled along each side rather
        /// than drawn corner to corner: a straight line between two corners hangs in the air
        /// over a dip and disappears into the next rise, and the point of showing it on the
        /// ground is to see what it covers.
        /// </summary>
        private static void DrawGroundBox(LineRenderer line, Vector3 centre, float reach, float angle)
        {
            var system = ZoneSystem.instance;
            var points = SortBoxPerSide * 4;
            line.positionCount = points;

            // Turned here the same way Sorting.Inside turns it back, or the outline
            // would promise one square and the sorter would work another.
            var radians = Sorting.NormaliseAngle(angle) * Mathf.Deg2Rad;
            var cos = Mathf.Cos(radians);
            var sin = Mathf.Sin(radians);

            for (var i = 0; i < points; i++)
            {
                var side = i / SortBoxPerSide;
                var along = -reach + (i % SortBoxPerSide) / (float)SortBoxPerSide * reach * 2f;

                float localX, localZ;
                switch (side)
                {
                    case 0: localX = along; localZ = -reach; break;
                    case 1: localX = reach; localZ = along; break;
                    case 2: localX = -along; localZ = reach; break;
                    default: localX = -reach; localZ = -along; break;
                }

                var point = new Vector3(centre.x + localX * cos - localZ * sin, centre.y,
                                        centre.z + localX * sin + localZ * cos);

                if (system != null && system.GetGroundHeight(point, out var ground)) point.y = ground;
                point.y += 0.15f;
                line.SetPosition(i, point);
            }
        }

        private static bool CreateSortZonePreview()
        {
            if (PreviewMaterial() == null) return false;

            _sortPreview = new GameObject("AstvardSortZonePreview");
            _sortLine = MakeGroundLine(_sortPreview.transform, "Outline", true);
            _sortLine.widthMultiplier = 0.5f;
            _sortLine.startColor = Faded(SortPreviewColour, 0.85f);
            _sortLine.endColor = _sortLine.startColor;

            _sortCentreLine = MakeGroundLine(_sortPreview.transform, "Centre", true);
            _sortCentreLine.widthMultiplier = 0.3f;
            _sortCentreLine.startColor = Faded(SortPreviewColour, 0.6f);
            _sortCentreLine.endColor = _sortCentreLine.startColor;
            return true;
        }

        internal static void DestroySortZonePreview()
        {
            if (_sortPreview != null) Destroy(_sortPreview);
            _sortLine = null;
            _sortCentreLine = null;
        }

        // Green, to tell it apart from the road's and the paving's amber: these two are
        // shown by different pages but they live on the same ground.
        private static readonly Color SortPreviewColour = new Color(0.45f, 0.9f, 0.5f);

        // ---------------- the pages ----------------

        internal static GameObject SortSlotsButton;

        internal static GameObject SortSlotsHint;

        internal static GameObject SortSlotsInput;

        internal static GameObject SortSlotsApply;

        internal static GameObject SortLiftButton;

        internal static GameObject SortLiftHint;

        internal static GameObject SortLiftInput;

        internal static GameObject SortLiftApply;

        /// <summary>
        /// Две ручки сортировки, каждая своей страницей за кнопкой, подписанной своим
        /// значением - как всё в этой панели. В конфиге они тоже есть, но подбирать их
        /// приходится глядя на базу, а не на файл.
        /// </summary>
        private void CreateSortSettingWidgets(Jotunn.Managers.GUIManager gui)
        {
            SortKilnsButton = MakeButton(gui, "", () =>
            {
                SetFeedKilns(!FeedKilnsOn);
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    FeedKilnsOn
                        ? "Дрова в угольные печи кладём"
                        : "Угольные печи стоят — дрова целы");
                RefreshMenu();
            });

            SortCoalButton = MakeButton(gui, "", () =>
            {
                SetFieldText(SortCoalInput, CoalKeep.ToString());
                MenuState = StateSortCoal;
                RefreshMenu();
            });

            SortCoalHint = MakeText(gui, "");

            SortCoalInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                UnityEngine.UI.InputField.ContentType.IntegerNumber, "угля, напр. 500", 16, 160f, 32f);
            AddFixedSize(SortCoalInput, 160f, 32f);

            SortCoalApply = MakeButton(gui, "Применить", () =>
            {
                SetCoalKeep(Mathf.RoundToInt(ParseField(SortCoalInput, CoalKeep)));
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, CoalKeep > 0
                    ? $"Печи жгут, пока угля меньше {CoalKeep}"
                    : "Печи жгут без предела");
                MenuState = StateSortSetup;
                RefreshMenu();
            });

            SortCookButton = MakeButton(gui, "", () =>
            {
                SetFeedCooking(!FeedCookingOn);
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    FeedCookingOn
                        ? "Еду на кухни кладём"
                        : "Кухни стоят — запасы целы");
                RefreshMenu();
            });

            SortFoodButton = MakeButton(gui, "", () =>
            {
                SetFieldText(SortFoodInput, FoodKeep.ToString());
                MenuState = StateSortFood;
                RefreshMenu();
            });

            SortFoodHint = MakeText(gui, "");

            SortFoodInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                UnityEngine.UI.InputField.ContentType.IntegerNumber, "порций, напр. 100", 16, 160f, 32f);
            AddFixedSize(SortFoodInput, 160f, 32f);

            SortFoodApply = MakeButton(gui, "Применить", () =>
            {
                SetFoodKeep(Mathf.RoundToInt(ParseField(SortFoodInput, FoodKeep)));
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, FoodKeep > 0
                    ? $"Готовим, пока блюда меньше {FoodKeep}"
                    : "Готовим без предела");
                MenuState = StateSortSetup;
                RefreshMenu();
            });

            SortSlotsButton = MakeButton(gui, "", () =>
            {
                SetFieldText(SortSlotsInput, OwnChestSlots.ToString());
                MenuState = StateSortSlots;
                RefreshMenu();
            });

            SortSlotsHint = MakeText(gui, "");

            SortSlotsInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                UnityEngine.UI.InputField.ContentType.IntegerNumber, "ячейки, напр. 4", 16, 160f, 32f);
            AddFixedSize(SortSlotsInput, 160f, 32f);

            SortSlotsApply = MakeButton(gui, "Применить", () =>
            {
                SetOwnChestSlots(Mathf.RoundToInt(ParseField(SortSlotsInput, OwnChestSlots)));
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, OwnChestSlots > 0
                    ? $"Свой сундук — от {OwnChestSlots} ячеек"
                    : "Сундуки категории больше не делятся");
                MenuState = StateSortSetup;
                RefreshMenu();
            });

            SortLiftButton = MakeButton(gui, "", () =>
            {
                SetFieldText(SortLiftInput, ChestLabelLift.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                MenuState = StateSortLift;
                RefreshMenu();
            });

            SortLiftHint = MakeText(gui, "");

            SortLiftInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                UnityEngine.UI.InputField.ContentType.DecimalNumber, "метры, напр. 0.5", 16, 160f, 32f);
            AddFixedSize(SortLiftInput, 160f, 32f);

            SortLiftApply = MakeButton(gui, "Применить", () =>
            {
                SetChestLabelLift(ParseField(SortLiftInput, ChestLabelLift));
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    $"Подписи на {ChestLabelLift:0.##} м над сундуком");
                MenuState = StateSortSetup;
                RefreshMenu();
            });
        }

        private static void RefreshSortSettingViews()
        {
            SetLabel(SortKilnsButton, FeedKilnsOn ? "Наполнять печи: вкл" : "Наполнять печи: выкл");
            SetLabel(SortCoalButton, CoalKeep > 0
                ? $"Уголь на складе: {CoalKeep}"
                : "Уголь на складе: без предела");

            var coal = SortCoalHint != null ? SortCoalHint.GetComponentInChildren<Text>(true) : null;
            if (coal != null)
                coal.text = $"Сколько угля держать{NEWLINE}в сундуках. Набралось —{NEWLINE}"
                            + $"печи стоят, разошёлся —{NEWLINE}жгут снова.{NEWLINE}{NEWLINE}"
                            + $"Считается в сундуках сбора{NEWLINE}и в помеченных сундуках{NEWLINE}"
                            + $"зоны.{NEWLINE}0 — без предела.";

            SetLabel(SortCookButton, FeedCookingOn ? "Наполнять кухни: вкл" : "Наполнять кухни: выкл");
            SetLabel(SortFoodButton, FoodKeep > 0
                ? $"Еды на складе: {FoodKeep}"
                : "Еды на складе: без предела");

            var food = SortFoodHint != null ? SortFoodHint.GetComponentInChildren<Text>(true) : null;
            if (food != null)
                food.text = $"Сколько держать готовой еды,{NEWLINE}по каждому блюду свой{NEWLINE}"
                            + $"счёт. Набралось столько{NEWLINE}жареного мяса — мясо жарить{NEWLINE}"
                            + $"перестают, рыбу и хлеб{NEWLINE}готовят дальше.{NEWLINE}{NEWLINE}"
                            + $"Считается в сундуках сбора{NEWLINE}и в помеченных сундуках{NEWLINE}"
                            + $"зоны.{NEWLINE}0 — без предела.";

            SetLabel(SortSlotsButton, OwnChestSlots > 0
                ? $"Свой сундук: от {OwnChestSlots} ячеек"
                : "Свой сундук: не делить");
            SetLabel(SortLiftButton, $"Высота подписей: {ChestLabelLift:0.##} м");

            var slots = SortSlotsHint != null ? SortSlotsHint.GetComponentInChildren<Text>(true) : null;
            if (slots != null)
                slots.text = $"Сколько ячеек должна занимать{NEWLINE}куча, чтобы получить свой{NEWLINE}"
                             + $"сундук. 500 дерева — это{NEWLINE}10 ячеек, 30 руды — одна.{NEWLINE}"
                             + $"Больше число — меньше{NEWLINE}закреплённых сундуков.{NEWLINE}"
                             + $"0 — не делить вовсе.";

            var lift = SortLiftHint != null ? SortLiftHint.GetComponentInChildren<Text>(true) : null;
            if (lift != null)
                lift.text = $"На сколько метров подпись{NEWLINE}поднята над сундуком,{NEWLINE}"
                            + $"от 0 до 5. Ноль — на самом{NEWLINE}дне сундука.";
        }

        private static void RefreshSortSettingVisibility(bool allowed)
        {
            RefreshSortSettingViews();

            var coalPage = allowed && MenuState == StateSortCoal;
            SetActive(SortCoalHint, coalPage);
            SetActive(SortCoalInput, coalPage);
            SetActive(SortCoalApply, coalPage);

            var foodPage = allowed && MenuState == StateSortFood;
            SetActive(SortFoodHint, foodPage);
            SetActive(SortFoodInput, foodPage);
            SetActive(SortFoodApply, foodPage);

            var slots = allowed && MenuState == StateSortSlots;
            SetActive(SortSlotsHint, slots);
            SetActive(SortSlotsInput, slots);
            SetActive(SortSlotsApply, slots);

            var lift = allowed && MenuState == StateSortLift;
            SetActive(SortLiftHint, lift);
            SetActive(SortLiftInput, lift);
            SetActive(SortLiftApply, lift);
        }

        internal static GameObject SortZoneHint;

        internal static GameObject SortZoneNameInput;

        internal static GameObject SortZoneSizeInput;

        internal static GameObject SortZoneApply;

        internal static GameObject SortZoneDelete;

        /// <summary>Какая зона сейчас открыта.</summary>
        private static int _editingSortZone;

        // And which zone that is, as the server numbers them. The list is rebuilt from
        // under this page every time anybody touches a zone anywhere: a page holding a
        // place in that list would answer «Удалить» about whichever zone landed there.
        private static int _editingZoneId;

        // Удаление в одно нажатие стирало зону, которую хотели всего лишь посмотреть.
        private static float _zoneDeleteArmedAt;

        /// <summary>
        /// Страница одной зоны: имя, размер, удаление. Имя — чтобы «Зоны: 3» не превращались
        /// в загадку, размер — потому что подобрать его с первого раза не выходит ни у кого,
        /// а удаление отсюда и с подтверждением, как всё, чего не вернуть.
        /// </summary>
        private void CreateSortZonePageWidgets(Jotunn.Managers.GUIManager gui)
        {
            SortZoneHint = MakeText(gui, "");

            SortZoneNameInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                UnityEngine.UI.InputField.ContentType.Standard, "имя, напр. Двор", 16, 200f, 32f);
            AddFixedSize(SortZoneNameInput, 200f, 32f);

            SortZoneSizeInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                UnityEngine.UI.InputField.ContentType.DecimalNumber, "метры", 16, 160f, 32f);
            AddFixedSize(SortZoneSizeInput, 160f, 32f);

            SortZoneApply = MakeButton(gui, "Применить", () =>
            {
                var zones = SortingZones();
                if (_editingSortZone < 0 || _editingSortZone >= zones.Count) return;

                var name = FieldText(SortZoneNameInput, "");
                var radius = Sorting.ClampRadius(ParseField(SortZoneSizeInput, zones[_editingSortZone].Radius));

                if (!ChangeSortingZone(_editingSortZone, name, radius)) return;

                MenuState = StateSortZones;
                RefreshMenu();
            });

            SortZoneShowButton = MakeButton(gui, "", () => { ToggleShownZone(_editingSortZone); RefreshMenu(); });

            SortZoneShapeButton = MakeButton(gui, "", () =>
            {
                var zones = SortingZones();
                if (_editingSortZone < 0 || _editingSortZone >= zones.Count) return;

                ChangeSortingZoneShape(_editingSortZone, !zones[_editingSortZone].Square);
                RefreshMenu();
            });

            SortZoneMoveButton = MakeButton(gui, "Перенести", () => StartSortZoneMove(_editingSortZone));

            SortZoneDelete = MakeButton(gui, "", () =>
            {
                // Вторым нажатием, как у всего, что не вернёшь.
                if (Time.realtimeSinceStartup - _zoneDeleteArmedAt > 5f)
                {
                    _zoneDeleteArmedAt = Time.realtimeSinceStartup;
                    RefreshMenu();
                    return;
                }

                _zoneDeleteArmedAt = 0f;
                RemoveSortingZone(_editingSortZone);
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Зона убрана");
                MenuState = StateSortZones;
                RefreshMenu();
            });
        }

        private static void OpenSortZone(int at)
        {
            var zones = SortingZones();
            if (at < 0 || at >= zones.Count) return;

            _editingSortZone = at;
            _editingZoneId = zones[at].Id;
            _zoneDeleteArmedAt = 0f;
            SetFieldText(SortZoneNameInput, zones[at].Name ?? "");
            SetFieldText(SortZoneSizeInput,
                zones[at].Radius.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));

            MenuState = StateSortZone;
            RefreshMenu();
        }

        internal static GameObject SortZoneShowButton;

        internal static GameObject SortZoneShapeButton;

        internal static GameObject SortZoneMoveButton;

        /// <summary>Какую зону несём. -1 — ставим новую.</summary>
        private static int _movingZone = -1;

        // Какую зону сейчас обводим на земле, -1 — никакую. Живёт до конца сессии: это
        // «дай посмотреть, где край», а не настройка, которую хочется найти завтра.
        private static int _shownZone = -1;

        private static GameObject _shownZoneObject;

        private static LineRenderer _shownZoneLine;

        private static float _shownZoneDrawnAt;

        /// <summary>Обводит поставленную зону по земле, пока не выключат.</summary>
        internal static void ToggleShownZone(int at)
        {
            _shownZone = _shownZone == at ? -1 : at;
            _shownZoneDrawnAt = 0f;

            if (_shownZone < 0 && _shownZoneObject != null) _shownZoneObject.SetActive(false);
        }

        internal static void UpdateShownZone()
        {
            var zones = SortingZones();
            if (_shownZone < 0 || _shownZone >= zones.Count || Player.m_localPlayer == null)
            {
                if (_shownZoneObject != null) _shownZoneObject.SetActive(false);
                return;
            }

            // Зона не двигается, но земля под ней подгружается, поэтому контур пересчитывается
            // изредка, а не каждый кадр: 72 точки на кадр ради неподвижного круга — впустую.
            if (Time.realtimeSinceStartup - _shownZoneDrawnAt < 0.5f) return;
            _shownZoneDrawnAt = Time.realtimeSinceStartup;

            if (_shownZoneLine == null)
            {
                if (PreviewMaterial() == null) return;

                _shownZoneObject = new GameObject("AstvardShownZone");
                _shownZoneLine = MakeGroundLine(_shownZoneObject.transform, "Outline", true);
                _shownZoneLine.widthMultiplier = 0.4f;
                _shownZoneLine.startColor = Faded(SortPreviewColour, 0.6f);
                _shownZoneLine.endColor = _shownZoneLine.startColor;
            }

            var zone = zones[_shownZone];
            var centre = new Vector3(zone.X, Player.m_localPlayer.transform.position.y, zone.Z);

            _shownZoneObject.SetActive(true);
            if (zone.Square) DrawGroundBox(_shownZoneLine, centre, zone.Radius, zone.Angle);
            else DrawGroundRing(_shownZoneLine, centre, zone.Radius);
        }

        internal static void DestroyShownZone()
        {
            if (_shownZoneObject != null) Destroy(_shownZoneObject);
            _shownZoneLine = null;
            _shownZone = -1;
        }

        /// <summary>Чем зона зовётся в списке: именем, если дали, иначе формой и размером.</summary>
        internal static string SortZoneTitle(Sorting.Zone zone)
        {
            if (zone == null) return "?";
            if (!string.IsNullOrEmpty(zone.Name)) return zone.Name;

            return zone.Square ? $"квадрат {zone.Radius:0} м" : $"круг {zone.Radius:0} м";
        }

        private static void RefreshSortZonePage(bool allowed)
        {
            var zones = SortingZones();
            var open = allowed && MenuState == StateSortZone
                       && _editingSortZone >= 0 && _editingSortZone < zones.Count;

            SetLabel(SortZoneShowButton, _shownZone == _editingSortZone && _shownZone >= 0
                ? "Подсветка: вкл"
                : "Подсветка: выкл");

            var opened = SortingZones();
            SetLabel(SortZoneShapeButton,
                _editingSortZone >= 0 && _editingSortZone < opened.Count && opened[_editingSortZone].Square
                    ? "Форма: квадрат"
                    : "Форма: круг");

            SetLabel(SortZoneDelete, Time.realtimeSinceStartup - _zoneDeleteArmedAt <= 5f
                ? "Точно? Нажми ещё раз"
                : "Удалить зону");

            var hint = SortZoneHint != null ? SortZoneHint.GetComponentInChildren<Text>(true) : null;
            if (hint != null && open)
            {
                var zone = zones[_editingSortZone];
                var player = Player.m_localPlayer;
                var away = player != null
                    ? Vector2.Distance(new Vector2(zone.X, zone.Z),
                                       new Vector2(player.transform.position.x, player.transform.position.z))
                    : 0f;

                hint.text = $"{SortZoneTitle(zone)}{NEWLINE}"
                            + $"{(zone.Square ? "Квадрат" : "Круг")} {zone.Radius:0} м, "
                            + $"до неё {away:0} м."
                            + (zone.Square && zone.Angle > 0.05f ? $"{NEWLINE}Повёрнута на {zone.Angle:0.#}°." : "")
                            + (ZonesOnServer && !IsMyZone(zone)
                                ? $"{NEWLINE}{NEWLINE}Зона игрока {ZonePersonName(zone.Owner)}.{NEWLINE}"
                                  + $"Ты в ней разбираешь и видишь{NEWLINE}подписи, менять её может{NEWLINE}"
                                  + "только он."
                                : $"{NEWLINE}{NEWLINE}Имя и расстояние от середины,{NEWLINE}"
                                  + $"от {Sorting.MinZoneRadius:0} до {Sorting.MaxZoneRadius:0} м.");
            }

            SetActive(SortZoneHint, open);
            SetActive(SortZoneNameInput, open);
            SetActive(SortZoneSizeInput, open);
            SetActive(SortZoneApply, open);
            SetActive(SortZoneShowButton, open);
            SetActive(SortZoneShapeButton, open);
            SetActive(SortZoneMoveButton, open);
            SetActive(SortZoneDelete, open);

            // Only the one who put the zone down may change it; the rest of the crew see
            // the page and its numbers, which is what tells them whose corner they are in.
            var mine = !open || IsMyZone(zones[_editingSortZone]);
            SetActive(SortZoneNameInput, open && mine);
            SetActive(SortZoneSizeInput, open && mine);
            SetActive(SortZoneApply, open && mine);
            SetActive(SortZoneShapeButton, open && mine);
            SetActive(SortZoneMoveButton, open && mine);
            SetActive(SortZoneDelete, open && mine);
        }

        private static void RebuildSortViews()
        {
            var zones = SortingZones();

            SetLabel(SortOnButton, SortingOn ? "Сортировка: вкл" : "Сортировка: выкл");
            SetLabel(SortZonesButton, $"Зоны: {zones.Count}");
            // «40 из 43» - это и есть ответ на «почему вот тут подписи нет»: их больше,
            // чем мод берётся показывать разом, и лишние отпали не молча.
            SetLabel(SortLabelsButton, !ChestLabelsOn
                ? "Подписи: выкл"
                : ChestLabelsMissed > 0
                    ? $"Подписи: вкл ({ChestLabelsShown} из {ChestLabelsShown + ChestLabelsMissed})"
                    : $"Подписи: вкл ({ChestLabelsShown})");
            SetLabel(SortShapeButton, _sortSquare ? "Форма: квадрат" : "Форма: круг");
            SetLabel(SortRecheckButton, Rechecking ? "Перепроверяю…" : "Перепроверить");
            SetLabel(SortAutoFillButton, ZoneStationsOn ? "Автонаполнение: вкл" : "Автонаполнение: выкл");

            var player = Player.m_localPlayer;
            var at = player != null
                ? Sorting.ZoneAt(zones, player.transform.position.x, player.transform.position.z)
                : -1;

            var hint = SortHint != null ? SortHint.GetComponentInChildren<Text>(true) : null;
            if (hint != null)
                hint.text = $"Разбирает непомеченные сундуки{NEWLINE}и тележки в помеченные,{NEWLINE}"
                            + $"пока ты стоишь в своей зоне.{NEWLINE}{NEWLINE}"
                            + SortingWhy();

            var placeHint = SortPlaceHint != null ? SortPlaceHint.GetComponentInChildren<Text>(true) : null;
            if (placeHint != null)
                placeHint.text = $"Радиус от середины, м:{NEWLINE}от {Sorting.MinZoneRadius:0} "
                                 + $"до {Sorting.MaxZoneRadius:0}.{NEWLINE}У квадрата это половина{NEWLINE}"
                                 + $"стороны.{NEWLINE}Квадрат вращается Q и E,{NEWLINE}"
                                 + $"с Shift — мельче.{NEWLINE}{NEWLINE}"
                                 + $"Панель можно не закрывать:{NEWLINE}меняешь радиус — круг{NEWLINE}"
                                 + $"меняется сразу. Tab прячет{NEWLINE}панель, ЛКМ ставит.{NEWLINE}{NEWLINE}"
                                 + $"Зоны не должны пересекаться.";

            if (MenuState == StateSortZones)
                _itemOffset = MenuPaging.Clamp(_itemOffset, zones.Count, MaxTemplateButtons);
            _shownSortZones = MenuState == StateSortZones
                ? MenuPaging.Shown(_itemOffset, zones.Count, MaxTemplateButtons)
                : 0;

            var zonesHint = SortZonesHint != null ? SortZonesHint.GetComponentInChildren<Text>(true) : null;
            if (zonesHint != null)
                zonesHint.text = zones.Count == 0
                    ? $"Зон нет.{NEWLINE}«Поставить зону» — и ЛКМ{NEWLINE}там, где стоят сундуки."
                    : $"Нажми на зону, чтобы{NEWLINE}переименовать, изменить{NEWLINE}или убрать."
                      + WindowNote(_itemOffset, zones.Count);

            for (var i = 0; i < MaxTemplateButtons; i++)
            {
                if (i >= _shownSortZones)
                {
                    SetLabel(SortZoneButtons[i], "");
                    continue;
                }

                var zone = zones[_itemOffset + i];
                var away = player != null
                    ? Vector2.Distance(new Vector2(zone.X, zone.Z),
                                       new Vector2(player.transform.position.x, player.transform.position.z))
                    : 0f;

                // Имя и размер вместе: с одним именем в строке оставалось только
                // расстояние, и его принимали за радиус - он ведь тоже в метрах.
                var shape = zone.Square ? $"квадрат {zone.Radius:0} м" : $"круг {zone.Radius:0} м";
                var title = string.IsNullOrEmpty(zone.Name) ? shape : $"{zone.Name} · {shape}";

                // Чья она, если не моя: в списке теперь лежат и зоны, в которые вписали
                // меня, а они выглядели ровно как свои - до первой попытки что-то в них
                // поменять.
                var whose = ZonesOnServer && !IsMyZone(zone)
                    ? $" · {ZonePersonName(zone.Owner)}"
                    : "";

                SetLabel(SortZoneButtons[i], $"{title}{whose} — до неё {away:0} м");
            }

            var markHint = SortMarkHint != null ? SortMarkHint.GetComponentInChildren<Text>(true) : null;
            if (markHint != null)
                markHint.text = $"Выбери, чем станет сундук,{NEWLINE}и открой его.{NEWLINE}"
                                + $"Непомеченный — из него берут.{NEWLINE}"
                                + $"«Личный» — не трогают вовсе.{NEWLINE}"
                                + $"Пометь несколько под одно —{NEWLINE}"
                                + $"большая куча займёт свой{NEWLINE}сундук целиком, мелочь{NEWLINE}"
                                + $"ляжет вместе в общий.";
        }

        private static void RefreshSortVisibility()
        {
            RebuildSortViews();

            var allowed = RuleAllows("sort");
            RefreshSortSettingVisibility(allowed);
            RefreshSortZonePage(allowed);
            RefreshSortCrew(allowed);

            SetActive(SortingButton, allowed && MenuState == StateFeatures);

            var page = allowed && MenuState == StateSorting;
            SetActive(SortHint, page);
            SetActive(SortSetupButton, page);

            // Сама работа осталась на странице сортировки, а всё, что настраивают один
            // раз и забывают, - за кнопкой «Настройки».
            var setup = allowed && MenuState == StateSortSetup;
            SetActive(SortOnButton, setup);
            SetActive(SortAutoFillButton, setup);
            SetActive(SortKilnsButton, setup);
            SetActive(SortCoalButton, setup);
            SetActive(SortCookButton, setup);
            SetActive(SortFoodButton, setup);
            SetActive(SortLabelsButton, setup);
            SetActive(SortSlotsButton, setup);
            SetActive(SortLiftButton, setup);
            SetActive(SortPlaceButton, page);
            SetActive(SortZonesButton, page);
            SetActive(SortRecheckButton, page);
            SetActive(SortMarkButton, page);

            var placing = allowed && MenuState == StateSortPlace;
            SetActive(SortPlaceHint, placing);
            SetActive(SortShapeButton, placing);
            SetActive(SortRadiusInput, placing);
            SetActive(SortPlaceStartButton, placing);

            var listing = allowed && MenuState == StateSortZones;
            SetActive(SortZonesHint, listing);
            for (var i = 0; i < MaxTemplateButtons; i++)
                SetActive(SortZoneButtons[i], listing && i < _shownSortZones);

            var marking = allowed && MenuState == StateSortMark;
            SetActive(SortMarkHint, marking);
            foreach (var button in SortMarkButtons) SetActive(button, marking);
            SetActive(SortPrivateButton, marking);
            SetActive(SortClearButton, marking);
        }
    }
}
