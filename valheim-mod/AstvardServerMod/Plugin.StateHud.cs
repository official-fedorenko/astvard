using System.Collections.Generic;
using Jotunn.Managers;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>
        /// Что сейчас включено — строкой под панелью быстрых предметов.
        ///
        /// Every one of these is invisible until it surprises somebody: god mode noticed
        /// when a troll fails to hurt, flight noticed on the way down after it was switched
        /// off, debug mode noticed when Z sends the player into the sky in front of the
        /// people they were building for. The panel is shut most of the time, and the one
        /// that matters most - flight - has no button in it at all, so a line that is always
        /// there is the only honest answer.
        ///
        /// Nothing is shown when nothing is on: a line that is always present stops being
        /// read within a day.
        /// </summary>
        private static UnityEngine.UI.Text _stateHud;

        private static string _stateShown;

        private static float _stateTickAt;

        // Often enough that pressing a switch and looking down answers at once, rarely
        // enough that it is not doing this every frame for a line that rarely changes.
        private const float StateTick = 0.25f;

        private static readonly Color StateColour = new Color(1f, 0.72f, 0.3f);

        /// <summary>What is on, in the words the panel uses for it, or nothing at all.</summary>
        internal static string StateLine()
        {
            var player = Player.m_localPlayer;
            if (player == null) return "";

            var on = new List<string>();

            // Admin mode first: it is the one the rest hang off, and the one somebody walks
            // away in without meaning to.
            if (IsAdminUnlocked) on.Add("админка");
            if (player.InGodMode()) on.Add("бессмертие");
            if (player.IsDebugFlying()) on.Add("полёт");
            if (Player.m_debugMode) on.Add("отладка");
            if (player.NoCostCheat()) on.Add("стройка даром");

            return on.Count == 0 ? "" : "Включено: " + string.Join(" · ", on.ToArray());
        }

        internal static void TickStateHud()
        {
            if (GUIManager.IsHeadless()) return;

            if (Time.realtimeSinceStartup < _stateTickAt) return;
            _stateTickAt = Time.realtimeSinceStartup + StateTick;

            var text = StateLine();

            if (_stateHud == null)
            {
                // Nothing on and nothing made: the usual case, and it costs nothing.
                if (text.Length == 0) return;

                _stateHud = MakeStateHud();
                if (_stateHud == null) return;
                _stateShown = null;
            }

            if (text == _stateShown) return;

            _stateShown = text;
            _stateHud.text = text;
            if (_stateHud.gameObject.activeSelf != (text.Length > 0))
                _stateHud.gameObject.SetActive(text.Length > 0);
        }

        /// <summary>
        /// Made under the runes, on the same bar, so the two lines move together at any UI
        /// scale. Remade when the main menu takes the HUD away with it, like the rune line.
        /// </summary>
        private static UnityEngine.UI.Text MakeStateHud()
        {
            var gui = GUIManager.Instance;
            if (gui == null || Hud.instance == null) return null;

            var bar = Hud.instance.GetComponentInChildren<HotkeyBar>(true);
            if (bar == null) return null;

            var slot = bar.m_elementPrefab != null ? bar.m_elementPrefab.GetComponent<RectTransform>() : null;
            var size = slot != null ? slot.rect.size : new Vector2(64f, 64f);
            var pivot = slot != null ? slot.pivot : new Vector2(0.5f, 0.5f);

            var go = gui.CreateText("", bar.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero,
                gui.AveriaSerifBold, 16, StateColour, true, Color.black,
                420f, 26f, false);

            var rect = go.GetComponent<RectTransform>();
            rect.pivot = new Vector2(0f, 1f);

            // One line below the runes, which sit six pixels under the bar.
            rect.localPosition = new Vector3(-pivot.x * size.x, -pivot.y * size.y - 30f, 0f);

            var text = go.GetComponent<UnityEngine.UI.Text>();
            text.alignment = TextAnchor.UpperLeft;
            text.raycastTarget = false;
            return text;
        }

        /// <summary>«вкл» or «выкл» on the switch itself, for the ones that have a button.</summary>
        private static void RefreshCheatLabels()
        {
            var player = Player.m_localPlayer;

            SetLabel(GodButton, player != null && player.InGodMode() ? "God: вкл" : "God: выкл");
            SetLabel(DebugModeButton, Player.m_debugMode ? "Debugmode: вкл" : "Debugmode: выкл");
        }
    }
}
