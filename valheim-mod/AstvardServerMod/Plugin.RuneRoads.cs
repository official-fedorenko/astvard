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
        /// Круг вокруг камня и шаг факелов по нему.
        ///
        /// Восемь метров - просьба хозяина. Каждому камню свой круг, даже если камни
        /// стоят кучкой и круги налезут друг на друга: так он и просил, и это честнее -
        /// один круг на двоих выглядел бы как промах укладки, а не как замысел.
        /// </summary>
        private const float StoneRing = 8f;

        private const float StoneTorchStep = 4f;

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

        /// <summary>Что считаем камнем с надписью. Отбор по имени локации, не по префабу.</summary>
        private static bool IsRuneStone(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            var lower = name.ToLowerInvariant();
            return lower.Contains("runestone") || lower.Contains("vegvisir");
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

            var land = Continent(world, zones.m_waterLevel, x, z);
            if (land.Count == 0)
            {
                SayAboutZone(sender, "Ты стоишь не на суше — с воды материк не обвести.");
                return;
            }

            // Породы считаем отдельно от штук: «камней 40» не отличает сорок лорных от
            // сорока меток боссов, а дорожки между ними нужны разные.
            var kinds = new Dictionary<string, int>();
            var spots = new List<Vector3>();

            foreach (var pair in zones.m_locationInstances)
            {
                var where = pair.Value;
                var name = where.m_location != null ? where.m_location.m_name : null;
                if (!IsRuneStone(name)) continue;

                if (!OnLand(land, where.m_position.x, where.m_position.z)) continue;

                int had;
                kinds[name] = (kinds.TryGetValue(name, out had) ? had : 0) + 1;
                spots.Add(where.m_position);
            }

            var said = new System.Text.StringBuilder();
            said.Append($"Материк: {land.Count * LandStep * LandStep / 1000000f:0.0} км². ");

            if (spots.Count == 0)
            {
                said.Append("Камней с надписями на нём не нашлось ни одного. Это не обязательно "
                            + "пусто: если генератор кладёт их растительностью, а не локациями, "
                            + "списка таких камней в игре нет вовсе.");
                Say(sender, said.ToString(), kinds, 0f);
                return;
            }

            var road = TreeLength(spots);
            said.Append($"Камней: {spots.Count}. Кратчайшая сеть между ними — {road / 1000f:0.0} км.");

            Say(sender, said.ToString(), kinds, road);
        }

        private static void Say(long sender, string text, Dictionary<string, int> kinds, float road)
        {
            SayAboutZone(sender, text);

            var said = new System.Text.StringBuilder(text);
            foreach (var pair in kinds) said.Append($" | {pair.Key}: {pair.Value}");

            Log.LogInfo($"[AstvardServerMod] Runes: {said}");
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

            var spots = StonesOn(zones, world, x, z);
            if (spots.Count == 0)
            {
                SayAboutZone(sender, "Камней на этом материке не нашлось — класть не к чему.");
                return;
            }

            var job = new RuneJob { Sender = sender, Stones = spots.Count };

            // Круги вперёд дорог: если укладку остановят на полпути, у камней уже будет
            // то, ради чего всё затевалось, а недостающая дорога - это просто дорога.
            foreach (var stone in spots)
                job.Queue.Add(CircleJob(stone, radius, width, paved, smooth, clear,
                                        torch, creator, platform));

            foreach (var edge in TreeEdges(spots))
            {
                job.Metres += Vector3.Distance(edge.Key, edge.Value);
                job.Queue.Add(RoadBetween(edge.Key, edge.Value, radius, width, paved, smooth,
                                          clear, torch, spacing, creator, platform));
            }

            _runeJob = job;
            Instance.StartCoroutine(RunRuneJob(job));

            var said = $"Камней {job.Stones}, дорог {job.Metres / 1000f:0.0} км, "
                       + $"заданий {job.Queue.Count}. Начал.";
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

        /// <summary>Мощёный круг вокруг камня и кольцо факелов по нему.</summary>
        private static RoadJob CircleJob(Vector3 stone, float radius, float width, bool paved,
                                         bool smooth, bool clear, int torch, long creator,
                                         string platform)
        {
            // Путь из двух точек в полуметре: укладка идёт по отрезкам, и одной точки ей
            // мало - цикл по парам не сделал бы ни шага. Радиус при этом наш, восьмиметровый,
            // и он же задаёт круг.
            var job = new RoadJob
            {
                Id = Random.Range(1, int.MaxValue),
                Path = new List<Vector3> { stone, stone + new Vector3(0.5f, 0f, 0f) },
                Radius = StoneRing,
                Width = StoneRing * 2f,
                Paint = paved ? Heightmap.m_paintMaskPaved : Heightmap.m_paintMaskDirt,
                Smooth = smooth,
                Clear = clear,
                // Факелы вдоль полуметрового пути встали бы кучкой у камня; кольцо ставим
                // сами, ниже.
                Torch = 0,
                Spacing = StoneTorchStep,
                Creator = creator,
                Platform = platform,
                Ring = torch > 0 ? torch : 0,
                Centre = stone,
            };

            return job;
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
            var posts = Geometry.RingPosts(new Vec2(piece.Centre.x, piece.Centre.z),
                                           StoneRing, StoneTorchStep);
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

        /// <summary>Камни этого материка — тот же отбор, что и у отчёта.</summary>
        private static List<Vector3> StonesOn(ZoneSystem zones, WorldGenerator world, float x, float z)
        {
            var land = Continent(world, zones.m_waterLevel, x, z);
            var spots = new List<Vector3>();
            if (land.Count == 0) return spots;

            foreach (var pair in zones.m_locationInstances)
            {
                var where = pair.Value;
                var name = where.m_location != null ? where.m_location.m_name : null;
                if (!IsRuneStone(name)) continue;
                if (!OnLand(land, where.m_position.x, where.m_position.z)) continue;

                spots.Add(where.m_position);
            }

            return spots;
        }

        /// <summary>
        /// Рёбра кратчайшей сети - тот же Прим, что считает её длину в отчёте, только с
        /// запомненными парами.
        /// </summary>
        private static List<KeyValuePair<Vector3, Vector3>> TreeEdges(List<Vector3> spots)
        {
            var edges = new List<KeyValuePair<Vector3, Vector3>>();
            if (spots.Count < 2) return edges;

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
                if (pick != 0) edges.Add(new KeyValuePair<Vector3, Vector3>(spots[from[pick]], spots[pick]));

                for (var i = 0; i < spots.Count; i++)
                {
                    if (inTree[i]) continue;

                    var a = spots[pick];
                    var b = spots[i];
                    var flat = new Vector2(a.x - b.x, a.z - b.z).magnitude;
                    if (flat < best[i]) { best[i] = flat; from[i] = pick; }
                }
            }

            return edges;
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
        /// Длина кратчайшей сети, связывающей все камни, - остовное дерево по Приму.
        ///
        /// Квадрат от числа камней: их десятки, а не тысячи, и городить что-то умнее
        /// значит платить сложностью за время, которого и так нет.
        /// </summary>
        private static float TreeLength(List<Vector3> spots)
        {
            var inTree = new bool[spots.Count];
            var best = new float[spots.Count];
            for (var i = 0; i < spots.Count; i++) best[i] = float.MaxValue;

            best[0] = 0f;
            var total = 0f;

            for (var step = 0; step < spots.Count; step++)
            {
                var pick = -1;
                for (var i = 0; i < spots.Count; i++)
                    if (!inTree[i] && (pick < 0 || best[i] < best[pick])) pick = i;

                if (pick < 0 || best[pick] == float.MaxValue) break;

                inTree[pick] = true;
                total += best[pick];

                for (var i = 0; i < spots.Count; i++)
                {
                    if (inTree[i]) continue;

                    // По земле, а не по прямой в пространстве: подъём на гору дорожку
                    // не удлиняет, её кладут по карте.
                    var a = spots[pick];
                    var b = spots[i];
                    var flat = new Vector2(a.x - b.x, a.z - b.z).magnitude;
                    if (flat < best[i]) best[i] = flat;
                }
            }

            return total;
        }
    }
}
