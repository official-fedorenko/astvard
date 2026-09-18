using System.Collections.Generic;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>
        /// Кого хозяин вписал в свою зону — и кого может вписать.
        ///
        /// By name and never by number: the panel hangs in front of everybody, and a
        /// platform id is the one thing about a person that is nobody else's business. The
        /// list to choose from is whoever is on right now, which also settles how the server
        /// is to find them - it looks the name up among its own sockets, so nothing has to
        /// be sent that could be made up.
        ///
        /// Being written in is leave to work in the zone, not leave to move it: the chests
        /// inside are somebody's, and so is the question of where the zone ends.
        /// </summary>
        internal static GameObject SortCrewButton;

        internal static GameObject SortCrewHint;

        internal static readonly GameObject[] SortCrewButtons = new GameObject[MaxTemplateButtons];

        internal static GameObject SortCrewAddButton;

        internal static GameObject SortInviteHint;

        internal static readonly GameObject[] SortInviteButtons = new GameObject[MaxTemplateButtons];

        private void CreateSortCrewWidgets(GUIManager gui)
        {
            SortCrewButton = MakeButton(gui, "", () =>
            {
                _itemOffset = 0;
                MenuState = StateSortCrew;
                RefreshMenu();
            });

            SortCrewHint = MakeText(gui, "");

            for (var slot = 0; slot < MaxTemplateButtons; slot++)
            {
                var index = slot;
                SortCrewButtons[slot] = MakeButton(gui, "", () =>
                {
                    var zone = CrewZone();
                    if (zone == null) return;

                    var at = MenuPaging.Clamp(_itemOffset, zone.Members.Count, MaxTemplateButtons) + index;
                    if (at >= zone.Members.Count) return;

                    SendZoneStrike(zone.Id, zone.Members[at]);
                });
            }

            SortCrewAddButton = MakeButton(gui, "Вписать игрока", () =>
            {
                _itemOffset = 0;
                MenuState = StateSortInvite;
                RefreshMenu();
            });

            SortInviteHint = MakeText(gui, "");

            for (var slot = 0; slot < MaxTemplateButtons; slot++)
            {
                var index = slot;
                SortInviteButtons[slot] = MakeButton(gui, "", () =>
                {
                    var zone = CrewZone();
                    var names = InviteChoices();
                    if (zone == null) return;

                    var at = MenuPaging.Clamp(_itemOffset, names.Count, MaxTemplateButtons) + index;
                    if (at >= names.Count) return;

                    SendZoneInvite(zone.Id, names[at]);
                    MenuState = StateSortCrew;
                    RefreshMenu();
                });
            }
        }

        /// <summary>The zone whose crew is on screen, or null when the page is not that page.</summary>
        internal static Sorting.Zone CrewZone()
        {
            var zones = SortingZones();
            if (_editingSortZone < 0 || _editingSortZone >= zones.Count) return null;

            return zones[_editingSortZone];
        }

        /// <summary>
        /// Who can be written in: everyone on right now, less myself and less the people who
        /// are in the zone already. Only those on, because the server finds a person by the
        /// name their socket is playing under - and because somebody who has never been here
        /// while you were is somebody you have no way to mean.
        /// </summary>
        internal static List<string> InviteChoices()
        {
            var names = new List<string>();
            var net = ZNet.instance;
            var zone = CrewZone();
            if (net == null || zone == null) return names;

            var mine = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "";

            var taken = new HashSet<string>();
            foreach (var member in zone.Members) taken.Add(ZonePersonName(member));
            taken.Add(ZonePersonName(zone.Owner));

            foreach (var who in net.GetPlayerList())
            {
                if (string.IsNullOrEmpty(who.m_name)) continue;
                if (who.m_name == mine || taken.Contains(who.m_name)) continue;
                if (names.Contains(who.m_name)) continue;

                names.Add(who.m_name);
            }

            return names;
        }

        /// <summary>Whether the crew pages make any sense here: my zone, kept by a server.</summary>
        internal static bool CrewPageOpen()
        {
            var zone = CrewZone();
            return ZonesOnServer && zone != null && IsMyZone(zone);
        }

        private static void RefreshSortCrew(bool allowed)
        {
            var zone = CrewZone();
            var members = zone != null ? zone.Members : new List<string>();

            SetLabel(SortCrewButton, $"Игроки: {members.Count}");
            SetActive(SortCrewButton, allowed && MenuState == StateSortZone && CrewPageOpen());

            var crew = allowed && MenuState == StateSortCrew && CrewPageOpen();
            var invite = allowed && MenuState == StateSortInvite && CrewPageOpen();

            var hint = SortCrewHint != null ? SortCrewHint.GetComponentInChildren<Text>(true) : null;
            if (hint != null && crew)
                hint.text = $"{SortZoneTitle(zone)}{NEWLINE}"
                            + (members.Count == 0
                                ? $"Пока в ней только ты.{NEWLINE}{NEWLINE}"
                                : $"Вписано: {members.Count}.{NEWLINE}Нажми на игрока — уберёшь.{NEWLINE}{NEWLINE}")
                            + $"Вписанный видит подписи{NEWLINE}над сундуками и разбирает{NEWLINE}"
                            + $"в этой зоне, но менять её{NEWLINE}не может.";

            var choices = invite ? InviteChoices() : new List<string>();
            var second = SortInviteHint != null ? SortInviteHint.GetComponentInChildren<Text>(true) : null;
            if (second != null && invite)
                second.text = choices.Count == 0
                    ? $"Вписать некого: в игре{NEWLINE}сейчас никого, кроме тех,{NEWLINE}кто уже в зоне."
                    : $"Кого вписать — из тех,{NEWLINE}кто сейчас в игре.";

            for (var i = 0; i < MaxTemplateButtons; i++)
            {
                var at = MenuPaging.Clamp(_itemOffset, members.Count, MaxTemplateButtons) + i;
                var shown = crew && at < members.Count;
                if (shown) SetLabel(SortCrewButtons[i], $"Убрать: {ZonePersonName(members[at])}");
                SetActive(SortCrewButtons[i], shown);

                var pick = MenuPaging.Clamp(_itemOffset, choices.Count, MaxTemplateButtons) + i;
                var offered = invite && pick < choices.Count;
                if (offered) SetLabel(SortInviteButtons[i], choices[pick]);
                SetActive(SortInviteButtons[i], offered);
            }

            SetActive(SortCrewHint, crew);
            SetActive(SortCrewAddButton, crew);
            SetActive(SortInviteHint, invite);
        }
    }
}
