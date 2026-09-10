using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        internal static GameObject SettingsButton;

        internal static GameObject PlayerCooldownButton;

        internal static GameObject AllowedHint;

        internal static readonly GameObject[] AllowedButtons = new GameObject[MaxTemplateButtons];

        internal static GameObject CooldownHint;

        internal static GameObject CooldownInput;

        internal static GameObject CooldownApplyButton;

        // Where «Назад» on a server template's page leads: the list it was opened from,
        // which is the server's list or the one in «Настройки».
        private static int _sharedItemBack = StateSharedList;

        /// <summary>
        /// «Настройки» of the admin menu: one button per setting, the choice behind it, and
        /// the builds opened to players, each leading to its page where it can be closed.
        /// </summary>
        private void CreateSettingsWidgets(GUIManager gui)
        {
            SettingsButton = MakeButton(gui, "Настройки", () =>
            {
                MenuState = StateSettings;
                // The list and the pause as the server has them now, not as last heard.
                AskSharedList();
                RefreshMenu();
            });

            PlayerCooldownButton = MakeButton(gui, "", () =>
            {
                SetFieldText(CooldownInput, _playerBuildMinutes.ToString());
                MenuState = StatePlayerCooldown;
                RefreshMenu();
            });

            CreateTerrainRulesButton(gui);

            AllowedHint = MakeText(gui, "");

            for (var slot = 0; slot < MaxTemplateButtons; slot++)
            {
                var index = slot;
                AllowedButtons[slot] = MakeButton(gui, "", () =>
                {
                    if (index >= PlayerTemplates.Count) return;

                    _selectedShared = PlayerTemplates[index];
                    _sharedItemBack = StateSettings;
                    MenuState = StateSharedItem;
                    RefreshMenu();
                });
            }

            CooldownHint = MakeText(gui, "Как часто игрок может ставить\nразрешённые постройки,\nв минутах. 0 — без паузы.\nОтмена постройки паузу\nне сбрасывает.");

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
                MenuState = StateSettings;
                RefreshMenu();
            });

            CreateTerrainRuleWidgets(gui);
        }

        private static void RebuildSettingsViews()
        {
            SetLabel(PlayerCooldownButton, _playerBuildMinutes > 0
                ? $"Пауза построек: {_playerBuildMinutes} мин"
                : "Пауза построек: нет");

            var hint = AllowedHint != null ? AllowedHint.GetComponentInChildren<Text>(true) : null;
            if (hint != null)
                hint.text = PlayerTemplates.Count == 0
                    ? $"Игрокам пока ничего{NEWLINE}не разрешено."
                    : $"Разрешено игрокам: {PlayerTemplates.Count}"
                      + (PlayerTemplates.Count > MaxTemplateButtons
                          ? $"{NEWLINE}Показаны первые {MaxTemplateButtons}."
                          : "");

            for (var i = 0; i < MaxTemplateButtons; i++)
                SetLabel(AllowedButtons[i], i < PlayerTemplates.Count
                    ? $"{PlayerTemplates[i].Name} ({PlayerTemplates[i].Pieces})"
                    : "");
        }

        private static void RefreshSettingsVisibility(bool admin)
        {
            RebuildSettingsViews();

            SetActive(SettingsButton, admin && MenuState == StateAdmin);
            SetActive(PlayerCooldownButton, admin && MenuState == StateSettings);
            SetActive(AllowedHint, admin && MenuState == StateSettings);
            for (var i = 0; i < MaxTemplateButtons; i++)
                SetActive(AllowedButtons[i], admin && MenuState == StateSettings && i < PlayerTemplates.Count);

            SetActive(CooldownHint, admin && MenuState == StatePlayerCooldown);
            SetActive(CooldownInput, admin && MenuState == StatePlayerCooldown);
            SetActive(CooldownApplyButton, admin && MenuState == StatePlayerCooldown);

            RefreshTerrainRuleViews(admin);
        }
    }
}
