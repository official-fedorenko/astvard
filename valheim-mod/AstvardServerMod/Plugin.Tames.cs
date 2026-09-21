using System.Collections.Generic;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>
        /// Кормление зверей из помеченных сундуков: еда кладётся им под ноги.
        ///
        /// The game feeds an animal one way and one way only: the food lies on the ground
        /// and the creature finds it itself - MonsterAI looks around every few seconds,
        /// walks over and eats it. There is no «hand it to the mouth» call to borrow, and
        /// writing one would step around everything hung off that moment: taming, breeding,
        /// contentment, the effects. So the mod does exactly what the player does - takes
        /// out of a chest the thing this particular animal eats, and drops it at its feet.
        ///
        /// What it eats is not a list in the mod either. Every creature carries its own
        /// (`MonsterAI.m_consumeItems`), which is why this works for a boar, a wolf, a lox
        /// and whatever the next update tames, without a line changed here.
        /// </summary>
        private static BepInEx.Configuration.ConfigEntry<bool> _feedTames;

        internal static void BindTames(BepInEx.Configuration.ConfigFile config)
        {
            _feedTames = config.Bind("Сортировка", "FeedTames", true,
                "Класть ли еду зверям из помеченных сундуков зоны — прирученным и тем, кого "
                + "приручаешь. Кладётся под ноги, как её кладёт игрок: только голодному и "
                + "только если рядом ещё ничего не лежит. "
                + "«Функции» → «Сортировка» → «Настройки» → «Кормить зверей».");
        }

        internal static bool FeedTamesOn
        {
            get { return _feedTames == null || _feedTames.Value; }
        }

        internal static void SetFeedTames(bool on)
        {
            if (_feedTames != null) _feedTames.Value = on;
        }

        // Буфер под поиск лежащей еды: проход идёт каждую секунду, и новый массив на каждого
        // зверя - это мусор на ровном месте. Шестнадцати хватает: спрашиваем мы про пять
        // метров вокруг зверя, и если там уже шестнадцать предметов, ответ всё равно «еда
        // рядом есть».
        private static readonly Collider[] FoodNearby = new Collider[16];

        private static int _itemLayer;

        private static int ItemLayer
        {
            get
            {
                if (_itemLayer == 0) _itemLayer = LayerMask.GetMask("item");
                return _itemLayer;
            }
        }

        /// <summary>
        /// Кладёт еду голодным зверям вокруг игрока. Возвращает, сколько их нашлось.
        /// </summary>
        private static int FeedTames(Player player, Sorting.Zone zone, List<Container> supplyChests)
        {
            SayTameFacts();

            var where = player.transform.position;
            var hungry = 0;

            TamesSeen = 0;

            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;

                var spot = character.transform.position;
                if ((spot - where).sqrMagnitude > HarvestScanRadius * HarvestScanRadius) continue;

                var tame = character.GetComponent<Tameable>();
                if (tame == null) continue;

                // Всех, а не только голодных: «голодных 0» при пустом загоне и при десятке
                // сытых читается одинаково, а значит по-разному.
                TamesSeen++;

                if (!tame.IsHungry()) continue;

                var ai = character.GetComponent<MonsterAI>();
                if (ai == null || ai.m_consumeItems == null || ai.m_consumeItems.Count == 0) continue;

                hungry++;

                // Зверь подбирает то, что уже лежит, и ищет он сам: вторая порция рядом с
                // несъеденной первой - это куча у загона, которую никто не ест.
                if (FoodLiesNear(ai, spot)) continue;

                var inZone = zone != null && Sorting.Inside(zone, spot.x, spot.z);
                var food = inZone ? ZoneSupply : supplyChests;

                AcceptScratch.Clear();
                foreach (var item in ai.m_consumeItems)
                    if (item != null) AcceptScratch.Add(PrefabName(item.gameObject));

                if (AcceptScratch.Count == 0) continue;

                var taken = TakeSupply(spot, food, AcceptScratch);
                if (taken == null) continue;

                // Еда уже вынута из сундука, так что «не нашёл, что класть» здесь означало бы
                // уничтоженный кусок мяса. Поэтому две независимые дороги к префабу: список
                // самого зверя, из которого мы и спрашивали, и общий справочник игры.
                var prefab = FoodPrefab(ai, taken);
                if (prefab == null && ObjectDB.instance != null)
                    prefab = ObjectDB.instance.GetItemPrefab(taken);

                if (prefab == null)
                {
                    Log.LogWarning($"[AstvardServerMod] Tames: took {taken} out of a chest and "
                                   + "could not find the prefab to drop. Nothing was given.");
                    continue;
                }

                DropFood(prefab, spot);
            }

            TamesHungry = hungry;
            return hungry;
        }

        /// <summary>
        /// Лежит ли рядом со зверем то, что он ест. Тем же способом, что спрашивает игра:
        /// слой предметов, тот же радиус поиска и сравнение по имени вещи, а не по префабу.
        /// </summary>
        private static bool _tameFactsSaid;

        /// <summary>Сколько приручённых зверей нашлось в зоне за прошлый проход.</summary>
        internal static int TamesSeen;

        internal static int TamesHungry;

        /// <summary>
        /// Что игра сама знает о приручении и приплоде — один раз в лог.
        ///
        /// Числа лежат в данных префабов, из кода их не прочитать: `m_tamingTime` у класса
        /// стоит 1800 секунд, а у кабана в префабе своё. Отсюда и эта строка - спросить
        /// живую игру оказалось единственным честным способом ответить на «сколько
        /// приручается кабан и на каком расстоянии они плодятся».
        ///
        /// Время идёт, **только пока зверь сыт** (`Tameable.DecreaseRemainingTime` зовётся
        /// с кормёжки), а игрок с эффектом ускорения в `m_tamingSpeedMultiplierRange`
        /// метрах множит скорость на `m_tamingBoostMultiplier`.
        /// </summary>
        private static void SayTameFacts()
        {
            if (_tameFactsSaid) return;

            var scene = ZNetScene.instance;
            if (scene == null || scene.m_prefabs == null) return;

            _tameFactsSaid = true;

            foreach (var prefab in scene.m_prefabs)
            {
                if (prefab == null) continue;

                var tame = prefab.GetComponent<Tameable>();
                if (tame == null) continue;

                var beast = prefab.GetComponent<Character>();
                var name = beast != null ? beast.m_name : prefab.name;
                if (!string.IsNullOrEmpty(name) && Localization.instance != null)
                {
                    var said = Localization.instance.Localize(name);
                    if (!string.IsNullOrEmpty(said) && !said.StartsWith("$")) name = said;
                }

                var ai = prefab.GetComponent<MonsterAI>();
                var love = prefab.GetComponent<Procreation>();

                var line = $"{name} ({prefab.name}): taming {tame.m_tamingTime / 60f:0.#} min of "
                           + $"being fed, a bite lasts {tame.m_fedDuration / 60f:0.#} min, "
                           + $"x{tame.m_tamingBoostMultiplier:0.#} with a boosted player within "
                           + $"{tame.m_tamingSpeedMultiplierRange:0} m";

                if (ai != null)
                    line += $"; finds food within {ai.m_consumeSearchRange:0.#} m";

                if (love != null)
                    line += $"; breeds with a partner within {love.m_partnerCheckRange:0.#} m, "
                            + $"at most {love.m_maxCreatures} within {love.m_totalCheckRange:0.#} m, "
                            + $"pregnancy {love.m_pregnancyDuration / 60f:0.#} min, "
                            + $"needs {love.m_requiredLovePoints} feedings";

                Log.LogInfo($"[AstvardServerMod] Tames: {line}.");
            }
        }

        private static bool FoodLiesNear(MonsterAI ai, Vector3 spot)
        {
            var found = Physics.OverlapSphereNonAlloc(spot, ai.m_consumeSearchRange, FoodNearby, ItemLayer);

            for (var i = 0; i < found; i++)
            {
                var collider = FoodNearby[i];
                if (collider == null || collider.attachedRigidbody == null) continue;

                var lying = collider.attachedRigidbody.GetComponent<ItemDrop>();
                if (lying == null || lying.m_itemData == null) continue;

                foreach (var edible in ai.m_consumeItems)
                {
                    if (edible == null || edible.m_itemData == null) continue;
                    if (edible.m_itemData.m_shared.m_name == lying.m_itemData.m_shared.m_name) return true;
                }
            }

            return false;
        }

        private static GameObject FoodPrefab(MonsterAI ai, string prefabName)
        {
            foreach (var item in ai.m_consumeItems)
                if (item != null && item.gameObject.name == prefabName) return item.gameObject;

            return null;
        }

        /// <summary>
        /// Кладёт одну штуку под ноги зверю - ровно то, что делает игрок, бросая еду.
        ///
        /// Slightly above the ground and turned at random: two animals fed in the same
        /// second would otherwise get two items in the same spot, and one of them would be
        /// pushed out by physics into wherever.
        /// </summary>
        private static void DropFood(GameObject prefab, Vector3 at)
        {
            var aside = Random.insideUnitCircle * 0.4f;
            var spot = at + Vector3.up * 0.4f + new Vector3(aside.x, 0f, aside.y);

            var spawned = Instantiate(prefab, spot, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));

            var drop = spawned.GetComponent<ItemDrop>();
            if (drop != null && drop.m_itemData != null) drop.m_itemData.m_stack = 1;

            ItemDrop.OnCreateNew(spawned);
        }
    }
}
