using System.Globalization;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>
        /// Куда игрок утащил окна — чтобы это пережило перезапуск игры.
        ///
        /// Внутри сессии перетаскивание держалось само: окна прячутся `SetActive(false)`,
        /// а это объект не двигает. Терялось оно при следующем запуске - три окна
        /// создавались заново по центру, и раскладку приходилось собирать каждый вечер.
        ///
        /// Хранится у **клиента**, в его же конфиге: это не правило сервера и не общее
        /// добро, а привычка одного человека к своему экрану.
        /// </summary>
        private static BepInEx.Configuration.ConfigEntry<string> _markSpot;

        private static BepInEx.Configuration.ConfigEntry<string> _infoSpot;

        private static BepInEx.Configuration.ConfigEntry<string> _aboutSpot;

        internal static void BindWindowSpots(BepInEx.Configuration.ConfigFile config)
        {
            _markSpot = config.Bind("Окна", "Mark", "",
                "Куда утащено окно «Пометить сундук», в точках от середины экрана. "
                + "Пусто — по центру. Пишется само, когда окно закрывается.");

            _infoSpot = config.Bind("Окна", "Info", "",
                "Куда утащено окно «Информация». Пусто — по центру.");

            _aboutSpot = config.Bind("Окна", "About", "",
                "Куда утащено окно «Ознакомиться». Пусто — по центру.");
        }

        /// <summary>
        /// Сколько точек середина окна обязана оставаться на экране.
        ///
        /// Утащенное на край широкого монитора окно на узком оказалось бы за экраном, и
        /// достать его было бы нечем - ни мышью, ни кнопкой. Зажимаем **середину**, а не
        /// края: так окно всегда можно ухватить и вернуть, каким бы большим оно ни было.
        /// Требовать, чтобы оно влезало целиком, нельзя - размер у этих окон считается
        /// вёрсткой и в момент первого показа ещё нулевой.
        /// </summary>
        private const float WindowReach = 60f;

        /// <summary>Ставит окно туда, где его оставили. Зовётся при показе, а не при создании.</summary>
        private static void PlaceWindow(GameObject panel, BepInEx.Configuration.ConfigEntry<string> spot)
        {
            if (panel == null || spot == null || string.IsNullOrEmpty(spot.Value)) return;

            var parts = spot.Value.Split(';');
            if (parts.Length != 2) return;

            float x, y;
            if (!float.TryParse(parts[0], NumberStyles.Float, Invariant, out x)) return;
            if (!float.TryParse(parts[1], NumberStyles.Float, Invariant, out y)) return;

            var rect = panel.GetComponent<RectTransform>();
            if (rect == null) return;

            rect.anchoredPosition = OnScreen(rect, new Vector2(x, y));
        }

        /// <summary>Запоминает, где окно стоит. Зовётся, когда его прячут.</summary>
        private static void RememberWindow(GameObject panel, BepInEx.Configuration.ConfigEntry<string> spot)
        {
            if (panel == null || spot == null || !panel.activeSelf) return;

            var rect = panel.GetComponent<RectTransform>();
            if (rect == null) return;

            var at = rect.anchoredPosition;
            var said = at.x.ToString("F0", Invariant) + ";" + at.y.ToString("F0", Invariant);

            // Только при изменении: BepInEx по умолчанию пишет файл на каждую запись
            // значения, а прячутся окна по нескольку раз за минуту.
            if (spot.Value != said) spot.Value = said;
        }

        private static Vector2 OnScreen(RectTransform rect, Vector2 at)
        {
            var parent = rect.parent as RectTransform;
            var area = parent != null && parent.rect.size.x > 1f
                ? parent.rect.size
                : new Vector2(Screen.width, Screen.height);

            var limit = area * 0.5f;
            return new Vector2(
                Mathf.Clamp(at.x, -limit.x + WindowReach, limit.x - WindowReach),
                Mathf.Clamp(at.y, -limit.y + WindowReach, limit.y - WindowReach));
        }

        /// <summary>
        /// Все окна мода разом — для закрытия инвентаря.
        ///
        /// Одним местом, а не тремя строками в патче: прятать окно и забывать, где оно
        /// стояло, надо вместе, иначе следующее окно однажды заведут и вспомнить о нём
        /// забудут. Именно так и вышло с уборкой за ушедшим игроком.
        /// </summary>
        internal static void HideModWindows()
        {
            RememberWindow(MarkPanel, _markSpot);
            RememberWindow(SortInfoPanel, _infoSpot);
            RememberWindow(AboutPanel, _aboutSpot);

            if (MarkPanel != null) MarkPanel.SetActive(false);
            if (SortInfoPanel != null) SortInfoPanel.SetActive(false);
            if (AboutPanel != null) AboutPanel.SetActive(false);
        }
    }
}
