using System.Text;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>
        /// «Функции» → «Сортировка» → «Настройки» → «Медовухи»: что варить и сколько держать.
        ///
        /// Список — не наш: в нём ровно то, что этот игрок открыл и что бочка умеет
        /// сбраживать. Поэтому у новичка страница почти пуста, а у того, кто дошёл до
        /// равнин, длинная, и ни одной строки под это писать не пришлось.
        ///
        /// Заказ — в бутылках готового напитка, а не в основах: человек думает «хочу
        /// двадцать лечебных», а сколько для этого бочек — арифметика, и она наша.
        /// </summary>
        internal static GameObject BrewButton;

        internal static GameObject BrewToggleButton;

        internal static GameObject BrewHint;

        internal static readonly GameObject[] BrewRowButtons = new GameObject[MaxTemplateButtons];

        internal static GameObject BrewOneHint;

        internal static GameObject BrewTakeButton;

        internal static GameObject BrewKeepInput;

        internal static GameObject BrewApplyButton;

        internal static GameObject BrewStopButton;

        private static string _brewChosen;

        private static int _shownBrews;

        private void CreateBrewPageWidgets(GUIManager gui)
        {
            BrewButton = MakeButton(gui, "", () =>
            {
                _itemOffset = 0;
                MenuState = StateBrews;
                RefreshMenu();
            });

            BrewToggleButton = MakeButton(gui, "", () =>
            {
                SetBrewEnabled(!BrewEnabled);
                RefreshMenu();
            });

            // Кнопка есть только пока есть что забирать - то есть ровно один раз за всю
            // жизнь этой установки, после переезда заказов на персонажей.
            BrewTakeButton = MakeButton(gui, "", () =>
            {
                var took = TakeSharedOrder();
                if (took > 0)
                    Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                        $"Прежние заказы ({took}) теперь твои");

                RefreshMenu();
            });

            BrewHint = MakeText(gui, "");

            for (var slot = 0; slot < MaxTemplateButtons; slot++)
            {
                var index = slot;
                BrewRowButtons[slot] = MakeButton(gui, "", () =>
                {
                    var brews = KnownBrews();
                    var at = MenuPaging.Clamp(_itemOffset, brews.Count, MaxTemplateButtons) + index;
                    if (at >= brews.Count) return;

                    _brewChosen = brews[at].Base;
                    var keep = BrewWish(_brewChosen);
                    SetFieldText(BrewKeepInput, keep > 0 ? keep.ToString() : "");
                    MenuState = StateBrewOne;
                    RefreshMenu();
                });
            }

            BrewOneHint = MakeText(gui, "");

            BrewKeepInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.IntegerNumber, "сколько бутылок", 16, 160f, 32f);
            AddFixedSize(BrewKeepInput, 160f, 32f);

            BrewApplyButton = MakeButton(gui, "Варить", () =>
            {
                if (_brewChosen == null) return;

                var keep = Mathf.RoundToInt(ParseField(BrewKeepInput, BrewWish(_brewChosen)));
                SetBrewWish(_brewChosen, keep);

                var brew = BrewOf(_brewChosen);
                var title = brew != null ? brew.Title : _brewChosen;
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, keep > 0
                    ? $"{title}: держим {keep}"
                    : $"{title}: не варим");

                MenuState = StateBrews;
                RefreshMenu();
            });

            BrewStopButton = MakeButton(gui, "Не варить", () =>
            {
                if (_brewChosen == null) return;

                SetBrewWish(_brewChosen, 0);
                MenuState = StateBrews;
                RefreshMenu();
            });
        }

        /// <summary>Строка списка: что получится и сколько его держим.</summary>
        private static string BrewRowLabel(Brew brew)
        {
            var keep = BrewWish(brew.Base);
            if (keep <= 0) return $"{brew.Title} — не варим";

            var have = InStock(brew.Drink);
            var going = BrewingCount(brew.Base);
            return going > 0
                ? $"{brew.Title} — {have}/{keep}, в бочках {going}"
                : $"{brew.Title} — {have}/{keep}";
        }

        /// <summary>Что нужно на одну основу — словами игры, а не нашими.</summary>
        private static string BrewCost(Brew brew)
        {
            if (brew == null || !ReadRecipeCost(brew.Recipe)) return "рецепт не прочитался";

            var line = new StringBuilder();
            foreach (var need in BrewNeed)
            {
                if (line.Length > 0) line.Append(", ");
                line.Append(ItemTitleOf(need.Key)).Append(' ').Append(need.Value);
            }

            return line.ToString();
        }

        /// <summary>Имя предмета по имени префаба: в подсказке нужно человеческое.</summary>
        private static string ItemTitleOf(string prefab)
        {
            var scene = ZNetScene.instance;
            var found = scene != null ? scene.GetPrefab(prefab) : null;
            var drop = found != null ? found.GetComponent<ItemDrop>() : null;
            return drop != null ? ItemTitle(drop.m_itemData) : prefab;
        }

        /// <summary>
        /// Какая станция нужна этому рецепту и какого уровня.
        ///
        /// Числом, а не словами «нужного уровня»: уровень станции в игре - это
        /// `1 + число приставок рядом` (`CraftingStation.GetLevel`), то есть голый котёл
        /// это единица, а выше он становится от полок и столов вокруг него. Сколько
        /// требует конкретная медовуха, лежит в данных рецепта, и прочитать это можно
        /// только у живой игры - написать число в коде значило бы выдумать его.
        /// </summary>
        private static string StationNeed(Brew brew)
        {
            var station = brew.Recipe != null ? brew.Recipe.m_craftingStation : null;
            if (station == null) return "не нужна";

            var name = station.name;
            if (!string.IsNullOrEmpty(station.m_name) && Localization.instance != null)
            {
                var said = Localization.instance.Localize(station.m_name);
                if (!string.IsNullOrEmpty(said) && !said.StartsWith("$")) name = said;
            }

            var level = brew.Recipe.m_minStationLevel;
            return level > 1 ? $"{name}, уровень {level}" : name;
        }

        private static void RebuildBrewViews()
        {
            var brews = KnownBrews();

            if (MenuState == StateBrews)
                _itemOffset = MenuPaging.Clamp(_itemOffset, brews.Count, MaxTemplateButtons);
            _shownBrews = MenuState == StateBrews
                ? MenuPaging.Shown(_itemOffset, brews.Count, MaxTemplateButtons)
                : 0;

            var hint = BrewHint != null ? BrewHint.GetComponentInChildren<Text>(true) : null;
            if (hint != null)
            {
                if (brews.Count == 0)
                    hint.text = $"Варить пока нечего: ни одного{NEWLINE}рецепта основы ты ещё не{NEWLINE}открыл.";
                else
                    hint.text = $"Сколько бутылок держать на{NEWLINE}складе. Варим из сундуков{NEWLINE}"
                                + $"подачи, пока рядом стоит{NEWLINE}котёл для медовух и есть{NEWLINE}"
                                + $"пустая бочка."
                                + (string.IsNullOrEmpty(BrewWhy)
                                    ? ""
                                    : $"{NEWLINE}{NEWLINE}Сейчас не варим:{NEWLINE}{BrewWhy}.")
                                + (SharedOrderLeft() > 0
                                    ? $"{NEWLINE}{NEWLINE}Заказы теперь у каждого{NEWLINE}"
                                      + $"персонажа свои. Прежние{NEWLINE}общие ждут, кому{NEWLINE}достаться."
                                    : "")
                                + WindowNote(_itemOffset, brews.Count);
            }

            for (var i = 0; i < MaxTemplateButtons; i++)
            {
                if (i >= _shownBrews)
                {
                    SetLabel(BrewRowButtons[i], "");
                    continue;
                }

                SetLabel(BrewRowButtons[i], BrewRowLabel(brews[_itemOffset + i]));
            }

            var one = BrewOneHint != null ? BrewOneHint.GetComponentInChildren<Text>(true) : null;
            if (one != null && MenuState == StateBrewOne)
            {
                var brew = BrewOf(_brewChosen);
                if (brew == null)
                {
                    one.text = "Этого рецепта больше нет.";
                }
                else
                {
                    var keep = BrewWish(brew.Base);
                    one.text = $"{brew.Title}{NEWLINE}{NEWLINE}"
                               + $"С бочки выходит {brew.PerBrew}.{NEWLINE}"
                               + $"На складе {InStock(brew.Drink)}, в{NEWLINE}бочках {BrewingCount(brew.Base)}.{NEWLINE}{NEWLINE}"
                               + $"На одну бочку нужно:{NEWLINE}{BrewCost(brew)}.{NEWLINE}{NEWLINE}"
                               + $"Станция: {StationNeed(brew)}.{NEWLINE}{NEWLINE}"
                               + (keep > 0 ? $"Сейчас держим {keep}." : "Сейчас не варим.");
                }
            }
        }

        private static void RefreshBrewVisibility()
        {
            RebuildBrewViews();

            var mine = RuleAllows("brewing");

            // «Из скольких» дописывается только когда числа разошлись — то есть когда
            // часть заказов сделана персонажем, знавшим больше рецептов, чем этот.
            // Без этого «заказов 4» на месте вчерашних восьми выглядит потерей заказов.
            var mineCount = BrewWishesHere();
            var all = BrewWishes().Count;
            SetLabel(BrewButton, BrewEnabled
                ? (all > mineCount
                    ? $"Медовухи: заказов {mineCount} из {all}"
                    : $"Медовухи: заказов {mineCount}")
                : "Медовухи: выкл");
            SetLabel(BrewToggleButton, BrewEnabled ? "Варка: вкл" : "Варка: выкл");

            // Просьба хозяина: варка стоит рядом с остальным, что настраивают однажды,
            // - в «Настройках» сортировки, а не в «Работе с сундуками». Сундуки там про
            // то, куда класть, а медовухи про то, что делать.
            SetActive(BrewButton, mine && MenuState == StateSortSetup);

            var waiting = SharedOrderLeft();
            SetLabel(BrewTakeButton, $"Забрать прежние заказы ({waiting})");

            var list = mine && MenuState == StateBrews;
            SetActive(BrewHint, list);
            SetActive(BrewToggleButton, list);
            SetActive(BrewTakeButton, list && waiting > 0);
            for (var i = 0; i < MaxTemplateButtons; i++)
                SetActive(BrewRowButtons[i], list && i < _shownBrews);

            var one = mine && MenuState == StateBrewOne;
            SetActive(BrewOneHint, one);
            SetActive(BrewKeepInput, one);
            SetActive(BrewApplyButton, one);
            SetActive(BrewStopButton, one);
        }
    }
}
