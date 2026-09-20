using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        internal static GameObject SortInfoPanel;

        internal static GameObject SortInfoText;

        private static bool _sortInfoShown;

        /// <summary>
        /// «Сортировка» → «Информация»: всё, что мод сейчас делает, одним окном.
        ///
        /// Состояние копилось в подсказке самой панели, а панель узкая - 260 точек в
        /// ширину, - и каждая новая строка выдавливала предыдущую или растягивала столбец
        /// на пол-экрана. Сказать при этом есть что: разбор, огород, варка, станции зоны,
        /// и у каждого своя причина, по которой он сейчас ничего не делает. Поэтому текст
        /// переехал в своё окно по центру, а на странице осталась кнопка.
        ///
        /// Окно то же по устройству, что и у пометки сундука: растёт под содержимое, его
        /// можно таскать, и оно гаснет вместе с панелью.
        /// </summary>
        private void CreateSortInfoWindow(GUIManager gui)
        {
            SortInfoPanel = gui.CreateWoodpanel(
                GUIManager.CustomGUIFront.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                560f, 400f, true);
            SortInfoPanel.SetActive(false);

            var layout = SortInfoPanel.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(24, 24, 24, 24);
            layout.spacing = 12f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            var fitter = SortInfoPanel.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            gui.CreateText(
                "Что происходит", SortInfoPanel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                PanelFont(), 20, gui.ValheimOrange, true, Color.black,
                500f, 0f, false);

            SortInfoText = gui.CreateText(
                "", SortInfoPanel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                PanelFont(), 16, gui.ValheimBeige, true, Color.black,
                500f, 0f, false);

            var le = SortInfoText.AddComponent<LayoutElement>();
            le.preferredWidth = 500f;
        }

        internal static void ToggleSortInfo()
        {
            _sortInfoShown = !_sortInfoShown;
            RefreshMenu();
        }

        internal static bool SortInfoShown
        {
            get { return _sortInfoShown; }
        }

        /// <summary>
        /// Всё, что мод знает про эту зону, одним текстом.
        ///
        /// Порядок не случаен: сверху то, что меняется каждую секунду и объясняет «почему
        /// ничего не происходит», ниже — устройство базы, которое человек и так помнит, и
        /// в самом низу то, что приходит снаружи. Пустые разделы не печатаются: «ульев 0»
        /// и «предел никого не держит» в списке из десяти строк читать некому.
        /// </summary>
        private static string SortInfoLines()
        {
            var said = new System.Text.StringBuilder();

            var zone = StandingZone();
            if (zone != null)
            {
                var shape = zone.Square ? $"квадрат {zone.Radius:0} м" : $"круг {zone.Radius:0} м";
                var name = string.IsNullOrEmpty(zone.Name) ? shape : $"«{zone.Name}», {shape}";

                said.Append("Зона: ").Append(name).Append('.');

                // Чья она — вопрос не праздный: во вписанной зоне нельзя менять ни размер,
                // ни форму, и «кнопки не нажимаются» объясняется только этим.
                if (ZonesOnServer)
                    said.Append(NEWLINE)
                        .Append(IsMyZone(zone) ? "Она твоя." : "Хозяин другой — ты вписан.");
            }

            Line(said, SortingWhy());
            Line(said, GardenWhy());
            Line(said, ZoneLimitsRu);
            Line(said, ZoneHasRu);
            Line(said, ZoneFiresRu);

            if (TamesSeen > 0)
                Line(said, $"Зверей рядом: {TamesSeen}, голодных {TamesHungry}.");

            if (ChestLabelsShown + ChestLabelsMissed > 0)
                Line(said, ChestLabelsMissed > 0
                    ? $"Подписей: {ChestLabelsShown} из {ChestLabelsShown + ChestLabelsMissed}."
                    : $"Подписей: {ChestLabelsShown}.");

            Line(said, $"Полок: {Sorting.Count}, с сайта расписано {Sorting.ChosenCount} предметов.");

            Line(said, !BrewEnabled
                ? "Варка выключена."
                : string.IsNullOrEmpty(BrewWhy)
                    ? "Варка идёт своим чередом."
                    : $"Варка: {BrewWhy}.");

            return said.ToString();
        }

        private static void Line(System.Text.StringBuilder said, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            if (said.Length > 0) said.Append(NEWLINE).Append(NEWLINE);
            said.Append(text);
        }

        internal static void RefreshSortInfo(bool open)
        {
            if (SortInfoPanel == null) return;

            if (!open)
            {
                SortInfoPanel.SetActive(false);
                return;
            }

            var text = SortInfoText != null
                ? SortInfoText.GetComponentInChildren<Text>(true)
                : null;

            if (text != null) text.text = SortInfoLines();

            SortInfoPanel.SetActive(true);
        }
    }
}
