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

        internal static void BindGarden(BepInEx.Configuration.ConfigFile config)
        {
            _reapGarden = config.Bind("Сортировка", "Reap", true,
                "Собирать ли созревшее с грядок внутри зоны в помеченные сундуки. "
                + "«Функции» → «Сортировка» → «Настройки» → «Собирать урожай».");

            _sowGarden = config.Bind("Сортировка", "Sow", true,
                "Подсаживать ли на освободившееся место. Саженец берётся тот, что вырастает "
                + "в эту культуру, и платится семенами из помеченных сундуков — как у игрока. "
                + "«Настройки» → «Подсаживать».");
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

        private static int _noSeeds;

        private static int _noSapling;

        private static int _growing;

        private static int _outside;

        private static string _gardenSaid;

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

            // Сначала подсадка на места прошлого прохода, потом сбор: иначе собранное в
            // этом проходе засеялось бы тут же, по живому.
            var beds = Beds.Count;
            var sown = SowBeds(supply);

            Beds.Clear();
            Beds.AddRange(BedsNext);
            BedsNext.Clear();

            var reaped = ReapBeds(player, zone);

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
                  + (_outside > 0 ? $",{NEWLINE}рядом вне зоны {_outside}" : "")
                  + (_noRoom > 0 ? $",{NEWLINE}некуда сложить {_noRoom}" : "")
                  + (_noSeeds > 0 ? $",{NEWLINE}нет семян на {_noSeeds}" : "")
                  + ".";

            var said = zone == null
                ? "ты вне зоны — грядки не трогаем"
                : $"ripe {_ripe}, reaped {reaped}, sown {sown} of {beds} beds"
                  + (_growing > 0 ? $", {_growing} still growing" : "")
                  + (_outside > 0 ? $", {_outside} ripe outside the zone" : "")
                  + (_noRoom > 0 ? $", {_noRoom} with nowhere to put the crop" : "")
                  + (_notOurs > 0 ? $", {_notOurs} counted by somebody else" : "")
                  + (_extras > 0 ? $", {_extras} left alone (extra drops)" : "")
                  + (_noSeeds > 0 ? $", {_noSeeds} unsown for want of seeds" : "")
                  + (_noSapling > 0 ? $", {_noSapling} with no sapling to put back" : "");

            if (said == _gardenSaid) return;

            _gardenSaid = said;
            Log.LogInfo($"[AstvardServerMod] Garden: {said}.");
        }

        /// <summary>Собирает созревшее внутри зоны. Возвращает, сколько грядок сорвано.</summary>
        private static int ReapBeds(Player player, Sorting.Zone zone)
        {
            if (!ReapOn || zone == null) return 0;

            _ripe = 0;
            _noRoom = 0;
            _notOurs = 0;
            _extras = 0;
            _outside = 0;

            // Грядки, которым ещё расти, считаются отдельно и не ради красоты: «созрело 0»
            // само по себе не отличает пустой огород от посаженного час назад, а это
            // первое, что спрашивает человек, у которого «ничего не происходит».
            _growing = 0;
            foreach (var plant in Object.FindObjectsByType<Plant>(FindObjectsSortMode.None))
            {
                if (plant == null) continue;

                var bed = plant.transform.position;
                if (Sorting.Inside(zone, bed.x, bed.z)) _growing++;
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
                    if ((spot - where).sqrMagnitude <= Sorting.NearReach * Sorting.NearReach) _outside++;
                    continue;
                }

                // С добавкой в придачу пусть разбирается игрок: дополнительный дроп мы
                // сложить не умеем, а бросить его на землю - значит устроить свалку.
                if (pickable.m_extraDrops != null && !pickable.m_extraDrops.IsEmpty())
                {
                    _extras++;
                    continue;
                }

                _ripe++;

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

                BedsNext.Add(new Bed { At = spot, Crop = pickable.m_itemPrefab.name });
                reaped++;
            }

            return reaped;
        }

        /// <summary>Возвращает саженцы на места, освобождённые прошлым проходом.</summary>
        private static int SowBeds(List<Container> supply)
        {
            if (!SowOn || Beds.Count == 0) return 0;

            var player = Player.m_localPlayer;
            if (player == null) return 0;

            _noSeeds = 0;
            _noSapling = 0;

            var creator = player.GetPlayerID();
            var platform = PlatformManager.DistributionPlatform.LocalUser.PlatformUserID;
            var sown = 0;

            foreach (var bed in Beds)
            {
                var sapling = SaplingFor(bed.Crop);
                if (sapling == null)
                {
                    _noSapling++;
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
            }

            Beds.Clear();
            return sown;
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
            if (!_saplingsRead)
            {
                var scene = ZNetScene.instance;
                if (scene == null || scene.m_prefabs == null) return null;

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

                Log.LogInfo($"[AstvardServerMod] Garden: {SaplingByCrop.Count} crops can be sown back.");
            }

            GameObject sown;
            return SaplingByCrop.TryGetValue(crop, out sown) ? sown : null;
        }
    }
}
