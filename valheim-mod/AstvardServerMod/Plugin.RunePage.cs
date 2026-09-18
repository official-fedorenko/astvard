using System.Collections.Generic;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        internal static GameObject RunesButton;

        internal static GameObject RosterHint;

        internal static GameObject RosterSearchInput;

        internal static GameObject RosterSearchButton;

        internal static readonly GameObject[] RosterButtons = new GameObject[MaxTemplateButtons];

        internal static GameObject GrantHint;

        internal static GameObject GrantInput;

        internal static GameObject GrantGiveButton;

        internal static GameObject GrantTakeButton;

        /// <summary>Whose page is open: the platform id, which is what a grant travels by.</summary>
        private static string _grantTarget = "";

        private static int _shownRoster;

        /// <summary>
        /// «Руны игроков»: the ledger the admin hands from, and one player's page behind it.
        ///
        /// The list is everybody - those on the server now, those who have ever played, and,
        /// once the site tells the server about them, those who only registered. That is the
        /// whole point of it being one list: the person an admin is looking for is usually
        /// standing next to them, but the one they promised runes to yesterday may be
        /// asleep, and both have to be reachable from the same page.
        /// </summary>
        private void CreateRunePageWidgets(GUIManager gui)
        {
            RunesButton = MakeButton(gui, "Руны игроков", OpenRoster);

            RosterHint = MakeText(gui, "");

            RosterSearchInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.Standard, "ник или номер", 16, 200f, 32f);
            AddFixedSize(RosterSearchInput, 200f, 32f);

            // Enter is what a person presses after typing a name, so it has to work; the
            // button beside it is for the hand that never leaves the mouse.
            var search = RosterSearchInput != null
                ? RosterSearchInput.GetComponentInChildren<InputField>(true)
                : null;
            if (search != null) search.onEndEdit.AddListener(_ => ApplyRosterSearch());

            RosterSearchButton = MakeButton(gui, "Найти", ApplyRosterSearch);

            for (var slot = 0; slot < MaxTemplateButtons; slot++)
            {
                var index = slot;
                RosterButtons[slot] = MakeButton(gui, "", () =>
                {
                    var shown = RosterShown();
                    var at = MenuPaging.Clamp(_itemOffset, shown.Count, MaxTemplateButtons) + index;
                    if (at >= shown.Count) return;

                    _grantTarget = shown[at].Id;
                    SetFieldText(GrantInput, "1");
                    MenuState = StateRuneGrant;
                    RefreshMenu();
                });
            }

            GrantHint = MakeText(gui, "");

            GrantInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.IntegerNumber, "сколько рун", 16, 160f, 32f);
            AddFixedSize(GrantInput, 160f, 32f);

            GrantGiveButton = MakeButton(gui, "Выдать", () => Grant(1));
            GrantTakeButton = MakeButton(gui, "Забрать", () => Grant(-1));
        }

        private static void OpenRoster()
        {
            _itemOffset = 0;
            MenuState = StateRunes;
            // The numbers as the server has them now: runes are earned while the panel is
            // shut, and an admin paying from yesterday's list would pay the wrong number.
            RequestRoster();
            RefreshMenu();
        }

        private static void ApplyRosterSearch()
        {
            RosterQuery = FieldText(RosterSearchInput, "");
            _itemOffset = 0;
            RefreshMenu();
        }

        /// <summary>The ledger as this page shows it: what was searched for, in list order.</summary>
        private static List<Roster.Row> RosterShown()
        {
            return Roster.Search(RosterRows, RosterQuery);
        }

        private static Roster.Row GrantRow()
        {
            foreach (var row in RosterRows)
                if (row.Id == _grantTarget) return row;

            // Somebody may have been paid and the list not caught up; the page still knows
            // who it is about, which is enough to go on paying them.
            return new Roster.Row { Id = _grantTarget, Site = "", Character = "" };
        }

        private static void Grant(int sign)
        {
            var amount = Mathf.RoundToInt(ParseField(GrantInput, 1));
            amount = Mathf.Clamp(Mathf.Abs(amount), 1, Roster.MaxGrant) * sign;

            GrantRunes(_grantTarget, amount);

            var row = GrantRow();
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, sign > 0
                ? $"{Roster.Display(row)}: +{amount}"
                : $"{Roster.Display(row)}: {amount}");
        }

        private static void RebuildRunePageViews()
        {
            var shown = RosterShown();

            if (MenuState == StateRunes)
                _itemOffset = MenuPaging.Clamp(_itemOffset, shown.Count, MaxTemplateButtons);
            _shownRoster = MenuState == StateRunes
                ? MenuPaging.Shown(_itemOffset, shown.Count, MaxTemplateButtons)
                : 0;

            var hint = RosterHint != null ? RosterHint.GetComponentInChildren<Text>(true) : null;
            if (hint != null)
            {
                if (!RosterAsked)
                    hint.text = $"Спрашиваю сервер…";
                else if (RosterRows.Count == 0)
                    hint.text = $"Ведомость пуста: на сервере{NEWLINE}ещё никто не играл.";
                else if (shown.Count == 0)
                    hint.text = $"По «{RosterQuery}» никого.{NEWLINE}Очисти поле и нажми «Найти»,{NEWLINE}чтобы увидеть всех.";
                else
                    hint.text = $"Кому сколько рун. Всего: {RosterRows.Count}."
                                + (string.IsNullOrEmpty(RosterQuery) ? "" : $"{NEWLINE}Найдено: {shown.Count}.")
                                + WindowNote(_itemOffset, shown.Count);
            }

            for (var i = 0; i < MaxTemplateButtons; i++)
            {
                if (i >= _shownRoster)
                {
                    SetLabel(RosterButtons[i], "");
                    continue;
                }

                var row = shown[_itemOffset + i];
                SetLabel(RosterButtons[i], row.Online
                    ? $"{Roster.Display(row)} — {row.Balance}, в игре"
                    : $"{Roster.Display(row)} — {row.Balance}");
            }

            var grantHint = GrantHint != null ? GrantHint.GetComponentInChildren<Text>(true) : null;
            if (grantHint != null)
            {
                var row = GrantRow();
                var where = row.Online ? "В игре сейчас." : row.Played ? "Заходил раньше." : "На сервере ещё не был.";

                // Both names when they differ: the nickname is who the admin is looking for,
                // the character is who the other players see standing there.
                var also = !string.IsNullOrEmpty(row.Site) && !string.IsNullOrEmpty(row.Character)
                           && row.Site != row.Character
                    ? $"{NEWLINE}В игре: {row.Character}"
                    : "";

                grantHint.text = $"{Roster.Display(row)}{also}{NEWLINE}Руны: {row.Balance}. {where}"
                                 + $"{NEWLINE}Сколько выдать или забрать:";
            }
        }

        private static void RefreshRunePageVisibility(bool admin)
        {
            RebuildRunePageViews();

            SetActive(RunesButton, admin && MenuState == StateAdmin);

            var roster = admin && MenuState == StateRunes;
            SetActive(RosterHint, roster);
            SetActive(RosterSearchInput, roster);
            SetActive(RosterSearchButton, roster);
            for (var i = 0; i < MaxTemplateButtons; i++)
                SetActive(RosterButtons[i], roster && i < _shownRoster);

            var granting = admin && MenuState == StateRuneGrant;
            SetActive(GrantHint, granting);
            SetActive(GrantInput, granting);
            SetActive(GrantGiveButton, granting);
            SetActive(GrantTakeButton, granting);
        }
    }
}
