using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        internal static GameObject SettingsButton;

        internal static GameObject BuildRulesButton;

        internal static GameObject TerrainRulesButton;

        internal static GameObject FeatureRulesButton;

        internal static GameObject RuneMinutesButton;

        internal static GameObject RuneMinutesHint;

        internal static GameObject RuneMinutesInput;

        internal static GameObject RuneMinutesApply;

        internal static GameObject RulesHint;

        internal static GameObject PlayerCooldownButton;

        internal static GameObject AllowedListButton;

        internal static GameObject CooldownHint;

        internal static GameObject CooldownInput;

        internal static GameObject CooldownApplyButton;

        internal static GameObject AllowedHint;

        internal static readonly GameObject[] AllowedButtons = new GameObject[MaxTemplateButtons];

        internal static GameObject RuleEditHint;

        internal static GameObject RuleEditToggle;

        internal static GameObject RuleEditInput;

        internal static GameObject RuleEditApply;

        internal static readonly GameObject[] RuleChoiceButtons = new GameObject[3];

        private static readonly string[] RuleChoiceLabels = { "Нельзя", "Даром", "Платно" };

        // Where «Назад» on a server template's page leads: the list it was opened from,
        // the server's own or the one of what players may build.
        private static int _sharedItemBack = StateSharedList;

        private static int _editingRule;

        /// <summary>
        /// «Настройки» of the admin menu: a page for each part of the game players may be
        /// let into, one button per setting on it with the choice behind it, as every
        /// setting in this panel is. Made in the order the pages show them.
        /// </summary>
        private void CreateSettingsWidgets(GUIManager gui)
        {
            SettingsButton = MakeButton(gui, "Настройки", () => OpenRulePage(StateSettings));

            BuildRulesButton = MakeButton(gui, "Постройки для игроков", () => OpenRulePage(StateBuildRules));
            TerrainRulesButton = MakeButton(gui, "Рельеф для игроков", () => OpenRulePage(StateTerrainRules));
            FeatureRulesButton = MakeButton(gui, "Функции для игроков", () => OpenRulePage(StateFeatureRules));

            RuneMinutesButton = MakeButton(gui, "", () =>
            {
                SetFieldText(RuneMinutesInput, _runeMinutes.ToString());
                OpenRulePage(StateRuneMinutes);
            });

            RulesHint = MakeText(gui, "");

            PlayerCooldownButton = MakeButton(gui, "", () =>
            {
                SetFieldText(CooldownInput, _playerBuildMinutes.ToString());
                MenuState = StatePlayerCooldown;
                RefreshMenu();
            });

            for (var i = 0; i < PlayerRules.Length; i++)
            {
                var index = i;
                PlayerRuleButtons[i] = MakeButton(gui, "", () =>
                {
                    var rule = PlayerRules[index];
                    if (rule.Kind == RuleKind.Toggle)
                    {
                        // Nothing to set but yes or no: the button is the switch.
                        SetPlayerRule(rule.Key, rule.PlayersValue > 0 ? 0 : 1);
                        return;
                    }

                    _editingRule = index;
                    if (rule.Kind == RuleKind.Limit || rule.Kind == RuleKind.ChoiceLimit)
                        SetFieldText(RuleEditInput, rule.PlayerLimit.ToString());
                    MenuState = StateRuleEdit;
                    RefreshMenu();
                });
            }

            AllowedListButton = MakeButton(gui, "", () => OpenRulePage(StateAllowedList));

            CooldownHint = MakeText(gui, "Как часто игрок может ставить\nбесплатные постройки,\nв минутах. 0 — без паузы.\nОтмена постройки паузу\nне сбрасывает.");

            CooldownInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.IntegerNumber, "минуты, напр. 5", 16, 160f, 32f);
            AddFixedSize(CooldownInput, 160f, 32f);

            CooldownApplyButton = MakeButton(gui, "Применить", () =>
            {
                var minutes = Mathf.Clamp(Mathf.RoundToInt(ParseField(CooldownInput, _playerBuildMinutes)),
                                          0, MaxPlayerBuildMinutes);
                SetPlayerBuildPause(minutes);
                MenuState = StateBuildRules;
                RefreshMenu();
            });

            RuneMinutesHint = MakeText(gui, "Сколько минут в игре даёт\nигроку одну руну, от 1 до 1440.\nМеняется сразу, без перезапуска:\nнабранное к руне время\nне пропадает.");

            RuneMinutesInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.IntegerNumber, "минуты, напр. 60", 16, 160f, 32f);
            AddFixedSize(RuneMinutesInput, 160f, 32f);

            RuneMinutesApply = MakeButton(gui, "Применить", () =>
            {
                SetRuneMinutes(Mathf.RoundToInt(ParseField(RuneMinutesInput, _runeMinutes)));
                MenuState = StateSettings;
                RefreshMenu();
            });

            AllowedHint = MakeText(gui, "");

            for (var slot = 0; slot < MaxTemplateButtons; slot++)
            {
                var index = slot;
                AllowedButtons[slot] = MakeButton(gui, "", () =>
                {
                    if (index >= PlayerTemplates.Count) return;

                    _selectedShared = PlayerTemplates[index];
                    _sharedItemBack = StateAllowedList;
                    MenuState = StateSharedItem;
                    RefreshMenu();
                });
            }

            RuleEditHint = MakeText(gui, "");

            RuleEditToggle = MakeButton(gui, "", () =>
            {
                var rule = PlayerRules[_editingRule];
                SetPlayerRule(rule.Key, rule.PlayersValue > 0 ? 0 : 1);
            });

            RuleEditInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.IntegerNumber, "метры", 16, 160f, 32f);
            AddFixedSize(RuleEditInput, 160f, 32f);

            RuleEditApply = MakeButton(gui, "Применить", () =>
            {
                var rule = PlayerRules[_editingRule];
                var limit = ClampLimit(rule, Mathf.RoundToInt(ParseField(RuleEditInput, rule.PlayerLimit)));
                SetPlayerRule(rule.Key + ".max", limit);
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    $"{rule.Title} игрокам: {rule.LimitWord} до {limit} м");
                OpenRulePage(RulePage(rule.Group));
            });

            for (var i = 0; i < RuleChoiceButtons.Length; i++)
            {
                var choice = i;
                RuleChoiceButtons[i] = MakeButton(gui, RuleChoiceLabels[i], () =>
                {
                    var rule = PlayerRules[_editingRule];
                    SetPlayerRule(rule.Key, choice);
                    // Straight back to the page, where the button now says what was chosen -
                    // unless there is a reach to set too, and leaving would mean coming back.
                    if (rule.Kind != RuleKind.ChoiceLimit) OpenRulePage(RulePage(rule.Group));
                });
            }
        }

        private static void OpenRulePage(int state)
        {
            MenuState = state;
            // The rules and the lists as the server has them now, not as last heard.
            AskSharedList();
            RefreshMenu();
        }

        private static int RulePage(RuleGroup group)
        {
            switch (group)
            {
                case RuleGroup.Terrain: return StateTerrainRules;
                case RuleGroup.Build: return StateBuildRules;
                default: return StateFeatureRules;
            }
        }

        private static void RebuildSettingsViews()
        {
            SetLabel(PlayerCooldownButton, _playerBuildMinutes > 0
                ? $"Пауза построек: {_playerBuildMinutes} мин"
                : "Пауза построек: нет");
            SetLabel(AllowedListButton, $"Шаблоны игрокам: {PlayerTemplates.Count}");
            SetLabel(RuneMinutesButton, $"Руна за {RuneTime(_runeMinutes)}");

            for (var i = 0; i < PlayerRules.Length; i++)
                SetLabel(PlayerRuleButtons[i], RuleLabel(PlayerRules[i]));

            var rulesHint = RulesHint != null ? RulesHint.GetComponentInChildren<Text>(true) : null;
            if (rulesHint != null)
            {
                switch (MenuState)
                {
                    case StateTerrainRules:
                        rulesHint.text = $"Что игроки могут в «Рельефе».{NEWLINE}Админа это не касается.{NEWLINE}"
                                         + $"Чужие обереги игрокам{NEWLINE}мешают всегда.";
                        break;
                    case StateBuildRules:
                        rulesHint.text = $"Что игроки могут в своих{NEWLINE}«Постройках». Админа это{NEWLINE}"
                                         + $"не касается.{NEWLINE}«Ресурсы с игроков: нет» —{NEWLINE}"
                                         + $"всё даром, а «платно»{NEWLINE}ниже ждёт своего часа.";
                        break;
                    default:
                        rulesHint.text = $"Что игроки могут в «Функциях».{NEWLINE}Админа это не касается.";
                        break;
                }
            }

            var allowedHint = AllowedHint != null ? AllowedHint.GetComponentInChildren<Text>(true) : null;
            if (allowedHint != null)
                allowedHint.text = PlayerTemplates.Count == 0
                    ? $"Игрокам пока ничего{NEWLINE}не разрешено. Разрешить —{NEWLINE}на странице шаблона."
                    : $"Разрешено игрокам: {PlayerTemplates.Count}"
                      + (PlayerTemplates.Count > MaxTemplateButtons
                          ? $"{NEWLINE}Показаны первые {MaxTemplateButtons}."
                          : "");

            for (var i = 0; i < MaxTemplateButtons; i++)
                SetLabel(AllowedButtons[i], i < PlayerTemplates.Count
                    ? $"{PlayerTemplates[i].Name} ({PlayerTemplates[i].Pieces})"
                    : "");

            var rule = PlayerRules[Mathf.Clamp(_editingRule, 0, PlayerRules.Length - 1)];
            SetLabel(RuleEditToggle, rule.PlayersValue > 0 ? "Игрокам: разрешено" : "Игрокам: запрещено");

            var editHint = RuleEditHint != null ? RuleEditHint.GetComponentInChildren<Text>(true) : null;
            if (editHint != null)
            {
                var reach = $"{NEWLINE}Наибольший {rule.LimitWord}, м:{NEWLINE}"
                            + $"от {rule.LimitMin} до {rule.LimitMax}.";
                var said = $"{NEWLINE}Сейчас: {ChoiceWord(rule.PlayersValue)}."
                           + (string.IsNullOrEmpty(rule.Note) ? "" : NEWLINE + rule.Note);

                editHint.text = $"«{rule.Title}» для игроков."
                                + (rule.Kind == RuleKind.Limit ? reach
                                    : rule.Kind == RuleKind.ChoiceLimit ? said + reach : said);
            }
        }

        private static void RefreshSettingsVisibility(bool admin)
        {
            RebuildSettingsViews();

            SetActive(SettingsButton, admin && MenuState == StateAdmin);

            var settings = admin && MenuState == StateSettings;
            SetActive(BuildRulesButton, settings);
            SetActive(TerrainRulesButton, settings);
            SetActive(FeatureRulesButton, settings);
            SetActive(RuneMinutesButton, settings);

            var runes = admin && MenuState == StateRuneMinutes;
            SetActive(RuneMinutesHint, runes);
            SetActive(RuneMinutesInput, runes);
            SetActive(RuneMinutesApply, runes);

            var group = MenuState == StateTerrainRules ? RuleGroup.Terrain
                : MenuState == StateBuildRules ? RuleGroup.Build
                : RuleGroup.Features;
            var groupPage = admin && (MenuState == StateTerrainRules || MenuState == StateBuildRules
                                      || MenuState == StateFeatureRules);
            SetActive(RulesHint, groupPage);
            for (var i = 0; i < PlayerRules.Length; i++)
                SetActive(PlayerRuleButtons[i], groupPage && PlayerRules[i].Group == group);

            var buildPage = admin && MenuState == StateBuildRules;
            SetActive(PlayerCooldownButton, buildPage);
            SetActive(AllowedListButton, buildPage);

            SetActive(CooldownHint, admin && MenuState == StatePlayerCooldown);
            SetActive(CooldownInput, admin && MenuState == StatePlayerCooldown);
            SetActive(CooldownApplyButton, admin && MenuState == StatePlayerCooldown);

            var allowed = admin && MenuState == StateAllowedList;
            SetActive(AllowedHint, allowed);
            for (var i = 0; i < MaxTemplateButtons; i++)
                SetActive(AllowedButtons[i], allowed && i < PlayerTemplates.Count);

            var editing = admin && MenuState == StateRuleEdit;
            var kind = PlayerRules[Mathf.Clamp(_editingRule, 0, PlayerRules.Length - 1)].Kind;
            SetActive(RuleEditHint, editing);
            var reaches = kind == RuleKind.Limit || kind == RuleKind.ChoiceLimit;
            SetActive(RuleEditToggle, editing && kind == RuleKind.Limit);
            SetActive(RuleEditInput, editing && reaches);
            SetActive(RuleEditApply, editing && reaches);
            foreach (var button in RuleChoiceButtons)
                SetActive(button, editing && (kind == RuleKind.Choice || kind == RuleKind.ChoiceLimit));
        }
    }
}
