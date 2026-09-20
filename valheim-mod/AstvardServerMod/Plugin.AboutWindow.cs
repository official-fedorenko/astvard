using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        internal static GameObject AboutPanel;

        internal static GameObject AboutText;

        /// <summary>
        /// «Ознакомиться» — своим окном по центру, а не строчками в панели.
        ///
        /// Панель узкая, 260 точек, и висит сбоку: пока на этой странице был один абзац
        /// про сервер, места хватало. Потом туда легли мир и сид, потом часы мира с
        /// пятью строками, и страница переросла экран - а читают её именно тогда, когда
        /// хотят разглядеть, а не пролистать.
        ///
        /// Устроено ровно как окно «Информация» у сортировки: растёт под содержимое,
        /// стоит по центру и гаснет вместе с панелью (`Patches.cs`). Второй такой же
        /// код - плата за то, что окна независимы: закрыв одно, второе не трогаем.
        /// </summary>
        private void CreateAboutWindow(GUIManager gui)
        {
            AboutPanel = gui.CreateWoodpanel(
                GUIManager.CustomGUIFront.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                520f, 400f, true);
            AboutPanel.SetActive(false);

            var layout = AboutPanel.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(24, 24, 24, 24);
            layout.spacing = 12f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            var fitter = AboutPanel.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            gui.CreateText(
                "Ознакомиться", AboutPanel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                PanelFont(), 20, gui.ValheimOrange, true, Color.black,
                460f, 0f, false);

            AboutText = gui.CreateText(
                "", AboutPanel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                PanelFont(), 16, gui.ValheimBeige, true, Color.black,
                460f, 0f, false);

            var le = AboutText.AddComponent<LayoutElement>();
            le.preferredWidth = 460f;
        }

        internal static void RefreshAbout(bool open)
        {
            if (AboutPanel == null) return;

            if (!open)
            {
                RememberWindow(AboutPanel, _aboutSpot);
                AboutPanel.SetActive(false);
                return;
            }

            if (!AboutPanel.activeSelf) PlaceWindow(AboutPanel, _aboutSpot);

            // Добираться до текста надо через скрытый объект: кнопка переключает флаг
            // раньше, чем окно включится, и без `true` поиск возвращал бы null, а правка
            // молча терялась. Эту яму окно уже проходило в панели.
            var text = AboutText != null ? AboutText.GetComponentInChildren<Text>(true) : null;
            if (text != null) text.text = AboutLines();

            AboutPanel.SetActive(true);
        }
    }
}
