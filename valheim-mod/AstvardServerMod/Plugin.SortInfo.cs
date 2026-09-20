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

        /// <summary>Строка про станции зоны по-русски — та же, что уходит в лог.</summary>
        internal static string ZoneStationsRu = "";

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

            if (text != null)
            {
                var stations = string.IsNullOrEmpty(ZoneStationsRu)
                    ? "Станции: в зоне их нет."
                    : ZoneStationsRu;

                var brewing = !BrewEnabled
                    ? "Варка: выключена."
                    : string.IsNullOrEmpty(BrewWhy)
                        ? "Варка: идёт своим чередом."
                        : $"Варка: {BrewWhy}.";

                text.text = SortingWhy()
                            + $"{NEWLINE}{NEWLINE}" + GardenWhy()
                            + $"{NEWLINE}{NEWLINE}" + stations
                            + $"{NEWLINE}{NEWLINE}" + brewing;
            }

            SortInfoPanel.SetActive(true);
        }
    }
}
