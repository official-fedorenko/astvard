using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        // What a chest carries about itself, in its own ZDO: the mark it was given, and
        // whether its owner told the sorter to keep out.
        private const string SortMarkKey = "astvard_sort";

        private const string PrivateChestKey = "astvard_private";

        /// <summary>«Личный»: never a source and never a bin. Whatever is in it stays.</summary>
        internal const int MarkPrivate = -2;

        internal const int MarkNone = -1;

        /// <summary>Armed by a button, spent on the next chest opened. Like PendingChestAssign.</summary>
        internal static int? PendingSortMark;

        // A second between sweeps, a few things moved in each. Not because the frame could
        // not take more, but because the owner asked for it to look like somebody is doing
        // it rather than like a chest emptying itself.
        private const float SortTick = 1f;

        private const int SortMovesPerSweep = 3;

        // Пока идёт перепроверка. Больше, потому что она конечна: разобрать стену
        // сундуков по трём ходам в секунду - это минуты, за которые человек решит,
        // что кнопка не работает.
        private const int SortRecheckMoves = 12;

        // Сколько перепроверка живёт, если её никто не остановит. Кончается она не по
        // часам, а по работе - проходом, которому нечего было переложить, - но предел
        // нужен: раскладка, которая почему-то колеблется, иначе гоняла бы сундуки весь
        // вечер, а игрок видел бы только «Перепроверяю…» на кнопке.
        private const float SortRecheckMax = 60f;

        private const int SortRecheckCap = 600;

        private static bool _rechecking;

        private static float _recheckUntil;

        // Взведено ли «и дальше» удержанием Shift, а не кнопкой панели.
        private static bool _armedByShift;

        /// <summary>
        /// Отпустил Shift — «и дальше» кончилось.
        ///
        /// Shift is also the key you run with, so a player who sprinted to the chest and
        /// pressed E without letting go has armed the repeat without asking for it - and the
        /// next chest they meant to open would be re-marked instead. Armed by the panel's own
        /// button, the mark survives the walk over as it always did; armed by holding the key,
        /// it lives exactly as long as the key does.
        /// </summary>
        internal static void NoteArmedByShift(bool armed)
        {
            _armedByShift = armed;
        }

        internal static void TickArmedByShift()
        {
            if (!_armedByShift || HoldingShift) return;

            _armedByShift = false;
            PendingSortMark = null;
            PendingChestAssign = null;
        }

        private static int _recheckMoved;

        // Дошло ли дело хоть до одного прохода. Нет - значит игрок вне зоны или в этой
        // зоне сейчас разбирает другой, и «всё на своих местах» было бы неправдой:
        // никто ничего не смотрел.
        private static bool _recheckRan;

        private static bool _recheckQuiet;

        /// <summary>Идёт ли сейчас перепроверка — страница подписывает этим кнопку.</summary>
        internal static bool Rechecking
        {
            get { return _rechecking; }
        }

        /// <summary>
        /// Пройти по всем помеченным сундукам и переложить то, что лежит не там.
        ///
        /// The sweep does this anyway, every second - but with what is left of three
        /// moves after the carts and the unmarked chests have had their turn, which on a
        /// busy base is nothing at all. Marking a new chest, or emptying one, leaves the
        /// old arrangement standing until the base goes quiet, and from the outside that
        /// is indistinguishable from «не работает».
        /// </summary>
        /// <param name="quiet">
        /// Запущена сама, а не кнопкой: молчит, пока не доложит итог. Пометка сундука и так
        /// отвечает игроку своей строкой, и вторая поверх неё стёрла бы первую.
        /// </param>
        internal static void StartRecheck(bool quiet = false)
        {
            var already = _rechecking;

            _rechecking = true;
            _recheckUntil = Time.realtimeSinceStartup + SortRecheckMax;

            // Повторный запуск продлевает начатое, а не начинает заново: с зажатым Shift
            // помечают стену сундуков подряд, и счёт переложенного должен быть один.
            if (!already)
            {
                _recheckMoved = 0;
                _recheckRan = false;
                _recheckQuiet = quiet;

                if (!quiet)
                    Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                        "Перепроверяю сундуки в зоне");
            }
            else if (!quiet)
            {
                _recheckQuiet = false;
            }
        }

        /// <summary>
        /// Конец перепроверки — и слово о том, чем она кончилась.
        ///
        /// Three endings, because they mean different things to whoever pressed the button:
        /// something was put right, everything already was, or it never got to look at all -
        /// the player is outside every zone, or somebody else is working this one. Saying
        /// «всё на своих местах» for the last of those would be a lie about work nobody did.
        /// </summary>
        private static void EndRecheck()
        {
            var ran = _recheckRan;
            var moved = _recheckMoved;
            var quiet = _recheckQuiet;

            _rechecking = false;
            _recheckRan = false;
            _recheckMoved = 0;
            _recheckQuiet = false;

            if (ran)
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    moved > 0
                        ? $"Перепроверено: переложено {moved}"
                        : "Перепроверено: всё на своих местах");
            else if (!quiet)
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    "Перепроверять нечего: сортировка сейчас не идёт");

            // Кнопка подписана состоянием, и без этого она осталась бы «Перепроверяю…»
            // до следующего открытия панели.
            RefreshMenu();
        }

        // Каймы вокруг зоны больше нет, и повозке поблажки тоже: правило одно - что внутри
        // зоны, то и разбирается, остальное не трогается. Выбор хозяина после того, как
        // кайма плюс «повозка рядом со мной» сложились в двадцать шесть метров от края и
        // повозка начала разбираться там, где зоной уже не пахнет. Зону проще расширить,
        // чем помнить два расстояния, которые складываются.

        private static BepInEx.Configuration.ConfigEntry<bool> _sortEnabled;

        private static BepInEx.Configuration.ConfigEntry<string> _sortZones;

        private static BepInEx.Configuration.ConfigEntry<int> _ownChestSlots;

        private static readonly List<Sorting.Zone> SortZones = new List<Sorting.Zone>();

        private static bool _sortZonesRead;

        private static float _sortSaidAt;

        // What the last sweep found, kept only so the page can explain itself. A sorter
        // that does nothing and says nothing is indistinguishable from a broken one, and
        // «пригнал повозку, она не выгрузилась» has half a dozen answers.
        private static int _lastBins;

        private static int _lastSources;

        private static int _lastZone = -1;

        // Кто-то другой в этой зоне за рулём: мы только смотрим.
        private static bool _lastPassenger;

        // Carts seen in the zone, and what the log last said. The cart is the one thing
        // here found on an assumption - that it carries a Piece like everything else the
        // sweep walks - and this is the line that will say so either way.
        private static int _lastCarts;

        /// <summary>Сколько сундуков подачи в зоне сортировщик обошёл стороной.</summary>
        private static int _lastSupply;

        private static string _lastSaid = "";

        // Why the last sweep left something where it was: no chest will take this kind
        // at all, or the ones that would are full. Two very different fixes.
        private static int _lastNowhere;

        private static int _lastNoRoom;

        // The first thing that did not move this sweep, by name. Counts say how much is
        // stuck; a name says which chest to go and look at.
        private static string _stuck = "";

        // Есть ли поблизости пустые ячейки, до которых застрявшему предмету нельзя.
        // «Сундуки полны» при видимом пустом сундуке читается как поломка, хотя это ровно
        // то правило, ради которого всё затевалось: закреплённый сундук ждёт свою кучу.
        private static bool _lastReserved;

        private static bool _saidNoOwner;

        internal static void BindSorting(BepInEx.Configuration.ConfigFile config)
        {
            _sortEnabled = config.Bind("Сортировка", "Enabled", true,
                "Разбирает ли сортировщик сундуки, пока ты в своей зоне. "
                + "Переключается в игре: «Функции» → «Сортировка».");

            // The zones belong to whoever plays on this machine, and the sorter runs on this
            // machine, so this is the whole of their home. The server is not told: it has
            // nothing to do while a player is standing right there.
            _sortZones = config.Bind("Сортировка", "Zones", "",
                "Зоны сортировки: x,z,радиус,квадрат,поворот — через точку с запятой. "
                + "Ставятся в игре, руками править незачем.");

            _zonesSeeded = config.Bind("Сортировка", "ZonesSent", false,
                "Служебное: отданы ли зоны этого клиента серверу. Ставится само, руками "
                + "не нужно. false — при первом входе на сервер с модом зоны уедут туда.");

            _ownChestSlots = config.Bind("Сортировка", "OwnChestSlots", 4,
                "Сколько ячеек должна занимать куча одного предмета, чтобы получить свой "
                + "сундук внутри категории. 0 — не делить: все сундуки категории берут всё.");

            _zoneStations = config.Bind("Сортировка", "ZoneStations", true,
                "Работают ли станции внутри зоны сортировки с её помеченными сундуками: "
                + "берут оттуда сырьё и складывают туда готовое, по категориям. Назначенные "
                + "руками сундуки подачи и сбора работают как прежде и идут первыми. "
                + "Далеко ли станция дотянется, решает «Зона станций». "
                + "Переключается в игре: «Функции» → «Сортировка» → «Автонаполнение».");

            _feedKilns = config.Bind("Сортировка", "FeedKilns", true,
                "Класть ли дрова в угольные печи. false — печи стоят, и общий запас дерева "
                + "не уходит в уголь; плавильни, кухни и остального это не касается. "
                + "«Функции» → «Сортировка» → «Наполнять печи».");

            // Сто - это стенка сундука с запасом и примерно вечер работы печей. Ноль
            // как сток означал «жечь, пока есть дрова», то есть предел надо было вспомнить
            // и выставить до того, как он понадобился.
            _coalKeep = config.Bind("Сортировка", "CoalKeep", 100,
                "Сколько угля держать на складе. Набралось столько в сундуках — печи "
                + "перестают жечь, разошёлся — начинают снова. 0 — без предела. "
                + "«Функции» → «Сортировка» → «Уголь на складе».");

            _feedCooking = config.Bind("Сортировка", "FeedCooking", true,
                "Класть ли еду на всё, где она готовится: костёр с подставкой, железную "
                + "кухню, печь для хлеба. false — кухни стоят, запасы целы. "
                + "«Функции» → «Сортировка» → «Настройки» → «Наполнять кухни».");

            _foodKeep = config.Bind("Сортировка", "FoodKeep", 100,
                "Сколько готовой еды держать на складе — по каждому блюду отдельно. "
                + "Набралось столько жареного мяса — мясо жарить перестают, а рыбу и хлеб "
                + "готовят дальше. 0 — без предела. «Настройки» → «Еды на складе».");

            _groupSpan = config.Bind("Сортировка", "GroupSpan", 8f,
                "На каком расстоянии сундуки считаются стоящими вместе, от 0 до 64 м. Куча "
                + "держится одной такой кучки и не расползается по базе. Мерится до "
                + "соседнего сундука, а не через всю группу: стена сундуков — одна кучка, "
                + "какой бы длинной ни была. 0 — не собирать в кучки вовсе.");
        }

        private static BepInEx.Configuration.ConfigEntry<bool> _zonesSeeded;

        private static BepInEx.Configuration.ConfigEntry<float> _groupSpan;

        private static BepInEx.Configuration.ConfigEntry<bool> _zoneStations;

        /// <summary>
        /// «Автонаполнение»: станции в зоне сортировки кормятся её помеченными сундуками.
        ///
        /// Назначать сундук каждой печи - работа, которой на разобранной базе быть не
        /// должно: всё сырьё и так разложено по категориям, и печи достаточно знать, что
        /// она стоит в этой зоне. Назначенные сундуки при этом не отменяются и идут
        /// первыми: указать пальцем - по-прежнему самый точный ответ на «который из них».
        /// </summary>
        internal static bool ZoneStationsOn
        {
            get { return _zoneStations == null || _zoneStations.Value; }
        }

        internal static void SetZoneStations(bool on)
        {
            if (_zoneStations != null) _zoneStations.Value = on;
        }

        private static BepInEx.Configuration.ConfigEntry<bool> _feedKilns;

        /// <summary>
        /// Класть ли дрова в угольные печи.
        ///
        /// Everything else the automation feeds takes something that was mined or grown for
        /// it; a charcoal kiln takes the wood everybody builds out of, and standing in a zone
        /// full of it, it will take all of it. This is the switch for that, apart from the
        /// rest: a base can want its smelters fed and its kilns left alone.
        /// </summary>
        internal static bool FeedKilnsOn
        {
            get { return _feedKilns == null || _feedKilns.Value; }
        }

        internal static void SetFeedKilns(bool on)
        {
            if (_feedKilns != null) _feedKilns.Value = on;
        }

        private static BepInEx.Configuration.ConfigEntry<int> _coalKeep;

        /// <summary>
        /// Сколько угля держать на складе; 0 — без предела.
        ///
        /// Off and on rather than a rate: the kilns burn while the chests hold less than
        /// this and stand while they hold more, so the pile settles around the number
        /// without anybody watching it.
        /// </summary>
        internal static int CoalKeep
        {
            get { return Mathf.Clamp(_coalKeep != null ? _coalKeep.Value : 0, 0, 100000); }
        }

        internal static void SetCoalKeep(int amount)
        {
            if (_coalKeep != null) _coalKeep.Value = Mathf.Clamp(amount, 0, 100000);
        }

        private static BepInEx.Configuration.ConfigEntry<bool> _feedCooking;

        /// <summary>
        /// Класть ли еду на кухни.
        ///
        /// The kilns' switch answered «не жги всё дерево»; this is the same question about
        /// the hunt. A station left to itself will cook every last piece of meat, and meat
        /// raw is what a new recipe wants.
        /// </summary>
        internal static bool FeedCookingOn
        {
            get { return _feedCooking == null || _feedCooking.Value; }
        }

        internal static void SetFeedCooking(bool on)
        {
            if (_feedCooking != null) _feedCooking.Value = on;
        }

        private static BepInEx.Configuration.ConfigEntry<int> _foodKeep;

        /// <summary>
        /// Сколько готовой еды держать на складе, по каждому блюду отдельно; 0 — без предела.
        ///
        /// Counted per dish rather than over all food together: one number for everything
        /// would mean a hundred grilled necks quietly stopping the fish and the bread too,
        /// and a larder of one thing is not a larder.
        /// </summary>
        internal static int FoodKeep
        {
            get { return Mathf.Clamp(_foodKeep != null ? _foodKeep.Value : 0, 0, 100000); }
        }

        internal static void SetFoodKeep(int amount)
        {
            if (_foodKeep != null) _foodKeep.Value = Mathf.Clamp(amount, 0, 100000);
        }

        /// <summary>How close two chests stand to count as one group.</summary>
        internal static float GroupSpan
        {
            get { return Mathf.Clamp(_groupSpan != null ? _groupSpan.Value : 8f, 0f, 64f); }
        }

        /// <summary>How big a pile has to be before it earns a chest of its own.</summary>
        internal static int OwnChestSlots
        {
            get { return Mathf.Clamp(_ownChestSlots != null ? _ownChestSlots.Value : 4, 0, 64); }
        }

        /// <summary>Из игры: с какой кучи начинается свой сундук.</summary>
        internal static void SetOwnChestSlots(int slots)
        {
            if (_ownChestSlots != null) _ownChestSlots.Value = Mathf.Clamp(slots, 0, 64);
        }

        internal static bool SortingOn
        {
            get { return _sortEnabled == null || _sortEnabled.Value; }
        }

        internal static void SetSortingOn(bool on)
        {
            if (_sortEnabled != null) _sortEnabled.Value = on;
        }

        // ---------------- the zones ----------------

        internal static List<Sorting.Zone> SortingZones()
        {
            if (!_sortZonesRead)
            {
                _sortZonesRead = true;
                SortZones.Clear();
                if (_sortZones != null) SortZones.AddRange(Sorting.Parse(_sortZones.Value));
            }

            return SortZones;
        }

        internal static void SaveSortingZones()
        {
            if (_sortZones != null) _sortZones.Value = Sorting.Pack(SortZones);
        }

        /// <summary>
        /// Puts a zone down, unless it would share ground with one already there. Two zones
        /// over one chest would each have their own idea of where its contents belong.
        /// </summary>
        internal static bool AddSortingZone(Sorting.Zone zone)
        {
            var zones = SortingZones();

            // On a server the checks below belong to it: this client sees only the zones
            // it is in, so «здесь уже есть зона» is a question it cannot answer.
            if (ZonesOnServer)
            {
                zone.Id = 0;
                SendZone(zone);
                return true;
            }

            if (zones.Count >= Sorting.MaxZones)
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    $"Зон уже {Sorting.MaxZones} — больше некуда");
                return false;
            }

            foreach (var other in zones)
            {
                if (!Sorting.Overlap(zone, other)) continue;

                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    "Здесь уже есть зона сортировки — они не должны пересекаться");
                return false;
            }

            zones.Add(zone);
            SaveSortingZones();
            Log.LogInfo($"[AstvardServerMod] Sorting zone at {zone.X:F0},{zone.Z:F0} "
                        + $"reach {zone.Radius:F0} m, {(zone.Square ? "square" : "round")}.");
            return true;
        }

        /// <summary>
        /// Переименовать зону и поменять её размер. Размер проверяется тем же правилом,
        /// что и при установке: раздувшаяся зона, накрывшая соседнюю, дала бы одному
        /// сундуку двух хозяев - ровно то, из-за чего пересечения и запрещены.
        /// </summary>
        internal static bool ChangeSortingZone(int index, string name, float radius)
        {
            var zones = SortingZones();
            if (index < 0 || index >= zones.Count) return false;

            var zone = zones[index];
            var wanted = new Sorting.Zone
            {
                X = zone.X,
                Z = zone.Z,
                Radius = Sorting.ClampRadius(radius),
                Square = zone.Square,
                Angle = zone.Angle,
                Name = Sorting.CleanName(name),
            };

            if (!ReplaceSortingZone(index, wanted, "Такой размер налезет на соседнюю зону")) return false;

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                $"Зона: {(wanted.Name.Length > 0 ? wanted.Name : "без имени")}, {wanted.Radius:0} м");
            return true;
        }

        /// <summary>Сменить форму уже поставленной зоны. Квадрат шире круга по углам.</summary>
        internal static bool ChangeSortingZoneShape(int index, bool square)
        {
            var zones = SortingZones();
            if (index < 0 || index >= zones.Count) return false;

            var zone = zones[index];
            var wanted = new Sorting.Zone
            {
                X = zone.X,
                Z = zone.Z,
                Radius = zone.Radius,
                Square = square,
                Angle = square ? zone.Angle : 0f,
                Name = zone.Name,
            };

            if (!ReplaceSortingZone(index, wanted, "Квадрат тут налезет на соседнюю зону")) return false;

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                square ? "Зона стала квадратной" : "Зона стала круглой");
            return true;
        }

        /// <summary>Перенести зону туда, где стоит проекция, вместе с поворотом.</summary>
        internal static bool MoveSortingZone(int index, float x, float z, float angle)
        {
            var zones = SortingZones();
            if (index < 0 || index >= zones.Count) return false;

            var zone = zones[index];
            var wanted = new Sorting.Zone
            {
                X = x,
                Z = z,
                Radius = zone.Radius,
                Square = zone.Square,
                Angle = zone.Square ? Sorting.NormaliseAngle(angle) : 0f,
                Name = zone.Name,
            };

            if (!ReplaceSortingZone(index, wanted, "Здесь она налезет на соседнюю зону")) return false;

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Зона перенесена");
            return true;
        }

        /// <summary>
        /// Ставит зону на место index, если она не налезет на остальные. Одна проверка на
        /// все правки: раздутая, перенесённая и обращённая в квадрат ошибаются одинаково,
        /// и стоить это будет одного сундука с двумя хозяевами.
        /// </summary>
        private static bool ReplaceSortingZone(int index, Sorting.Zone wanted, string refusal)
        {
            var zones = SortingZones();
            if (index < 0 || index >= zones.Count) return false;

            // On a server the zones are its own, and so is the last word on whether two
            // of them meet: this client only sees the zones it is in, and a zone it
            // cannot see is exactly the one it would be told about too late.
            if (ZonesOnServer)
            {
                wanted.Id = zones[index].Id;
                SendZone(wanted);
                return true;
            }

            for (var i = 0; i < zones.Count; i++)
            {
                if (i == index || !Sorting.Overlap(wanted, zones[i])) continue;

                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, refusal);
                return false;
            }

            zones[index] = wanted;
            SaveSortingZones();
            return true;
        }

        internal static void RemoveSortingZone(int index)
        {
            var zones = SortingZones();
            if (index < 0 || index >= zones.Count) return;

            if (ZonesOnServer)
            {
                SendZoneDrop(zones[index].Id);
                return;
            }

            zones.RemoveAt(index);
            SaveSortingZones();
        }

        // ---------------- the mark on a chest ----------------

        /// <summary>
        /// Тот, кто владеет этим хранилищем в сети.
        ///
        /// У сундука `ZNetView` на нём самом, а **у повозки — на корне, тогда как
        /// `Container` сидит на дочернем объекте**. `GetComponent` возвращал там null, и
        /// перенос из повозки молча не делал ничего: она честно попадала в источники,
        /// считалась в логе, а добро не ехало — и отчитывалось как «не влезло».
        /// </summary>
        private static ZNetView ViewOf(Container container)
        {
            return container != null ? container.GetComponentInParent<ZNetView>() : null;
        }

        internal static bool IsPrivateChest(Container container)
        {
            var view = ViewOf(container);
            return view != null && view.IsValid() && view.GetZDO().GetBool(PrivateChestKey);
        }

        // Поиск Container внутри детали - рекурсивный обход всего префаба, а сундук в
        // детали не заводится и не пропадает. Обходов этих на большой базе выходят
        // тысячи в секунду: автоматика, сортировщик и подписи ходят по деталям каждый
        // сам, а деталей полторы тысячи. Ответ держится - в том числе ответ «нет
        // контейнера», а таких деталей почти все.
        private static readonly Dictionary<Piece, Container> ContainerByPiece =
            new Dictionary<Piece, Container>();

        private const int ContainerCacheMax = 4096;

        /// <summary>Сундук этой детали, найденный один раз за её жизнь.</summary>
        internal static Container ContainerOf(Piece piece)
        {
            if (piece == null) return null;

            Container found;
            if (ContainerByPiece.TryGetValue(piece, out found)) return found != null ? found : null;

            found = piece.GetComponentInChildren<Container>();
            ContainerByPiece[piece] = found;

            // Ключ здесь - сама деталь, и снесённая остаётся в словаре мёртвым объектом
            // Unity. Зоны грузятся и выгружаются весь вечер, так что чистить надо; но
            // разом и редко - перебрать словарь дешевле, чем помнить о каждой детали.
            if (ContainerByPiece.Count > ContainerCacheMax) ForgetDeadPieces();
            return found;
        }

        private static void ForgetDeadPieces()
        {
            var dead = new List<Piece>();
            foreach (var pair in ContainerByPiece)
                if (pair.Key == null) dead.Add(pair.Key);

            foreach (var piece in dead) ContainerByPiece.Remove(piece);
            Log.LogInfo($"[AstvardServerMod] Chest lookup: forgot {dead.Count} pieces that are gone, "
                        + $"{ContainerByPiece.Count} kept.");
        }

        /// <summary>The category this chest takes, or -1 when it is a source.</summary>
        internal static int ChestCategory(Container container)
        {
            var view = ViewOf(container);
            if (view == null || !view.IsValid()) return MarkNone;

            return Sorting.FromStored(view.GetZDO().GetInt(SortMarkKey));
        }

        /// <summary>
        /// Marks the chest the player just opened: a category, «личный», or nothing at all.
        /// Written by the owner, like every other field of a chest - a write to one we do
        /// not own would live in this client's memory and be gone on reload.
        /// </summary>
        internal static bool MarkChest(Container container, int mark)
        {
            // Shift: пометка остаётся в руках, и следующий открытый сундук получит ту же.
            // Стена сундуков размечается за один проход, а не за десять заходов в панель.
            var again = HoldingShift;
            PendingSortMark = again ? (int?)mark : null;
            _armedByShift = again;

            var view = ViewOf(container);
            if (view == null || !view.IsValid()) return false;

            if (!view.IsOwner()) view.ClaimOwnership();

            var zdo = view.GetZDO();
            zdo.Set(PrivateChestKey, mark == MarkPrivate);
            zdo.Set(SortMarkKey, mark >= 0 ? Sorting.ToStored(mark) : 0);

            var said = mark == MarkPrivate ? "личный — сортировщик его не трогает"
                : mark >= 0 ? $"под «{Sorting.Title(mark)}»"
                : "без пометки — из него разбирают";

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                again ? $"Сундук {said} · Shift — помечай дальше" : $"Сундук {said}");

            // Пометка - это и есть смена раскладки: у категории появился сундук, или у неё
            // его отняли, и то, что лежит по соседству, с этой секунды лежит не там. Ждать,
            // пока три хода в секунду разгребут это между повозками, незачем - и просить об
            // этом кнопкой тоже: момент известен точно.
            StartRecheck(quiet: true);
            return false;
        }

        /// <summary>What the hover text adds to a chest that was marked.</summary>
        internal static string SortMarkNote(Container container)
        {
            if (IsPrivateChest(container)) return "личный";

            var category = ChestCategory(container);
            return category >= 0 ? Sorting.Title(category) : "";
        }

        // ---------------- the sweep ----------------

        /// <summary>
        /// Carries things out of the unmarked chests and the carts into the marked ones,
        /// while the player stands inside one of their zones.
        ///
        /// Only while they stand there: the sorter is this client's, not the server's, and
        /// a base nobody is at has nothing that needs sorting. That also keeps it honest
        /// about what it can see - chests load around players, and sorting what is loaded
        /// is sorting what is there.
        ///
        /// A cart is a chest on wheels: Vagon.m_container is an ordinary Container, so the
        /// cart wheeled home is emptied by the same sweep with no case of its own. Whether
        /// this sweep finds it at all depends on the cart having a Piece, which is how
        /// everything else here is found; if it turns out it has none, the log will say so
        /// long before anybody notices the cart staying full.
        /// </summary>
        // Последняя жалоба: цикл идёт раз в секунду, и сломанное место пишет одно и то же
        // до конца вечера. Одна строка на причину — новость; шестьдесят в минуту — шум,
        // в котором тонет всё остальное.
        private static string _loopTrouble;

        private static void SayLoopTrouble(string where, System.Exception bad)
        {
            var said = where + ": " + bad.Message;
            if (said == _loopTrouble) return;

            _loopTrouble = said;
            Log.LogError($"[AstvardServerMod] {where} stumbled and kept going: {bad}");
        }

        internal static IEnumerator SortingLoop()
        {
            var pieces = new List<Piece>();
            var bins = new List<Container>();
            var sources = new List<Container>();

            // Одно ожидание на все проходы: Unity иначе заводит новое каждую секунду до
            // конца сессии, и сборщику мусора после нас остаётся горка.
            var second = new WaitForSeconds(SortTick);

            while (true)
            {
                yield return second;

                // Сбой внутри одного прохода не имеет права унести с собой всё до конца
                // сессии. Мод это уже проходил: контракт одной RPC разъехался с игрой,
                // исключение прилетело внутрь корутины, и автоматика молча умерла до
                // перезахода. Жалуемся один раз и живём дальше.
                try
                {
                    // Предел живёт здесь, а не в самой проверке: проход, до которого дело не
                    // дошло, ничего не переложит и не закончит её сам.
                    if (_rechecking && Time.realtimeSinceStartup > _recheckUntil) EndRecheck();

                    var player = Player.m_localPlayer;
                    if (player == null || !SortingOn || !RuleAllows("sort"))
                    {
                        // Ответ известен уже сейчас, и минуту молчать «Перепроверяю…» не за чем.
                        if (_rechecking) EndRecheck();
                        continue;
                    }

                    var where = player.transform.position;
                    var zones = SortingZones();
                    var at = Sorting.ZoneAt(zones, where.x, where.z);
                    _lastZone = at;
                    if (at < 0)
                    {
                        _lastBins = 0;
                        _lastSources = 0;
                        _lastPassenger = false;
                        if (_rechecking) EndRecheck();
                        continue;
                    }

                    var zone = zones[at];

                    // Somebody else is carrying things here this second. Two of us taking the
                    // same stack out of the same cart is an item made or an item lost.
                    _lastPassenger = ZonesOnServer && !zone.Drive;
                    if (_lastPassenger)
                    {
                        _lastBins = 0;
                        _lastSources = 0;
                        if (_rechecking) EndRecheck();
                        continue;
                    }

                    pieces.Clear();
                    Piece.GetAllPiecesInRadius(new Vector3(zone.X, where.y, zone.Z),
                        Sorting.ScanRadius(zone), pieces);

                    bins.Clear();
                    sources.Clear();
                    var carts = 0;
                    var supply = 0;
                    foreach (var piece in pieces)
                    {
                        if (piece == null) continue;

                        var container = ContainerOf(piece);
                        if (container == null) continue;

                        var spot = container.transform.position;
                        if (!Sorting.Inside(zone, spot.x, spot.z)) continue;

                        var cart = container.GetComponentInParent<Vagon>() != null;

                        // Somebody has it open: their hands are in it, and two hands in one chest
                        // is how an item ends up in neither.
                        if (container.IsInUse()) continue;
                        if (IsPrivateChest(container)) continue;

                        // Сундук подачи кормит плавильни и печи: автоматика берёт из него
                        // руду и уголь. Для сортировщика он «непомеченный», то есть источник,
                        // и он вычерпал бы его в первую же минуту, а печи встали бы без
                        // единой ошибки в логе. Сундук сбора трогать можно и нужно: туда
                        // падает готовое, и разложить его - ровно наша работа.
                        if (IsSupplyChest(container))
                        {
                            // Считаем, чтобы это было видно, а не только обещано: «он берёт
                            // из подачи» и «эта пометка не легла» выглядят одинаково, пока
                            // никто не назвал число.
                            supply++;
                            continue;
                        }

                        // Приёмником может стать и повозка, если её пометили, - она стоит в зоне.
                        if (ChestCategory(container) >= 0)
                        {
                            bins.Add(container);
                        }
                        else
                        {
                            sources.Add(container);
                            if (cart) carts++;
                        }
                    }

                    _lastBins = bins.Count;
                    _lastSources = sources.Count;
                    _lastCarts = carts;
                    _lastSupply = supply;

                    // Said once per change, not once a second: enough to answer «видит ли он
                    // тележку», quiet enough to leave on.
                    var said = $"bins {bins.Count}, sources {sources.Count}, carts {carts}, "
                               + $"supply left alone {supply}"
                               + (_stuck.Length > 0 ? $", stuck {_stuck}" : "");
                    if (said != _lastSaid)
                    {
                        _lastSaid = said;
                        Log.LogInfo($"[AstvardServerMod] Sorting zone: {said}.");
                    }

                    if (bins.Count == 0)
                    {
                        // Помеченных сундуков нет - перекладывать некуда и не из чего.
                        if (_rechecking) EndRecheck();
                        continue;
                    }

                    // A settled order, so that two chests equal in every other way are always
                    // picked between the same way round. The order the scan handed them over
                    // is whatever the physics felt like today.
                    bins.Sort(ByPlace);

                    var plan = BuildPlan(bins, sources);
                    _lastNowhere = 0;
                    _lastNoRoom = 0;
                    _stuck = "";
                    _lastReserved = false;

                    // During a recheck the marked chests go first: that is the whole of what
                    // was asked for, and a cart arriving mid-pass would otherwise eat it.
                    var recheck = Rechecking;
                    var budget = recheck ? SortRecheckMoves : SortMovesPerSweep;

                    var tidied = recheck ? TidyBins(bins, plan, budget) : 0;
                    var moved = tidied;
                    if (moved < budget) moved += SweepOnce(sources, bins, plan, budget - moved);

                    // Then put right what is already in the marked chests: a pile that grew
                    // out of the shared chest moves to its own, one that was spent moves back.
                    // At least one move is always kept for this - otherwise a base with carts
                    // coming in all evening never tidies itself at all.
                    if (!recheck) moved += TidyBins(bins, plan, Mathf.Max(1, budget - moved));

                    if (recheck)
                    {
                        _recheckRan = true;

                        // Только то, что переложила сама перепроверка: ходы обычного разбора
                        // (повозка в зоне) - чужая работа, и докладывать её своей значит
                        // сказать «переложено 12» о проходе, где перепроверке было нечего
                        // делать. Потолок считает то же самое и по той же причине.
                        _recheckMoved += tidied;

                        // Проход, которому нечего было переложить, и есть конец работы.
                        // Пятнадцать секунд, стоявшие здесь раньше, были числом с потолка:
                        // на стене сундуков они кончались посередине, а на прибранной базе
                        // тянулись ещё десять секунд после того, как всё уже легло.
                        if (tidied <= 0 || _recheckMoved >= SortRecheckCap) EndRecheck();
                    }

                    if (moved <= 0) continue;

                    // Something is happening over there: said rarely enough to be news, not noise.
                    if (Time.realtimeSinceStartup - _sortSaidAt < 10f) continue;
                    _sortSaidAt = Time.realtimeSinceStartup;
                    player.Message(MessageHud.MessageType.TopLeft, $"Сортировка: разложено {moved}");
                }
                catch (System.Exception bad)
                {
                    SayLoopTrouble("Sorting", bad);
                }
            }
        }

        /// <summary>
        /// Почему сортировщик ничего не делает — словами, на странице.
        ///
        /// Every one of these is a state the sorter can sit in for ever while looking
        /// exactly like a broken one. The rules are the sly one: a client that has not
        /// been told what players may do treats everything as closed, which is right,
        /// and silent, which is not.
        /// </summary>
        internal static string SortingWhy()
        {
            if (!SortingOn) return "Сортировка выключена.";

            if (!RuleAllows("sort"))
                return PlayerRulesKnown
                    ? "Админ закрыл сортировку игрокам."
                    : "Сервер ещё не прислал правила.";

            if (Player.m_localPlayer == null) return "";
            if (SortingZones().Count == 0) return "Зон нет — поставь первую.";
            if (_lastZone < 0) return "Ты вне своих зон.";
            if (_lastPassenger)
                return $"В этой зоне сейчас разбирает{NEWLINE}другой игрок — чтобы одну стопку{NEWLINE}"
                       + "не унесли дважды.";
            if (_lastBins == 0) return "В зоне нет помеченных сундуков.";
            if (_lastSources == 0) return "Разбирать нечего: всё помечено.";

            if (_lastNowhere > 0)
                return $"Некуда класть: {_lastNowhere}.{NEWLINE}{_stuck}{NEWLINE}"
                       + "Нет сундука этой категории и нет «Разного».";

            if (_lastNoRoom > 0)
                return $"Не влезло: {_lastNoRoom}.{NEWLINE}{_stuck}{NEWLINE}"
                       + (_lastReserved
                           ? $"Пустые ячейки есть, но они{NEWLINE}закреплены за другими кучами.{NEWLINE}"
                             + $"Пометь ещё сундук этой{NEWLINE}категории."
                           : $"Сундуки полны — пометь ещё{NEWLINE}один.");

            return $"Работает: {_lastBins} помечено, {_lastSources} разбирается"
                   + (_lastCarts > 0 ? $", из них тележек {_lastCarts}." : ".")
                   + (_lastSupply > 0
                       ? $"{NEWLINE}Сундуков подачи: {_lastSupply} —{NEWLINE}из них не беру."
                       : "");
        }

        /// <summary>
        /// Есть ли место в сундуке, до которого этому предмету нельзя. Отвечает на вопрос,
        /// который хозяин задаёт, глядя на полупустой сундук рядом с непристроенным железом.
        /// </summary>
        private static bool RoomSomewhereElse(List<Container> bins, Sorting.Plan plan, ItemDrop.ItemData item)
        {
            var allowed = plan.Where(item.m_shared.m_name, CategoryOf(item));

            for (var at = 0; at < bins.Count; at++)
            {
                if (allowed.Contains(at)) continue;

                var inventory = bins[at].GetInventory();
                if (inventory != null && inventory.GetEmptySlots() > 0) return true;
            }

            return false;
        }

        private static int ByPlace(Container a, Container b)
        {
            var one = a.transform.position;
            var two = b.transform.position;

            var byX = one.x.CompareTo(two.x);
            if (byX != 0) return byX;

            var byZ = one.z.CompareTo(two.z);
            return byZ != 0 ? byZ : one.y.CompareTo(two.y);
        }

        /// <summary>
        /// What the category has to hold, and which chest each part of it belongs in.
        ///
        /// Counted over the marked chests and the unmarked ones together, because what is
        /// still lying in a cart is as much part of the pile as what is already put away:
        /// five hundred wood should be given its chest on the sweep it arrives, not once it
        /// has been stuffed in among the ore.
        /// </summary>
        private static Sorting.Plan BuildPlan(List<Container> bins, List<Container> sources)
        {
            var states = new List<Sorting.BinState>();

            foreach (var bin in bins)
            {
                var inventory = bin.GetInventory();
                var spot = bin.transform.position;
                var state = new Sorting.BinState
                {
                    Category = ChestCategory(bin),
                    X = spot.x,
                    Z = spot.z,
                    Slots = inventory != null ? inventory.GetWidth() * inventory.GetHeight() : 0,
                };

                if (inventory != null)
                    foreach (var item in inventory.GetAllItems())
                    {
                        if (item == null || item.m_shared == null) continue;
                        state.Holds[item.m_shared.m_name] = state.Held(item.m_shared.m_name) + item.m_stack;
                    }

                states.Add(state);
            }

            var loads = new Dictionary<string, Sorting.Load>();
            Tally(loads, bins);
            Tally(loads, sources);

            return Sorting.MakePlan(states, new List<Sorting.Load>(loads.Values), OwnChestSlots, GroupSpan);
        }

        private static void Tally(Dictionary<string, Sorting.Load> loads, List<Container> from)
        {
            foreach (var container in from)
            {
                var inventory = container.GetInventory();
                if (inventory == null) continue;

                foreach (var item in inventory.GetAllItems())
                {
                    if (item == null || item.m_shared == null) continue;

                    var kind = item.m_shared.m_name;
                    Sorting.Load load;
                    if (!loads.TryGetValue(kind, out load))
                    {
                        load = new Sorting.Load
                        {
                            Kind = kind,
                            Category = CategoryOf(item),
                            StackSize = item.m_shared.m_maxStackSize,
                        };
                        loads[kind] = load;
                    }

                    load.Units += item.m_stack;
                }
            }
        }

        /// <summary>Carries what is in the unmarked chests and the carts to where the plan says.</summary>
        private static int SweepOnce(List<Container> sources, List<Container> bins, Sorting.Plan plan,
                                     int budget)
        {
            var moved = 0;

            foreach (var source in sources)
            {
                if (moved >= budget) break;

                var inventory = source.GetInventory();
                if (inventory == null) continue;

                var items = inventory.GetAllItems();

                // Backwards: moving an item out shifts everything after it down a place.
                for (var i = items.Count - 1; i >= 0 && moved < budget; i--)
                {
                    var item = items[i];
                    if (item == null || item.m_shared == null) continue;

                    var how = Deliver(source, bins, plan, item, -1);
                    if (how > 0) { moved++; continue; }

                    if (how < 0) _lastNowhere++; else _lastNoRoom++;

                    if (_stuck.Length == 0)
                    {
                        _stuck = $"«{ItemTitle(item)}» — {Sorting.Title(CategoryOf(item))}";
                        _lastReserved = RoomSomewhereElse(bins, plan, item);
                    }
                }
            }

            return moved;
        }

        /// <summary>
        /// Puts right what is already in the marked chests. Without this the plan would only
        /// ever apply to things arriving: wood that grew out of the shared chest would sit in
        /// it for ever with its own chest standing empty beside it, and a chest emptied of
        /// what it was kept for would never go back to being shared.
        /// </summary>
        /// <summary>Куда этот предмет годится, лучшее впереди; список переиспользуется.</summary>
        private static readonly List<int> Targets = new List<int>();

        private static int TidyBins(List<Container> bins, Sorting.Plan plan, int budget)
        {
            var moved = 0;

            for (var at = 0; at < bins.Count && moved < budget; at++)
            {
                var inventory = bins[at].GetInventory();
                if (inventory == null) continue;

                var items = inventory.GetAllItems();

                for (var i = items.Count - 1; i >= 0 && moved < budget; i--)
                {
                    var item = items[i];
                    if (item == null || item.m_shared == null) continue;

                    // Не «годится ли здесь», а «нет ли места лучше». «Разное» годится для
                    // всего - оно для того и есть, - и по старой проверке предмет, однажды
                    // туда попавший, оставался там навсегда: грибы лежали в «Разном» при
                    // пустом сундуке «Еда» рядом, и перепроверка честно отвечала, что всё
                    // на своих местах.
                    plan.WhereInto(item.m_shared.m_name, CategoryOf(item), Targets);

                    var rank = Targets.IndexOf(at);
                    if (rank == 0) continue;

                    // Не годится вовсе - годится любой из списка; годится, но не лучший -
                    // только те, что стоят впереди него.
                    var limit = rank < 0 ? Targets.Count : rank;
                    var better = false;

                    for (var t = 0; t < limit && !better; t++)
                    {
                        var to = Targets[t];
                        if (to == at || to < 0 || to >= bins.Count) continue;

                        var room = bins[to].GetInventory();
                        better = room != null && room.CanAddItem(item);
                    }

                    if (!better) continue;

                    if (Deliver(bins[at], bins, plan, item, at) > 0) moved++;
                }
            }

            return moved;
        }

        /// <summary>
        /// Moves one item to the first chest the plan names that has room for it. The chest
        /// it is in is skipped, so a sweep can never be spent moving something to where it is.
        /// </summary>
        private static int Deliver(Container from, List<Container> bins, Sorting.Plan plan,
                                   ItemDrop.ItemData item, int fromBin)
        {
            var any = false;

            foreach (var at in plan.Where(item.m_shared.m_name, CategoryOf(item)))
            {
                if (at == fromBin || at < 0 || at >= bins.Count) continue;

                any = true;

                var inventory = bins[at].GetInventory();
                if (inventory == null || !inventory.CanAddItem(item)) continue;

                if (MoveItem(from, bins[at], item)) return 1;
            }

            // Nothing would take it at all, or everything that would is full - said
            // apart, because one is «пометь ещё сундук» and the other «освободи место».
            return any ? 0 : -1;
        }

        /// <summary>
        /// Moves one item. Out of the old chest first and into the new one after: the other
        /// way round, an add that worked followed by a remove that did not would print the
        /// item. If the add fails anyway it goes back where it came from.
        /// </summary>
        private static bool MoveItem(Container from, Container to, ItemDrop.ItemData item)
        {
            var fromView = ViewOf(from);
            var toView = ViewOf(to);

            // Сказать вслух, а не списать на «нет места»: именно так повозка молчала.
            if (fromView == null || toView == null)
            {
                if (!_saidNoOwner)
                {
                    _saidNoOwner = true;
                    Log.LogWarning("[AstvardServerMod] Sorting: a container has no ZNetView above it — "
                                   + "nothing can be moved in or out of it.");
                }

                return false;
            }

            if (!fromView.IsValid() || !toView.IsValid()) return false;

            var fromInventory = from.GetInventory();
            var toInventory = to.GetInventory();
            if (fromInventory == null || toInventory == null || !toInventory.CanAddItem(item)) return false;

            if (!fromView.IsOwner()) fromView.ClaimOwnership();
            if (!toView.IsOwner()) toView.ClaimOwnership();

            if (!fromInventory.RemoveItem(item)) return false;

            if (toInventory.AddItem(item)) return true;

            fromInventory.AddItem(item);
            return false;
        }

        /// <summary>
        /// Which group of the ledger an item belongs to. The game's own item types, read
        /// through their names rather than their numbers, so a game that renumbers them
        /// stops compiling instead of quietly sorting the armoury into the larder.
        /// </summary>
        private static int CategoryOf(ItemDrop.ItemData item)
        {
            // What somebody said about this one outright beats the guess below, and the
            // mod's own list of drops beats the item type - the game calls a deer hide a
            // material, the same word it uses for stone.
            var chosen = Sorting.ChosenFor(item.m_shared.m_name);
            return chosen >= 0 ? chosen : DefaultCategoryOf(item);
        }

        /// <summary>
        /// Куда мод положил бы сам, без чужого слова. Это же уезжает в каталог на сайт:
        /// человеку надо видеть, что он меняет, а не что он уже поменял.
        /// </summary>
        private static int DefaultCategoryOf(ItemDrop.ItemData item)
        {
            if (Sorting.IsLoot(item.m_shared.m_name)) return Sorting.Loot;

            switch (item.m_shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.Material:
                    return Sorting.Materials;

                case ItemDrop.ItemData.ItemType.Consumable:
                case ItemDrop.ItemData.ItemType.Fish:
                    return Sorting.Food;

                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.Shield:
                case ItemDrop.ItemData.ItemType.Ammo:
                case ItemDrop.ItemData.ItemType.AmmoNonEquipable:
                case ItemDrop.ItemData.ItemType.Attach_Atgeir:
                    return Sorting.Weapons;

                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Hands:
                case ItemDrop.ItemData.ItemType.Shoulder:
                case ItemDrop.ItemData.ItemType.Utility:
                case ItemDrop.ItemData.ItemType.Trinket:
                case ItemDrop.ItemData.ItemType.Customization:
                    return Sorting.Armour;

                case ItemDrop.ItemData.ItemType.Tool:
                case ItemDrop.ItemData.ItemType.Torch:
                    return Sorting.Tools;

                case ItemDrop.ItemData.ItemType.Trophy:
                    return Sorting.Trophies;

                default:
                    return Sorting.Misc;
            }
        }
    }
}
