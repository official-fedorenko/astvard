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

        internal static GameObject RuneStonesButton;

        internal static void RegisterRuneRoadRpcs(ZRoutedRpc rpc)
        {
            rpc.Register<float, float>(RpcRunesAsk, OnRunesAsk);
        }

        internal static void AskRunes()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            var at = player.transform.position;
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcRunesAsk, at.x, at.z);

            player.Message(MessageHud.MessageType.Center, "Прошу сервер поискать камни");
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
