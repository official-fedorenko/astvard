using System.Collections.Generic;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        internal static GameObject MarkPanel;

        internal static GameObject MarkGrid;

        /// <summary>
        /// Окно пометки сундука: все полки разом, по центру экрана.
        ///
        /// В самой панели полки шли столбиком по восемь штук со страницами, и это работало,
        /// пока их было восемь. Встроенных стало четырнадцать, а сколько своих заведёт админ
        /// на сайте, не знает никто - листать столбик, выбирая полку для каждого сундука в
        /// стене, оказалось дольше, чем сама пометка. Поэтому у пометки своё окно: широкое,
        /// сеткой, без страниц.
        ///
        /// Панель при этом остаётся на месте: в ней «Назад», и ею же окно и закрывается.
        /// </summary>
        internal const int MaxMarkButtons = 48;

        internal static readonly GameObject[] MarkButtons = new GameObject[MaxMarkButtons];

        /// <summary>Что стоит за кнопкой: номер полки или одна из пометок-флажков.</summary>
        private static readonly List<int> MarkChoicesShown = new List<int>();

        private static GridLayoutGroup _markGrid;

        private static bool _markOverflowSaid;

        private void CreateMarkWindow(GUIManager gui)
        {
            MarkPanel = gui.CreateWoodpanel(
                GUIManager.CustomGUIFront.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                600f, 400f, true);
            MarkPanel.SetActive(false);

            var layout = MarkPanel.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(24, 24, 24, 24);
            layout.spacing = 12f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            // Окно растёт под содержимое: полок может стать и двадцать, и сорок, а окно,
            // заданное числом, в этот день молча обрежет половину.
            var fitter = MarkPanel.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            gui.CreateText(
                "Чем пометить сундук", MarkPanel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                PanelFont(), 20, gui.ValheimOrange, true, Color.black,
                520f, 0f, false);

            MarkGrid = new GameObject("AstvardMarkGrid", typeof(RectTransform));
            MarkGrid.transform.SetParent(MarkPanel.transform, false);

            _markGrid = MarkGrid.AddComponent<GridLayoutGroup>();
            _markGrid.cellSize = new Vector2(170f, 38f);
            _markGrid.spacing = new Vector2(10f, 8f);
            _markGrid.childAlignment = TextAnchor.UpperCenter;
            _markGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            _markGrid.constraintCount = 3;

            var gridFitter = MarkGrid.AddComponent<ContentSizeFitter>();
            gridFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            gridFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            for (var i = 0; i < MaxMarkButtons; i++)
            {
                var slot = i;
                var go = gui.CreateButton(
                    "", MarkGrid.transform,
                    new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                    170f, 38f);
                // Имена полок пишет админ на сайте, до 24 знаков, - в клетку 170 на 38
                // такое влезает не всегда.
                FitLabel(go);
                Silence(go);
                go.GetComponent<Button>().onClick.AddListener(() => PickMark(slot));
                go.SetActive(false);
                MarkButtons[i] = go;
            }
        }

        private static void PickMark(int slot)
        {
            if (slot < 0 || slot >= MarkChoicesShown.Count) return;

            ArmSortMark(MarkChoicesShown[slot]);
        }

        /// <summary>Подпись пометки: полка по имени, флажки — своими словами.</summary>
        private static string MarkTitleOf(int mark)
        {
            if (mark == MarkPrivate) return "Личный";
            if (mark == MarkHold) return "Не для станций";
            if (mark == MarkNone) return "Снять пометку";

            return Sorting.Title(mark);
        }

        internal static void RefreshMarkWindow(bool open)
        {
            if (MarkPanel == null) return;

            if (!open)
            {
                RememberWindow(MarkPanel, _markSpot);
                MarkPanel.SetActive(false);
                return;
            }

            if (!MarkPanel.activeSelf) PlaceWindow(MarkPanel, _markSpot);

            // Только полки. Флажки - «Личный», «Не для станций», «Снять пометку» - стоят
            // в самой панели под «Назад»: они не полка, и в сетке полок их приходилось бы
            // искать глазами среди четырнадцати похожих кнопок.
            MarkChoicesShown.Clear();
            foreach (var category in MarkChoices()) MarkChoicesShown.Add(category);

            if (MarkChoicesShown.Count > MaxMarkButtons)
            {
                if (!_markOverflowSaid)
                {
                    _markOverflowSaid = true;
                    Log.LogWarning($"[AstvardServerMod] Mark window: {MarkChoicesShown.Count} marks, "
                                   + $"only {MaxMarkButtons} fit.");
                }

                MarkChoicesShown.RemoveRange(MaxMarkButtons,
                                             MarkChoicesShown.Count - MaxMarkButtons);
            }

            // Столбцов столько, чтобы окно росло **вширь, а не ввысь**: столбик из двадцати
            // кнопок - это ровно то, от чего уходили, а высота упирается в экран раньше
            // ширины. Ступенями, а не формулой: сетка, меняющая ширину на каждой новой
            // полке, выглядит как дрожание.
            if (_markGrid != null)
            {
                var count = MarkChoicesShown.Count;
                _markGrid.constraintCount = count > 36 ? 6
                    : count > 24 ? 5
                    : count > 12 ? 4
                    : 3;
            }

            for (var i = 0; i < MaxMarkButtons; i++)
            {
                var show = i < MarkChoicesShown.Count;
                if (show) SetLabel(MarkButtons[i], MarkTitleOf(MarkChoicesShown[i]));
                SetActive(MarkButtons[i], show);
            }

            MarkPanel.SetActive(true);
        }
    }
}
