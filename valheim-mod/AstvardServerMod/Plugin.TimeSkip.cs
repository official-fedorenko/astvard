using Jotunn.Managers;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        internal static GameObject TimeSkipButton;

        internal static GameObject TimeSkipHint;

        internal static GameObject TimeSkipInput;

        internal static GameObject TimeSkipApply;

        private const string RpcTimeSkip = "AstvardTimeSkip";

        /// <summary>Десять игровых суток в обе стороны — довольно для любой проверки.</summary>
        private const float MaxSkipHours = 240f;

        /// <summary>
        /// «Читы» → «Сдвинуть время на».
        ///
        /// Считается в **игровых часах**, а не в секундах: игровые сутки короче настоящих
        /// (`EnvMan.m_dayLengthSec`, у игры по умолчанию 1200 с), и «сдвинь на 3600» в
        /// голове ни во что не превращается, а «на два часа» превращается сразу.
        ///
        /// Двигает время **сервер**, а не клиент: мировое время принадлежит ему, и
        /// записанное у себя вернулось бы назад с ближайшей синхронизацией. Отсюда и RPC с
        /// проверкой `ServerAllows` - то же, чем закрыты все остальные админские команды.
        /// Игра делает это ровно так же: её собственная `skiptime` объявлена
        /// `onlyServer: true, remoteCommand: true`.
        ///
        /// **Это время всего мира**, а не одной грядки: вместе с ростом уедут день и ночь,
        /// брага, улей и счёт дней. Для проверки огорода - то, что нужно; на живой базе
        /// стоит помнить, что соседу тоже настанет ночь.
        /// </summary>
        private void CreateTimeSkipWidgets(GUIManager gui)
        {
            TimeSkipButton = MakeButton(gui, "Сдвинуть время", () =>
            {
                SetFieldText(TimeSkipInput, "2");
                MenuState = StateTimeSkip;
                RefreshMenu();
            });

            TimeSkipHint = MakeText(gui, "");

            TimeSkipInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                UnityEngine.UI.InputField.ContentType.DecimalNumber, "часов, напр. 2", 16, 160f, 32f);
            AddFixedSize(TimeSkipInput, 160f, 32f);

            TimeSkipApply = MakeButton(gui, "Сдвинуть", () =>
            {
                var hours = Mathf.Clamp(ParseField(TimeSkipInput, 2f), -MaxSkipHours, MaxSkipHours);
                ZRoutedRpc.instance?.InvokeRoutedRPC(RpcTimeSkip, hours);

                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    $"Прошу сервер сдвинуть время на {hours:0.##} ч");

                MenuState = StateCheats;
                RefreshMenu();
            });
        }

        internal static void RegisterTimeSkipRpcs(ZRoutedRpc rpc)
        {
            rpc.Register<float>(RpcTimeSkip, OnTimeSkip);
        }

        /// <summary>Сколько настоящих секунд в игровом часе — по длине суток самой игры.</summary>
        private static float SecondsPerGameHour()
        {
            var env = EnvMan.instance;
            var day = env != null && env.m_dayLengthSec > 0 ? env.m_dayLengthSec : 1200L;

            return day / 24f;
        }

        private static void OnTimeSkip(long sender, float hours)
        {
            if (!ServerAllows(sender)) return;

            var net = ZNet.instance;
            if (net == null) return;

            hours = Mathf.Clamp(hours, -MaxSkipHours, MaxSkipHours);

            var was = net.GetTimeSeconds();
            var now = was + hours * SecondsPerGameHour();

            // Вниз - только до нуля: отрицательное мировое время игра нигде не ждёт, а
            // выяснять, что она с ним сделает, дешевле не на живом мире.
            if (now < 0d) now = 0d;

            net.SetNetTime(now);

            Log.LogInfo($"[AstvardServerMod] Time: moved {hours:0.##} game hours "
                        + $"({now - was:0} s), now {now:0}, asked by {SenderName(sender)}.");
        }

        internal static void RefreshTimeSkip(bool admin)
        {
            SetActive(TimeSkipButton, admin && MenuState == StateCheats);

            var page = admin && MenuState == StateTimeSkip;
            SetActive(TimeSkipHint, page);
            SetActive(TimeSkipInput, page);
            SetActive(TimeSkipApply, page);

            if (!page) return;

            var hint = TimeSkipHint != null
                ? TimeSkipHint.GetComponentInChildren<UnityEngine.UI.Text>(true)
                : null;
            if (hint == null) return;

            hint.text = $"На сколько игровых часов{NEWLINE}сдвинуть время мира.{NEWLINE}"
                        + $"Игровой час — это{NEWLINE}{SecondsPerGameHour():0} с настоящих.{NEWLINE}{NEWLINE}"
                        + $"Двигает весь мир: рост{NEWLINE}грядок, брагу, улей,{NEWLINE}"
                        + $"день и ночь.{NEWLINE}Отрицательное — назад.";
        }
    }
}
