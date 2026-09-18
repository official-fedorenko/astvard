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

        private const float SortRecheckFor = 15f;

        private static float _recheckUntil;

        private static int _recheckMoved;

        /// <summary>Идёт ли сейчас перепроверка — страница подписывает этим кнопку.</summary>
        internal static bool Rechecking
        {
            get { return Time.realtimeSinceStartup < _recheckUntil; }
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
        internal static void StartRecheck()
        {
            _recheckUntil = Time.realtimeSinceStartup + SortRecheckFor;
            _recheckMoved = 0;

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                "Перепроверяю сундуки в зоне");
        }

        // A square reaches to its corner, which is further than its half-side. The sweep
        // that gathers pieces asks for a radius, so it has to ask for the diagonal and let
        // Sorting.Inside throw back what fell outside the box.
        private const float SquareDiagonal = 1.415f;

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

            _groupSpan = config.Bind("Сортировка", "GroupSpan", 8f,
                "На каком расстоянии сундуки считаются стоящими вместе, от 0 до 64 м. Куча "
                + "держится одной такой кучки и не расползается по базе. Мерится до "
                + "соседнего сундука, а не через всю группу: стена сундуков — одна кучка, "
                + "какой бы длинной ни была. 0 — не собирать в кучки вовсе.");
        }

        private static BepInEx.Configuration.ConfigEntry<bool> _zonesSeeded;

        private static BepInEx.Configuration.ConfigEntry<float> _groupSpan;

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
            PendingSortMark = null;

            var view = ViewOf(container);
            if (view == null || !view.IsValid()) return false;

            if (!view.IsOwner()) view.ClaimOwnership();

            var zdo = view.GetZDO();
            zdo.Set(PrivateChestKey, mark == MarkPrivate);
            zdo.Set(SortMarkKey, mark >= 0 ? Sorting.ToStored(mark) : 0);

            var said = mark == MarkPrivate ? "личный — сортировщик его не трогает"
                : mark >= 0 ? $"под «{Sorting.Title(mark)}»"
                : "без пометки — из него разбирают";

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, $"Сундук {said}");
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
        internal static IEnumerator SortingLoop()
        {
            var pieces = new List<Piece>();
            var bins = new List<Container>();
            var sources = new List<Container>();

            while (true)
            {
                yield return new WaitForSeconds(SortTick);

                var player = Player.m_localPlayer;
                if (player == null || !SortingOn || !RuleAllows("sort")) continue;

                var where = player.transform.position;
                var zones = SortingZones();
                var at = Sorting.ZoneAt(zones, where.x, where.z);
                _lastZone = at;
                if (at < 0)
                {
                    _lastBins = 0;
                    _lastSources = 0;
                    _lastPassenger = false;
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
                    continue;
                }

                var reach = Sorting.ClampRadius(zone.Radius) * (zone.Square ? SquareDiagonal : 1f);

                pieces.Clear();
                Piece.GetAllPiecesInRadius(new Vector3(zone.X, where.y, zone.Z), reach, pieces);

                bins.Clear();
                sources.Clear();
                var carts = 0;
                foreach (var piece in pieces)
                {
                    if (piece == null) continue;

                    var container = piece.GetComponentInChildren<Container>();
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
                    if (IsSupplyChest(container)) continue;

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

                // Said once per change, not once a second: enough to answer «видит ли он
                // тележку», quiet enough to leave on.
                var said = $"bins {bins.Count}, sources {sources.Count}, carts {carts}"
                           + (_stuck.Length > 0 ? $", stuck {_stuck}" : "");
                if (said != _lastSaid)
                {
                    _lastSaid = said;
                    Log.LogInfo($"[AstvardServerMod] Sorting zone: {said}.");
                }

                if (bins.Count == 0) continue;

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

                var moved = recheck ? TidyBins(bins, plan, budget) : 0;
                if (moved < budget) moved += SweepOnce(sources, bins, plan, budget - moved);

                // Then put right what is already in the marked chests: a pile that grew
                // out of the shared chest moves to its own, one that was spent moves back.
                // At least one move is always kept for this - otherwise a base with carts
                // coming in all evening never tidies itself at all.
                if (!recheck) moved += TidyBins(bins, plan, Mathf.Max(1, budget - moved));

                if (recheck) _recheckMoved += moved;

                // The pass is over: say what came of it, because «ничего не двинулось»
                // and «кнопка не нажалась» look the same from where the player stands.
                if (recheck && !Rechecking)
                    Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                        _recheckMoved > 0
                            ? $"Перепроверено: переложено {_recheckMoved}"
                            : "Перепроверено: всё на своих местах");

                if (moved <= 0) continue;

                // Something is happening over there: said rarely enough to be news, not noise.
                if (Time.realtimeSinceStartup - _sortSaidAt < 10f) continue;
                _sortSaidAt = Time.realtimeSinceStartup;
                player.Message(MessageHud.MessageType.TopLeft, $"Сортировка: разложено {moved}");
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
                   + (_lastCarts > 0 ? $", из них тележек {_lastCarts}." : ".");
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

                    // Where it belongs already, which is most of everything most sweeps.
                    if (plan.Belongs(item.m_shared.m_name, CategoryOf(item), at)) continue;

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
            var kind = item.m_shared.m_name;
            var chosen = Sorting.ChosenFor(kind);
            if (chosen >= 0) return chosen;
            if (Sorting.IsLoot(kind)) return Sorting.Loot;

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
