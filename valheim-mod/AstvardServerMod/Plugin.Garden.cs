using System.Collections.Generic;
using Splatform;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>
        /// Огород: созревшее уезжает в помеченные сундуки, а на освободившееся место
        /// возвращается саженец.
        ///
        /// Собирается это тем же способом, каким собирает человек, и потому без единого
        /// сюрприза: столько же, сколько дала бы грядка ему (включая ставку ресурсов
        /// мира), и та же отметка «сорвано» - со сроком восхода у ягод и с исчезновением
        /// у моркови. Подсадка тоже не выдумана: ставится тот самый саженец, который
        /// вырастает в эту культуру, и платится он тем же, чем платит игрок, - семенами
        /// из сундука.
        ///
        /// Сажаем **только туда, откуда сейчас сорвали**. На той земле только что росло,
        /// значит она вскопана, биом верный, солнце есть и место свободно - всё то, что
        /// саженец проверяет сам и о чём у нас нет способа спросить, не переписав пол-игры.
        /// </summary>
        private static BepInEx.Configuration.ConfigEntry<bool> _reapGarden;

        private static BepInEx.Configuration.ConfigEntry<bool> _sowGarden;

        private static BepInEx.Configuration.ConfigEntry<bool> _sowEmpty;

        private static BepInEx.Configuration.ConfigEntry<bool> _sowTrees;

        private static BepInEx.Configuration.ConfigEntry<int> _cropKeep;

        private static BepInEx.Configuration.ConfigEntry<int> _bedCap;

        internal static void BindGarden(BepInEx.Configuration.ConfigFile config)
        {
            _reapGarden = config.Bind("Сортировка", "Reap", true,
                "Собирать ли созревшее с грядок внутри зоны в помеченные сундуки. "
                + "«Функции» → «Сортировка» → «Настройки» → «Собирать урожай».");

            _sowGarden = config.Bind("Сортировка", "Sow", true,
                "Подсаживать ли на освободившееся место. Саженец берётся тот, что вырастает "
                + "в эту культуру, и платится семенами из помеченных сундуков — как у игрока. "
                + "Пределы «Урожая на складе» и «Грядок в зоне» её держат так же, как засев. "
                + "«Настройки» → «Огород» → «Пересаживать».");

            _sowEmpty = config.Bind("Сортировка", "SowEmpty", true,
                "Засевать ли пустую вскопанную землю внутри зоны — не только те места, где "
                + "мод сам сорвал. Сажается то, чего в сундуках зоны больше всего, и только "
                + "там, где растение само согласилось бы расти. "
                + "«Настройки» → «Огород» → «Засаживать».");

            _sowTrees = config.Bind("Сортировка", "SowTrees", false,
                "Сажать ли саженцы деревьев вместе с грядками. По умолчанию нет: дереву "
                + "вскопанная земля не нужна, и засев занял бы всю зону подряд, а не одни "
                + "грядки. «Настройки» → «Огород» → «Деревья».");

            _cropKeep = config.Bind("Сортировка", "CropKeep", 100,
                "До какого запаса сажать — и засевать пустое, и пересаживать, — по каждой "
                + "культуре свой счёт. Моркови на складе столько — новую морковь не сажаем, "
                + "лук и ячмень сажаем дальше. Созревшее собирается в любом случае. 0 — без "
                + "предела. "
                + "«Настройки» → «Огород» → «Урожая на складе».");

            _bedCap = config.Bind("Сортировка", "BedsInZone", 100,
                "Сколько грядок держать в зоне: больше этого числа мод не сажает и не "
                + "пересаживает. Считаются все занятые места — растущие, созревшие и "
                + "посаженные руками. Сбор идёт мимо предела. 0 — без предела. "
                + "«Настройки» → «Огород» → «Грядок в зоне».");
        }

        /// <summary>
        /// Сколько грядок мод держит в зоне.
        ///
        /// Засев идёт по всей вскопанной земле зоны, а её у человека обычно больше, чем он
        /// собирался засаживать: решётка накрывает поле целиком и за несколько проходов
        /// уносит в землю весь запас семян. «Урожая на складе» от этого не спасает - он
        /// считает **собранное**, то есть срабатывает часами позже, когда сеять уже нечем.
        ///
        /// Считаются все занятые грядки, а не только наши. Предел про то, сколько огорода
        /// нужно, а огороду всё равно, чьи руки его сажали.
        ///
        /// Пересадку он держит наравне с засевом. Она возвращает сорванное, но начинает
        /// этим **новую** работу, а не доделывает старую, и семя, потраченное на грядку
        /// сверх просимого, потрачено зря. Мимо пределов идёт только сбор - по решению
        /// хозяина, и там оно верно: созревшая грядка это уже сделанная работа.
        ///
        /// Оттого предел и умеет огород **уменьшать**: поставил меньше, чем стоит, - и
        /// лишние грядки не возвращаются по мере сбора. Выкапывать мод не выкапывает
        /// ничего.
        /// </summary>
        internal static int BedCap
        {
            get { return Mathf.Clamp(_bedCap != null ? _bedCap.Value : 0, 0, 100000); }
        }

        internal static void SetBedCap(int amount)
        {
            if (_bedCap != null) _bedCap.Value = Mathf.Clamp(amount, 0, 100000);
        }

        /// <summary>
        /// До какого запаса засевать, по каждой культуре отдельно.
        ///
        /// Одно число на весь огород означало бы кладовую из одной культуры: сотня моркови
        /// остановила бы и лук, и ячмень. Считается так же, как у еды и угля, - по той же
        /// таблице запаса, собранной за проход.
        ///
        /// **Сбора это не касается** (решение хозяина 20.09.2026, по живому огороду, где
        /// созрело 27 морковок и не собралась ни одна). Предел придуман против того, чтобы
        /// сундуки забивались тем, что и так никуда не денется, - то есть против **лишней
        /// посадки**. Созревшая грядка это уже не запас на будущее, а сделанная работа: её
        /// посадили - мод или сам хозяин, - и оставлять её стоять только потому, что склад
        /// полон, значит отменять чужое решение задним числом.
        /// </summary>
        internal static bool SowEmptyOn
        {
            get { return _sowEmpty == null || _sowEmpty.Value; }
        }

        internal static void SetSowEmpty(bool on)
        {
            if (_sowEmpty != null) _sowEmpty.Value = on;
        }

        internal static bool SowTreesOn
        {
            get { return _sowTrees != null && _sowTrees.Value; }
        }

        internal static void SetSowTrees(bool on)
        {
            if (_sowTrees != null) _sowTrees.Value = on;
        }

        internal static int CropKeep
        {
            get { return Mathf.Clamp(_cropKeep != null ? _cropKeep.Value : 0, 0, 100000); }
        }

        internal static void SetCropKeep(int amount)
        {
            if (_cropKeep != null) _cropKeep.Value = Mathf.Clamp(amount, 0, 100000);
        }

        internal static bool ReapOn
        {
            get { return _reapGarden == null || _reapGarden.Value; }
        }

        internal static void SetReap(bool on)
        {
            if (_reapGarden != null) _reapGarden.Value = on;
        }

        internal static bool SowOn
        {
            get { return _sowGarden == null || _sowGarden.Value; }
        }

        internal static void SetSow(bool on)
        {
            if (_sowGarden != null) _sowGarden.Value = on;
        }

        // Грядки растут часами, так что пять секунд здесь - это «сразу». Заодно это и
        // цена: обход всех Pickable в загруженном мире дороже прохода по деталям, и раз
        // в секунду его гонять незачем.
        private const float GardenTick = 5f;

        private static float _gardenAt;

        /// <summary>Место, с которого только что сорвали, и что на нём росло.</summary>
        private struct Bed
        {
            internal Vector3 At;

            internal string Crop;
        }

        // Подсаживаем не в тот же проход, а в следующий: сорванное исчезает у владельца
        // записи, и саженец, поставленный в ту же секунду, встал бы в ещё живую морковь.
        private static readonly List<Bed> Beds = new List<Bed>();

        private static readonly List<Bed> BedsNext = new List<Bed>();

        private static readonly List<ZDOID> SownScratch = new List<ZDOID>();

        // Почему проход ничего не сделал - счётчики одного прохода. Огород, который
        // молча ничего не делает, неотличим от огорода, которому нечего делать: так и
        // вышло у хозяина в первый же вечер, и сказать ему было нечем.
        private static int _ripe;

        private static int _noRoom;

        private static int _notOurs;

        private static int _extras;

        /// <summary>Пустых вскопанных мест в зоне, засеяно, и почему не засеяно.</summary>
        private static int _empty;

        private static int _emptySown;

        private static int _emptyNoSeeds;

        private static string _sowingWhat;

        private static int _noSeeds;

        private static int _noSapling;

        /// <summary>Сколько пересадок отложено: этого добра уже довольно.</summary>
        private static int _keptBack;

        private static int _growing;

        private static int _outside;

        private static string _gardenSaid;

        /// <summary>Сколько культур сейчас не засеваем: их на складе уже довольно.</summary>
        internal static int CropsFull;

        /// <summary>Сколько грядок в зоне занято и уткнулся ли засев в их предел.</summary>
        internal static int BedsInZone;

        internal static bool BedsFull;

        // То же самое, но по-русски и для панели: лог читать некому, пока человек играет.
        // Хозяин это и спросил первым делом - «а где посмотреть надпись?».
        private static string _gardenPanel = "Огород: ещё не смотрел.";

        /// <summary>Строка для страницы «Сортировка».</summary>
        internal static string GardenWhy()
        {
            if (!ReapOn && !SowOn) return "Огород: выключен.";

            return _gardenPanel;
        }

        /// <summary>
        /// Из посекундного прохода автоматики: раз в пять секунд смотрим грядки зоны.
        /// </summary>
        private static void TickGarden(Player player, Sorting.Zone zone, List<Container> supply)
        {
            if (Time.realtimeSinceStartup < _gardenAt) return;
            _gardenAt = Time.realtimeSinceStartup + GardenTick;

            // Про предел грядок отвечают оба сеятеля, а спрашивают его в разное время,
            // так что обнуляется он здесь, до них обоих: в засеве он стирал бы ответ,
            // который дала пересадка десятью строками выше.
            BedsFull = false;

            // Сначала подсадка на места прошлого прохода, потом сбор: иначе собранное в
            // этом проходе засеялось бы тут же, по живому.
            var beds = Beds.Count;
            var sown = SowBeds(supply);

            Beds.Clear();
            Beds.AddRange(BedsNext);
            BedsNext.Clear();

            var reaped = ReapBeds(player, zone);

            // «Заполнение огорода» - то, ради чего всё и просили. Подсадка на свои же
            // места закрывает только половину: грядку, сорванную руками, не вернуть
            // ничем - морковь при сборе уничтожается, и места от неё не остаётся.
            // Поэтому смотрим саму землю: вскопано, пусто, биом верный - сажаем.
            sown += SowEmpty(zone, supply);

            Say(zone, beds, sown, reaped);
        }

        /// <summary>
        /// Одна строка на изменение: что огород видел и чего не сделал.
        ///
        /// Раньше строка выходила, только когда что-то собрано, - то есть ровно в том
        /// случае, когда и так всё хорошо. «Не работает» выглядело как тишина, а причин
        /// у тишины полдюжины: грядки вне зоны, ты вне зоны, класть некуда, грядку
        /// считает не твой клиент, сеять нечем.
        /// </summary>
        private static void Say(Sorting.Zone zone, int beds, int sown, int reaped)
        {
            _gardenPanel = zone == null
                ? "Огород: ты вне зоны."
                : $"Огород: созрело {_ripe}"
                  + (reaped > 0 ? $", собрано {reaped}" : "")
                  + (sown > 0 ? $", посажено {sown}" : "")
                  + (_growing > 0 ? $",{NEWLINE}ещё растёт {_growing}" : "")
                  + (BedsFull ? $",{NEWLINE}занято {BedsInZone} грядок из {BedCap}" : "")
                  + (_outside > 0 ? $",{NEWLINE}рядом вне зоны {_outside}" : "")
                  + (_noRoom > 0 ? $",{NEWLINE}некуда сложить {_noRoom}" : "")
                  + (_noSeeds > 0 ? $",{NEWLINE}нет семян на {_noSeeds}" : "")
                  + (_keptBack > 0 ? $",{NEWLINE}не пересажено {_keptBack} — хватает" : "")
                  + (_empty > 0 ? $",{NEWLINE}пустых грядок {_empty}" : "")
                  + (_emptySown > 0 ? $",{NEWLINE}засеяно {_emptySown}" : "")
                  + (_sowingWhat != null ? $" ({_sowingWhat}," : "")
                  + (_sowingGrow != null ? $"{NEWLINE}растёт {_sowingGrow})" : _sowingWhat != null ? ")" : "")
                  + (_emptyNoSeeds > 0 ? $",{NEWLINE}семена кончились" : "")
                  + ".";

            var said = zone == null
                ? "ты вне зоны — грядки не трогаем"
                : $"ripe {_ripe}, reaped {reaped}, sown {sown} of {beds} beds"
                  + (_growing > 0 ? $", {_growing} still growing" : "")
                  + (BedsFull ? $", {BedsInZone} beds of {BedCap}, the cap holds" : "")
                  + (_outside > 0 ? $", {_outside} ripe outside the zone" : "")
                  + (_noRoom > 0 ? $", {_noRoom} with nowhere to put the crop" : "")
                  + (_notOurs > 0 ? $", {_notOurs} counted by somebody else" : "")
                  + (_extras > 0 ? $", {_extras} left alone (extra drops)" : "")
                  + (_noSeeds > 0 ? $", {_noSeeds} unsown for want of seeds" : "")
                  + (_noSapling > 0 ? $", {_noSapling} with no sapling to put back" : "")
                  + (_keptBack > 0 ? $", {_keptBack} not put back, enough already" : "")
                  + (_sowingWhat != null ? $", sowing {_sowingWhat} every {_sowingStep:0.00} m"
                                        : ", nothing to sow with")
                  + (_empty > 0 ? $", {_empty} empty beds, {_emptySown} filled" : "")
                  + (_emptyNoSeeds > 0 ? ", ran out of seeds" : "");

            if (said == _gardenSaid) return;

            _gardenSaid = said;
            Log.LogInfo($"[AstvardServerMod] Garden: {said}.");
        }

        /// <summary>Собирает созревшее внутри зоны. Возвращает, сколько грядок сорвано.</summary>
        private static int ReapBeds(Player player, Sorting.Zone zone)
        {
            _ripe = 0;
            _noRoom = 0;
            _notOurs = 0;
            _extras = 0;
            _outside = 0;
            BedsInZone = 0;

            if (zone == null) return 0;

            // Грядки, которым ещё расти, считаются отдельно и не ради красоты: «созрело 0»
            // само по себе не отличает пустой огород от посаженного час назад, а это
            // первое, что спрашивает человек, у которого «ничего не происходит».
            _growing = 0;
            foreach (var plant in Object.FindObjectsByType<Plant>(FindObjectsSortMode.None))
            {
                if (plant == null) continue;

                var bed = plant.transform.position;
                if (!Sorting.Inside(zone, bed.x, bed.z)) continue;

                _growing++;
                BedsInZone++;
            }

            var where = player.transform.position;
            var reaped = 0;

            foreach (var pickable in Object.FindObjectsByType<Pickable>(FindObjectsSortMode.None))
            {
                if (pickable == null || pickable.GetPicked()) continue;
                if (pickable.m_itemPrefab == null) continue;

                // Внутри зоны расстояние не меряется вовсе - то же правило, что у
                // станций: что в одной зоне, то и работает вместе. Круга вокруг игрока
                // здесь и не было смысла держать: грядка дальше 64 м всё равно попадает
                // в обход только тогда, когда её кто-то подгрузил.
                var spot = pickable.transform.position;
                if (!Sorting.Inside(zone, spot.x, spot.z))
                {
                    // Созревшее рядом, но за чертой: чаще всего это и есть ответ на
                    // «огород не собирается» - зону просто обвели не вокруг грядок.
                    // Считается только то, что вообще сажают: в первый же вечер сюда
                    // насчиталось 167 - дикая малина, ветки и кремень вокруг базы, - и
                    // за ними было не видно ответа на заданный вопрос.
                    if ((spot - where).sqrMagnitude <= Sorting.NearReach * Sorting.NearReach
                        && SaplingFor(Utils.GetPrefabName(pickable.gameObject)) != null) _outside++;
                    continue;
                }

                // Созревшая грядка - тоже занятое место: земля под ней не свободна, и
                // предел считает её наравне с растущей. До всех отказов ниже - собрать её
                // может быть и нельзя, а стоять она всё равно стоит.
                if (SaplingFor(Utils.GetPrefabName(pickable.gameObject)) != null) BedsInZone++;

                // С добавкой в придачу пусть разбирается игрок: дополнительный дроп мы
                // сложить не умеем, а бросить его на землю - значит устроить свалку.
                if (pickable.m_extraDrops != null && !pickable.m_extraDrops.IsEmpty())
                {
                    _extras++;
                    continue;
                }

                _ripe++;

                if (!ReapOn) continue;

                // Владение берётся до всего: считает грядку её хозяин, и сорвать чужую
                // мы не можем - RPC на той стороне просто ничего не сделает.
                var view = pickable.GetComponent<ZNetView>();
                if (view == null || !view.IsValid()) continue;
                if (!view.IsOwner()) view.ClaimOwnership();
                if (!view.IsOwner())
                {
                    _notOurs++;
                    continue;
                }

                // Столько же, сколько дала бы грядка человеку: ставка ресурсов мира
                // считается той же игровой функцией, а не нашей арифметикой.
                var amount = pickable.m_dontScale
                    ? pickable.m_amount
                    : Mathf.Max(pickable.m_minAmountScaled,
                                Game.instance.ScaleDrops(pickable.m_itemPrefab, pickable.m_amount));

                if (amount <= 0) continue;

                // Складываем раньше, чем срываем. Некуда - пусть растёт дальше: грядка,
                // сорванная в никуда, - это потерянный урожай, а не отложенный.
                if (!TryStoreNearby(spot, pickable.m_itemPrefab, amount))
                {
                    _noRoom++;
                    continue;
                }

                // Та же рассылка, которой заканчивает сама игра: у себя это отметит
                // сорванным (или снесёт, если восходить нечему), у соседей - погасит куст.
                view.InvokeRPC(ZNetView.Everybody, "RPC_SetPicked", true);

                // Имя **выросшего объекта** (`Pickable_Carrot`), а не предмета (`Carrot`):
                // словарь саженцев собран из `Plant.m_grownPrefabs`, то есть из того, во
                // что саженец превращается. Пока сюда клали имя предмета, поиск саженца не
                // совпадал ни разу - подсадка не могла сработать вовсе, и молчала об этом.
                BedsNext.Add(new Bed { At = spot, Crop = Utils.GetPrefabName(pickable.gameObject) });
                reaped++;
            }

            return reaped;
        }

        /// <summary>Возвращает саженцы на места, освобождённые прошлым проходом.</summary>
        private static int SowBeds(List<Container> supply)
        {
            // Обнуление - до всех выходов, а не после. Стояло после, и счётчик прошлого
            // прохода оставался в строке навсегда: «1 unsown for want of seeds» висело в
            // логе и на странице ещё десять проходов спустя, хотя подсаживать было нечего
            // вовсе. Диагностика, которая врёт, хуже её отсутствия - ровно этим здесь и
            // занимались весь вечер.
            _noSeeds = 0;
            _noSapling = 0;
            _keptBack = 0;

            if (!SowOn || Beds.Count == 0) return 0;

            var player = Player.m_localPlayer;
            if (player == null) return 0;

            var creator = player.GetPlayerID();
            var platform = PlatformManager.DistributionPlatform.LocalUser.PlatformUserID;
            var sown = 0;

            // Сколько грядок занято **сейчас**: счёт прошлого прохода вёлся до того, как
            // он сорвал эти самые места, и они в нём ещё считаются занятыми. Вычесть их
            // обязательно - огород, стоящий ровно в предел, иначе перестал бы
            // пересаживаться вовсе и высох бы за вечер.
            var kept = Mathf.Max(0, BedsInZone - Beds.Count);

            foreach (var bed in Beds)
            {
                var sapling = SaplingFor(bed.Crop);
                if (sapling == null)
                {
                    _noSapling++;
                    continue;
                }

                // Пределы спрашиваются до семян и по тем же правилам, что у засева:
                // пересадка возвращает сорванное, но тратит на это новое семя.
                if (CropFull(sapling))
                {
                    _keptBack++;
                    continue;
                }

                if (BedCap > 0 && kept >= BedCap)
                {
                    BedsFull = true;
                    _keptBack++;
                    continue;
                }

                // Префаб найден - только теперь берём семена: вынуть их и не найти, куда
                // сажать, значило бы съесть семена ни за что.
                if (!PaySeeds(sapling, bed.At, supply))
                {
                    _noSeeds++;
                    continue;
                }

                SownScratch.Clear();
                PlacePiece(sapling, bed.At, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f),
                           creator, platform, SownScratch);
                sown++;
                kept++;
            }

            Beds.Clear();
            return sown;
        }

        // Сколько пустых грядок засаживать за проход. Не ради скорости: ошибка в выборе
        // места стоит семян, и пусть она стоит десяти штук, а не всего запаса.
        private const int SowPerPass = 10;

        private static readonly Collider[] SpaceScratch = new Collider[8];

        private static int _spaceMask;

        /// <summary>
        /// Сколько метров саженец дерева держит до ближайшей постройки.
        ///
        /// Грядке хватает собственного `m_growRadius` - она маленькая и никуда не
        /// вырастет. Дерево вырастает в ствол с кроной, роняет ветки и однажды падает,
        /// а «место свободно» игра проверяет по **саженцу**, то есть по полуметровому
        /// прутику. Четыре метра - просьба хозяина, и она про то же: сажать можно рядом
        /// с домом, но не в дом.
        /// </summary>
        private const float TreeClearance = 4f;

        private static readonly Collider[] BuildScratch = new Collider[32];

        private static int _buildMask;

        private static float _sowingStep;

        private static string _sowingGrow;

        /// <summary>
        /// Сколько растёт каждая культура, словами, - для строки в логе.
        ///
        /// Числа спрошены у самих префабов (`Plant.m_growTime` и `m_growTimeMax`): у каждой
        /// грядки время своё, случайное между ними по её семени, так что честный ответ -
        /// это вилка, а не одно число. Писать его сюда из памяти нельзя: в данных префабов
        /// оно меняется от обновления к обновлению, а проверить это можно только так.
        /// </summary>
        private static string GrowTimes()
        {
            var said = new List<string>();

            foreach (var pair in SaplingByCrop)
            {
                var sapling = pair.Value;
                if (sapling == null || IsTree(sapling)) continue;

                var plant = sapling.GetComponent<Plant>();
                if (plant == null) continue;

                said.Add($"{pair.Key} {GrowSpan(plant)}");
            }

            said.Sort(System.StringComparer.Ordinal);
            return said.Count > 0 ? string.Join(", ", said.ToArray()) : "none";
        }

        /// <summary>Вилка времени роста в минутах, как её видит человек.</summary>
        private static string GrowSpan(Plant plant)
        {
            var from = plant.m_growTime / 60f;
            var to = plant.m_growTimeMax / 60f;

            return Mathf.Abs(to - from) < 0.5f
                ? $"{from:0} мин"
                : $"{from:0}-{to:0} мин";
        }

        /// <summary>
        /// Шаг сетки: сколько держать между серединами двух растений.
        ///
        /// Не «два радиуса роста», как кажется: игра считает место занятым, когда в шар
        /// `m_growRadius` попадает **чужой коллайдер**, а не чужая середина. Значит
        /// держать надо радиус плюс собственную толщину соседа - её и меряем у самого
        /// префаба, тем же `LocalBox`, каким меряется сундук для ряда. Числа в коде тут
        /// были бы выдумкой: у моркови, ячменя и сосны это разные величины.
        /// </summary>
        private static float Spacing(GameObject sapling, Plant plant)
        {
            var half = 0f;

            Bounds box;
            if (LocalBox(sapling, out box)) half = Mathf.Max(box.extents.x, box.extents.z);

            // Пять сантиметров про запас: у края шара решает уже точность float, и
            // растение, поставленное впритык, объявило бы себе «нет места».
            return Mathf.Max(0.3f, plant.m_growRadius + half + 0.05f);
        }

        /// <summary>
        /// Засевает вскопанную, но пустую землю внутри зоны.
        ///
        /// Условия спрашиваются те же, по которым растение само решает, живо ли оно
        /// (`Plant.UpdateHealth` и `HaveGrowSpace` в игре): вскопано, биом подходит, рядом
        /// ничего не стоит. Своей арифметики тут нет ни строчки - иначе саженец встал бы
        /// и тут же умер, а семена бы кончились.
        /// </summary>
        private static int SowEmpty(Sorting.Zone zone, List<Container> supply)
        {
            _empty = 0;
            _emptySown = 0;
            _emptyNoSeeds = 0;
            _sowingWhat = null;
            _sowingGrow = null;

            if (!SowEmptyOn || zone == null) return 0;

            if (BedCap > 0 && BedsInZone >= BedCap)
            {
                BedsFull = true;
                return 0;
            }

            var player = Player.m_localPlayer;
            var zones = ZoneSystem.instance;
            if (player == null || zones == null) return 0;

            var sapling = SaplingToSow(supply);
            if (sapling == null) return 0;

            var plant = sapling.GetComponent<Plant>();
            if (plant == null) return 0;

            if (_spaceMask == 0)
                _spaceMask = LayerMask.GetMask("Default", "static_solid", "Default_small",
                                               "piece", "piece_nonsolid");

            var title = sapling.GetComponent<Piece>();
            _sowingWhat = title != null && !string.IsNullOrEmpty(title.m_name) && Localization.instance != null
                ? Localization.instance.Localize(title.m_name)
                : sapling.name;
            _sowingGrow = GrowSpan(plant);

            var creator = player.GetPlayerID();
            var platform = PlatformManager.DistributionPlatform.LocalUser.PlatformUserID;

            var spacing = Spacing(sapling, plant);
            _sowingStep = spacing;

            var tree = IsTree(sapling);

            // Шахматная укладка, а не клетка. У клетки соседи по диагонали стоят в 1.41
            // шага, то есть между четырьмя растениями остаётся место, которого хватило бы
            // ещё на одно; в шахматной у каждого шесть соседей и все ровно на шаге -
            // плотнее уложить круги одного радиуса нельзя вовсе.
            var rowStep = spacing * 0.866f;   // √3/2: высота равностороннего треугольника

            // Решётка считается от начала мира, а не от середины зоны: зону двигают,
            // вращают и меняют ей размер, и привязанная к ней сетка разъехалась бы с тем,
            // что посажено вчера. От мира она всегда одна и та же.
            var reach = zone.Square ? zone.Radius * 1.415f : zone.Radius;
            var firstRow = Mathf.FloorToInt((zone.Z - reach) / rowStep);
            var lastRow = Mathf.CeilToInt((zone.Z + reach) / rowStep);

            for (var row = firstRow; row <= lastRow; row++)
            {
                var z = row * rowStep;

                // Нечётный ряд сдвинут на полшага - от этого и получается шахматка.
                var shift = (row & 1) == 0 ? 0f : spacing * 0.5f;
                var firstCol = Mathf.FloorToInt((zone.X - reach - shift) / spacing);
                var lastCol = Mathf.CeilToInt((zone.X + reach - shift) / spacing);

                for (var col = firstCol; col <= lastCol; col++)
                {
                    if (_emptySown >= SowPerPass) return _emptySown;

                    if (BedCap > 0 && BedsInZone + _emptySown >= BedCap)
                    {
                        BedsFull = true;
                        return _emptySown;
                    }

                    var x = col * spacing + shift;
                    if (!Sorting.Inside(zone, x, z)) continue;

                    float y;
                    if (!zones.GetGroundHeight(new Vector3(x, 0f, z), out y)) continue;

                    var at = new Vector3(x, y, z);
                    var ground = Heightmap.FindHeightmap(at);
                    if (ground == null) continue;

                    if (plant.m_needCultivatedGround && !ground.IsCultivated(at)) continue;
                    if ((ground.GetBiome(at) & plant.m_biome) == 0) continue;

                    if (tree && Built(at)) continue;

                    // Занято - значит занято: тут уже растёт, лежит или стоит. Игра
                    // смотрит те же слои, только прощает соседнее больное растение;
                    // мы не прощаем - лишняя грядка дешевле съеденного семени.
                    if (Physics.OverlapSphereNonAlloc(at, plant.m_growRadius, SpaceScratch,
                                                      _spaceMask) > 0) continue;

                    _empty++;

                    if (!PaySeeds(sapling, at, supply))
                    {
                        _emptyNoSeeds++;
                        return _emptySown;
                    }

                    SownScratch.Clear();
                    PlacePiece(sapling, at, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f),
                               creator, platform, SownScratch);
                    _emptySown++;
                }
            }

            return _emptySown;
        }

        /// <summary>
        /// Чем засевать: тем, чего в сундуках зоны больше всего.
        ///
        /// Выбор один на проход и предсказуемый - «сажаем то, чего у тебя много». Культура,
        /// которой на складе уже хватает (см. «Урожая на складе»), пропускается: незачем
        /// сажать то, что мы тут же перестанем собирать.
        /// </summary>
        private static GameObject SaplingToSow(List<Container> supply)
        {
            if (!ReadSaplings()) return null;

            GameObject best = null;
            var bestSeeds = 0;
            CropsFull = 0;

            foreach (var sapling in SaplingByCrop.Values)
            {
                if (sapling == null) continue;
                if (!SowTreesOn && IsTree(sapling)) continue;

                var piece = sapling.GetComponent<Piece>();
                if (piece == null || piece.m_resources == null || piece.m_resources.Length == 0) continue;

                // Урожай этого саженца: если его на складе уже довольно, сажать незачем.
                if (CropFull(sapling))
                {
                    CropsFull++;
                    continue;
                }

                var seeds = int.MaxValue;
                foreach (var need in piece.m_resources)
                {
                    if (need == null || need.m_resItem == null) continue;

                    var had = InStock(need.m_resItem.gameObject.name);
                    if (need.m_amount > 0) had /= need.m_amount;
                    seeds = Mathf.Min(seeds, had);
                }

                if (seeds == int.MaxValue || seeds <= 0 || seeds <= bestSeeds) continue;

                bestSeeds = seeds;
                best = sapling;
            }

            return best;
        }

        /// <summary>
        /// Стоит ли в четырёх метрах отсюда постройка.
        ///
        /// Спрашивается физикой, а не обходом деталей: `GetAllPiecesInRadius` проходит
        /// **все** детали мира на каждый вопрос, а вопросов здесь - по одному на каждое
        /// место решётки. Слои те же, на которых лежат постройки.
        ///
        /// Грядки и кусты из ответа выбрасываются: они тоже `Piece`, и без этого дерево
        /// не встало бы рядом с собственным огородом - то есть ровно там, где его и
        /// сажают. Признак - `Plant` или `Pickable` на той же детали.
        ///
        /// Если ответ не поместился в буфер, место считается занятым. Правило просили
        /// «на всякий случай», и случай, в котором мы не досмотрели, - как раз такой.
        /// </summary>
        private static bool Built(Vector3 at)
        {
            if (_buildMask == 0) _buildMask = LayerMask.GetMask("piece", "piece_nonsolid");

            var found = Physics.OverlapSphereNonAlloc(at, TreeClearance, BuildScratch, _buildMask);
            if (found >= BuildScratch.Length) return true;

            for (var i = 0; i < found; i++)
            {
                var hit = BuildScratch[i];
                if (hit == null) continue;

                var piece = hit.GetComponentInParent<Piece>();
                if (piece == null) continue;

                if (piece.GetComponent<Plant>() != null || piece.GetComponent<Pickable>() != null)
                    continue;

                return true;
            }

            return false;
        }

        private static readonly Dictionary<string, bool> Trees = new Dictionary<string, bool>();

        /// <summary>
        /// Вырастет ли из этого саженца дерево.
        ///
        /// Спрошено у игры: у выросшего дерева есть `TreeBase` - тот самый, которому сама
        /// `Plant.Grow` зовёт `Grow()`. Список пород в коде устарел бы с ближайшим
        /// обновлением, а признак - нет. Вопрос не праздный: дереву **вскопанная земля не
        /// нужна**, и засев без этого отбора занял бы саженцами всю зону подряд, а не
        /// одни грядки.
        /// </summary>
        private static bool IsTree(GameObject sapling)
        {
            bool tree;
            if (Trees.TryGetValue(sapling.name, out tree)) return tree;

            tree = false;
            var plant = sapling.GetComponent<Plant>();
            if (plant != null && plant.m_grownPrefabs != null)
                foreach (var grown in plant.m_grownPrefabs)
                    if (grown != null && grown.GetComponent<TreeBase>() != null)
                    {
                        tree = true;
                        break;
                    }

            Trees[sapling.name] = tree;
            return tree;
        }

        /// <summary>
        /// Довольно ли уже этой культуры на складе, чтобы не сажать её снова.
        ///
        /// Один вопрос на оба сеятеля: засев выбирает этим, чем засеять пустое, а
        /// пересадка - стоит ли возвращать сорванное. Разойтись им нельзя, иначе одна
        /// половина огорода живёт по пределу, а вторая его не знает.
        /// </summary>
        private static bool CropFull(GameObject sapling)
        {
            if (CropKeep <= 0) return false;

            var crop = CropOf(sapling);
            return crop != null && InStock(crop) >= CropKeep;
        }

        /// <summary>Что вырастет из этого саженца — имя предмета, которым это ляжет в сундук.</summary>
        private static string CropOf(GameObject sapling)
        {
            var plant = sapling.GetComponent<Plant>();
            if (plant == null || plant.m_grownPrefabs == null) return null;

            foreach (var grown in plant.m_grownPrefabs)
            {
                if (grown == null) continue;

                var pickable = grown.GetComponent<Pickable>();
                if (pickable != null && pickable.m_itemPrefab != null) return pickable.m_itemPrefab.name;
            }

            return null;
        }

        /// <summary>
        /// Платит за саженец из помеченных сундуков. Всё или ничего: половина цены - это
        /// съеденные семена и ни одной грядки.
        /// </summary>
        private static bool PaySeeds(GameObject sapling, Vector3 at, List<Container> supply)
        {
            var piece = sapling.GetComponent<Piece>();
            if (piece == null || piece.m_resources == null) return true;

            foreach (var need in piece.m_resources)
            {
                if (need == null || need.m_resItem == null) continue;

                // По одной штуке за раз, как берёт всё остальное в автоматике. Саженец
                // стоит одно семя - на большее эта дорога и не рассчитана.
                for (var i = 0; i < need.m_amount; i++)
                {
                    AcceptScratch.Clear();
                    AcceptScratch.Add(need.m_resItem.gameObject.name);

                    if (TakeSupply(at, supply, AcceptScratch) == null) return false;
                }
            }

            return true;
        }

        // Что во что вырастает, спрошено у самой игры: у каждого саженца свой список
        // всходов. Список строится один раз - префабы за сессию не меняются.
        private static readonly Dictionary<string, GameObject> SaplingByCrop =
            new Dictionary<string, GameObject>();

        private static bool _saplingsRead;

        private static GameObject SaplingFor(string crop)
        {
            if (!ReadSaplings()) return null;

            GameObject sown;
            return SaplingByCrop.TryGetValue(crop, out sown) ? sown : null;
        }

        private static bool ReadSaplings()
        {
            if (!_saplingsRead)
            {
                var scene = ZNetScene.instance;
                if (scene == null || scene.m_prefabs == null) return false;

                _saplingsRead = true;
                foreach (var prefab in scene.m_prefabs)
                {
                    if (prefab == null) continue;

                    var plant = prefab.GetComponent<Plant>();
                    if (plant == null || plant.m_grownPrefabs == null) continue;

                    foreach (var grown in plant.m_grownPrefabs)
                    {
                        if (grown == null) continue;

                        // Первый победил: у нескольких саженцев бывает общий всход, и
                        // выбирать между ними нам нечем - а брать разный через раз хуже,
                        // чем брать один и тот же.
                        if (!SaplingByCrop.ContainsKey(grown.name)) SaplingByCrop[grown.name] = prefab;
                    }
                }

                Log.LogInfo($"[AstvardServerMod] Garden: {SaplingByCrop.Count} crops can be sown back."
                            + $" Growing times: {GrowTimes()}");
            }

            return true;
        }
    }
}
