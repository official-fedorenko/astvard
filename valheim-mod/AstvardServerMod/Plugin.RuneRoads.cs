using System.Collections;
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

            player.Message(MessageHud.MessageType.Center, "Считаю камни. Нажми ещё раз, чтобы класть");
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
        private enum MarkKind { None, Stone, Spawn, Boss }

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

        private static MarkKind KindOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return MarkKind.None;

            var lower = name.ToLowerInvariant();
            if (lower == SpawnSpot) return MarkKind.Spawn;
            if (BossSpots.Contains(lower)) return MarkKind.Boss;

            // `DrakeLorestone` в горах - такой же читаемый камень, как `Runestone_*`,
            // только назван иначе; без «lorestone» он выпадал из сети молча. Vegvisir
            // отдельной локацией не кладётся вовсе, но имя оставлено: оно даром.
            return lower.Contains("runestone") || lower.Contains("lorestone")
                   || lower.Contains("vegvisir")
                ? MarkKind.Stone
                : MarkKind.None;
        }

        /// <summary>Круги ставятся в этом порядке: спавн, алтари, камни.</summary>
        private static int Rank(MarkKind kind)
        {
            return kind == MarkKind.Spawn ? 0 : kind == MarkKind.Boss ? 1 : 2;
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

            int stones = 0, bosses = 0;
            var home = false;
            foreach (var mark in marks)
            {
                if (mark.Kind == MarkKind.Stone) stones++;
                else if (mark.Kind == MarkKind.Boss) bosses++;
                else if (mark.Kind == MarkKind.Spawn) home = true;
            }

            var net = Whole(marks);

            said.Append($"Камней: {stones}, алтарей: {bosses}, спавн: ");
            said.Append(home ? "нашёлся" : "не нашёлся");
            said.Append($". Сеть — {net.Metres / 1000f:0.0} км");
            if (net.Shortcuts > 0)
                said.Append($", в ней срезок {net.Shortcuts} на "
                            + $"{net.ShortcutMetres / 1000f:0.0} км");

            said.Append($". Кругов {marks.Count}: {Rings(marks)}.");

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

            Log.LogInfo($"[AstvardServerMod] Runes: {said}");

            if (census.Count == 0) return;

            // Перепись всего материка - в лог и только в лог. Имя, которого нет в нашем
            // отборе, иначе не увидеть ниоткуда: сеть промолчит о том, чего не взяла.
            var all = new System.Text.StringBuilder();
            foreach (var pair in census)
            {
                if (all.Length > 0) all.Append(", ");
                all.Append(pair.Key).Append(' ').Append(pair.Value);
            }

            Log.LogInfo($"[AstvardServerMod] Runes: this land holds — {all}");
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
        }

        private static RuneJob _runeJob;

        private static void OnRunesStop(long sender)
        {
            if (!ServerAllows(sender) || _runeJob == null) return;

            _runeJob.Stop = true;
            if (_roadJob != null) _roadJob.Stop = true;

            Log.LogInfo($"[AstvardServerMod] Runes: stop asked by {SenderName(sender)}.");
        }

        private static void OnRunesLay(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!ServerAllows(sender))
            {
                SayAboutZone(sender, "Дороги по камням кладёт сервер только админам.");
                return;
            }

            if (_runeJob != null)
            {
                SayAboutZone(sender, "Сервер уже кладёт сеть — останови её или дождись.");
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
                Log.LogWarning($"[AstvardServerMod] Runes: unreadable request from "
                               + $"{SenderName(sender)}: {bad.Message}");
                return;
            }

            var zones = ZoneSystem.instance;
            var world = WorldGenerator.instance;
            if (zones == null || world == null) return;

            var marks = MarksOn(zones, world, x, z, out _, null);
            if (marks.Count == 0)
            {
                SayAboutZone(sender, "Ни камней, ни алтарей на этом материке — класть не к чему.");
                return;
            }

            var job = new RuneJob { Sender = sender, Stones = marks.Count };

            // Круги вперёд дорог: если укладку остановят на полпути, у меток уже будет
            // то, ради чего всё затевалось, а недостающая дорога - это просто дорога.
            // А среди кругов спавн и алтари вперёд камней, по той же причине.
            marks.Sort((a, b) => Rank(a.Kind).CompareTo(Rank(b.Kind)));

            foreach (var mark in marks)
                job.Queue.Add(CircleJob(mark, paved, smooth, clear, torch, spacing,
                                        creator, platform));

            var net = Whole(marks);
            var home = SpawnAt(marks);
            if (home.HasValue)
                net.Edges.Sort((a, b) => Touches(b, home.Value).CompareTo(Touches(a, home.Value)));

            foreach (var edge in net.Edges)
            {
                job.Metres += Flat(edge.Key, edge.Value);
                job.Queue.Add(RoadBetween(edge.Key, edge.Value, radius, width, paved, smooth,
                                          clear, torch, spacing, creator, platform));
            }

            _runeJob = job;
            Instance.StartCoroutine(RunRuneJob(job));

            var said = $"Кругов {job.Stones}, дорог {job.Metres / 1000f:0.0} км"
                       + (net.Shortcuts > 0 ? $" (срезок {net.Shortcuts})" : "")
                       + $", заданий {job.Queue.Count}. Начал.";
            SayAboutZone(sender, said);
            Log.LogInfo($"[AstvardServerMod] Runes: {said} Asked by {SenderName(sender)}.");
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

                // Кольцо факелов ставится после круга, а не вместе с ним: укладка
                // расставляет факелы вдоль пути, а путь у круга - полметра, и все они
                // встали бы кучкой у самого камня.
                if (piece.Ring > 0 && !job.Stop) RingTorches(piece);

                job.Done++;

                // Раз в десяток заданий, а не на каждое: сети из ста сорока строк в логе
                // никто не читает, а по одной в минуту видно, что дело идёт.
                if (job.Done % 10 == 0 || job.Done == job.Queue.Count)
                {
                    var said = $"Камни: сделано {job.Done} из {job.Queue.Count}, "
                               + $"{Time.realtimeSinceStartup - began:0} с.";
                    SayAboutZone(job.Sender, said);
                    Log.LogInfo($"[AstvardServerMod] Runes: {said}");
                }
            }

            var how = job.Stop ? "остановлено" : "готово";
            SayAboutZone(job.Sender, $"Камни: {how}, {job.Done} из {job.Queue.Count} за "
                                     + $"{(Time.realtimeSinceStartup - began) / 60f:0.#} мин.");
            Log.LogInfo($"[AstvardServerMod] Runes: {how}, {job.Done} of {job.Queue.Count}.");

            _runeJob = null;
        }

        /// <summary>Мощёный круг вокруг метки и кольцо факелов по нему.</summary>
        private static RoadJob CircleJob(Mark mark, bool paved, bool smooth, bool clear,
                                         int torch, float spacing, long creator, string platform)
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
            };
        }

        private static RoadJob RoadBetween(Vector3 from, Vector3 to, float radius, float width,
                                           bool paved, bool smooth, bool clear, int torch,
                                           float spacing, long creator, string platform)
        {
            // Тем же сэмплером, что и обычная дорожка: точка на метр пути. Своя кривая
            // здесь разошлась бы с той, которую кладёт кнопка, на первой же правке.
            var path = new List<Vector3>();
            RoadPoints(from, to, 0f, 1f, path);

            return new RoadJob
            {
                Id = Random.Range(1, int.MaxValue),
                Path = path,
                Radius = radius,
                Width = width,
                Paint = paved ? Heightmap.m_paintMaskPaved : Heightmap.m_paintMaskDirt,
                Smooth = smooth,
                Clear = clear,
                Torch = torch,
                Spacing = spacing,
                Creator = creator,
                Platform = platform,
            };
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
                Log.LogInfo($"[AstvardServerMod] Runes: ring at {piece.Centre.x:F0},"
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
            if (land.Count == 0) return marks;

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

                marks.Add(new Mark
                {
                    At = where.m_position,
                    Kind = kind,
                    Name = name,
                    Radius = RingFor(where.m_location.m_exteriorRadius),
                });
            }

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

        /// <summary>Сеть целиком: что класть и что о ней сказать.</summary>
        private sealed class Network
        {
            public readonly List<KeyValuePair<Vector3, Vector3>> Edges =
                new List<KeyValuePair<Vector3, Vector3>>();

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
            var points = new List<Vector3>(marks.Count);
            foreach (var mark in marks) points.Add(mark.At);

            var links = TreeLinks(points);
            AddSpawnLinks(marks, points, links);

            var was = links.Count;
            AddShortcuts(points, links);

            var net = new Network { Shortcuts = links.Count - was };
            for (var i = 0; i < links.Count; i++)
            {
                var from = points[links[i].A];
                var to = points[links[i].B];
                var step = Flat(from, to);

                net.Metres += step;
                if (i >= was) net.ShortcutMetres += step;

                net.Edges.Add(new KeyValuePair<Vector3, Vector3>(from, to));
            }

            return net;
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

        private static Vector3? SpawnAt(List<Mark> marks)
        {
            foreach (var mark in marks)
                if (mark.Kind == MarkKind.Spawn) return mark.At;

            return null;
        }

        private static int Touches(KeyValuePair<Vector3, Vector3> edge, Vector3 at)
        {
            return edge.Key == at || edge.Value == at ? 1 : 0;
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
