using System.Collections.Generic;
using System.Linq;
using Jotunn.Managers;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        internal static GameObject PlayerBuildButton;

        internal static GameObject PlayerBuildHint;

        internal static GameObject PlayerSnapButton;

        internal static GameObject PlayerFloorButton;

        internal static GameObject PlayerFenceButton;

        internal static GameObject PlayerCopyButton;

        internal static GameObject PlayerSaveButton;

        internal static GameObject PlayerMyTemplatesButton;

        internal static GameObject PlayerServerTemplatesButton;

        internal static readonly GameObject[] PlayerTemplateButtons = new GameObject[MaxTemplateButtons];

        internal static GameObject TemplatePlayersButton;

        internal static GameObject SharedPlayersButton;

        internal static GameObject TemplateSubmitButton;

        /// <summary>
        /// The shared templates an admin has opened to players - what a player's own
        /// «Постройки» lists. Filled from the server's list, which says so per template.
        /// </summary>
        private static readonly List<SharedTemplate> PlayerTemplates = new List<SharedTemplate>();

        /// <summary>What we last asked the server for to build rather than to keep; see OnTemplateBody.</summary>
        private static string _awaitedBuild;

        // Asked once per connection: every change an admin makes after that is broadcast
        // to everyone connected, and a reconnect brings a new ZNet.
        private static ZNet _sharedListAskedOn;

        private void CreatePlayerBuildWidgets(GUIManager gui)
        {
            PlayerBuildButton = MakeButton(gui, "Постройки", () =>
            {
                MenuState = StatePlayerBuild;
                AskSharedList();
                RefreshMenu();
            });

            PlayerBuildHint = MakeText(gui, "");

            // A player's page holds what the admins opened, each behind its own rule; the
            // templates the server hands out are a page of their own under it.
            PlayerSnapButton = MakeButton(gui, "", () =>
            {
                IsSnapEnabled = !IsSnapEnabled;
                UpdateSnapButtonLabel();
                RefreshMenu();
            });

            PlayerFloorButton = MakeButton(gui, "Заполнить пол", StartFloorSeed);

            PlayerFenceButton = MakeButton(gui, "Обнести забором", () =>
            {
                MenuState = StateFence;
                RefreshMenu();
            });

            PlayerCopyButton = MakeButton(gui, "Копировать", () =>
            {
                _copyToFile = false;
                OpenCopyForm();
            });

            PlayerSaveButton = MakeButton(gui, "Сохранить постройку", () =>
            {
                _copyToFile = true;
                OpenCopyForm();
            });

            PlayerMyTemplatesButton = MakeButton(gui, "Мои шаблоны", () =>
            {
                MenuState = StateTemplates;
                ReloadTemplates();
                RefreshMenu();
            });

            PlayerServerTemplatesButton = MakeButton(gui, "", () =>
            {
                MenuState = StatePlayerTemplates;
                AskSharedList();
                RefreshMenu();
            });

            for (var slot = 0; slot < MaxTemplateButtons; slot++)
            {
                var index = slot;
                PlayerTemplateButtons[slot] = MakeButton(gui, "", () =>
                {
                    if (index < PlayerTemplates.Count) RequestPlayerBuild(PlayerTemplates[index]);
                });
            }
        }

        private void CreateTemplatePlayersWidget(GUIManager gui)
        {
            // A player's side of the same page: their build sent to the admins.
            TemplateSubmitButton = MakeButton(gui, "Поделиться", () => SubmitTemplate(_editingTemplate));

            TemplatePlayersButton = MakeButton(gui, "Разрешить игрокам", () =>
            {
                if (_editingTemplate == null) return;

                var allow = !IsForPlayers(_editingTemplate.Name);
                // Allowing sends the template as it is now, so players build this version,
                // and one never put on the server gets there on the way. The two arrive in
                // order: one connection, one reliable channel.
                if (allow) PushTemplate(_editingTemplate, false);
                SetForPlayers(_editingTemplate.Name, _editingTemplate.Category, _editingTemplate.Pieces, allow);
                RefreshMenu();
            });
        }

        private void CreateSharedPlayersWidget(GUIManager gui)
        {
            SharedPlayersButton = MakeButton(gui, "Разрешить игрокам", () =>
            {
                if (_selectedShared == null) return;

                SetForPlayers(_selectedShared.Name, _selectedShared.Category, _selectedShared.Pieces,
                              !_selectedShared.ForPlayers);
                RefreshMenu();
            });
        }

        private static SharedTemplate FindShared(string name)
        {
            foreach (var shared in SharedTemplates)
                if (shared.Name == name) return shared;
            return null;
        }

        private static bool IsForPlayers(string name)
        {
            var shared = FindShared(name);
            return shared != null && shared.ForPlayers;
        }

        /// <summary>
        /// Opens a server template to players, or closes it. The label turns at once rather
        /// than when the server's list comes back, since it is what says the press took;
        /// the list that follows puts right anything guessed wrong here.
        /// </summary>
        private static void SetForPlayers(string name, string category, int pieces, bool allow)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcTplPlayers, name, allow);

            var shared = FindShared(name);
            if (shared == null && allow)
            {
                shared = new SharedTemplate
                {
                    Name = name,
                    Category = category,
                    Author = LocalPlayerName(),
                    Pieces = pieces,
                };
                SharedTemplates.Add(shared);
            }
            if (shared != null) shared.ForPlayers = allow;
            RebuildPlayerTemplates();

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                allow ? $"Разрешено игрокам: {name}" : $"Запрещено игрокам: {name}");
        }

        private static void RebuildPlayerTemplates()
        {
            PlayerTemplates.Clear();
            PlayerTemplates.AddRange(SharedTemplates
                .Where(t => t.ForPlayers)
                .OrderBy(t => t.Category, System.StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(t => t.Name, System.StringComparer.CurrentCultureIgnoreCase));
        }

        /// <summary>Relabels a player's «Постройки», and the admin's two switches, from the last list.</summary>
        private static void RebuildPlayerBuildViews()
        {
            for (var i = 0; i < MaxTemplateButtons; i++)
                SetLabel(PlayerTemplateButtons[i], i < PlayerTemplates.Count
                    ? $"{PlayerTemplates[i].Name} ({PlayerTemplates[i].Pieces})"
                    : "");

            var hint = PlayerBuildHint != null
                ? PlayerBuildHint.GetComponentInChildren<UnityEngine.UI.Text>(true)
                : null;
            var wait = PlayerBuildWait;
            // What the pause holds back is what costs nothing: server templates, and a
            // floor or a fence the admins made free.
            var pause = (_playerBuildMinutes > 0
                            ? $"{NEWLINE}Бесплатные — не чаще{NEWLINE}раза в {_playerBuildMinutes} мин."
                            : "")
                        + (wait > 0 ? $"{NEWLINE}Следующая — через {FormatWait(wait)}." : "");
            if (hint != null)
                hint.text = MenuState == StatePlayerTemplates
                    ? (PlayerTemplates.Count == 0
                        ? "Админ пока ничего не разрешил."
                        : $"Разрешено админом. Нажми —{NEWLINE}появится проекция:{NEWLINE}"
                          + $"ЛКМ — поставить, Esc — отмена,{NEWLINE}"
                          + $"Q/E — поворот, Shift+Q/E — высота,{NEWLINE}"
                          + "P — закрепить, стрелки — сдвиг."
                          + (PlayerTemplates.Count > MaxTemplateButtons
                              ? $"{NEWLINE}Показаны первые {MaxTemplateButtons}."
                              : "")
                          + pause)
                    : "Постройки, открытые админом." + pause;

            SetLabel(PlayerSnapButton, IsSnapEnabled ? "Прилипание: вкл" : "Прилипание: выкл");
            SetLabel(PlayerServerTemplatesButton, $"Шаблоны сервера: {PlayerTemplates.Count}");

            SetLabel(TemplatePlayersButton, _editingTemplate != null && IsForPlayers(_editingTemplate.Name)
                ? "Запретить игрокам"
                : "Разрешить игрокам");
            SetLabel(SharedPlayersButton, _selectedShared != null && _selectedShared.ForPlayers
                ? "Запретить игрокам"
                : "Разрешить игрокам");
        }

        /// <summary>Whether a player's panel has anything to build at all.</summary>
        private static bool PlayerBuildsOpen
        {
            get
            {
                return PlayerTemplates.Count > 0 || RuleAllows("floor") || RuleAllows("fence")
                       || RuleAllows("copy");
            }
        }

        /// <summary>The pages a player builds from: their own, and the admins' tools opened to them.</summary>
        private static bool IsPlayerBuildPage(int state)
        {
            return state == StatePlayerBuild || state == StatePlayerTemplates || state == StateFence
                   || state == StateCopyForm || state == StateTemplates || state == StateTemplateList
                   || state == StateTemplateEdit;
        }

        private static void RefreshPlayerBuildVisibility(bool admin)
        {
            RebuildPlayerBuildViews();

            SetActive(PlayerBuildButton, !admin && MenuState == StateRoot && PlayerBuildsOpen);
            SetActive(PlayerBuildHint, !admin && (MenuState == StatePlayerBuild || MenuState == StatePlayerTemplates));

            var page = !admin && MenuState == StatePlayerBuild;
            SetActive(PlayerSnapButton, page && RuleAllows("snap"));
            SetActive(PlayerFloorButton, page && RuleAllows("floor"));
            SetActive(PlayerFenceButton, page && RuleAllows("fence"));
            SetActive(PlayerCopyButton, page && RuleAllows("copy"));
            SetActive(PlayerSaveButton, page && RuleAllows("copy"));
            SetActive(PlayerMyTemplatesButton, page && RuleAllows("copy"));
            SetActive(PlayerServerTemplatesButton, page && PlayerTemplates.Count > 0);

            for (var i = 0; i < MaxTemplateButtons; i++)
                SetActive(PlayerTemplateButtons[i], !admin && MenuState == StatePlayerTemplates
                                                    && i < PlayerTemplates.Count);

            SetActive(TemplatePlayersButton, admin && MenuState == StateTemplateEdit);
            SetActive(SharedPlayersButton, admin && MenuState == StateSharedItem);
        }

        /// <summary>
        /// A player's pick. The pieces come from the server, which hands them over only
        /// for a template an admin has opened to players; placement starts when they land.
        /// </summary>
        private static void RequestPlayerBuild(SharedTemplate template)
        {
            if (template == null || RefuseWhileBuilding()) return;

            _awaitedBuild = template.Name;
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcTplBuild, template.Name);
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, $"Загружаю «{template.Name}»…");
        }

        private static void StartPlayerBuild(string name, string body)
        {
            // Loading rewrites the clipboard, and a build going up is still reading it.
            if (RefuseWhileBuilding()) return;
            if (!LoadTemplate(body.Split('\n'), name)) return;

            StartPlacement($"шаблон «{name}»");
            _playerPlacement = true;
            _playerPlacementName = name;
            InventoryGui.instance?.Hide();
        }

        /// <summary>
        /// Asks for the shared list once per connection - with it come the pause between
        /// builds and the rules of «Рельеф» - so a player's panel knows what to offer at
        /// all; see _sharedListAskedOn.
        /// </summary>
        internal static void AskSharedListOnce()
        {
            if (ZNet.instance == null || ReferenceEquals(ZNet.instance, _sharedListAskedOn)) return;

            // What the last server allowed is not this one's word, and until the answer
            // comes nothing should be offered on its strength.
            _sharedListAskedOn = ZNet.instance;
            SharedTemplates.Clear();
            PlayerTemplates.Clear();
            _nextPlayerBuildAt = 0f;
            _playerRulesKnown = false;
            _myRunes = -1;
            AskSharedList();
        }

        /// <summary>
        /// A player's build asks every piece's place the way the hammer would; an admin's
        /// goes through, as WardsAllowPieces answers yes to admins itself.
        /// </summary>
        private static bool WardsAllowGhost()
        {
            if (IsAdminUnlocked || GhostRoot == null) return true;

            var origin = GhostRoot.transform.position;
            var rotation = GhostRoot.transform.rotation;
            var places = new List<Vector3>(Clipboard.Count);
            foreach (var entry in Clipboard) places.Add(origin + rotation * entry.LocalPos);
            return WardsAllowPieces("template", places);
        }
    }
}
