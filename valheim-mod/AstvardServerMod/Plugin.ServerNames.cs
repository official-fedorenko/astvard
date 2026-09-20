using System;
using System.Collections.Generic;
using Jotunn.Managers;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>
        /// Как наши серверы называются в списке игры.
        ///
        /// Хозяин спросил, почему его сервер в списке носит имя прежнего жильца IP.
        /// Ответ: имя приходит **не с сервера**. Клиент берёт его из двух мест
        /// (`MultiBackendMatchmaking.TryGetServerName`) - из своего кеша
        /// `serverlist_local/favorite` и из матчмейкинга, - а у нас `-public 0`, значит
        /// второго нет вовсе, и сам `ZNet` своё имя клиенту не сообщает: во всей сборке
        /// имя ставится ровно в одном месте, в кнопке «в избранное». В кеше же лежит то,
        /// что записали, когда по этому адресу сидел кто-то другой. Серверной правкой это
        /// не чинится никак - только отсюда, с клиента.
        ///
        /// Всё публичное, ни одной строки рефлексии: `SetServerName`, `ServerJoinData` и
        /// `ServerNameAtTimePoint` открыты, и компилятор присмотрит за ними при следующем
        /// обновлении игры - в отличие от `AccessTools`, который промолчит.
        /// </summary>
        private static BepInEx.Configuration.ConfigEntry<string> _serverNames;

        internal static void BindServerNames(BepInEx.Configuration.ConfigFile config)
        {
            _serverNames = config.Bind("Серверы", "Names",
                "72.61.139.115:2456=Аствард;127.0.0.1:2456=Аствард (отладка);"
                + "localhost:2456=Аствард (отладка)",
                "Как звать наши серверы в списке игры: «адрес=имя» через ';'. Адрес - тот "
                + "же, по которому заходишь; без порта подразумевается 2456. Имя ставится "
                + "у тебя на клиенте и никуда не уходит: игра его с сервера не спрашивает.");
        }

        /// <summary>
        /// Раз в пять секунд, а не один раз при старте.
        ///
        /// Имя живёт в словаре матчмейкинга, и загрузка списка кладёт туда своё - со
        /// временем **файла**. `SetServerName` уступает только тому, что новее, а мы
        /// каждый раз берём нынешний `UtcNow`, так что наше выигрывает всегда, когда бы
        /// список ни перечитали. Один вызов при старте этого не даёт: игрок открывает
        /// избранное позже, игра пишет файл, и с ним возвращается чужое имя.
        ///
        /// Стоит это двух записей в словарь - мерить нечего.
        /// </summary>
        private const float NamesTick = 5f;

        private static float _namesAt;

        private static string _namesRead;

        private static readonly List<KeyValuePair<ServerJoinData, string>> Names =
            new List<KeyValuePair<ServerJoinData, string>>();

        private static bool _namesSaid;

        internal static void TickServerNames()
        {
            if (GUIManager.IsHeadless() || _serverNames == null) return;

            if (Time.realtimeSinceStartup < _namesAt) return;
            _namesAt = Time.realtimeSinceStartup + NamesTick;

            // Без него вызов только напишет ошибку в лог и ничего не сделает.
            if (MultiBackendMatchmaking.Instance == null) return;

            ReadNames();

            // Спросить у игры, чьё имя лежало тут до нас, **нельзя** - и это не недосмотр,
            // а следствие того, как всё устроено. Мы ставим своё раньше, чем игра читает
            // список (он читается, только когда игрок откроет «Присоединиться к игре»), а
            // `SetServerName` уступает лишь более новому - значит загрузка со временем
            // файла отвергается, и чужое имя в словарь не попадает уже никогда. Сколько
            // тиков ни спрашивай, ответом всегда будет наше собственное. Строка «было
            // такое-то» здесь жила два коммита и не напечаталась ни разу.
            //
            // Доказывать это в логе и не нужно: прежнее имя лежит в самом файле, и
            // читает его `tools/serverlist/read.py`. Тащить разбор двоичного формата в
            // мод ради строчки - это код, который сломается на следующей версии формата,
            // взамен на то, что и так видно снаружи.
            foreach (var pair in Names)
                MultiBackendMatchmaking.SetServerName(pair.Key,
                    new ServerNameAtTimePoint(pair.Value, DateTime.UtcNow));

            if (_namesSaid || Names.Count == 0) return;

            _namesSaid = true;

            // Что именно поставили, а не только сколько: «два имени» не отличает верный
            // адрес от опечатки в нём, а опечатка здесь - самая вероятная беда.
            var said = new System.Text.StringBuilder();
            foreach (var pair in Names)
            {
                if (said.Length > 0) said.Append(", ");
                said.Append(pair.Key.Dedicated.m_host).Append(':')
                    .Append(pair.Key.Dedicated.m_port).Append(" → «").Append(pair.Value).Append('»');
            }

            Log.LogInfo($"[AstvardServerMod] Server names: {said}.");
        }

        /// <summary>
        /// Разбирает строку настройки. Заново - только когда она изменилась.
        ///
        /// Ключ у игры - хост и порт по отдельности (`ServerJoinDataDedicated.Equals`), и
        /// строку «хост:порт» разбирает её собственный конструктор, а не мы: у него же
        /// лежит и умолчание 2456, и нормализация адреса, который разобрался как IP.
        /// Своя арифметика тут промахнулась бы мимо ключа, и наше имя легло бы **рядом**
        /// с чужим, а не поверх него.
        /// </summary>
        private static void ReadNames()
        {
            var text = _serverNames.Value ?? "";
            if (text == _namesRead) return;

            _namesRead = text;
            _namesSaid = false;
            Names.Clear();

            foreach (var record in text.Split(';'))
            {
                var at = record.IndexOf('=');
                if (at <= 0) continue;

                var address = record.Substring(0, at).Trim();
                var name = record.Substring(at + 1).Trim();
                if (address.Length == 0 || name.Length == 0) continue;

                try
                {
                    Names.Add(new KeyValuePair<ServerJoinData, string>(
                        new ServerJoinData(new ServerJoinDataDedicated(address)), name));
                }
                catch (Exception bad)
                {
                    // Адрес пишет человек, и ошибиться в нём легко. Молчать нельзя:
                    // снаружи это выглядит как «мод не работает».
                    Log.LogWarning($"[AstvardServerMod] Server name: «{address}» is not an "
                                   + $"address I can use — {bad.Message}");
                }
            }
        }
    }
}
