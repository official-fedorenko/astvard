using System.Collections;
using BepInEx.Configuration;
using Splatform;
using System.Collections.Generic;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>
        /// Камни с надписями на материке, где стоит игрок, и кратчайшая сеть между ними.
        ///
        /// Пока это только **разведка**: сколько их, каких пород, и сколько выйдет дорог.
        /// Прокладку решаем по этим числам, а не до них - у дорожки цена не в секундах, а
        /// в правках рельефа, которые необратимы, и сеть из тридцати камней может дать
        /// десятки километров.
        ///
        /// Два дорогих вопроса оказались дешёвыми, и на этом всё держится:
        ///
        /// **Где камни** - спрашивается у `ZoneSystem.m_locationInstances`. Это публичный
        /// словарь **всех** локаций мира с координатами, заполненный при генерации, так
        /// что ни одной зоны подгружать не надо. Если камня там нет, значит генератор
        /// кладёт его растительностью, а её списка не существует вовсе - тогда искать
        /// пришлось бы порождением каждой зоны материка, и это совсем другая работа.
        /// Ответ даёт отчёт: пород в нём столько, сколько их в мире на самом деле.
        ///
        /// **Где материк** - заливкой по `WorldGenerator.GetHeight`. Это чистая функция
        /// от координат, зоны ей не нужны: шаг в 32 м даёт очертания за секунды, и
        /// «стоим на одном материке» становится вопросом связности, а не догадкой.
        /// </summary>
        private const string RpcRunesAsk = "AstvardRunesAsk";

        private const string RpcRunesLay = "AstvardRunesLay";

        private const string RpcRunesStop = "AstvardRunesStop";

        internal static GameObject RuneStonesButton;

        internal static GameObject RuneStonesStopButton;

        /// <summary>
        /// Круг вокруг каждой метки. Каждой свой, даже если метки стоят кучкой и круги
        /// налезут друг на друга: так просил хозяин, и это честнее - один круг на двоих
        /// выглядел бы как промах укладки, а не как замысел.
        ///
        /// Размер круга выбирает сервер, и выбирает не сам: спрашивает у локации её
        /// `m_exteriorRadius` - то, насколько широко генератор расчищал под неё землю,
        /// то есть её собственный размер. Три своих числа - камню, спавну, алтарю -
        /// разъехались бы с игрой на первом же её обновлении. Границы наши: меньше
        /// восьми метров круг не читается как площадь, а тридцать два - потолок ручного
        /// мощения, и у круга ему быть тем же.
        /// </summary>
        private const float RingMin = 8f;

        private const float RingMax = 32f;

        private static float RingFor(float own)
        {
            return Mathf.Clamp(own, RingMin, RingMax);
        }

        /// <summary>
        /// Класть ли сеть на горе. По умолчанию нет, и вот почему.
        ///
        /// Гора в Valheim - это поле валунов по 18-35 м на склонах круче сорока пяти
        /// градусов: `rock1_mountain` намерен переписью 18,2..34,8 м. Дорога там либо
        /// стоит (всё непроходимо), либо лезет между глыбами по отвесному - хозяин
        /// прислал четыре снимка ровно этого, добавив, что «в остальных биомах пока что
        /// всё хорошо». Настоящая горная дорога просит серпантина и десятков метров
        /// выемки, а укладка режет шесть.
        ///
        /// Поэтому метки на горе выпадают из сети целиком - ни круга, ни дорог: дорога,
        /// уходящая в камни и там кончающаяся, хуже её отсутствия. До горных рун и до
        /// алтаря Модера ходят ногами, как ходили.
        ///
        /// Ключ на случай, когда захочется обратно: `[Дороги] Mountains = true`. И число
        /// пропущенного всегда в отчёте - молчаливая пропажа алтаря читалась бы как
        /// поломка.
        /// </summary>
        private static ConfigEntry<bool> _netMountains;

        internal static void BindRuneRoads(ConfigFile config)
        {
            _netMountains = config.Bind("Дороги", "Mountains", false,
                "Класть ли сеть дорог на горе. Гора - это поле валунов по 18-35 м на "
                + "отвесных склонах: дорога там либо не находит пути, либо лезет между "
                + "глыбами. По умолчанию метки на горе выпадают из сети целиком.");
        }

        private static bool NetTakesMountains
        {
            get { return _netMountains != null && _netMountains.Value; }
        }

        /// <summary>Сколько меток последний обвод материка оставил на горе.</summary>
        private static int _marksInTheHills;

        internal static void RegisterRuneRoadRpcs(ZRoutedRpc rpc)
        {
            rpc.Register<float, float>(RpcRunesAsk, OnRunesAsk);
            rpc.Register<ZPackage>(RpcRunesLay, OnRunesLay);
            rpc.Register(RpcRunesStop, OnRunesStop);
        }

        /// <summary>
        /// Первое нажатие считает, второе кладёт.
        ///
        /// Так в этом моде сделано всё, чего не вернуть, - и здесь причина та же, только
        /// крупнее: сеть в тридцать километров правит рельеф в каждой зоне по пути, и
        /// отката у этого нет. Взвод держится десять секунд: столько нужно, чтобы
        /// прочесть отчёт, и мало, чтобы нажать второй раз случайно.
        /// </summary>
        private static float _runesArmedAt = -100f;

        internal static bool RunesArmed
        {
            get { return Time.realtimeSinceStartup - _runesArmedAt <= 10f; }
        }

        internal static void AskRunes()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            if (RunesArmed)
            {
                _runesArmedAt = -100f;
                LayRunes(player);
                return;
            }

            _runesArmedAt = Time.realtimeSinceStartup;

            var at = player.transform.position;
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcRunesAsk, at.x, at.z);

            player.Message(MessageHud.MessageType.Center, "Считаю сеть. Нажми ещё раз, чтобы класть");
            RefreshMenu();
        }

        /// <summary>
        /// Настройки берутся у обычной дорожки, до единой.
        ///
        /// Хозяин просил именно этого, и причина не только в его удобстве: своя копия
        /// ширины, сглаживания и факелов разъехалась бы с оригиналом через месяц, и
        /// разница вылезла бы посреди тридцати километров уже уложенного.
        /// </summary>
        private static void LayRunes(Player player)
        {
            if (ZRoutedRpc.instance == null) return;

            var width = RoadWidth();
            var scale = PaintGridScale(player.transform.position);
            var radius = Mathf.Max(width * 0.5f, scale * 0.75f);

            var pkg = new ZPackage();
            pkg.Write(player.transform.position.x);
            pkg.Write(player.transform.position.z);
            pkg.Write(radius);
            pkg.Write(width);
            pkg.Write(_roadPaved);
            pkg.Write(RoadSmoothingActive);
            pkg.Write(RoadClearingActive);
            pkg.Write(RoadTorchesActive ? _roadTorch : 0);
            pkg.Write(TorchSpacing());
            pkg.Write(player.GetPlayerID());
            pkg.Write(PlatformManager.DistributionPlatform.LocalUser.PlatformUserID.ToString());
            pkg.Write(RoadTorchForever);
            pkg.Write(IsRoadDetour);
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcRunesLay, pkg);

            _runesAsked = true;
            player.Message(MessageHud.MessageType.Center, "Отдаю сеть серверу");
            RefreshMenu();
        }

        /// <summary>
        /// Просили ли мы сервер класть. Кнопка остановки нужна только после этого, а
        /// узнать, кончил ли он, клиенту неоткуда - сервер шлёт сообщения, а не состояние.
        /// Лишняя кнопка, которую нажали впустую, не стоит ничего; отсутствующая стоит
        /// тридцати километров.
        /// </summary>
        private static bool _runesAsked;

        internal static bool RunesAsked
        {
            get { return _runesAsked; }
        }

        internal static void StopRunes()
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcRunesStop);
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Прошу сервер остановить укладку");
        }

        /// <summary>
        /// Шаг заливки материка. Берега при 32 м огрубляются, и это нарочно: нам нужна
        /// связность, а не контур. Мельче - вчетверо дороже за каждое деление.
        /// </summary>
        private const float LandStep = 32f;

        /// <summary>
        /// Докуда вообще есть смысл заливать. У мира край около 10 км, дальше вода и
        /// край света; берём с запасом и ограничиваем числом клеток, а не радиусом, -
        /// заливка по суше сама остановится о воду.
        /// </summary>
        private const int LandCellsMax = 200000;

        /// <summary>Что обносим кругом и связываем дорогами.</summary>
        private enum MarkKind { None, Stone, Spawn, Boss, Forge }

        /// <summary>Место на карте: где оно, какого рода и каким кругом его обносить.</summary>
        private sealed class Mark
        {
            public Vector3 At;
            public float Radius;
            public MarkKind Kind;
            public string Name;
        }

        /// <summary>
        /// Алтари боссов - списком имён локаций, снятым с самой игры.
        ///
        /// `StreamingAssets/SoftRef/manifest` перечисляет все 212 локаций сборки путями
        /// вида `Assets/world/Locations/<биом>/<имя>.prefab`; восемь имён ниже - оттуда,
        /// а не по памяти. Лишнее имя не стоит ничего: мира, где есть все восемь, у нас
        /// и не бывает. Недостающее стоило бы молчания - алтарь просто не попал бы в сеть.
        /// </summary>
        private static readonly HashSet<string> BossSpots = new HashSet<string>
        {
            "eikthyrnir", "gdking", "bonemass", "dragonqueen", "goblinking",
            "mistlands_dvergrbossentrance1", "faderlocation", "dn_bossroom",
        };

        private const string SpawnSpot = "starttemple";

        /// <summary>
        /// «Кузница возможностей» - по просьбе хозяина она тоже метка сети.
        ///
        /// Сама кузница - деталь `piece_upgradestation`, а сеть читает только локации,
        /// поэтому целиться надо в локацию, которая её держит: `AncientUpgradeStation`
        /// в горах. Связь установлена по данным сборки, а не по памяти: русское имя
        /// «Кузница возможностей» стоит в строке `piece_upgradestation` локализации
        /// (`resources.assets`), а среди 212 локаций манифеста ровно одна названа под
        /// неё. Если имя однажды разойдётся, это будет видно: перепись локаций материка
        /// уходит в лог при каждом отчёте, а в самом отчёте кузница считается отдельно.
        /// </summary>
        private const string ForgeSpot = "ancientupgradestation";

        private static MarkKind KindOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return MarkKind.None;

            var lower = name.ToLowerInvariant();
            if (lower == SpawnSpot) return MarkKind.Spawn;
            if (lower == ForgeSpot) return MarkKind.Forge;
            if (BossSpots.Contains(lower)) return MarkKind.Boss;

            // `DrakeLorestone` в горах - такой же читаемый камень, как `Runestone_*`,
            // только назван иначе; без «lorestone» он выпадал из сети молча. Vegvisir
            // отдельной локацией не кладётся вовсе, но имя оставлено: оно даром.
            return lower.Contains("runestone") || lower.Contains("lorestone")
                   || lower.Contains("vegvisir")
                ? MarkKind.Stone
                : MarkKind.None;
        }

        /// <summary>Круги ставятся в этом порядке: спавн, алтари, кузница, камни.</summary>
        private static int Rank(MarkKind kind)
        {
            if (kind == MarkKind.Spawn) return 0;
            if (kind == MarkKind.Boss) return 1;
            return kind == MarkKind.Forge ? 2 : 3;
        }

        private static void OnRunesAsk(long sender, float x, float z)
        {
            if (!ServerAllows(sender)) return;

            var zones = ZoneSystem.instance;
            var world = WorldGenerator.instance;
            if (zones == null || world == null)
            {
                SayAboutZone(sender, "Мир ещё не готов — попробуй через несколько секунд.");
                return;
            }

            int cells;
            var census = new Dictionary<string, int>();
            var marks = MarksOn(zones, world, x, z, out cells, census);

            if (cells == 0)
            {
                SayAboutZone(sender, "Ты стоишь не на суше — с воды материк не обвести.");
                return;
            }

            var said = new System.Text.StringBuilder();
            said.Append($"Материк: {cells * LandStep * LandStep / 1000000f:0.0} км². ");

            if (marks.Count == 0)
            {
                said.Append("Ни камней с надписями, ни алтарей на нём не нашлось. Это не "
                            + "обязательно пусто: если генератор кладёт их растительностью, а не "
                            + "локациями, списка таких камней в игре нет вовсе.");
                Say(sender, said.ToString(), census, marks);
                return;
            }

            int stones = 0, bosses = 0, forges = 0;
            var home = false;
            foreach (var mark in marks)
            {
                if (mark.Kind == MarkKind.Stone) stones++;
                else if (mark.Kind == MarkKind.Boss) bosses++;
                else if (mark.Kind == MarkKind.Forge) forges++;
                else if (mark.Kind == MarkKind.Spawn) home = true;
            }

            var net = Whole(marks);

            said.Append($"Камней: {stones}, алтарей: {bosses}, кузниц: {forges}, спавн: ");
            said.Append(home ? "нашёлся" : "не нашёлся");
            said.Append($". Сеть — {net.Metres / 1000f:0.0} км");
            if (net.Shortcuts > 0)
                said.Append($", в ней срезок {net.Shortcuts} на "
                            + $"{net.ShortcutMetres / 1000f:0.0} км");

            said.Append($". Кругов {marks.Count}: {Rings(marks)}");
            if (_marksInTheHills > 0)
                said.Append($". На горе пропущено меток: {_marksInTheHills}"
                            + " — туда сеть не ходит ([Дороги] Mountains)");
            said.Append(" — и каждый дорастёт до края запрета стройки, если тот шире.");

            Say(sender, said.ToString(), census, marks);
        }

        private static void Say(long sender, string text, Dictionary<string, int> census,
                                List<Mark> marks)
        {
            SayAboutZone(sender, text);

            // Породы считаем отдельно от штук: «камней 40» не отличает сорок лорных от
            // сорока алтарей, а круги у них разные - потому рядом и стоит их размер.
            var mine = new Dictionary<string, int>();
            var rings = new Dictionary<string, float>();
            foreach (var mark in marks)
            {
                int had;
                mine[mark.Name] = (mine.TryGetValue(mark.Name, out had) ? had : 0) + 1;
                rings[mark.Name] = mark.Radius;
            }

            var said = new System.Text.StringBuilder(text);
            foreach (var pair in mine)
                said.Append($" | {pair.Key}: {pair.Value}, круг {rings[pair.Key]:0.#} м");

            Log.LogInfo($"[AstvardServerMod] Road net: {said}");

            if (census.Count == 0) return;

            // Перепись всего материка - в лог и только в лог. Имя, которого нет в нашем
            // отборе, иначе не увидеть ниоткуда: сеть промолчит о том, чего не взяла.
            var all = new System.Text.StringBuilder();
            foreach (var pair in census)
            {
                if (all.Length > 0) all.Append(", ");
                all.Append(pair.Key).Append(' ').Append(pair.Value);
            }

            Log.LogInfo($"[AstvardServerMod] Road net: this land holds — {all}");
        }

        /// <summary>
        /// Какие круги выбрал сервер - словами и до того, как их станет не вернуть.
        /// Число в отчёте здесь важнее прочего: радиус больше никто не задаёт руками.
        /// </summary>
        private static string Rings(List<Mark> marks)
        {
            var said = new System.Text.StringBuilder();

            for (var rank = 0; rank <= 2; rank++)
            {
                float low = float.MaxValue, high = 0f;
                foreach (var mark in marks)
                {
                    if (Rank(mark.Kind) != rank) continue;

                    low = Mathf.Min(low, mark.Radius);
                    high = Mathf.Max(high, mark.Radius);
                }

                if (high <= 0f) continue;

                if (said.Length > 0) said.Append(", ");
                said.Append(rank == 0 ? "спавн " : rank == 1 ? "алтари " : "камни ");
                said.Append(low < high ? $"{low:0.#}–{high:0.#} м" : $"{low:0.#} м");
            }

            return said.ToString();
        }

        /// <summary>
        /// Что сервер сейчас кладёт по камням: очередь заданий и место в ней.
        ///
        /// Очередь, а не одно большое задание: укладка уже умеет класть путь кусками,
        /// подтыкая зоны, останавливаться и писать откат - вторая такая же рядом
        /// разошлась бы с ней на первой же правке. Поэтому каждое ребро сети и каждый
        /// круг вокруг камня становятся **обычным** заданием дорожки, и мы лишь подаём
        /// их по одному.
        /// </summary>
        private sealed class RuneJob
        {
            public long Sender;
            public readonly List<RoadJob> Queue = new List<RoadJob>();
            public int Done;
            public bool Stop;
            public int Stones;
            public float Metres;

            // Круги, уже уложенные этой сетью. Дороги получают этот же список ссылкой: к
            // тому мигу, когда дорога ставит факелы, круги обоих её концов уже уложены -
            // круг метки идёт прямо перед дорогой, которая до неё доводит.
            /// <summary>Дорожные факелы сети: круг, легший поверх, гасит свои из этого списка.</summary>
            public readonly List<ZDOID> Lit = new List<ZDOID>();

            /// <summary>Сколько их погашено кругами. Молчаливая уборка неотличима от промаха.</summary>
            public int Doused;

            /// <summary>
            /// Докуда должна дотянуться краска каждой метки. Дороги пишут, круги читают -
            /// и потому дороги обязаны лечь раньше кругов, как они и ложатся.
            /// </summary>
            public float[] Reach;
        }

        private static RuneJob _runeJob;

        private static void OnRunesStop(long sender)
        {
            if (!ServerAllows(sender) || _runeJob == null) return;

            _runeJob.Stop = true;
            if (_roadJob != null) _roadJob.Stop = true;

            Log.LogInfo($"[AstvardServerMod] Road net: stop asked by {SenderName(sender)}.");
        }

        private static void OnRunesLay(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!ServerAllows(sender))
            {
                SayAboutZone(sender, "Сеть дорог кладёт сервер только админам.");
                return;
            }

            if (_runeJob != null)
            {
                SayAboutZone(sender, "Сервер уже кладёт сеть — останови её или дождись.");
                return;
            }

            // Перепись считается тем же `_roadJob`, что и дорожка, но говорить про неё
            // «кладёт дорожку» нельзя: человек будет ждать не того и не столько.
            if (_surveying)
            {
                SayAboutZone(sender, "Сейчас идёт перепись — дождись её, сеть пойдёт следом.");
                return;
            }

            if (_roadJob != null)
            {
                SayAboutZone(sender, "Сервер сейчас кладёт обычную дорожку — дождись её.");
                return;
            }

            float x, z, radius, width, spacing;
            bool paved, smooth, clear;
            int torch;
            long creator;
            string platform;
            try
            {
                x = pkg.ReadSingle();
                z = pkg.ReadSingle();
                radius = pkg.ReadSingle();
                width = pkg.ReadSingle();
                paved = pkg.ReadBool();
                smooth = pkg.ReadBool();
                clear = pkg.ReadBool();
                torch = pkg.ReadInt();
                spacing = pkg.ReadSingle();
                creator = pkg.ReadLong();
                platform = pkg.ReadString();
            }
            catch (System.Exception bad)
            {
                Log.LogWarning($"[AstvardServerMod] Road net: unreadable request from "
                               + $"{SenderName(sender)}: {bad.Message}");
                return;
            }

            // Хвостовое поле, и читается отдельно: клиент постарше его не шлёт, а сеть ему
            // всё равно полагается.
            var forever = false;
            try { forever = pkg.ReadBool(); }
            catch (System.Exception) { forever = false; }

            var bend = false;
            try { bend = pkg.ReadBool(); }
            catch (System.Exception) { bend = false; }

            // Одной кнопкой. Без переписи сеть кладётся прямыми, а помнить порядок нажатий
            // человек не обязан: 23.09.2026 сеть так и легла - 163 задания по прямой, - и
            // сказал об этом только уголок экрана, которого хозяин не заметил.
            var ask = new NetAsk
            {
                X = x, Z = z, Radius = radius, Width = width, Spacing = spacing,
                Paved = paved, Smooth = smooth, Clear = clear, Forever = forever, Bend = bend,
                Torch = torch, Creator = creator, Platform = platform,
            };

            if (PlanReady) LayNet(sender, ask);
            else Instance.StartCoroutine(SurveyThenLay(sender, ask));
        }

        /// <summary>
        /// Разобранная просьба о сети.
        ///
        /// Завелась она оттого, что между разбором пакета и укладкой встала перепись, то
        /// есть минуты ожидания: разобранное надо чем-то донести через корутину.
        /// </summary>
        private struct NetAsk
        {
            public float X, Z, Radius, Width, Spacing;

            public bool Paved, Smooth, Clear, Forever, Bend;

            public int Torch;

            public long Creator;

            public string Platform;
        }

        /// <summary>
        /// Сперва перепись, следом укладка - и всё это по одному нажатию.
        ///
        /// Перепись живёт только в памяти сервера и не переживает перезапуска, так что
        /// «сделай сперва перепись» - это не разовая настройка, а правило, которое надо
        /// помнить каждый вечер. Помнить его теперь не надо.
        /// </summary>
        private static IEnumerator SurveyThenLay(long sender, NetAsk ask)
        {
            var zones = ZoneSystem.instance;
            var world = WorldGenerator.instance;
            if (zones == null || world == null || !zones.LocationsGenerated)
            {
                SayAboutZone(sender, "Мир ещё не готов — попробуй через несколько секунд.");
                yield break;
            }

            var land = Continent(world, zones.m_waterLevel, ask.X, ask.Z);
            if (land.Count == 0)
            {
                SayAboutZone(sender, "Ты стоишь не на суше — материк отсюда не обвести.");
                yield break;
            }

            SayAboutZone(sender, "Переписи ещё не было — сперва обхожу материк, это минуты. "
                                 + "Сеть пойдёт сразу за ней, нажимать больше ничего не надо.");
            _surveying = true;
            yield return RunSurvey(sender, new Vector3(ask.X, 0f, ask.Z), land);

            // Перепись могли остановить на середине - тогда класть по половине материка
            // хуже, чем не класть: дороги пойдут в обход того, что успели увидеть, и
            // напрямик сквозь остальное, и разницы снаружи не видно.
            if (!PlanReady)
            {
                SayAboutZone(sender, "Перепись не удалась — сеть не кладу.");
                yield break;
            }

            LayNet(sender, ask);
        }

        private static void LayNet(long sender, NetAsk ask)
        {
            // Тело укладки писалось, когда разобранное лежало прямо здесь, в локальных
            // переменных. Перепись встала перед ним, а не внутри него, так что и
            // переписывать его незачем - довольно распаковать запись обратно.
            float x = ask.X, z = ask.Z, radius = ask.Radius, width = ask.Width;
            var spacing = ask.Spacing;
            bool paved = ask.Paved, smooth = ask.Smooth, clear = ask.Clear;
            bool forever = ask.Forever, bend = ask.Bend;
            var torch = ask.Torch;
            var creator = ask.Creator;
            var platform = ask.Platform;

            var zones = ZoneSystem.instance;
            var world = WorldGenerator.instance;
            if (zones == null || world == null) return;

            var marks = MarksOn(zones, world, x, z, out _, null);
            if (marks.Count == 0)
            {
                SayAboutZone(sender, "Ни камней, ни алтарей на этом материке — класть не к чему.");
                return;
            }

            var job = new RuneJob
            {
                Sender = sender,
                Stones = marks.Count,
                Reach = new float[marks.Count],
            };

            var net = Whole(marks);

            // Сетка цены земли - одна на всю сеть, и это нарочно: проложенная дорога
            // дешевит землю вокруг себя, и следующие к ней прижимаются. Нет переписи -
            // нет и сетки, тогда дороги рисуются кривой, как рисовались до 23.09.2026.
            var field = PlanReady ? PlanField(SurveyLand, radius) : null;
            if (field != null)
                Log.LogInfo($"[AstvardServerMod] Road net: cost field {field.Wide}x{field.High} "
                            + $"cells of {PlanStep:F0} m, from {SurveyKnownCount} things known.");
            else
                SayAboutZone(sender, "Переписи не было — дороги пойдут по прямой, как раньше. "
                                     + "Сделай «Перепись материка», и они будут обходить.");

            var planned = 0;
            var straightened = 0;
            var climbedOver = 0;
            var wardedRings = 0;
            var began = Time.realtimeSinceStartup;

            foreach (var step in Steps(marks, net, new Vector3(x, 0f, z)))
            {
                if (step.Mark >= 0)
                {
                    // Обойти базу дорогой и замостить площадку в её середине - это
                    // половина вежливости. Круг метки, накрывающий чей-то оберег, не
                    // кладётся вовсе: рельеф назад не ходит.
                    if (field != null && TouchesAWard(marks[step.Mark].At, marks[step.Mark].Radius))
                    {
                        wardedRings++;
                        continue;
                    }

                    var ring = CircleJob(marks[step.Mark], paved, smooth, clear, torch,
                                         spacing, forever, bend, creator, platform);
                    ring.Mark = step.Mark;
                    ring.MarkReach = job.Reach;
                    job.Queue.Add(ring);
                    continue;
                }

                var link = net.Links[step.Edge];
                var from = net.Points[link.A];
                var to = net.Points[link.B];

                var road = RoadBetween(from, to, radius, width, paved, smooth, clear, torch,
                                       spacing, forever, bend, creator, platform);

                // Разобрать путь, проложить по цене, проверить - и только потом в очередь.
                if (field != null)
                {
                    bool climbed;
                    if (PlanRoad(field, road, from, to, marks[link.A].Radius, marks[link.B].Radius,
                                 out climbed))
                    {
                        planned++;
                        if (climbed) climbedOver++;
                    }
                    else
                    {
                        straightened++;
                    }
                }
                road.Lit = job.Lit;

                // Точки сети - это метки, одна к одной, так что номер связи и есть номер
                // метки на её конце.
                road.MarkA = link.A;
                road.MarkB = link.B;
                road.MarkReach = job.Reach;

                job.Metres += Flat(from, to);
                job.Queue.Add(road);
            }

            // Вся прокладка идёт разом, до укладки и в одном кадре: сетка цены, поиск по
            // каждой дороге и сглаживание. Сколько это стоит, должно быть числом в логе,
            // а не догадкой - сервер на это время стоит.
            if (field != null)
                Log.LogInfo($"[AstvardServerMod] Road net: planning {planned + straightened} roads "
                            + $"took {Time.realtimeSinceStartup - began:F1} s, "
                            + $"{climbedOver} of them had to climb over something.");

            _runeJob = job;
            Instance.StartCoroutine(RunRuneJob(job));

            // Сколько дорог проложено по цене земли, а сколько осталось прямыми: прямая
            // здесь значит «прохода не нашлось», и знать это надо до, а не после.
            var how = field == null
                ? ", проложено по прямой (переписи не было)"
                : straightened > 0
                    ? $", проложено с обходом {planned}, прямыми {straightened}"
                        + (climbedOver > 0 ? $" (перелезать пришлось {climbedOver})" : "")
                    : $", все {planned} проложены с обходом";

            var said = $"Кругов {job.Stones - wardedRings}, дорог {job.Metres / 1000f:0.0} км"
                       + (net.Shortcuts > 0 ? $" (срезок {net.Shortcuts})" : "")
                       + $", заданий {job.Queue.Count}{how}"
                       + (wardedRings > 0 ? $", кругов под оберегами не тронуто {wardedRings}" : "")
                       + ". Начал.";
            SayAboutZone(sender, said);
            Log.LogInfo($"[AstvardServerMod] Road net: {said} Asked by {SenderName(sender)}.");
        }

        /// <summary>
        /// Одно задание за другим, и ни одного сверх очереди.
        ///
        /// Ждём не по часам, а по освобождению слота: `_roadJob` и есть признак того, что
        /// укладка идёт, и она же его снимает, когда закончит. Ждать по времени значило
        /// бы гадать, сколько длится кусок, которому подтягивают зоны.
        /// </summary>
        private static IEnumerator RunRuneJob(RuneJob job)
        {
            var wait = new WaitForSeconds(0.5f);
            var began = Time.realtimeSinceStartup;

            while (job.Done < job.Queue.Count && !job.Stop && ZNet.instance != null)
            {
                while (_roadJob != null && !job.Stop && ZNet.instance != null) yield return wait;
                if (job.Stop || ZNet.instance == null) break;

                var piece = job.Queue[job.Done];
                GiveRoadRecord(piece);
                _roadJob = piece;
                yield return Instance.StartCoroutine(RunRoadJob(piece));

                // Круги кладутся последними, поэтому дороги уже расставили свои огни
                // там, где встала площадка. Гасим их до того, как поставим кольцевые, -
                // иначе рядом окажутся два ряда.
                if (piece.Grow && !job.Stop) job.Doused += DouseInsideRing(job, piece);

                // Кольцо факелов ставится после круга, а не вместе с ним: укладка
                // расставляет факелы вдоль пути, а путь у круга - полметра, и все они
                // встали бы кучкой у самого камня.
                if (piece.Ring > 0 && !job.Stop) RingTorches(piece);

                job.Done++;

                // Раз в десяток заданий, а не на каждое: сети из ста сорока строк в логе
                // никто не читает, а по одной в минуту видно, что дело идёт.
                if (job.Done % 10 == 0 || job.Done == job.Queue.Count)
                {
                    var said = $"Сеть: сделано {job.Done} из {job.Queue.Count}, "
                               + $"{Time.realtimeSinceStartup - began:0} с.";
                    SayAboutZone(job.Sender, said);
                    Log.LogInfo($"[AstvardServerMod] Road net: {said}");
                }
            }

            var how = job.Stop ? "остановлено" : "готово";
            var doused = job.Doused > 0 ? $", погашено под кругами: {job.Doused}" : "";
            SayAboutZone(job.Sender, $"Сеть: {how}, {job.Done} из {job.Queue.Count} за "
                                     + $"{(Time.realtimeSinceStartup - began) / 60f:0.#} мин{doused}.");
            Log.LogInfo($"[AstvardServerMod] Road net: {how}, {job.Done} of {job.Queue.Count}"
                        + (job.Doused > 0 ? $", {job.Doused} torches doused under rings" : "") + ".");

            _runeJob = null;
        }

        /// <summary>Мощёный круг вокруг метки и кольцо факелов по нему.</summary>
        private static RoadJob CircleJob(Mark mark, bool paved, bool smooth, bool clear,
                                         int torch, float spacing, bool forever, bool bend,
                                         long creator, string platform)
        {
            // Путь из двух точек в полуметре: укладка идёт по отрезкам, и одной точки ей
            // мало - цикл по парам не сделал бы ни шага. Круг задаёт не путь, а радиус,
            // и он у каждой метки свой.
            return new RoadJob
            {
                Id = Random.Range(1, int.MaxValue),
                Path = new List<Vector3> { mark.At, mark.At + new Vector3(0.5f, 0f, 0f) },
                Radius = mark.Radius,
                Width = mark.Radius * 2f,
                Paint = paved ? Heightmap.m_paintMaskPaved : Heightmap.m_paintMaskDirt,
                Smooth = smooth,
                Clear = clear,
                // Факелы вдоль полуметрового пути встали бы кучкой у метки; кольцо ставим
                // сами, ниже.
                Torch = 0,
                Spacing = spacing,
                Creator = creator,
                Platform = platform,
                Ring = torch > 0 ? torch : 0,
                Centre = mark.At,
                Grow = true,
                Forever = forever,
                Bend = bend,
            };
        }

        private static RoadJob RoadBetween(Vector3 from, Vector3 to, float radius, float width,
                                           bool paved, bool smooth, bool clear, int torch,
                                           float spacing, bool forever, bool bend, long creator,
                                           string platform)
        {
            // Тем же сэмплером, что и обычная дорожка: точка на метр пути. Своя кривая
            // здесь разошлась бы с той, которую кладёт кнопка, на первой же правке.
            var path = new List<Vector3>();
            RoadPoints(from, to, 0f, 1f, path);

            return new RoadJob
            {
                Id = Random.Range(1, int.MaxValue),
                Path = path,
                // Оба конца этой дороги - середины меток, и вокруг каждой ляжет круг,
                // так что концу есть куда сдвинуться, и этого никто не увидит.
                EndsInRings = true,
                Radius = radius,
                Width = width,
                Paint = paved ? Heightmap.m_paintMaskPaved : Heightmap.m_paintMaskDirt,
                Smooth = smooth,
                Clear = clear,
                Torch = torch,
                Spacing = spacing,
                Creator = creator,
                Platform = platform,
                Forever = forever,
                Bend = bend,
            };
        }

        /// <summary>
        /// Гасит дорожные факелы сети, оказавшиеся внутри круга.
        ///
        /// Круги кладутся последними (см. `Steps`), поэтому дороги успевают расставить
        /// свои огни там, где потом встанет площадка: ряд вдоль дороги внутри круга
        /// рядом с кольцевым читается как мусор — хозяин это и заметил.
        ///
        /// Гасим **только свои** — те, чьи записи сеть запомнила, когда ставила. Искать
        /// вокруг круга факелы по породе было бы проще и неверно: у камня вполне может
        /// стоять чужой, и он не наш, чтобы его трогать.
        ///
        /// Радиус берётся тот, который в самом деле лёг: круг мог дорасти до края
        /// запрета стройки. Меряется по краю кольца, а не краски — кольцевые стоят в
        /// `TorchMargin` за ободом, и дорожный рядом с ними читался бы как дубль.
        ///
        /// Что не нашлось в сцене, из списка всё равно убирается: зона круга загружена,
        /// мы её только что уложили, так что ненайденное — это снесённое кем-то ещё.
        /// </summary>
        private static int DouseInsideRing(RuneJob job, RoadJob ring)
        {
            var scene = ZNetScene.instance;
            if (scene == null || job.Lit.Count == 0) return 0;

            var reach = ring.Radius + TorchMargin + 0.5f;
            var doused = 0;

            for (var i = job.Lit.Count - 1; i >= 0; i--)
            {
                var zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(job.Lit[i]) : null;
                if (zdo == null)
                {
                    job.Lit.RemoveAt(i);
                    continue;
                }

                var at = zdo.GetPosition();
                var away = new Vector2(at.x - ring.Centre.x, at.z - ring.Centre.z);
                if (away.sqrMagnitude >= reach * reach) continue;

                var go = scene.FindInstance(job.Lit[i]);
                if (go != null)
                {
                    var view = go.GetComponent<ZNetView>();
                    if (view != null && view.IsValid())
                    {
                        view.ClaimOwnership();
                        scene.Destroy(go);
                        doused++;
                    }
                }

                job.Lit.RemoveAt(i);
            }

            return doused;
        }

        /// <summary>
        /// Факелы кольцом по краю круга - тем же `Geometry.RingPosts`, которым их ставит
        /// забор, и той же расстановкой, которой их ставит дорожка.
        /// </summary>
        private static void RingTorches(RoadJob piece)
        {
            // По ободу и с теми же полями, с какими их ставит ручное мощение
            // (`LineWithTorches`): одни и те же настройки не должны давать два разных
            // кольца, а шаг у факелов один на весь мод.
            var posts = Geometry.RingPosts(new Vec2(piece.Centre.x, piece.Centre.z),
                                           piece.Radius + TorchMargin, piece.Spacing);
            if (posts.Count == 0) return;

            var was = piece.Torch;
            piece.Torch = piece.Ring;

            int placed = 0, skipped = 0;
            PlaceRoadJobTorches(piece, posts, ref placed, ref skipped);
            piece.Torch = was;

            if (skipped > 0)
                Log.LogInfo($"[AstvardServerMod] Road net: ring at {piece.Centre.x:F0},"
                            + $"{piece.Centre.z:F0} — {placed} torches, {skipped} skipped.");
        }

        /// <summary>
        /// Метки этого материка - один проход и один отбор на отчёт и на укладку.
        ///
        /// Две копии отбора разошлись бы, и первым это заметил бы тот, кто уже нажал
        /// «класть»: отчёт обещал одно, сервер положил другое. `census` заодно считает
        /// **все** локации материка, и нужен он только отчёту.
        /// </summary>
        private static List<Mark> MarksOn(ZoneSystem zones, WorldGenerator world, float x, float z,
                                          out int cells, Dictionary<string, int> census)
        {
            var land = Continent(world, zones.m_waterLevel, x, z);
            var marks = new List<Mark>();
            cells = land.Count;
            _marksInTheHills = 0;
            if (land.Count == 0) return marks;

            var inTheHills = 0;

            foreach (var pair in zones.m_locationInstances)
            {
                var where = pair.Value;
                var name = where.m_location != null ? where.m_location.m_name : null;
                if (string.IsNullOrEmpty(name)) continue;
                if (!OnLand(land, where.m_position.x, where.m_position.z)) continue;

                if (census != null)
                {
                    int had;
                    census[name] = (census.TryGetValue(name, out had) ? had : 0) + 1;
                }

                var kind = KindOf(name);
                if (kind == MarkKind.None) continue;

                // Гора спрашивается у генератора - чистая функция от координат, ни одной
                // зоны поднимать не надо, как и для высоты.
                if (!NetTakesMountains
                    && world.GetBiome(where.m_position.x, where.m_position.z)
                       == Heightmap.Biome.Mountain)
                {
                    inTheHills++;
                    continue;
                }

                marks.Add(new Mark
                {
                    At = where.m_position,
                    Kind = kind,
                    Name = name,
                    Radius = RingFor(where.m_location.m_exteriorRadius),
                });
            }

            _marksInTheHills = inTheHills;
            return marks;
        }

        /// <summary>Ребро сети: две метки номерами, а не парой координат.</summary>
        private struct Link
        {
            public int A;
            public int B;
        }

        /// <summary>
        /// Кратчайшая сеть по Приму - номерами, потому что дальше по ней считаются
        /// расстояния **по дорогам**, а для этого нужен граф, а не список точек.
        ///
        /// Квадрат от числа меток: их сотни, а не тысячи, и городить что-то умнее значит
        /// платить сложностью за время, которого и так нет.
        /// </summary>
        private static List<Link> TreeLinks(List<Vector3> spots)
        {
            var links = new List<Link>();
            if (spots.Count < 2) return links;

            var inTree = new bool[spots.Count];
            var best = new float[spots.Count];
            var from = new int[spots.Count];
            for (var i = 0; i < spots.Count; i++) { best[i] = float.MaxValue; from[i] = 0; }

            best[0] = 0f;

            for (var step = 0; step < spots.Count; step++)
            {
                var pick = -1;
                for (var i = 0; i < spots.Count; i++)
                    if (!inTree[i] && (pick < 0 || best[i] < best[pick])) pick = i;

                if (pick < 0 || best[pick] == float.MaxValue) break;

                inTree[pick] = true;
                if (pick != 0) links.Add(new Link { A = from[pick], B = pick });

                for (var i = 0; i < spots.Count; i++)
                {
                    if (inTree[i]) continue;

                    var flat = Flat(spots[pick], spots[i]);
                    if (flat < best[i]) { best[i] = flat; from[i] = pick; }
                }
            }

            return links;
        }

        /// <summary>
        /// Материк, на котором стоит точка: заливка по суше с шагом `LandStep`.
        ///
        /// Высоту спрашиваем у генератора, а не у загруженной земли: земля есть только
        /// там, где кто-то был, а генератор отвечает про любую точку мира и одинаково.
        /// </summary>
        private static HashSet<long> Continent(WorldGenerator world, float water, float x, float z)
        {
            var seen = new HashSet<long>();
            var land = new HashSet<long>();

            var start = Cell(x, z);
            if (world.GetHeight(x, z) <= water) return land;

            var queue = new Queue<long>();
            queue.Enqueue(start);
            seen.Add(start);

            while (queue.Count > 0 && land.Count < LandCellsMax)
            {
                var cell = queue.Dequeue();
                var cx = (int)(cell >> 32);
                var cz = (int)(cell & 0xFFFFFFFF);

                land.Add(cell);

                for (var dx = -1; dx <= 1; dx++)
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0) continue;

                    var next = Key(cx + dx, cz + dz);
                    if (!seen.Add(next)) continue;

                    if (world.GetHeight((cx + dx) * LandStep, (cz + dz) * LandStep) > water)
                        queue.Enqueue(next);
                }
            }

            return land;
        }

        private static bool OnLand(HashSet<long> land, float x, float z)
        {
            return land.Contains(Cell(x, z));
        }

        private static long Cell(float x, float z)
        {
            return Key(Mathf.RoundToInt(x / LandStep), Mathf.RoundToInt(z / LandStep));
        }

        private static long Key(int x, int z)
        {
            return ((long)x << 32) | (uint)z;
        }

        /// <summary>
        /// Сеть целиком: точки, рёбра между ними и то, что о ней стоит сказать.
        ///
        /// Граф, а не список пар координат: по нему считается и длина обхода для срезок, и
        /// порядок укладки - от дома наружу.
        /// </summary>
        private sealed class Network
        {
            public readonly List<Vector3> Points = new List<Vector3>();
            public readonly List<Link> Links = new List<Link>();

            public int Shortcuts;
            public float ShortcutMetres;
            public float Metres;
        }

        /// <summary>
        /// Кратчайшее дерево по меткам, дороги от спавна и срезки.
        ///
        /// Одна функция и на отчёт, и на укладку - чтобы обещанные километры были теми
        /// самыми, которые лягут.
        /// </summary>
        private static Network Whole(List<Mark> marks)
        {
            var net = new Network();
            foreach (var mark in marks) net.Points.Add(mark.At);

            net.Links.AddRange(TreeLinks(net.Points));
            AddSpawnLinks(marks, net.Points, net.Links);

            var was = net.Links.Count;
            AddShortcuts(net.Points, net.Links);
            net.Shortcuts = net.Links.Count - was;

            for (var i = 0; i < net.Links.Count; i++)
            {
                var step = Flat(net.Points[net.Links[i].A], net.Points[net.Links[i].B]);

                net.Metres += step;
                if (i >= was) net.ShortcutMetres += step;
            }

            return net;
        }

        /// <summary>Шаг укладки: либо круг вокруг метки, либо дорога между двумя.</summary>
        private struct Step
        {
            public int Mark;
            public int Edge;
            public float Far;
        }

        /// <summary>
        /// Порядок укладки: сперва все дороги, потом все круги. Внутри каждой половины -
        /// от дома наружу.
        ///
        /// **Круги идут последними потому, что дорога ломает готовый круг.** Она приходит
        /// ровно в середину метки и по дороге выравнивает полосу под свой профиль -
        /// сквозь диск, который круг только что выровнял в плоскость. На стыке выходит
        /// канава или гребень через всю площадку, и видно это только в игре: хозяин
        /// принёс снимки 22.09.2026. Круг, уложенный после, накрывает концы дорог своей
        /// плоскостью, и площадка выходит целой, а дороги к ней сходятся.
        ///
        /// Что на этом потеряно, и это честная цена: **растущей сети больше нет**. Раньше
        /// круг метки ставился перед дорогой, которая до неё доводит, и в любой миг
        /// уложенное было связной сетью от дома; теперь остановка на середине оставит
        /// дороги без кругов. Дороги при этом связны и проходимы, а круг - украшение и
        /// ориентир, так что терять их не жалко.
        ///
        /// **Спавн и алтари остаются впереди - но среди кругов, а не вообще.** Их нельзя
        /// поставить до дорог по той же причине, по какой нельзя все остальные.
        ///
        /// Порядок «от дома наружу» внутри каждой половины остался, и он не только про
        /// вид: каждое задание ждёт, пока встанут земля и объекты его зон, соседние куски
        /// делят уже загруженные, а прыжок через материк грузит всё заново.
        ///
        /// Начало - спавн; если его на этом материке нет, ближайшая к нажавшему метка.
        /// </summary>
        private static List<Step> Steps(List<Mark> marks, Network net, Vector3 asked)
        {
            var steps = new List<Step>(marks.Count + net.Links.Count);
            if (marks.Count == 0) return steps;

            var start = 0;
            var near = float.MaxValue;
            for (var i = 0; i < marks.Count; i++)
            {
                if (marks[i].Kind == MarkKind.Spawn) { start = i; break; }

                var away = Flat(asked, marks[i].At);
                if (away < near) { near = away; start = i; }
            }

            var far = Roads(net.Points, net.Links)[start];

            var roads = new List<Step>();
            var first = new List<Step>();
            var stones = new List<Step>();

            for (var i = 0; i < marks.Count; i++)
            {
                var step = new Step { Mark = i, Edge = -1, Far = far[i] };
                if (marks[i].Kind == MarkKind.Stone) stones.Add(step);
                else first.Add(step);
            }

            for (var i = 0; i < net.Links.Count; i++)
                roads.Add(new Step
                {
                    Mark = -1,
                    Edge = i,
                    Far = Mathf.Max(far[net.Links[i].A], far[net.Links[i].B]),
                });

            roads.Sort((a, b) => a.Far.CompareTo(b.Far));
            first.Sort((a, b) => a.Far.CompareTo(b.Far));
            stones.Sort((a, b) => a.Far.CompareTo(b.Far));

            steps.AddRange(roads);
            steps.AddRange(first);
            steps.AddRange(stones);
            return steps;
        }

        /// <summary>
        /// Сколько дорог выходит из спавна.
        ///
        /// В кратчайшей сети у него ровно одно ребро - к ближайшему соседу. Для сети это
        /// верно, для места, откуда выходят каждый вечер, - нет, и хозяин просил дороги
        /// «к ближайшим камням», во множественном числе.
        /// </summary>
        private const int SpawnRoads = 3;

        private static void AddSpawnLinks(List<Mark> marks, List<Vector3> points,
                                          List<Link> links)
        {
            var home = -1;
            for (var i = 0; i < marks.Count; i++)
                if (marks[i].Kind == MarkKind.Spawn) { home = i; break; }

            if (home < 0) return;

            // Считаем те, что дерево уже дало: спавн, оказавшийся посреди камней, иначе
            // получил бы четыре дороги там, где просили три.
            var had = 0;
            foreach (var link in links)
                if (link.A == home || link.B == home) had++;

            var stones = new List<int>();
            for (var i = 0; i < marks.Count; i++)
                if (marks[i].Kind == MarkKind.Stone) stones.Add(i);

            stones.Sort((a, b) => Flat(points[home], points[a])
                                      .CompareTo(Flat(points[home], points[b])));

            foreach (var stone in stones)
            {
                if (had >= SpawnRoads) break;
                if (Joined(links, home, stone)) continue;

                links.Add(new Link { A = home, B = stone });
                had++;
            }
        }

        private static bool Joined(List<Link> links, int a, int b)
        {
            foreach (var link in links)
                if ((link.A == a && link.B == b) || (link.A == b && link.B == a)) return true;

            return false;
        }

        /// <summary>
        /// Срезки - там, где по готовой сети приходится далеко оббегать.
        ///
        /// Дерево - самая короткая сеть **целиком**, но не самый короткий путь между
        /// двумя метками: до камня за холмом можно бежать через три ветки. Поэтому мера
        /// здесь не «близко ли камни друг к другу», а «намного ли длиннее обход»: пара
        /// судится и тем, во сколько раз путь по дорогам длиннее прямой, и тем, сколько
        /// метров срезка сбережёт. Малый крюк никого не стоит объезжать дорогой, которой
        /// никто не просил.
        ///
        /// Жадно и по одной, пересчитывая расстояния после каждой: одна срезка чинит
        /// разом десятки пар, и без пересчёта сеть обросла бы десятком дорог в одном и
        /// том же месте.
        ///
        /// Числа подобраны на карте той же плотности (141 метка на 12 км², дерево под
        /// тридцать километров), а не на глаз. Вчетверо длиннее прямой значит, что срезка
        /// сберегает **втрое больше собственной длины** - иначе дорога стоит дороже, чем
        /// экономит. Полтора километра крюка - это уже «оббегать». Потолок в шесть штук
        /// держит прибавку около пяти километров: желающих спрямиться всегда больше, чем
        /// стоит строить, и останавливает именно он.
        /// </summary>
        private const float ShortcutTimes = 4f;

        private const float ShortcutSaves = 1500f;

        private const int ShortcutsMax = 6;

        private static void AddShortcuts(List<Vector3> points, List<Link> links)
        {
            for (var added = 0; added < ShortcutsMax; added++)
            {
                var roads = Roads(points, links);

                var most = 0f;
                int pickA = -1, pickB = -1;

                for (var a = 0; a < points.Count; a++)
                for (var b = a + 1; b < points.Count; b++)
                {
                    var along = roads[a][b];
                    if (along >= float.MaxValue) continue;

                    var straight = Flat(points[a], points[b]);
                    if (along < straight * ShortcutTimes) continue;

                    var saves = along - straight;
                    if (saves < ShortcutSaves || saves <= most) continue;

                    most = saves;
                    pickA = a;
                    pickB = b;
                }

                if (pickA < 0) return;

                links.Add(new Link { A = pickA, B = pickB });
            }
        }

        /// <summary>
        /// Расстояния по дорогам между всеми метками - Дейкстра из каждой.
        ///
        /// Куб от числа меток здесь не страшен: полторы сотни точек считаются за доли
        /// секунды, и считается это по нажатию кнопки, а не в кадре.
        /// </summary>
        private static float[][] Roads(List<Vector3> points, List<Link> links)
        {
            var count = points.Count;

            var near = new List<KeyValuePair<int, float>>[count];
            for (var i = 0; i < count; i++) near[i] = new List<KeyValuePair<int, float>>();

            foreach (var link in links)
            {
                var step = Flat(points[link.A], points[link.B]);
                near[link.A].Add(new KeyValuePair<int, float>(link.B, step));
                near[link.B].Add(new KeyValuePair<int, float>(link.A, step));
            }

            var all = new float[count][];

            for (var from = 0; from < count; from++)
            {
                var best = new float[count];
                var done = new bool[count];
                for (var i = 0; i < count; i++) best[i] = float.MaxValue;

                best[from] = 0f;

                for (var step = 0; step < count; step++)
                {
                    var pick = -1;
                    for (var i = 0; i < count; i++)
                        if (!done[i] && (pick < 0 || best[i] < best[pick])) pick = i;

                    if (pick < 0 || best[pick] == float.MaxValue) break;

                    done[pick] = true;

                    foreach (var hop in near[pick])
                    {
                        var far = best[pick] + hop.Value;
                        if (far < best[hop.Key]) best[hop.Key] = far;
                    }
                }

                all[from] = best;
            }

            return all;
        }

        /// <summary>
        /// Расстояние по карте, а не по прямой в пространстве: подъём на гору дорожку не
        /// удлиняет, её кладут по земле.
        /// </summary>
        private static float Flat(Vector3 a, Vector3 b)
        {
            return new Vector2(a.x - b.x, a.z - b.z).magnitude;
        }
    }
}
