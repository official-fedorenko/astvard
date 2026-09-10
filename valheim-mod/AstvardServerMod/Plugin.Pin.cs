using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        // ---------------- pinning a projection ----------------
        //
        // Every projection answers the same keys - the rule in valheim-mod/CLAUDE.md, and
        // the one a new tool with a projection has to follow: a click builds, Esc takes it
        // away, P pins it where it stands so the player can walk round it and look, and
        // the arrows nudge a pinned one along the ground. P again hands it back.
        //
        // P is the key under «З», for «закрепить». Nothing of the game's reads it, while
        // Z and B, the obvious ones, fly and switch off build costs in debug mode - the
        // mode an admin with a projection up is likely to be in - and G opens the radial
        // menu.

        internal const KeyCode PinKey = KeyCode.P;

        // An arrow press moves a pinned projection a metre; with Shift, a quarter.
        private const float PinNudge = 1f;

        private const float PinFineNudge = 0.25f;

        private const string PinnedMessage = "Закреплено: стрелки — сдвиг, P — открепить";

        private const string UnpinnedMessage = "Проекция снова за тобой";

        /// <summary>
        /// This frame's arrow presses, as a step along the ground the way the player sees
        /// it: up is away from the camera, right is to its right. False when no arrow was
        /// pressed. The step is flat - what it lands on is each tool's own business.
        /// </summary>
        private static bool PinNudgeThisFrame(out Vector3 step)
        {
            step = Vector3.zero;

            var forward = (Input.GetKeyDown(KeyCode.UpArrow) ? 1f : 0f)
                          - (Input.GetKeyDown(KeyCode.DownArrow) ? 1f : 0f);
            var right = (Input.GetKeyDown(KeyCode.RightArrow) ? 1f : 0f)
                        - (Input.GetKeyDown(KeyCode.LeftArrow) ? 1f : 0f);
            if (forward == 0f && right == 0f) return false;

            var view = GameCamera.instance != null
                ? GameCamera.instance.transform
                : Player.m_localPlayer != null ? Player.m_localPlayer.transform : null;
            if (view == null) return false;

            var ahead = view.forward;
            ahead.y = 0f;
            if (ahead.sqrMagnitude < 1e-4f) return false;
            ahead.Normalize();
            var aside = new Vector3(ahead.z, 0f, -ahead.x);

            var size = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)
                ? PinFineNudge
                : PinNudge;
            step = (ahead * forward + aside * right) * size;
            return true;
        }

        private static void SayPinned(bool pinned)
        {
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, pinned ? PinnedMessage : UnpinnedMessage);
        }
    }
}
