using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {

        internal static GameObject AutoCollectHint;

        internal static GameObject AutoCollectButton;

        internal static GameObject AssignChestButton;

        internal static GameObject UnassignChestButton;

        /// <summary>«Функции» → «Работа с сундуками»: подача, сбор, зона и установка вместе.</summary>
        internal static GameObject ChestWorkButton;

        internal static GameObject FillCategoryButton;

        internal static GameObject CollectCategoryButton;

        internal static GameObject FillHint;

        internal static GameObject FillButton;

        internal static GameObject FillAssignButton;

        internal static GameObject FillUnassignButton;

        // null = not armed, true = arm assign, false = arm unassign.
        internal static bool? PendingChestAssign;

        // Which role the armed button is about: supply chest or collection chest.
        internal static bool PendingChestSupply;

        internal static bool IsAutoCollectEnabled
        {
            get { return _autoCollect != null && _autoCollect.Value; }
            set { if (_autoCollect != null) _autoCollect.Value = value; }
        }

        internal static bool IsAutoFillEnabled
        {
            get { return _autoFill != null && _autoFill.Value; }
            set { if (_autoFill != null) _autoFill.Value = value; }
        }

        private static void UpdateAutoCollectButtonLabel()
        {
            var label = AutoCollectButton != null ? AutoCollectButton.GetComponentInChildren<Text>(true) : null;
            if (label != null) label.text = IsAutoCollectEnabled ? "Сбор в сундук: вкл" : "Сбор в сундук: выкл";
        }

        private static void UpdateFillButtonLabel()
        {
            var label = FillButton != null ? FillButton.GetComponentInChildren<Text>(true) : null;
            if (label != null) label.text = IsAutoFillEnabled ? "Наполнение: вкл" : "Наполнение: выкл";
        }

        // Lives in the chest's own ZDO, so the assignment is part of the world: it
        // survives a relog and every player sees the same chest, not only whoever set it.
        private const string CollectChestKey = "astvard_collect";

        private const string SupplyChestKey = "astvard_supply";

        private const float HarvestScanRadius = 64f;

        internal static bool IsCollectChest(Container container)
        {
            return HasChestFlag(container, CollectChestKey);
        }

        internal static bool IsSupplyChest(Container container)
        {
            return HasChestFlag(container, SupplyChestKey);
        }

        /// <summary>
        /// Пометка сундука. Владелец сети ищется вверх по объекту, а не на нём самом: у
        /// сундука ZNetView там же, где Container, **а у повозки Container на дочернем
        /// объекте, ZNetView на корне**. С GetComponent повозку нельзя было ни назначить
        /// на подачу, ни на сбор - и молча: пометка просто не читалась.
        /// </summary>
        private static bool HasChestFlag(Container container, string key)
        {
            var view = ViewOf(container);
            return view != null && view.IsValid() && view.GetZDO().GetBool(key);
        }

        /// <summary>
        /// Gives a chest one of its two roles, or takes it away. Returns false when the
        /// chest cannot be written to, so the caller can let it open as usual.
        /// A chest may hold both roles at once — nothing stops a barrel of coal from
        /// also being where the coal ends up.
        /// </summary>
        internal static bool SetChestRole(Container container, bool supply, bool enabled)
        {
            // Shift: назначение остаётся в руках, как и пометка сортировщика. Подача и сбор
            // назначаются пачкой у одной мастерской, а не по одному сундуку за заход.
            var again = HoldingShift;
            PendingChestAssign = again ? (bool?)enabled : null;
            NoteArmedByShift(again);

            var view = ViewOf(container);
            if (view == null || !view.IsValid()) return false;

            // The flag only sticks if the owner writes it, same as the inventory itself.
            if (!view.IsOwner()) view.ClaimOwnership();

            var key = supply ? SupplyChestKey : CollectChestKey;
            var was = view.GetZDO().GetBool(key);
            view.GetZDO().Set(key, enabled);

            string message;
            if (was == enabled)
                message = supply
                    ? (enabled ? "Из этого сундука уже берётся сырьё" : "Из этого сундука и так не берут")
                    : (enabled ? "Этот сундук уже для сбора" : "Этот сундук и так не для сбора");
            else
                message = supply
                    ? (enabled ? "Сундук назначен на подачу" : "Подача с сундука снята")
                    : (enabled ? "Сундук назначен для сбора" : "Сундук отвязан");

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                again ? message + " · Shift — назначай дальше" : message);
            Log.LogInfo("[AstvardServerMod] Chest " + (supply ? "supply " : "collect ")
                        + (enabled ? "set" : "cleared") + ".");
            return true;
        }

        /// <summary>
        /// Puts a produced item into a chest near <paramref name="origin"/>. Returns
        /// false when nothing could take it, so every caller can fall back to the
        /// vanilla behaviour of dropping it on the ground.
        /// </summary>
        internal static bool TryStoreNearby(Vector3 origin, GameObject prefab, int amount)
        {
            if (prefab == null || amount <= 0) return false;

            // Only a chest somebody pointed at. Guessing the nearest one meant a station
            // reached into whatever happened to stand by it - a sorting bin, a chest its
            // owner had called personal - and there was nothing to tell it otherwise.
            var target = FindChest(origin, AssignedChestRadius, prefab, amount, true, ChestZoneSquare);

            // Сундука сбора никто не назначил - но станция может стоять в зоне сортировки,
            // и тогда готовое едет прямо в сундук своей категории.
            if (target == null) target = FindZoneChest(origin, prefab, amount);
            if (target == null)
            {
                // Дальше вещь отдаётся игре, а игра кладёт её на землю у станции. Это не
                // потеря, но и не то, что замечают: пока об этом молчали, «уголь пропадает»
                // было единственным, что хозяин мог сказать о такой станции.
                SayNoRoom(origin, prefab);
                return false;
            }

            // A container saves itself to its ZDO only from the owner's side, so adding
            // to one we do not own would live in local memory and vanish on reload.
            var targetView = ViewOf(target);
            if (targetView == null || !targetView.IsValid()) return false;
            if (!targetView.IsOwner()) targetView.ClaimOwnership();

            return target.GetInventory().AddItem(prefab, amount);
        }

        // Жалуемся раз в минуту и по предмету: станций много, а беда одна, и шестьдесят
        // одинаковых строк в минуту - это не рассказ, а шум.
        private static readonly Dictionary<string, float> NoRoomSaid = new Dictionary<string, float>();

        private const float NoRoomEvery = 60f;

        // Раз на изменение, как и строка сортировщика: каждую секунду одно и то же - это
        // не новость, а шум, в котором тонет всё остальное.
        private static string _zoneStationsSaid;

        // Какой огонь в зоне чем горит. Мод не знает этого заранее и знать не должен:
        // топливо записано в самой детали, у деревянного факела смола, у костра дрова, у
        // иного пламени нет вовсе. Спрашивать у игры дешевле, чем помнить список, который
        // устареет с ближайшим обновлением.
        private static readonly Dictionary<string, string> ZoneFireKinds = new Dictionary<string, string>();

        private static void SayZoneStations(int smelters, int mine, int fires, int tames)
        {
            var kinds = new System.Text.StringBuilder();
            foreach (var pair in ZoneFireKinds)
            {
                kinds.Append(kinds.Length > 0 ? ", " : " — ");
                kinds.Append(pair.Key).Append(": ").Append(pair.Value);
            }

            var said = $"smelters {smelters} (mine {mine}), fires {fires}, "
                       + $"hungry beasts {tames}{kinds}";
            if (said == _zoneStationsSaid) return;

            _zoneStationsSaid = said;
            Log.LogInfo($"[AstvardServerMod] Zone stations: {said}"
                        + (smelters > mine
                            ? ". The rest answer to somebody else, and are fed by them."
                            : "."));
        }

        /// <summary>Станции в зоне некуда сдать готовое - сказать это один раз.</summary>
        private static void SayNoRoom(Vector3 origin, GameObject prefab)
        {
            if (prefab == null || Player.m_localPlayer == null) return;
            if (ZoneAround(origin) == null) return;

            var drop = prefab.GetComponent<ItemDrop>();
            var title = drop != null ? ItemTitle(drop.m_itemData) : prefab.name;

            float said;
            if (NoRoomSaid.TryGetValue(title, out said)
                && Time.realtimeSinceStartup - said < NoRoomEvery) return;

            NoRoomSaid[title] = Time.realtimeSinceStartup;
            Player.m_localPlayer.Message(MessageHud.MessageType.Center,
                $"{title}: в зоне некуда сложить — падает на землю");
            Log.LogInfo($"[AstvardServerMod] Station output: no chest in the zone can take "
                        + $"{title}, the game drops it at the station.");
        }

        private static readonly System.Reflection.MethodInfo MSpawnProcessed =
            AccessTools.Method(typeof(Smelter), "SpawnProcessed");

        /// <summary>
        /// Tips out what the smelter is still holding back.
        ///
        /// A stacking smelter does not hand over each bar as it is made. QueueProcessed
        /// counts them up in its own ZDO and only calls Spawn once the count reaches the
        /// item's max stack size - fifty coal, say - so the patch that routes Spawn into
        /// a chest never sees the remainder. It sits inside the kiln looking like nothing
        /// was produced, which is exactly what "it does not take everything" means.
        ///
        /// SpawnProcessed is the game's own way of emptying that counter, and it goes
        /// through Spawn, so calling it here needs no new path into the chest: whatever
        /// was being held simply arrives one sweep later.
        /// </summary>
        private static void FlushSmelter(Smelter smelter)
        {
            if (MSpawnProcessed == null || !smelter.m_spawnStack) return;

            if (!OwnedAndValid(smelter)) return;

            // The counter lives in the ZDO, and only its owner may write there.
            var view = StationView(smelter);
            if (view.GetZDO().GetInt(ZDOVars.s_spawnAmount) <= 0) return;

            MSpawnProcessed.Invoke(smelter, null);
        }

        /// <summary>
        /// Routes a smelter's finished product into a nearby chest. Covers smelters,
        /// blast furnaces and charcoal kilns alike — the game gives them all the same
        /// Smelter component.
        /// </summary>
        internal static bool TryCollectToChest(Smelter smelter, string ore, int stack)
        {
            if (smelter == null || stack <= 0) return false;

            var product = FindProduct(smelter, ore);
            if (!TryStoreNearby(smelter.transform.position, product, stack)) return false;

            // Spawn() plays this before dropping the item; keep the cue so the player
            // still gets the usual feedback that something came out.
            smelter.m_produceEffects.Create(smelter.transform.position, smelter.transform.rotation);
            return true;
        }

        /// <summary>
        /// Помеченный сундук зоны сортировки, готовый принять это добро: сначала сундук его
        /// категории, потом «Разное».
        ///
        /// The same order the sorter itself carries things in, and for the same reason:
        /// picking the nearest chest that has room would send bars to «Разное» while the
        /// «Материалы» chest stood open two steps further away, and the next sweep would
        /// carry them back - the two halves pulling against each other once a second.
        ///
        /// Внутри зоны сортировки расстояние не считается вовсе: раз станция и сундук в
        /// одной зоне, они работают вместе, на каком бы её краю ни стояли. «Зона станций»
        /// осталась про то, что снаружи зоны, - про сундук, назначенный станции руками.
        ///
        /// Two distances that add up are a rule nobody can hold in their head, and the one
        /// they hid did real harm: a kiln with the nearest chest full, or with the right
        /// chest a step too far, tipped its coal on the ground - the game's own way of
        /// finishing, and from the outside simply «уголь куда-то девается». The zone is
        /// drawn by hand around the base it belongs to, so it is the honest limit; making
        /// it smaller is a thing to do on the ground, not in a second number.
        /// </summary>
        /// <summary>
        /// Буферы поиска сундука. Заводились заново на **каждый** складываемый предмет, а
        /// складывается их за проход много: у каждого поиска свой, потому что
        /// `TryStoreNearby` зовёт обоих подряд.
        /// </summary>
        private static readonly List<Piece> ZoneChestScratch = new List<Piece>();

        private static readonly List<Piece> ChestScratch = new List<Piece>();

        private static Container FindZoneChest(Vector3 origin, GameObject prefab, int amount)
        {
            var zone = ZoneAround(origin);
            if (zone == null) return null;

            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null) return null;

            var want = CategoryOf(drop.m_itemData);

            // Вокруг середины зоны, а не вокруг станции: сундук на дальнем её краю - такой
            // же сундук этой зоны, и он теперь тоже считается.
            var pieces = ZoneChestScratch;
            pieces.Clear();
            Piece.GetAllPiecesInRadius(new Vector3(zone.X, origin.y, zone.Z),
                Sorting.ScanRadius(zone), pieces);

            Container best = null;
            var bestRank = int.MaxValue;
            var bestSqr = float.MaxValue;

            foreach (var piece in pieces)
            {
                if (piece == null) continue;

                var container = ContainerOf(piece);
                if (container == null) continue;

                var category = ChestCategory(container);
                if (category < 0) continue;
                if (category != want && category != Sorting.Misc) continue;
                if (IsPrivateChest(container) || IsSupplyChest(container)) continue;

                var spot = container.transform.position;
                if (!Sorting.Inside(zone, spot.x, spot.z)) continue;

                // Писать в сундук, который кто-то держит открытым, - верный рассинхрон.
                if (container.IsInUse()) continue;

                var view = ViewOf(container);
                if (view == null || !view.IsValid()) continue;
                if (!container.GetInventory().CanAddItem(prefab, amount)) continue;

                var rank = category == want ? 0 : 1;
                var sqr = (spot - origin).sqrMagnitude;
                if (best != null && (rank > bestRank || (rank == bestRank && sqr >= bestSqr))) continue;

                best = container;
                bestRank = rank;
                bestSqr = sqr;
            }

            return best;
        }

        /// <summary>
        /// Зона сортировки, внутри которой стоит эта точка, или null.
        ///
        /// One answer for the feeding half, the storing half and the question of whether a
        /// station may be emptied at all: three places that used to ask it each in their
        /// own words, and a rule three halves understand differently is a rule that will
        /// be wrong in one of them.
        /// </summary>
        internal static Sorting.Zone ZoneAround(Vector3 where, float slack = 0f)
        {
            if (!ZoneStationsOn || !SortingOn || !RuleAllows("sort")) return null;

            var zones = SortingZones();
            var at = Sorting.ZoneAt(zones, where.x, where.z, slack);
            if (at < 0) return null;

            // В общей зоне работает один клиент: двое вычерпали бы один сундук дважды.
            var zone = zones[at];
            return ZonesOnServer && !zone.Drive ? null : zone;
        }

        private static Container FindChest(Vector3 origin, float radius, GameObject product,
                                           int stack, bool assignedOnly, bool square)
        {
            var pieces = ChestScratch;
            pieces.Clear();
            // Квадрат достаёт до угла дальше, чем до стороны: смотрим шире, а отбираем
            // ниже, самой зоной.
            Piece.GetAllPiecesInRadius(origin, square ? radius * 1.415f : radius, pieces);

            Container best = null;
            var bestSqr = float.MaxValue;

            foreach (var piece in pieces)
            {
                if (piece == null) continue;

                var container = ContainerOf(piece);
                if (container == null) continue;
                if (assignedOnly != IsCollectChest(container)) continue;
                if (square && !InChestZone(origin, container.transform.position)) continue;

                // Writing into a chest somebody has open is a reliable way to desync it.
                if (container.IsInUse()) continue;

                var view = ViewOf(container);
                if (view == null || !view.IsValid()) continue;
                if (!container.GetInventory().CanAddItem(product, stack)) continue;

                // Sorting the same goods together beats raw proximity: with a chest
                // by the kiln and another by the kitchen, coal and food stop mixing.
                var sqr = (container.transform.position - origin).sqrMagnitude;
                if (!AlreadyHolds(container, product)) sqr += SortingBias;
                if (sqr >= bestSqr) continue;

                best = container;
                bestSqr = sqr;
            }

            return best;
        }

        // Far enough to outweigh any distance inside the search radius.
        private const float SortingBias = 1e6f;

        private static bool AlreadyHolds(Container container, GameObject prefab)
        {
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null) return false;
            return container.GetInventory().HaveItem(drop.m_itemData.m_shared.m_name);
        }

        private static readonly System.Reflection.MethodInfo MCookingFreeSlot =
            AccessTools.Method(typeof(CookingStation), "GetFreeSlot");

        private static readonly List<string> AcceptScratch = new List<string>();

        /// <summary>
        /// One sweep a second drives both halves: producers that only hand their goods
        /// over when asked, and stations waiting to be fed. Each vanilla "take" routine
        /// already refuses to act when nothing is ready, so they can be asked blindly.
        /// </summary>
        // Помеченные сундуки зоны, их места и общий список подачи для станции, которая в
        // этой зоне стоит. Заводятся один раз: проход идёт каждую секунду.
        private static readonly List<Container> ZoneBins = new List<Container>();

        /// <summary>Детали зоны: свой обход, потому что зона бывает шире, чем видно.</summary>
        private static readonly List<Piece> ZonePieces = new List<Piece>();

        private static readonly List<Container> ZoneSupply = new List<Container>();

        /// <summary>Сундуки сбора этого прохода: в них же считается запас угля.</summary>
        private static readonly List<Container> CollectChests = new List<Container>();

        /// <summary>Имя угля в данных игры — по нему он и считается в сундуках.</summary>
        private const string CoalPrefab = "Coal";

        private static string _kilnSaid;

        /// <summary>Что стоит в зоне и что упёрлось в предел — для окна «Информация».</summary>
        internal static string ZoneHasRu = "";

        internal static string ZoneLimitsRu = "";

        internal static string ZoneFiresRu = "";

        /// <summary>Блюда, которых на складе уже довольно: их перестали готовить.</summary>
        private static readonly HashSet<string> FoodFull = new HashSet<string>();

        private static readonly Dictionary<string, string> ZoneFiresSeen =
            new Dictionary<string, string>();

        private static IEnumerator AutomationLoop()
        {
            var pieces = new List<Piece>();
            var collectSpots = new List<Vector3>();
            var supplyChests = new List<Container>();

            // Одно ожидание на все проходы, а не новое каждую секунду.
            var second = new WaitForSeconds(1f);

            while (true)
            {
                yield return second;

                // Сбой внутри одного прохода не имеет права унести с собой всё до конца
                // сессии. Мод это уже проходил: контракт одной RPC разъехался с игрой,
                // исключение прилетело внутрь корутины, и автоматика молча умерла до
                // перезахода. Жалуемся один раз и живём дальше.
                try
                {

                    var player = Player.m_localPlayer;
                    if (player == null) continue;

                    // Счётчики зверей гасятся здесь, а не там, где их заполняют: заполняет
                    // их только `FeedTames`, а её зовут лишь при включённом кормлении и
                    // живой подаче. Выключил админ кормление - и окно «Что происходит»
                    // продолжало печатать числа прошлого прохода. Застывшая строка в окне,
                    // которое заводилось объяснять «почему ничего не делается», хуже
                    // отсутствующей: она отвечает неправдой.
                    TamesSeen = 0;
                    TamesHungry = 0;

                    pieces.Clear();
                    Piece.GetAllPiecesInRadius(player.transform.position, HarvestScanRadius, pieces);

                    // Сколько бочек уже бродит, считается этим же обходом - другого у нас
                    // нет. Решение варить смотрит на счёт прошлой секунды плюс то, что
                    // поставили сейчас: иначе двадцать пустых бочек примут двадцать порций
                    // заказа на шесть бутылок.
                    BrewSweepStart();

                    // Sort the chests out of the same sweep. Asking per station would mean
                    // re-walking every loaded piece once for each of them.
                    collectSpots.Clear();
                    supplyChests.Clear();
                    CollectChests.Clear();
                    foreach (var piece in pieces)
                    {
                        if (piece == null) continue;
                        var container = ContainerOf(piece);
                        if (container == null) continue;

                        if (IsCollectChest(container))
                        {
                            collectSpots.Add(container.transform.position);
                            CollectChests.Add(container);
                        }
                        if (IsSupplyChest(container)) supplyChests.Add(container);
                    }

                    // «Автонаполнение»: помеченные сундуки той зоны, в которой стоит игрок,
                    // годятся станциям в ней и как кладовая, и как приёмник. Собираются из
                    // того же обхода деталей - второй за ними не ходим.
                    // Кайма - только игроку: он может стоять у ворот, и зона от этого не
                    // перестаёт быть зоной. Станции и сундуки спрашиваются строго - у них
                    // черта и есть смысл, иначе печь во дворе соседа работала бы от нашего
                    // склада.
                    var zone = ZoneAround(player.transform.position, Sorting.NearReach);
                    ZoneBins.Clear();
                    ZoneSupply.Clear();

                    if (zone != null)
                    {
                        // Свой обход, вокруг середины зоны: круг вокруг игрока в 64 м
                        // короче самой зоны, а станция теперь работает с любым её сундуком.
                        // Лишний проход по деталям стоит одного сравнения расстояний на
                        // деталь - дешевле, чем объяснять, почему дальний сундук не берут.
                        ZonePieces.Clear();
                        Piece.GetAllPiecesInRadius(
                            new Vector3(zone.X, player.transform.position.y, zone.Z),
                            Sorting.ScanRadius(zone), ZonePieces);

                        foreach (var piece in ZonePieces)
                        {
                            if (piece == null) continue;

                            var container = ContainerOf(piece);
                            if (container == null) continue;
                            if (ChestCategory(container) < 0) continue;
                            if (IsPrivateChest(container) || IsSupplyChest(container)) continue;

                            var spot = container.transform.position;
                            if (!Sorting.Inside(zone, spot.x, spot.z)) continue;

                            ZoneBins.Add(container);
                        }
                    }

                    if (ZoneBins.Count > 0)
                    {
                        // Назначенные впереди: указать пальцем - точнее, чем «оно тут рядом».
                        ZoneSupply.AddRange(supplyChests);
                        ZoneSupply.AddRange(ZoneBins);
                    }

                    // Nothing assigned, or the half switched off: nothing to feed from, so
                    // skip the reads and reflection the feeding half would do.
                    var feeding = IsAutoFillEnabled && (supplyChests.Count > 0 || ZoneBins.Count > 0);

                    // And with nowhere to put anything either, the whole second pass is
                    // waste: five GetComponentInChildren per piece, each a recursive walk of
                    // a multi-part prefab, on every loaded piece within sixty-four metres,
                    // once a second. A base of fifteen hundred pieces is some seven thousand
                    // tree walks a second to reach a row of MayHarvest calls that can only
                    // answer false. The chest sweep above still has to run, since it is what
                    // decides this.
                    var harvesting = IsAutoCollectEnabled && (collectSpots.Count > 0 || ZoneBins.Count > 0);
                    if (!feeding && !harvesting) continue;

                    // Уголь считается раз за проход, а не у каждой печи: печей на базе
                    // десяток, а сундуки одни и те же. Считаем там, куда уголь и уезжает -
                    // в сундуках сбора и в помеченных сундуках зоны.
                    CountStock();
                    var zoneSmelters = 0;
                    var zoneMine = 0;
                    var zoneFires = 0;
                    ZoneFireKinds.Clear();
                    var kilnsMayBurn = FeedKilnsOn
                                       && (CoalKeep <= 0 || InStock(CoalPrefab) < CoalKeep);

                    FoodFull.Clear();
                    int zoneCook = 0, zoneBarrels = 0, zoneHives = 0, zoneKilns = 0;

                    foreach (var piece in pieces)
                    {
                        if (piece == null) continue;

                        // Станция в зоне ест из её сундуков и сдаёт в них; станция вне
                        // зоны живёт как жила, назначенными сундуками.
                        var place = piece.transform.position;
                        var inZone = zone != null && Sorting.Inside(zone, place.x, place.z);
                        var food = inZone ? ZoneSupply : supplyChests;

                        // Все пять частей детали - одним вопросом и один раз за её жизнь.
                        var parts = PartsOf(piece);
                        if (parts == null) continue;

                        var cooking = parts.Cooking;
                        if (cooking != null)
                        {
                            if (inZone) zoneCook++;
                            if (harvesting && MayHarvest(cooking, collectSpots, inZone))
                                cooking.GetComponent<ZNetView>()
                                    .InvokeRPC("RPC_RemoveDoneItem", player.transform.position, 1);
                            // «Наполнять кухни» гасит и топливо: кухня, которой не
                            // дают еды, в огне не нуждается.
                            if (feeding && FeedCookingOn) FillCooking(cooking, food);
                        }

                        var beehive = parts.Hive;
                        if (beehive != null && inZone) zoneHives++;
                        if (beehive != null && harvesting && MayHarvest(beehive, collectSpots, inZone))
                            beehive.GetComponent<ZNetView>().InvokeRPC("RPC_Extract");

                        var fermenter = parts.Barrel;
                        if (fermenter != null)
                        {
                            if (inZone) zoneBarrels++;
                            if (harvesting && MayHarvest(fermenter, collectSpots, inZone))
                                fermenter.GetComponent<ZNetView>().InvokeRPC("RPC_Tap");
                            if (feeding) FillFermenter(fermenter, food);
                            // Готовая основа из сундука идёт первой: варить новую, когда
                            // одна лежит рядом, значит тратить мёд на то, что уже есть.
                            if (feeding) TryBrewInto(fermenter, food);
                            else BrewFeedingOff();
                            BrewSweepSaw(fermenter);
                        }

                        var smelter = parts.Smelter;
                        if (smelter != null)
                        {
                            // Считаем плавильни зоны и те из них, что слушаются именно
                            // нас: чужая по сети станция молча пропускается, и «мод её не
                            // кормит» снаружи выглядит так же, как «в ней нет руды».
                            if (inZone)
                            {
                                zoneSmelters++;
                                if (MakesCoal(smelter)) zoneKilns++;
                                var own = StationView(smelter);
                                if (own != null && own.IsOwner()) zoneMine++;
                            }

                            // Печь — это плавильня, у которой из превращения выходит уголь.
                            // Забирать у неё дрова можно отдельно от всего остального.
                            if (feeding && (kilnsMayBurn || !MakesCoal(smelter))) FillSmelter(smelter, food);
                            // Gated like its three siblings. Tipping the kiln out with
                            // nowhere to put the coal turns one fifty-stack at the end of
                            // the burn into fifty singles on the floor, one a second.
                            if (harvesting && MayHarvest(smelter, collectSpots, inZone)) FlushSmelter(smelter);
                        }

                        var fireplace = parts.Fire;
                        if (fireplace != null)
                        {
                            // Факел - это тот же Fireplace, что и костёр: одна ветка на
                            // всё, что горит, и смола в него едет из сундуков зоны, как
                            // дрова в костёр. Считаем их отдельно - спросили именно про них.
                            if (inZone)
                            {
                                if (!fireplace.m_infiniteFuel && fireplace.m_fuelItem != null) zoneFires++;

                                var kind = Utils.GetPrefabName(piece.gameObject);
                                if (!ZoneFireKinds.ContainsKey(kind))
                                    ZoneFireKinds[kind] = fireplace.m_infiniteFuel ? "burns for ever"
                                        : fireplace.m_fuelItem != null ? fireplace.m_fuelItem.gameObject.name
                                        : "no fuel of its own";

                                // То же, но словами игрока: в логе имена префабов, а в
                                // окне человек ищет глазами «смолу», а не «Resin».
                                if (!ZoneFiresSeen.ContainsKey(kind))
                                    ZoneFiresSeen[kind] = fireplace.m_infiniteFuel
                                        ? PieceTitle(piece.gameObject) + ": горит вечно"
                                        : fireplace.m_fuelItem != null
                                            ? PieceTitle(piece.gameObject) + ": "
                                              + ItemTitle(fireplace.m_fuelItem)
                                            : PieceTitle(piece.gameObject) + ": без топлива";
                            }

                            if (feeding) FillFireplace(fireplace, food);
                        }
                    }

                    // Звери - такая же половина наполнения, как станции: еда им тоже
                    // берётся из помеченных сундуков, и по тому же правилу зоны.
                    var tames = feeding && FeedTamesOn ? FeedTames(player, zone, supplyChests) : 0;

                    SayZoneHas(zone, zoneSmelters, zoneMine, zoneKilns, zoneCook, zoneFires,
                               zoneBarrels, zoneHives);
                    SayZoneLimits();

                    // Огород - сам по себе: собирать урожай есть куда и тогда, когда
                    // станций в зоне нет вовсе, а грядки растут часами, так что у него
                    // свой, редкий такт.
                    TickGarden(player, zone, ZoneBins.Count > 0 ? ZoneSupply : supplyChests);

                    BrewSweepDone();

                    if (zone != null) SayZoneStations(zoneSmelters, zoneMine, zoneFires, tames);
                }
                catch (System.Exception bad)
                {
                    SayLoopTrouble("Automation", bad);
                }
            }
        }

        /// <summary>
        /// Only the owner runs a producer's logic. The chest check matters too: without
        /// somewhere to put the goods, harvesting unattended would just tip them onto
        /// the ground, which is worse than leaving them where they are.
        ///
        /// It stays a proximity test. A chest that is in range but full still ends in a
        /// ground drop, because at this point nobody knows which item is about to
        /// appear; closing that would mean threading the product through every branch.
        /// </summary>
        /// <summary>
        /// То же, но станции в зоне сортировки засчитываются и её помеченные сундуки.
        /// </summary>
        /// <summary>
        /// Делает ли эта станция уголь.
        ///
        /// Asked of the conversions rather than of the prefab's name: «угольная печь» is
        /// whatever turns something into coal, and a name checked against a list is a name
        /// that stops being right the day the game adds another one.
        /// </summary>
        /// <summary>
        /// Что стоит в зоне — человеческими словами, для окна «Информация».
        ///
        /// Счёт идёт в том же обходе, которым делается работа: второй проход ради показа
        /// не окупится, а эти числа всё равно уже посчитаны по дороге. Пустые виды не
        /// перечисляются - «ульев 0» в списке из семи строк читать некому.
        /// </summary>
        private static void SayZoneHas(Sorting.Zone zone, int smelters, int mine, int kilns,
                                       int cooking, int fires, int barrels, int hives)
        {
            if (zone == null)
            {
                ZoneHasRu = "";
                return;
            }

            var said = new System.Text.StringBuilder();
            Add(said, smelters - kilns, "плавильня", "плавильни", "плавилен");
            Add(said, kilns, "угольная печь", "угольные печи", "угольных печей");
            Add(said, cooking, "кухня", "кухни", "кухонь");
            Add(said, fires, "огонь", "огня", "огней");
            Add(said, barrels, "бочка", "бочки", "бочек");
            Add(said, hives, "улей", "улья", "ульев");

            ZoneHasRu = said.Length > 0
                ? "В зоне: " + said + "."
                : "В зоне нет ни одной станции.";

            // Плавильня в общей зоне, доставшаяся другому клиенту, стоит молча: считает её
            // он, и кормит тоже он. Это ровно та порода вопросов, ради которых окно и
            // заводилось, и единственный факт, которого в остальных строках нет. Когда
            // наши все - не пишем ничего: в списке из десяти строк норме места нет.
            if (smelters > mine)
                ZoneHasRu += NEWLINE + $"Из них наших {mine} — остальные считает "
                             + "другой клиент, он их и кормит.";

            var fireLines = new System.Text.StringBuilder();
            foreach (var pair in ZoneFiresSeen)
            {
                if (fireLines.Length > 0) fireLines.Append(NEWLINE);
                fireLines.Append(pair.Value);
            }

            ZoneFiresRu = fireLines.ToString();
        }

        /// <summary>Число со словом в нужном падеже: «одна бочка», «две бочки», «пять бочек».</summary>
        private static void Add(System.Text.StringBuilder said, int count,
                                string one, string few, string many)
        {
            if (count <= 0) return;

            if (said.Length > 0) said.Append(", ");

            var tail = count % 100 / 10 == 1 ? many
                : count % 10 == 1 ? one
                : count % 10 >= 2 && count % 10 <= 4 ? few
                : many;

            said.Append(count).Append(' ').Append(tail);
        }

        /// <summary>
        /// Какие пределы держат прямо сейчас.
        ///
        /// Предел, о котором не сказано, неотличим от поломки: за один вечер «ничего не
        /// происходит» трижды означало «на складе уже довольно» - уголь, готовая еда и
        /// урожай. Поэтому здесь не сами числа из настроек, а то, **сработали ли они**.
        /// </summary>
        private static void SayZoneLimits()
        {
            var said = new System.Text.StringBuilder();

            if (CoalKeep > 0)
            {
                var coal = InStock(CoalPrefab);
                if (coal >= CoalKeep)
                    said.Append($"Угля {coal} при пределе {CoalKeep} — печи стоят.");
            }

            if (FoodFull.Count > 0)
            {
                if (said.Length > 0) said.Append(NEWLINE);
                said.Append($"Блюд набрало предел {FoodKeep}: {FoodFull.Count} — их не готовим.");
            }

            if (CropsFull > 0)
            {
                if (said.Length > 0) said.Append(NEWLINE);
                said.Append($"Культур набрало предел {CropKeep}: {CropsFull} — их не засеваем.");
            }

            if (BedsFull)
            {
                if (said.Length > 0) said.Append(NEWLINE);
                said.Append($"Грядок в зоне {BedsInZone} при пределе {BedCap} — новых не сажаем.");
            }

            ZoneLimitsRu = said.Length > 0 ? said.ToString() : "Пределы никого не держат.";
        }

        private static bool MakesCoal(Smelter smelter)
        {
            if (smelter == null || smelter.m_conversion == null) return false;

            foreach (var conversion in smelter.m_conversion)
            {
                if (conversion == null || conversion.m_to == null) continue;
                if (PrefabName(conversion.m_to.gameObject) != CoalPrefab) continue;

                // Названа один раз за сессию: если переключатель «Наполнять печи» вдруг
                // ничего не делает, первый вопрос - узнал ли мод печь вообще.
                if (_kilnSaid != smelter.name)
                {
                    _kilnSaid = smelter.name;
                    Log.LogInfo($"[AstvardServerMod] Coal kiln: {smelter.name}.");
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Что и сколько лежит в сундуках сбора и в помеченных сундуках зоны. Штуками, а
        /// не ячейками: «сто угля» — это сто углей, сколько бы стопок они ни занимали.
        ///
        /// Counted once a sweep into one table rather than asked per station: a base has a
        /// dozen kilns and cooking stations and one set of chests, and each question used
        /// to walk all of them. With a limit per dish there would be ten such walks per
        /// station a second.
        /// </summary>
        private static readonly Dictionary<string, int> Stock = new Dictionary<string, int>();

        /// <summary>
        /// Сундуки, уже посчитанные в этом проходе.
        ///
        /// Списка два, и они **пересекаются**: сундук сбора внутри зоны с пометкой
        /// сортировщика попадает и в `CollectChests`, и в `ZoneBins` - обе роли на одном
        /// сундуке разрешены нарочно. Сложение двух списков читало его содержимое дважды,
        /// и на этом стояли все три предела разом: сотня угля в таком сундуке считалась
        /// двумя, печи вставали вдвое раньше срока, а окно «Что происходит» уверенно
        /// называло неверное число. Заводится один раз - проход идёт каждую секунду.
        /// </summary>
        private static readonly HashSet<Container> Counted = new HashSet<Container>();

        private static void CountStock()
        {
            Stock.Clear();
            Counted.Clear();
            CountStock(CollectChests);
            CountStock(ZoneBins);
        }

        /// <summary>
        /// Имя префаба предмета - по одному вопросу на префаб за сессию.
        ///
        /// `UnityEngine.Object.name` уходит в нативный код и возвращает **новую строку**
        /// на каждый вызов. Спрашивается оно по стопке в каждом сундуке каждую секунду,
        /// плюс у каждой станции при подаче: на стене в сорок сундуков это сотни строк в
        /// секунду ради имени, которое у префаба одно и навсегда.
        ///
        /// Чистить словарь не нужно и нечем: ключ - сам префаб из `ObjectDB`, он живёт
        /// столько же, сколько игра, и число их - число предметов игры.
        /// </summary>
        private static readonly Dictionary<GameObject, string> PrefabNames =
            new Dictionary<GameObject, string>();

        internal static string PrefabName(GameObject prefab)
        {
            if (prefab == null) return "";

            string name;
            if (PrefabNames.TryGetValue(prefab, out name)) return name;

            name = prefab.name;
            PrefabNames[prefab] = name;
            return name;
        }

        private static void CountStock(List<Container> chests)
        {
            foreach (var container in chests)
            {
                if (container == null || !Counted.Add(container)) continue;

                var inventory = container.GetInventory();
                if (inventory == null) continue;

                foreach (var item in inventory.GetAllItems())
                {
                    if (item == null || item.m_dropPrefab == null) continue;

                    int had;
                    var name = PrefabName(item.m_dropPrefab);
                    Stock[name] = (Stock.TryGetValue(name, out had) ? had : 0) + item.m_stack;
                }
            }
        }

        private static int InStock(string prefab)
        {
            int had;
            return Stock.TryGetValue(prefab, out had) ? had : 0;
        }

        private static bool MayHarvest(Component producer, List<Vector3> assignedChests, bool inZone)
        {
            if (MayHarvest(producer, assignedChests)) return true;

            // Помеченный сундук зоны годится с любого её края, поэтому спрашивается не
            // расстояние, а сама зона: станция внутри - значит есть куда сдавать.
            if (!inZone || ZoneBins.Count == 0 || !Alive(producer)) return false;

            var origin = producer.transform.position;
            var zone = ZoneAround(origin);
            return zone != null && Sorting.Inside(zone, origin.x, origin.z);
        }

        private static bool MayHarvest(Component producer, List<Vector3> assignedChests)
        {
            // Вопрос, а не работа: владение заберёт тот, кто станет опустошать.
            if (!Alive(producer)) return false;

            var origin = producer.transform.position;

            foreach (var chest in assignedChests)
                if (InChestZone(origin, chest)) return true;

            return false;
        }

        /// <summary>Живая запись станции в сети. Вверх по объекту - как у повозки.</summary>
        private static ZNetView StationView(Component producer)
        {
            var view = producer != null ? producer.GetComponentInParent<ZNetView>() : null;
            return view != null && view.IsValid() ? view : null;
        }

        /// <summary>Спросить о станции можно, не забирая её себе.</summary>
        private static bool Alive(Component producer)
        {
            return StationView(producer) != null;
        }

        /// <summary>
        /// Станция, с которой мы сейчас будем работать: берём её себе, а не ждём.
        ///
        /// Владелец записи - тот, кто станцию считает: у него идёт её секунда, он решает,
        /// что из неё выпадет, и у него же выполняется наш собственный перехват выдачи. А
        /// сундуки, зоны и пометки - наши, клиентские. Печь, оставшаяся за сервером, поэтому
        /// выплёвывает уголь на землю: перехват там отработал, места не нашёл (откуда серверу
        /// знать наши зоны) и честно отдал ход игре. И в обратную сторону: пока мы не хозяева,
        /// мы её молча пропускали, а снаружи это выглядит как «мод её не видит».
        ///
        /// Игра сама раздаёт ничейные записи тому, кто рядом (ZDOMan.ReleaseNearbyZDOS), так
        /// что обычно это ничего не меняет; спор двух игроков у одной печи она решает так же,
        /// как решала бы его без нас. Берём только там, где сейчас же и работаем, - спрашивать
        /// («готово ли») можно и чужую.
        /// </summary>
        private static bool OwnedAndValid(Component producer)
        {
            var view = StationView(producer);
            if (view == null) return false;

            if (!view.IsOwner()) view.ClaimOwnership();
            return view.IsOwner();
        }

        // ---------------- feeding ----------------

        /// <summary>
        /// Removes one unit of anything the station accepts from a chest, and reports
        /// which prefab it was. Only from a chest put on supply duty: a station that
        /// helped itself to the nearest one emptied whatever stood beside it.
        /// </summary>
        private static string TakeSupply(Vector3 origin, List<Container> supplyChests,
                                         List<string> accepted)
        {
            if (accepted.Count == 0) return null;

            return TakeFrom(origin, supplyChests, AssignedChestRadius, accepted, ChestZoneSquare);
        }

        private static string TakeFrom(Vector3 origin, List<Container> chests,
                                       float radius, List<string> accepted, bool square)
        {
            // Станция в зоне сортировки берёт из её сундуков на любом расстоянии - то же
            // правило, по которому она в них и сдаёт. Снаружи зоны всё как было: назначенный
            // сундук ищется в «Зоне станций».
            var zone = ZoneAround(origin);
            var range = radius * radius;
            Container bestChest = null;
            ItemDrop.ItemData bestItem = null;
            var bestSqr = float.MaxValue;

            foreach (var container in chests)
            {
                if (container == null || container.IsInUse()) continue;

                var spot = container.transform.position;
                var sqr = (spot - origin).sqrMagnitude;
                if (sqr >= bestSqr) continue;

                var sameZone = zone != null && Sorting.Inside(zone, spot.x, spot.z);
                if (!sameZone &&
                    (square ? !InChestZone(origin, spot) : sqr > range)) continue;

                // Одно место на все дороги: сюда сходится всякое изъятие из сундука в
                // станцию, и назначенная подача тоже. Флажок сильнее назначения - его
                // ставят позже и ради этого самого. Спрашивается он после дешёвых
                // отсечек: это подъём вверх по объекту и чтение ZDO по строковому ключу,
                // а ответ от порядка не зависит - такой сундук пропускается в любом
                // случае, и `bestSqr` до этой строки не двигают.
                if (IsHoldChest(container)) continue;

                var view = ViewOf(container);
                if (view == null || !view.IsValid()) continue;

                foreach (var item in container.GetInventory().GetAllItems())
                {
                    if (item == null || item.m_dropPrefab == null) continue;
                    if (!accepted.Contains(PrefabName(item.m_dropPrefab))) continue;

                    bestChest = container;
                    bestItem = item;
                    bestSqr = sqr;
                    break;
                }
            }

            if (bestChest == null) return null;

            // Same rule as storing: only the owner's write reaches the ZDO.
            var bestView = ViewOf(bestChest);
            if (bestView == null || !bestView.IsValid()) return null;
            if (!bestView.IsOwner()) bestView.ClaimOwnership();

            var name = PrefabName(bestItem.m_dropPrefab);
            return bestChest.GetInventory().RemoveItem(bestItem, 1) ? name : null;
        }

        private static void FillSmelter(Smelter smelter, List<Container> supply)
        {
            if (!OwnedAndValid(smelter)) return;

            var view = smelter.GetComponent<ZNetView>();
            var zdo = view.GetZDO();
            var origin = smelter.transform.position;

            // Руда вперёд топлива, и это порядок, а не вкус: топливо кладётся только в
            // работающую печь, а работает она ровно тогда, когда в ней есть что плавить.
            var queued = zdo.GetInt(ZDOVars.s_queued);

            if (queued < smelter.m_maxOre)
            {
                AcceptScratch.Clear();
                foreach (var conversion in smelter.m_conversion)
                    if (conversion != null && conversion.m_from != null)
                        AcceptScratch.Add(PrefabName(conversion.m_from.gameObject));

                var ore = TakeSupply(origin, supply, AcceptScratch);
                // 1.0 added a trailing cheated flag to RPC_AddOre. Sending the old
                // single argument made the receiver read a bool past the end of the
                // package: an EndOfStreamException thrown straight into this coroutine,
                // which killed every automation sweep for the rest of the session.
                // The ore came out of a chest, so it is not cheated.
                if (ore != null)
                {
                    view.InvokeRPC("RPC_AddOre", ore, false);
                    queued++;
                }
            }

            // Печь, которой нечего плавить, топлива не жжёт вовсе - UpdateSmelter тратит
            // его только пока в очереди есть руда, - так что уголь, положенный в пустую,
            // просто лежит в ней. Десяток плавилен так забирает со склада две сотни углей,
            // и со стороны это ровно «уголь куда-то девается»: со склада ушёл, в сундуках
            // его нет, и никто ничего не сжёг. Теперь топливо едет туда, где им займутся.
            if (queued > 0 && smelter.m_maxFuel > 0 && smelter.m_fuelItem != null &&
                zdo.GetFloat(ZDOVars.s_fuel) <= smelter.m_maxFuel - 1)
            {
                AcceptScratch.Clear();
                AcceptScratch.Add(PrefabName(smelter.m_fuelItem.gameObject));
                if (TakeSupply(origin, supply, AcceptScratch) != null)
                    view.InvokeRPC("RPC_AddFuel");
            }
        }

        private static void FillCooking(CookingStation cooking, List<Container> supply)
        {
            if (!OwnedAndValid(cooking)) return;

            var view = cooking.GetComponent<ZNetView>();
            var zdo = view.GetZDO();
            var origin = cooking.transform.position;

            // Еда вперёд топлива, по той же причине, что руда вперёд угля у плавильни, но
            // причина здесь другая и хуже: железная кухня жжёт уголь и пустой
            // (m_useFueldWhileEmpty у неё true), так что топливо в кухню, на которой ничего
            // не готовится, - это уголь в огонь под пустым вертелом.
            var added = false;

            if (MCookingFreeSlot != null && (int)MCookingFreeSlot.Invoke(cooking, null) != -1)
            {
                AcceptScratch.Clear();
                foreach (var conversion in cooking.m_conversion)
                {
                    if (conversion == null || conversion.m_from == null) continue;

                    // Склад считается по каждому блюду отдельно - см. FoodKeep.
                    if (FoodKeep > 0 && conversion.m_to != null
                        && InStock(PrefabName(conversion.m_to.gameObject)) >= FoodKeep)
                    {
                        FoodFull.Add(PrefabName(conversion.m_to.gameObject));
                        continue;
                    }

                    AcceptScratch.Add(PrefabName(conversion.m_from.gameObject));
                }

                if (AcceptScratch.Count > 0)
                {
                    var raw = TakeSupply(origin, supply, AcceptScratch);
                    // Same trailing cheated flag as the smelter, same crash without it.
                    if (raw != null)
                    {
                        view.InvokeRPC("RPC_AddItem", raw, false);
                        added = true;
                    }
                }
            }

            if (!cooking.m_useFuel || cooking.m_fuelItem == null) return;
            if (!added && !StillCooking(cooking, zdo)) return;
            if (zdo.GetFloat(ZDOVars.s_fuel) > cooking.m_maxFuel - 1) return;

            AcceptScratch.Clear();
            AcceptScratch.Add(PrefabName(cooking.m_fuelItem.gameObject));
            if (TakeSupply(origin, supply, AcceptScratch) != null) view.InvokeRPC("RPC_AddFuel");
        }

        /// <summary>
        /// Готовится ли на кухне что-нибудь прямо сейчас.
        ///
        /// Read straight out of the ZDO, where the game keeps it: «slot0», «slot1» and so
        /// on hold the name of whatever lies on each spit. No reflection for this - a
        /// private helper of the game is one more thing to re-check after every update,
        /// and these keys are plain strings we already read for fuel and ore. «Done» is
        /// decided the way IsItemDone decides it: a slot holding the result of a
        /// conversion, or the burnt item, is finished rather than cooking.
        /// </summary>
        private static bool StillCooking(CookingStation cooking, ZDO zdo)
        {
            if (cooking.m_slots == null || zdo == null) return false;

            for (var i = 0; i < cooking.m_slots.Length; i++)
            {
                var name = zdo.GetString("slot" + i);
                if (name.Length == 0) continue;
                if (!IsCooked(cooking, name)) return true;
            }

            return false;
        }

        private static bool IsCooked(CookingStation cooking, string name)
        {
            if (cooking.m_overCookedItem != null && cooking.m_overCookedItem.name == name) return true;

            foreach (var conversion in cooking.m_conversion)
                if (conversion != null && conversion.m_to != null
                    && PrefabName(conversion.m_to.gameObject) == name)
                    return true;

            return false;
        }

        private static void FillFermenter(Fermenter fermenter, List<Container> supply)
        {
            if (!OwnedAndValid(fermenter)) return;

            var view = fermenter.GetComponent<ZNetView>();
            // Status.Empty is exactly "no content stored", so the ZDO answers this
            // without reaching for the private enum. 1.0 stores that content as the
            // stable hash of the prefab name rather than the name, and ZDO keeps ints
            // and strings in separate stores - so the old GetString returned "" for
            // every fermenter, full or empty, and this guard never once fired.
            if (view.GetZDO().GetInt(ZDOVars.s_content) != 0) return;

            AcceptScratch.Clear();
            foreach (var conversion in fermenter.m_conversion)
            {
                if (conversion == null || conversion.m_from == null) continue;

                // Тот же склад, что у кухонь, и по тому же счёту - по сорту. Основу
                // для браги делают руками, так что «не бери вовсе» ей не нужно:
                // положили основу в помеченный сундук - значит хотели брагу. Нужен
                // только предел, иначе сотня медовухи станет двумя сотнями.
                if (FoodKeep > 0 && conversion.m_to != null
                    && InStock(PrefabName(conversion.m_to.gameObject)) >= FoodKeep)
                {
                    FoodFull.Add(PrefabName(conversion.m_to.gameObject));
                    continue;
                }

                AcceptScratch.Add(PrefabName(conversion.m_from.gameObject));
            }

            if (AcceptScratch.Count == 0) return;

            var brew = TakeSupply(fermenter.transform.position, supply, AcceptScratch);
            // Register<int, bool> in 1.0, not <string>. Sending the name made the
            // receiver read four bytes of UTF-8 as a hash, reject the unknown item,
            // and leave the brew destroyed - TakeSupply had already removed it from
            // the chest. Hash it the way the game does, and say it is not cheated.
            if (brew != null) view.InvokeRPC("RPC_AddItem", brew.GetStableHashCode(), false);
        }

        private static void FillFireplace(Fireplace fireplace, List<Container> supply)
        {
            if (!OwnedAndValid(fireplace) || fireplace.m_fuelItem == null) return;
            if (fireplace.m_infiniteFuel) return;

            var view = fireplace.GetComponent<ZNetView>();
            if (view.GetZDO().GetFloat(ZDOVars.s_fuel) > fireplace.m_maxFuel - 1f) return;

            AcceptScratch.Clear();
            AcceptScratch.Add(fireplace.m_fuelItem.gameObject.name);
            if (TakeSupply(fireplace.transform.position, supply, AcceptScratch) != null)
                view.InvokeRPC("RPC_AddFuel");
        }

        private static GameObject FindProduct(Smelter smelter, string ore)
        {
            foreach (var conversion in smelter.m_conversion)
            {
                if (conversion == null || conversion.m_from == null) continue;
                if (conversion.m_from.gameObject.name != ore) continue;
                return conversion.m_to != null ? conversion.m_to.gameObject : null;
            }
            return null;
        }

        /// <summary>Reads the preview's own snap points, in root-local space.</summary>
        private static void CollectGhostSnapPoints()
        {
            GhostSnapLocal.Clear();
            SnapGrid.Clear();
            _snapCacheTime = float.NegativeInfinity;
            _ghostRadius = 1f;
            if (GhostRoot == null) return;

            var root = GhostRoot.transform;
            var points = new List<Transform>();

            foreach (var ghost in Ghosts)
            {
                if (ghost == null) continue;

                // Piece.GetSnapPoints only walks tagged child transforms, so it
                // still works on a preview whose components are all switched off.
                var piece = ghost.GetComponent<Piece>();
                if (piece != null)
                {
                    points.Clear();
                    piece.GetSnapPoints(points);
                    foreach (var point in points)
                        if (point != null) GhostSnapLocal.Add(root.InverseTransformPoint(point.position));
                }

                _ghostRadius = Mathf.Max(_ghostRadius, ghost.transform.localPosition.magnitude);
            }

            DedupePoints(GhostSnapLocal);
            Log.LogInfo($"[AstvardServerMod] Preview snap points: {GhostSnapLocal.Count}, radius {_ghostRadius:F1}");
        }
    }
}
