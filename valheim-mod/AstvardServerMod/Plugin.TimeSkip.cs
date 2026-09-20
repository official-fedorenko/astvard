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
            // Разведка камней живёт рядом со сдвигом времени: обе спрашивают сервер и
            // обе - про мир целиком, а не про то, что под ногами.
            RuneStonesButton = MakeButton(gui, "Камни с надписями", AskRunes);

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

        /// <summary>
        /// Который час в мире - для «Ознакомиться».
        ///
        /// Часов игра не знает вовсе: у неё есть доля прожитых суток 0..1, а часы здесь
        /// наши, двадцать четыре на сутки, - те же, которыми двигает время кнопка выше.
        ///
        /// Сроки считаются по **прожитой доле**, а не по часам (`Geometry`): час дня
        /// длиннее часа ночи в 2⅓ раза, и «до заката 3:28» в настоящих минутах значит
        /// совсем не то, что те же 3:28 до рассвета.
        ///
        /// Что сейчас - утро, день или ночь - спрашивается у самой игры, а не считается
        /// по нашим часам: на этих же флагах у неё висят холод, погода и сон, и ответ
        /// должен быть тот самый, а не похожий.
        /// </summary>
        internal static string GameClockRu()
        {
            var env = EnvMan.instance;
            var net = ZNet.instance;
            if (env == null || net == null || env.m_dayLengthSec <= 0) return "";

            var length = (double)env.m_dayLengthSec;
            var elapsed = (float)(net.GetTimeSeconds() % length / length);
            var clock = Geometry.ClockFromElapsed(elapsed);

            var said = new System.Text.StringBuilder();
            said.Append($"Время: {Clock(clock)}, день {env.GetDay()}");

            var night = EnvMan.IsNight();
            var what = night ? "ночь" : EnvMan.IsAfternoon() ? "день" : "утро";

            said.Append($"{NEWLINE}Сейчас: {what}, ")
                .Append(night
                    ? $"до рассвета {Until(elapsed, 0.25f, length)}"
                    : $"до заката {Until(elapsed, 0.75f, length)}");

            // Спать можно с полудня и всю ночь (`IsAfternoon || IsNight`), плюс полминуты
            // выдержки после подъёма - её мы не считаем, а спрашиваем вместе со всем
            // остальным у `CanSleep`.
            said.Append($"{NEWLINE}Спать: ")
                .Append(EnvMan.CanSleep()
                    ? "можно"
                    : clock >= 0.5f || clock <= 0.25f
                        ? "только что вставал"
                        : $"с 12:00, через {Until(elapsed, 0.5f, length)}");

            said.Append($"{NEWLINE}До полуночи: {Until(elapsed, 0f, length)}");
            said.Append($"{NEWLINE}Сутки: {Span(length)} — светло {Span(length * 0.7)}, "
                        + $"темно {Span(length * 0.3)}");

            return said.ToString();
        }

        private static string Clock(float clock)
        {
            var minutes = (int)(clock * 24f * 60f) % (24 * 60);
            return $"{minutes / 60:00}:{minutes % 60:00}";
        }

        /// <summary>Сколько настоящего времени осталось до этого часа циферблата.</summary>
        private static string Until(float elapsed, float clock, double length)
        {
            return Span(Geometry.ElapsedUntil(elapsed, Geometry.ElapsedFromClock(clock)) * length);
        }

        private static string Span(double seconds)
        {
            var whole = (int)System.Math.Round(seconds);
            if (whole < 60) return $"{whole} с";

            return whole % 60 == 0 ? $"{whole / 60} мин" : $"{whole / 60} мин {whole % 60:00} с";
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
            SetActive(RuneStonesButton, admin && MenuState == StateCheats);

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
