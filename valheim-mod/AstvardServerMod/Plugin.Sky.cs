using System.Collections.Generic;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>
        /// Погода и ветер на всех, а не на того, кто нажал.
        ///
        /// `EnvMan.SetForceEnvironment` и `SetDebugWind` пишут в обычные поля клиента -
        /// ни ZDO, ни RPC, - а обычную погоду каждый клиент выводит сам из мирового
        /// времени. Мод стоит и на сервере, и от этого тянет думать, что назначенная
        /// гроза дойдёт до всех; не доходила. Поэтому кнопки теперь не делают ничего
        /// сами, а **просят сервер**, и он один решает, какое над базой небо.
        ///
        /// Держит его сервер, а не мир: ни в сохранении, ни в ZDO этому места нет, и
        /// придумывать его там значило бы хранить чит в мире. Перезапуск сервера
        /// возвращает обычную погоду - это и правильно, и заметно.
        /// </summary>
        private const string RpcSkyWeather = "AstvardSkyWeather";

        private const string RpcSkyWind = "AstvardSkyWind";

        private const string RpcSky = "AstvardSky";

        private static string _skyEnv = "";

        private static bool _skyWind;

        private static float _skyWindAngle;

        private static float _skyWindPower;

        /// <summary>
        /// Ноль значит «сервер ни разу ничего не назначал», и тогда слать нечего вовсе.
        ///
        /// Номер, а не сравнение полей: «вернули обычную погоду» - это такое же
        /// изменение, как гроза, и по пустой строке его от «ничего не было» не отличить.
        /// На этом ровно и ломается очевидная реализация: снять грозу удаётся тому, кто
        /// снял, а у остальных она остаётся до перезахода.
        /// </summary>
        private static int _skyRev;

        private static readonly Dictionary<long, int> SkySent = new Dictionary<long, int>();

        internal static void RegisterSkyRpcs(ZRoutedRpc rpc)
        {
            rpc.Register<string>(RpcSkyWeather, OnSkyWeather);
            rpc.Register<int, float, float>(RpcSkyWind, OnSkyWind);
            rpc.Register<string, int, float, float>(RpcSky, OnSky);
        }

        // ---------------- что просит клиент ----------------

        internal static void AskWeather(string env)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcSkyWeather, env ?? "");
        }

        internal static void AskWind(bool on, float angle, float power)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcSkyWind, on ? 1 : 0, angle, power);
        }

        // ---------------- что делает сервер ----------------

        /// <summary>
        /// Имя погоды проверяется по **нашему** списку кнопок, а не по `m_environments`
        /// игры.
        ///
        /// У выделенного сервера спрашивать `EnvMan` нельзя: если его там не окажется
        /// или он окажется пустым, проверка ответит «такой погоды нет» на всё подряд, и
        /// кнопки перестанут работать молча. А список кнопок мы знаем и так, и клиент,
        /// который пришлёт что-то помимо него, ничего не добьётся.
        /// </summary>
        private static bool AllowedWeather(string env)
        {
            if (string.IsNullOrEmpty(env)) return true;

            foreach (var option in WeatherOptions)
                if (option.Env == env) return true;

            return false;
        }

        private static void OnSkyWeather(long sender, string env)
        {
            if (!ServerAllows(sender)) return;

            env = env ?? "";
            if (!AllowedWeather(env))
            {
                Log.LogWarning($"[AstvardServerMod] Sky: {SenderName(sender)} asked for "
                               + $"weather «{env}», which is not one of ours.");
                return;
            }

            if (env == _skyEnv) return;

            _skyEnv = env;
            SkyChanged($"weather {(env.Length == 0 ? "back to normal" : env)}", sender);
        }

        private static void OnSkyWind(long sender, int on, float angle, float power)
        {
            if (!ServerAllows(sender)) return;

            var wind = on != 0;

            // Границы держит сервер, а не поле ввода: поле - это удобство того, кто
            // печатает, а не обещание того, что придёт по сети.
            angle = Mathf.Repeat(angle, 360f);
            power = Mathf.Clamp01(power);

            if (wind == _skyWind
                && (!wind || (Mathf.Approximately(angle, _skyWindAngle)
                              && Mathf.Approximately(power, _skyWindPower)))) return;

            _skyWind = wind;
            _skyWindAngle = angle;
            _skyWindPower = power;

            SkyChanged(wind ? $"wind {angle:F0} deg at {power:F2}" : "wind back to normal", sender);
        }

        /// <summary>
        /// Небо стало другим: поднять номер и забыть, кому что слали.
        ///
        /// Рассылка идёт обычным обходом пиров - тем же, что раздаёт зоны, - а не
        /// отдельным «всем сразу». Так поздно зашедший получает нынешнее небо тем же
        /// кодом, что и все остальные, а не вторым, который однажды разойдётся с первым.
        /// </summary>
        private static void SkyChanged(string what, long sender)
        {
            _skyRev++;
            SkySent.Clear();

            Log.LogInfo($"[AstvardServerMod] Sky: {what}, asked by {SenderName(sender)}.");

            // Хозяин игры и сервер в одном лице - на нём обход пиров не сработает,
            // потому что пиров нет. Выделенному серверу рисовать небо некому.
            if (!Jotunn.Managers.GUIManager.IsHeadless())
                ApplySky(_skyEnv, _skyWind, _skyWindAngle, _skyWindPower);
        }

        /// <summary>Из обхода пиров: этому уже говорили, каким стало небо?</summary>
        private static void SendSky(ZNetPeer peer)
        {
            if (_skyRev == 0 || peer == null) return;

            int last;
            if (SkySent.TryGetValue(peer.m_uid, out last) && last == _skyRev) return;

            SkySent[peer.m_uid] = _skyRev;
            ZRoutedRpc.instance?.InvokeRoutedRPC(peer.m_uid, RpcSky,
                _skyEnv, _skyWind ? 1 : 0, _skyWindAngle, _skyWindPower);
        }

        // ---------------- что делает клиент ----------------

        private static void OnSky(long sender, string env, int wind, float angle, float power)
        {
            // Выделенному серверу небо ни к чему, а хозяину одиночной игры мы его уже
            // поставили в SkyChanged - оттуда же, где решили.
            if (Jotunn.Managers.GUIManager.IsHeadless()) return;

            ApplySky(env, wind != 0, angle, power);
        }

        private static void ApplySky(string env, bool wind, float angle, float power)
        {
            var man = EnvMan.instance;
            if (man == null) return;

            man.SetForceEnvironment(env ?? "");

            if (wind) man.SetDebugWind(angle, power);
            else if (man.m_debugWind) man.ResetDebugWind();

            var player = Player.m_localPlayer;
            if (player == null) return;

            player.Message(MessageHud.MessageType.Center, string.IsNullOrEmpty(env)
                ? "Погода снова обычная"
                : $"Погода: {WeatherLabel(env)}");

            if (wind)
                player.Message(MessageHud.MessageType.Center,
                    $"Ветер на {CompassNames[(int)Mathf.Repeat(Mathf.Round(angle / 45f), 8f)]}, "
                    + $"сила {power * 10f:F0}");
        }

        /// <summary>Как эта погода подписана на кнопке — имя префаба человеку не скажет ничего.</summary>
        private static string WeatherLabel(string env)
        {
            foreach (var option in WeatherOptions)
                if (option.Env == env) return option.Label;

            return env;
        }
    }
}
