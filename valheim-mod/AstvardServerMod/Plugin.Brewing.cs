using System.Collections.Generic;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>
        /// Варка медовух: от ингредиентов в сундуке до бочки.
        ///
        /// Цепочка в игре из трёх шагов — сварить основу в «Котле для медовух», поставить
        /// её в бочку, снять через двое суток бутылки, — и две последние мод делал и
        /// раньше. Не хватало первой: пока человек руками не наварит основ, бочки стоят
        /// пустые, и вся автоматика упирается в это.
        ///
        /// Списка напитков здесь нет ни одного: что бродит, говорит сама бочка (её таблица
        /// превращений), из чего варится — рецепт игры, а что из этого игрок открыл —
        /// `IsRecipeKnown`. Новый напиток в новой версии игры появится на странице сам.
        ///
        /// Ингредиенты списываются ровно по рецепту и только целым заказом: взять полсостава
        /// и не сварить — это молча съеденный мёд.
        /// </summary>
        private static BepInEx.Configuration.ConfigEntry<bool> _brewEnabled;

        private static BepInEx.Configuration.ConfigEntry<string> _brewWishes;

        internal static void BindBrewing(BepInEx.Configuration.ConfigFile config)
        {
            _brewEnabled = config.Bind("Медовухи", "Enabled", true,
                "Варить ли медовухи самому. Меняется в игре: «Функции» → «Работа с сундуками» → «Медовухи».");

            _brewWishes = config.Bind("Медовухи", "Keep", "",
                "Что варить и сколько бутылок держать на складе: «основа=число» через точку с "
                + "запятой. Пустая строка — не варить ничего. Правится из игры, руками сюда лезть незачем.");
        }

        internal static bool BrewEnabled
        {
            get { return _brewEnabled == null || _brewEnabled.Value; }
        }

        internal static void SetBrewEnabled(bool on)
        {
            if (_brewEnabled != null) _brewEnabled.Value = on;
        }

        /// <summary>Заказы: имя префаба основы → сколько бутылок готового держать.</summary>
        internal static Dictionary<string, int> BrewWishes()
        {
            return Brewing.ReadWishes(_brewWishes != null ? _brewWishes.Value : "");
        }

        internal static int BrewWish(string baseName)
        {
            int keep;
            return BrewWishes().TryGetValue(baseName, out keep) ? keep : 0;
        }

        internal static void SetBrewWish(string baseName, int keep)
        {
            if (_brewWishes == null || string.IsNullOrEmpty(baseName)) return;

            var wishes = BrewWishes();
            if (keep <= 0) wishes.Remove(baseName);
            else wishes[baseName] = keep > Brewing.MaxKeep ? Brewing.MaxKeep : keep;

            _brewWishes.Value = Brewing.PackWishes(wishes);
        }

        // ---------------- что вообще можно сварить ----------------

        /// <summary>Одна строка списка: основа, что из неё выходит и по какому рецепту.</summary>
        internal sealed class Brew
        {
            internal string Base;

            internal string Drink;

            internal string Title;

            internal int PerBrew;

            internal Recipe Recipe;
        }

        private static readonly List<Brew> BrewList = new List<Brew>();

        private static float _brewListAt = -999f;

        /// <summary>
        /// Что этот игрок умеет варить: превращения бочки, у которых есть известный ему
        /// рецепт основы.
        ///
        /// Пересобирается раз в пять секунд, а не каждый кадр: обход рецептов игры — это
        /// несколько сотен строк, а открыть новый рецепт посреди варки можно, и ждать
        /// перезахода ради этого незачем.
        /// </summary>
        internal static List<Brew> KnownBrews()
        {
            if (Time.time - _brewListAt < 5f) return BrewList;
            _brewListAt = Time.time;

            BrewList.Clear();

            var player = Player.m_localPlayer;
            var scene = ZNetScene.instance;
            var db = ObjectDB.instance;
            if (player == null || scene == null || db == null || db.m_recipes == null) return BrewList;

            var barrel = scene.GetPrefab("fermenter");
            var fermenter = barrel != null ? barrel.GetComponent<Fermenter>() : null;
            if (fermenter == null || fermenter.m_conversion == null) return BrewList;

            foreach (var conversion in fermenter.m_conversion)
            {
                if (conversion == null || conversion.m_from == null || conversion.m_to == null) continue;

                var baseName = conversion.m_from.gameObject.name;
                var recipe = RecipeFor(db, baseName);
                if (recipe == null || !recipe.m_enabled) continue;

                var key = recipe.m_item != null && recipe.m_item.m_itemData.m_shared != null
                    ? recipe.m_item.m_itemData.m_shared.m_name
                    : null;
                if (string.IsNullOrEmpty(key) || !player.IsRecipeKnown(key)) continue;

                BrewList.Add(new Brew
                {
                    Base = baseName,
                    Drink = conversion.m_to.gameObject.name,
                    // Показываем готовый напиток, а не основу: заказывают его, а основа —
                    // это внутренняя кухня, о которой человеку думать незачем.
                    Title = ItemTitle(conversion.m_to.m_itemData),
                    PerBrew = conversion.m_producedItems > 0 ? conversion.m_producedItems : 1,
                    Recipe = recipe
                });
            }

            BrewList.Sort((a, b) => string.Compare(a.Title, b.Title, System.StringComparison.CurrentCulture));
            return BrewList;
        }

        private static Recipe RecipeFor(ObjectDB db, string itemPrefab)
        {
            foreach (var recipe in db.m_recipes)
            {
                if (recipe == null || recipe.m_item == null) continue;
                if (recipe.m_item.gameObject.name == itemPrefab) return recipe;
            }

            return null;
        }

        internal static Brew BrewOf(string baseName)
        {
            foreach (var brew in KnownBrews())
                if (brew.Base == baseName) return brew;

            return null;
        }

        // ---------------- сколько уже бродит ----------------

        /// <summary>Хэш содержимого бочки → сколько таких бочек стоит. Считается обходом станций.</summary>
        private static readonly Dictionary<int, int> BrewingNow = new Dictionary<int, int>();

        private static readonly Dictionary<int, int> BrewingNext = new Dictionary<int, int>();

        /// <summary>Заложенное этим же проходом: обход один, и учесть его надо сразу.</summary>
        private static readonly Dictionary<int, int> BrewingAdded = new Dictionary<int, int>();

        internal static void BrewSweepStart()
        {
            BrewingNext.Clear();
            BrewingAdded.Clear();
            _whyRank = 0;
            _whyNext = null;
            _sawBarrel = false;
        }

        // ---------------- почему не варится ----------------

        /// <summary>
        /// «Не работает» без причины — это вечер догадок: у огорода их сегодня было четыре
        /// подряд, и ни одна не была видна, пока страница не начала объяснять.
        ///
        /// Причин много, и они разной глубины: «все бочки заняты» человек проверит глазами
        /// за секунду, а «не хватает мёда» — нет. Поэтому за проход побеждает та, до которой
        /// дошли дальше всех: она и есть настоящая помеха.
        /// </summary>
        private static int _whyRank;

        private static string _whyNext;

        private static string _why;

        private static bool _sawBarrel;

        private static void Why(int rank, string text)
        {
            if (rank < _whyRank) return;
            _whyRank = rank;
            _whyNext = text;
        }

        /// <summary>Что мешает варить прямо сейчас. Пусто — значит ничего не мешает.</summary>
        internal static string BrewWhy
        {
            get { return _why; }
        }

        /// <summary>
        /// Общее «Наполнение» выключено. Говорится отсюда, а не изнутри варки: та в этом
        /// случае не зовётся вовсе, и страница молчала бы, хотя причина известна.
        /// </summary>
        internal static void BrewFeedingOff()
        {
            Why(1, "выключено наполнение станций");
        }

        /// <summary>Бочка с содержимым: 1.0 держит в ZDO не имя, а стабильный хэш префаба.</summary>
        internal static void BrewSweepSaw(Fermenter fermenter)
        {
            if (fermenter == null) return;

            var view = fermenter.GetComponent<ZNetView>();
            if (view == null || !view.IsValid()) return;

            _sawBarrel = true;

            var content = view.GetZDO().GetInt(ZDOVars.s_content);
            if (content == 0) return;

            int had;
            BrewingNext[content] = (BrewingNext.TryGetValue(content, out had) ? had : 0) + 1;
        }

        internal static void BrewSweepDone()
        {
            BrewingNow.Clear();
            foreach (var pair in BrewingNext) BrewingNow[pair.Key] = pair.Value;

            // Бочек нет вовсе — это не «все заняты», и спутать их значит отправить
            // человека искать несуществующую занятую бочку.
            if (!_sawBarrel && _whyRank < 3) _whyNext = "поблизости нет бочек";
            _why = _whyNext;

            // Та же причина - в лог, раз на изменение. Страницу читает человек, стоя у
            // бочки; лог читают, когда бочка пустая, а почему - уже не видно. Вечер с
            // огородом показал, что вторая половина случается чаще первой.
            if (_why != _whySaid)
            {
                _whySaid = _why;
                Log.LogInfo(string.IsNullOrEmpty(_why)
                    ? "[AstvardServerMod] Brewing: nothing in the way."
                    : $"[AstvardServerMod] Brewing: not brewing — {_why}.");
            }
        }

        // Не null и не пустая строка: и то и другое бывает настоящим ответом, а первый
        // проход обязан сказать о себе.
        private static string _whySaid = "\0";

        private static int BrewingCount(string baseName)
        {
            var hash = baseName.GetStableHashCode();
            int now, added;
            return (BrewingNow.TryGetValue(hash, out now) ? now : 0)
                   + (BrewingAdded.TryGetValue(hash, out added) ? added : 0);
        }

        // ---------------- сама варка ----------------

        private static readonly List<string> BrewBases = new List<string>();

        private static readonly List<KeyValuePair<string, int>> BrewNeed = new List<KeyValuePair<string, int>>();

        /// <summary>
        /// Пустая бочка и заказ: сварить основу из сундуков и поставить.
        ///
        /// Возвращает true, если что-то поставили, — тогда обход эту бочку больше не
        /// трогает. Молчит по любой причине: нет заказа, нечем платить, нет котла рядом.
        /// </summary>
        internal static bool TryBrewInto(Fermenter fermenter, List<Container> supply)
        {
            if (fermenter == null) return false;

            if (!BrewEnabled) { Why(1, "варка выключена"); return false; }
            if (!RuleAllows("brewing")) { Why(1, "админ закрыл варку игрокам"); return false; }
            if (!OwnedAndValid(fermenter)) return false;

            var view = fermenter.GetComponent<ZNetView>();
            if (view.GetZDO().GetInt(ZDOVars.s_content) != 0) { Why(2, "все бочки заняты"); return false; }

            var wishes = BrewWishes();
            if (wishes.Count == 0) { Why(3, "ничего не заказано"); return false; }

            var brews = KnownBrews();
            if (brews.Count == 0) { Why(3, "ни одного рецепта основы не открыто"); return false; }

            var origin = fermenter.transform.position;

            BrewBases.Clear();
            foreach (var brew in brews)
            {
                if (!wishes.ContainsKey(brew.Base)) continue;
                // Котёл нужен настоящий: варка без станции — это уже не автоматика, а
                // выдача предметов из воздуха.
                if (!StationReady(brew.Recipe, origin)) continue;
                BrewBases.Add(brew.Base);
            }

            if (BrewBases.Count == 0)
            {
                Why(4, "рядом нет котла для медовух нужного уровня");
                return false;
            }

            var pick = Brewing.Next(BrewBases,
                name =>
                {
                    var brew = BrewOf(name);
                    if (brew == null) return 0;
                    int keep;
                    wishes.TryGetValue(name, out keep);
                    return Brewing.StillToBrew(keep, InStock(brew.Drink), BrewingCount(name), brew.PerBrew);
                },
                name =>
                {
                    int keep;
                    return wishes.TryGetValue(name, out keep) ? keep : 0;
                });

            if (pick == null)
            {
                Why(5, "заказанного уже хватает");
                return false;
            }

            var chosen = BrewOf(pick);
            if (chosen == null || !ReadRecipeCost(chosen.Recipe))
            {
                Why(6, "рецепт не прочитался");
                return false;
            }

            if (!Brewing.CanAfford(BrewNeed, name => CountInSupply(origin, supply, name)))
            {
                Why(7, $"не хватает: {Missing(origin, supply)}");
                return false;
            }

            if (!TakeForBrew(origin, supply))
            {
                Why(7, "составляющие разобрали, пока мы считали");
                return false;
            }

            // Та же дорога, что у готовой основы из сундука: имя хэшем, «не читерское».
            view.InvokeRPC("RPC_AddItem", pick.GetStableHashCode(), false);

            var hash = pick.GetStableHashCode();
            int added;
            BrewingAdded[hash] = (BrewingAdded.TryGetValue(hash, out added) ? added : 0) + 1;

            SayBrewed(chosen);
            Why(9, null);
            return true;
        }

        /// <summary>Чего именно не хватает — словами игры и с числами.</summary>
        private static string Missing(Vector3 origin, List<Container> supply)
        {
            var line = new System.Text.StringBuilder();
            foreach (var need in BrewNeed)
            {
                var have = CountInSupply(origin, supply, need.Key);
                if (have >= need.Value) continue;

                if (line.Length > 0) line.Append(", ");
                line.Append(ItemTitleOf(need.Key)).Append(' ').Append(need.Value - have);
            }

            return line.Length > 0 ? line.ToString() : "чего-то из состава";
        }

        private static readonly List<CraftingStation> BrewStations = new List<CraftingStation>();

        /// <summary>
        /// Стоит ли рядом станция рецепта и хватает ли ей уровня.
        ///
        /// «Рядом» здесь — та же «Зона станций», что у печей и плавилен, а не собственная
        /// даль игры: остальная автоматика меряет ею же, и объяснять две разные дали одним
        /// и тем же словом было бы враньём. Уровень спрашиваем у самой станции — она
        /// считает его по пристройкам вокруг котла, и повторять этот счёт незачем.
        /// </summary>
        /// <summary>
        /// Есть ли рядом станция, которую просит рецепт.
        ///
        /// «Рядом» здесь значит то же, что у сундуков (просьба хозяина, 20.09.2026): что в
        /// одной зоне сортировки, то работает вместе, на каком бы её краю ни стояло. До
        /// этого котёл искался в «Зоне станций» вокруг бочки - восемь метров, - и бочки с
        /// котлом приходилось держать в обнимку, хотя сундуки для той же варки брались со
        /// всей зоны. Два разных расстояния на одну работу никто не держит в голове.
        ///
        /// Ближний котёл **за** краем зоны при этом остался рабочим: правило расширено, а
        /// не подменено. Поэтому проходов два, и оба нужны - зона не обязана накрывать всё,
        /// что стоит у бочки.
        ///
        /// `FindStationsInRange` список не чистит, а дополняет, и меряет расстояние **в
        /// трёх измерениях** - отсюда и `Sorting.ScanRadius`, у которого запас по высоте
        /// уже заложен: иначе котёл этажом ниже выпал бы из своей же зоны.
        /// </summary>
        private static bool StationReady(Recipe recipe, Vector3 spot)
        {
            if (recipe == null) return false;
            if (recipe.m_craftingStation == null) return true;

            var zone = ZoneAround(spot);

            BrewStations.Clear();
            if (zone != null)
                CraftingStation.FindStationsInRange(recipe.m_craftingStation.m_name,
                                                   new Vector3(zone.X, spot.y, zone.Z),
                                                   Sorting.ScanRadius(zone), BrewStations);

            CraftingStation.FindStationsInRange(recipe.m_craftingStation.m_name, spot,
                                                AssignedChestRadius, BrewStations);

            foreach (var station in BrewStations)
            {
                if (station == null || station.GetLevel() < recipe.m_minStationLevel) continue;

                var at = station.transform.position;
                if (zone != null && Sorting.Inside(zone, at.x, at.z)) return true;
                if (InChestZone(spot, at)) return true;
            }

            return false;
        }

        /// <summary>Что и сколько нужно на одну основу — прямо из рецепта игры.</summary>
        private static bool ReadRecipeCost(Recipe recipe)
        {
            BrewNeed.Clear();
            if (recipe == null || recipe.m_resources == null) return false;

            foreach (var need in recipe.m_resources)
            {
                if (need == null || need.m_resItem == null) continue;

                var amount = need.GetAmount(1);
                if (amount <= 0) continue;

                BrewNeed.Add(new KeyValuePair<string, int>(need.m_resItem.gameObject.name, amount));
            }

            return BrewNeed.Count > 0;
        }

        /// <summary>Сколько такого лежит в сундуках подачи, откуда мы и будем брать.</summary>
        private static int CountInSupply(Vector3 origin, List<Container> supply, string prefab)
        {
            var count = 0;
            foreach (var container in supply)
            {
                if (!SupplyReachable(container, origin)) continue;

                foreach (var item in container.GetInventory().GetAllItems())
                {
                    if (item == null || item.m_dropPrefab == null) continue;
                    if (PrefabName(item.m_dropPrefab) != prefab) continue;
                    count += item.m_stack;
                }
            }

            return count;
        }

        /// <summary>
        /// Годится ли этот сундук: флажок «не для станций» сильнее всего остального — его
        /// ставят именно затем, чтобы отсюда не брали.
        /// </summary>
        private static bool SupplyReachable(Container container, Vector3 origin)
        {
            if (container == null || container.IsInUse() || IsHoldChest(container)) return false;

            var view = ViewOf(container);
            if (view == null || !view.IsValid()) return false;

            var spot = container.transform.position;
            var zone = ZoneAround(origin);
            if (zone != null && Sorting.Inside(zone, spot.x, spot.z)) return true;

            return InChestZone(origin, spot);
        }

        /// <summary>
        /// Списать состав целиком. Если на полпути что-то исчезло — вернуть взятое назад:
        /// съеденный мёд без медовухи хуже, чем ненаступившая варка.
        /// </summary>
        private static bool TakeForBrew(Vector3 origin, List<Container> supply)
        {
            var taken = new List<KeyValuePair<string, int>>();

            foreach (var need in BrewNeed)
            {
                var left = need.Value;

                foreach (var container in supply)
                {
                    if (left <= 0) break;
                    if (!SupplyReachable(container, origin)) continue;

                    var view = ViewOf(container);
                    if (!view.IsOwner()) view.ClaimOwnership();

                    var inventory = container.GetInventory();
                    while (left > 0)
                    {
                        var item = inventory.GetItem(need.Key, -1, true);
                        if (item == null || item.m_dropPrefab == null
                            || PrefabName(item.m_dropPrefab) != need.Key) break;

                        var step = item.m_stack < left ? item.m_stack : left;
                        if (!inventory.RemoveItem(item, step)) break;
                        left -= step;
                    }
                }

                taken.Add(new KeyValuePair<string, int>(need.Key, need.Value - left));

                if (left > 0)
                {
                    GiveBackBrew(origin, supply, taken);
                    return false;
                }
            }

            return true;
        }

        /// <summary>Вернуть в сундуки то, что уже успели забрать у несостоявшейся варки.</summary>
        private static void GiveBackBrew(Vector3 origin, List<Container> supply,
                                         List<KeyValuePair<string, int>> taken)
        {
            var scene = ZNetScene.instance;
            if (scene == null) return;

            foreach (var back in taken)
            {
                var left = back.Value;
                if (left <= 0) continue;

                var prefab = scene.GetPrefab(back.Key);
                var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (drop == null) continue;

                foreach (var container in supply)
                {
                    if (left <= 0) break;
                    if (!SupplyReachable(container, origin)) continue;

                    var inventory = container.GetInventory();
                    while (left > 0 && inventory.CanAddItem(prefab, 1))
                    {
                        if (!inventory.AddItem(prefab, 1)) break;
                        left--;
                    }
                }

                // Класть некуда — роняем под ноги бочке: в никуда предметы не деваются.
                if (left > 0) ItemDrop.DropItem(drop.m_itemData, left, origin + Vector3.up, Quaternion.identity);
            }
        }

        private static float _brewSaidAt = -999f;

        private static void SayBrewed(Brew brew)
        {
            Log.LogInfo($"[AstvardServerMod] Brewing: started {brew.Base} ({brew.PerBrew} of {brew.Drink}).");

            // В углу экрана — не чаще раза в десять секунд: бочек бывает двадцать, и
            // двадцать строк подряд это не отчёт, а стена.
            if (Time.time - _brewSaidAt < 10f) return;
            _brewSaidAt = Time.time;

            Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, $"Ставим: {brew.Title}");
        }
    }
}
