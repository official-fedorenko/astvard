using System.Collections;
using System.Collections.Generic;
using System.Text;
using BepInEx;
using UnityEngine;

namespace AstvardServerMod
{
    /// <summary>
    /// Перепись того, что видит укладка.
    ///
    /// Заведена 23.09.2026 по просьбе хозяина - не как функция мода, а как глаза для
    /// разбора: «скрипт берёт все объекты что есть и выводит на карту». Восемь укладок
    /// подряд чинились по числам из лога, и каждый раз число называло одну беду и молчало
    /// про остальные; чтобы переписывать сами вычисления, надо сперва увидеть их вход
    /// целиком, а не по одной строке.
    ///
    /// **Судит она теми же проверками, что и дорога** - `IsUndergrowth`, `IsBerryBush`,
    /// `BreaksDownTo`, `DropsOnly` - и это в ней главное. Своя копия правил показала бы
    /// не то, что видит укладка, а то, что видит перепись, и разбор ушёл бы в сторону от
    /// беды. Поэтому здесь нет ни одного собственного суждения о породе: только вызовы.
    ///
    /// Зоны она поднимает так же, как укладка: пачками, чужой машинерией
    /// (<see cref="PokeRoadJobZones"/>, <see cref="AppendRoadJobObjects"/>) - для этого
    /// заводится обычное `RoadJob` и кладётся в `_roadJob`. Оттого перепись и укладка не
    /// могут идти разом, и это верно: обе держат землю, а земля одна.
    ///
    /// Рельефа она не трогает **ничем**: ни компиляторов, ни краски, ни сноса. Отката у
    /// неё поэтому нет и не нужно.
    /// </summary>
    public partial class Plugin
    {
        private const string RpcSurveyAsk = "AstvardSurveyAsk";

        /// <summary>Файл переписи: рядом с прочими файлами мода, у сервера.</summary>
        private const string SurveyFile = "astvard-survey.txt";

        /// <summary>Сколько зон поднимать разом. Столько же примерно держит кусок дороги.</summary>
        private const int SurveyZonesAtOnce = 24;

        internal static GameObject SurveyButton;

        private static bool _surveying;

        private static string SurveyPath
        {
            get { return System.IO.Path.Combine(Paths.ConfigPath, SurveyFile); }
        }

        internal static void RegisterSurveyRpcs(ZRoutedRpc rpc)
        {
            rpc.Register<float>(RpcSurveyAsk, OnSurveyAsk);
        }

        // ---------------- клиент ----------------

        /// <summary>Сколько метров вокруг спавна переписывать.</summary>
        internal static float SurveyReach = 1000f;

        internal static void AskSurvey()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcSurveyAsk, SurveyReach);
            player.Message(MessageHud.MessageType.Center,
                $"Перепись вокруг спавна, {SurveyReach:F0} м — жди, это минуты");
        }

        // ---------------- сервер ----------------

        private static void OnSurveyAsk(long sender, float reach)
        {
            if (!ServerAllows(sender)) return;

            var zones = ZoneSystem.instance;
            if (zones == null || !zones.LocationsGenerated)
            {
                SayAboutZone(sender, "Мир ещё не готов — попробуй через несколько секунд.");
                return;
            }

            if (_surveying || _roadJob != null)
            {
                SayAboutZone(sender, "Сервер уже занят землёй — дождись, пока он освободится.");
                return;
            }

            // Не число с потолка: `RoadJobMargin` и так держит полосу в 64 м, а перепись
            // по всему материку подняла бы тысячи зон разом.
            reach = Mathf.Clamp(reach, 64f, 2000f);

            Vector3 centre;
            var what = SurveyCentre(zones, out centre);

            _surveying = true;
            Instance.StartCoroutine(RunSurvey(sender, centre, reach, what));
        }

        /// <summary>
        /// Откуда мерить. Спавн, если он на этой карте есть, - о нём и спрашивали.
        ///
        /// Ищется он там же, где его ищет сеть: в словаре локаций мира, который заполнен с
        /// генерации. Не нашёлся - значит карта без храма, и тогда меряем от середины
        /// мира, а не молчим: перепись не о спавне, спавн в ней только точка отсчёта.
        /// </summary>
        private static string SurveyCentre(ZoneSystem zones, out Vector3 centre)
        {
            foreach (var pair in zones.m_locationInstances)
            {
                var where = pair.Value;
                if (where.m_location == null) continue;
                if (where.m_location.m_name != "StartTemple") continue;

                centre = where.m_position;
                return "спавн";
            }

            centre = Vector3.zero;
            return "середина мира";
        }

        /// <summary>
        /// Обход круга зонами, пачка за пачкой, с записью найденного в файл.
        ///
        /// Пишется он по ходу дела, а не в конце: перепись на километр - это десятки
        /// тысяч строк и минуты работы, а сервер, поднятый из Claude, умирает вместе с
        /// приложением. Оборванная на середине перепись всё равно чего-то стоит;
        /// несохранённая не стоит ничего.
        /// </summary>
        private static IEnumerator RunSurvey(long sender, Vector3 centre, float reach, string what)
        {
            var job = new RoadJob { Id = Random.Range(1, int.MaxValue), Sender = sender };
            var began = Time.realtimeSinceStartup;
            var batches = 0;
            var things = 0;
            var seen = new HashSet<int>();

            var file = new System.IO.StreamWriter(SurveyPath, false, new UTF8Encoding(false));
            try
            {
                _roadJob = job;

                file.WriteLine($"# перепись вокруг «{what}» {centre.x:F0} {centre.z:F0}, "
                               + $"радиус {reach:F0} м");
                file.WriteLine("# род\tимя\tx\tz\tрадиус\tрост");

                // Локации - первыми и без единой зоны: мир знает их с генерации. Это те
                // самые круги, вокруг которых дорога гнётся заранее, и увидеть их надо
                // именно так, как их видит она.
                var places = 0;
                foreach (var pair in ZoneSystem.instance.m_locationInstances)
                {
                    var where = pair.Value;
                    if (where.m_location == null) continue;
                    if (Flat(where.m_position, centre) > reach) continue;

                    file.WriteLine($"location\t{where.m_location.m_name}\t{where.m_position.x:F1}\t"
                                   + $"{where.m_position.z:F1}\t{where.m_location.m_exteriorRadius:F1}\t0");
                    places++;
                }

                file.Flush();
                Log.LogInfo($"[AstvardServerMod] Survey {job.Id}: {places} locations within "
                            + $"{reach:F0} m of {what}, before a single zone was built.");

                var batch = new List<Vector2s>();
                foreach (var zone in SurveyZones(centre, reach))
                {
                    batch.Add(zone);
                    if (batch.Count < SurveyZonesAtOnce) continue;

                    yield return SurveyBatch(job, batch, centre, reach, file, seen,
                                             count => things += count);
                    batches++;
                    batch.Clear();

                    SendRoadNews(sender, job.Id, RoadNews.Progress,
                        $"Перепись: {things} объектов, {batches * SurveyZonesAtOnce} зон", centre, 0f);

                    if (job.Stop || ZNet.instance == null) break;
                }

                if (batch.Count > 0 && !job.Stop && ZNet.instance != null)
                {
                    yield return SurveyBatch(job, batch, centre, reach, file, seen,
                                             count => things += count);
                    batches++;
                }
            }
            finally
            {
                file.Dispose();
                job.Zones.Clear();
                if (_roadJob == job) _roadJob = null;
                _surveying = false;
            }

            Log.LogInfo($"[AstvardServerMod] Survey {job.Id} done: {things} things in {batches} "
                        + $"batches, {Time.realtimeSinceStartup - began:F0} s, written to {SurveyFile}.");
            SendRoadNews(sender, job.Id, RoadNews.Done,
                $"Перепись готова: {things} объектов вокруг «{what}», файл {SurveyFile}", centre, 0f);
        }

        /// <summary>Клетки круга, от середины наружу: оборванная перепись тогда о середине.</summary>
        private static IEnumerable<Vector2s> SurveyZones(Vector3 centre, float reach)
        {
            var home = ZoneSystem.GetZone(centre);
            var rings = Mathf.CeilToInt(reach / 64f);

            for (var ring = 0; ring <= rings; ring++)
                for (var dx = -ring; dx <= ring; dx++)
                    for (var dz = -ring; dz <= ring; dz++)
                    {
                        // Только внешний обод кольца: внутренние отданы прошлым кругам.
                        if (ring > 0 && Mathf.Abs(dx) != ring && Mathf.Abs(dz) != ring) continue;

                        var zone = new Vector2s(home.x + dx, home.y + dz);
                        var at = ZoneSystem.GetZonePos(zone);
                        if (Flat(at, centre) > reach + 64f) continue;

                        yield return zone;
                    }
        }

        /// <summary>Одна пачка зон: поднять, дождаться, переписать, отпустить.</summary>
        private static IEnumerator SurveyBatch(RoadJob job, List<Vector2s> batch, Vector3 centre,
                                               float reach, System.IO.TextWriter file,
                                               HashSet<int> seen, System.Action<int> counted)
        {
            job.Zones.Clear();
            foreach (var zone in batch) job.Zones.Add(zone);

            var deadline = Time.time + 60f;
            while (!RoadJobHasGround(job) && !job.Stop && Time.time < deadline && ZNet.instance != null)
                yield return RoadJobWait;

            deadline = Time.time + 60f;
            while ((RoadJobObjectsPending(job) > 0 || !RoadJobZonesSettled(job))
                   && !job.Stop && Time.time < deadline && ZNet.instance != null)
                yield return RoadJobWait;

            if (job.Stop || ZNet.instance == null) yield break;

            // Кадр на то, что только что проснулось: объекты локаций встают не в тот же миг.
            yield return null;
            if (ZNetScene.instance == null) yield break;

            counted(WriteSurvey(job, centre, reach, file, seen));
            file.Flush();
        }

        /// <summary>
        /// Всё, что стоит в зонах этой пачки, одной строкой каждое.
        ///
        /// Сцена обходится один раз на пачку, а не на зону: `FindObjectsByType` идёт по
        /// всем объектам мира, и звать её на каждую из семисот зон значило бы семьсот
        /// полных обходов.
        /// </summary>
        private static int WriteSurvey(RoadJob job, Vector3 centre, float reach,
                                       System.IO.TextWriter file, HashSet<int> seen)
        {
            var wrote = 0;

            foreach (var thing in Object.FindObjectsByType<Destructible>(FindObjectsSortMode.None))
                if (SurveyOne(job, centre, reach, file, seen, thing == null ? null : thing.gameObject))
                    wrote++;

            foreach (var tree in Object.FindObjectsByType<TreeBase>(FindObjectsSortMode.None))
                if (SurveyOne(job, centre, reach, file, seen, tree == null ? null : tree.gameObject))
                    wrote++;

            foreach (var log in Object.FindObjectsByType<TreeLog>(FindObjectsSortMode.None))
                if (SurveyOne(job, centre, reach, file, seen, log == null ? null : log.gameObject))
                    wrote++;

            foreach (var rock in Object.FindObjectsByType<MineRock>(FindObjectsSortMode.None))
                if (SurveyOne(job, centre, reach, file, seen, rock == null ? null : rock.gameObject))
                    wrote++;

            foreach (var rock in Object.FindObjectsByType<MineRock5>(FindObjectsSortMode.None))
                if (SurveyOne(job, centre, reach, file, seen, rock == null ? null : rock.gameObject))
                    wrote++;

            foreach (var pick in Object.FindObjectsByType<Pickable>(FindObjectsSortMode.None))
                if (SurveyOne(job, centre, reach, file, seen, pick == null ? null : pick.gameObject))
                    wrote++;

            foreach (var nest in Object.FindObjectsByType<CreatureSpawner>(FindObjectsSortMode.None))
                if (SurveyOne(job, centre, reach, file, seen, nest == null ? null : nest.gameObject))
                    wrote++;

            return wrote;
        }

        private static bool SurveyOne(RoadJob job, Vector3 centre, float reach,
                                      System.IO.TextWriter file, HashSet<int> seen, GameObject go)
        {
            if (go == null) return false;

            // Только зоны этой пачки: соседние тоже стоят в сцене, и без этого каждая
            // вещь попала бы в перепись столько раз, сколько пачек её застали.
            var at = go.transform.position;
            if (!job.Zones.Contains(ZoneSystem.GetZone(at))) return false;
            if (Flat(at, centre) > reach) return false;
            if (!seen.Add(go.GetInstanceID())) return false;

            float tall;
            var kind = SurveyKind(go, out tall);
            file.WriteLine($"{kind}\t{Utils.GetPrefabName(go)}\t{at.x:F1}\t{at.z:F1}\t"
                           + $"{FlatRadius(go):F1}\t{tall:F0}");
            return true;
        }

        /// <summary>
        /// Чем вещь считает укладка - и ничем иным.
        ///
        /// Порядок здесь тот же, в каком судит `RoadBlockers` и снос, и вызовы те же. Если
        /// перепись и дорога когда-нибудь разойдутся в ответе об одном валуне, разбор
        /// пойдёт искать беду там, где её нет.
        /// </summary>
        private static string SurveyKind(GameObject go, out float tall)
        {
            tall = 0f;

            // Чужая постройка: ни снос, ни обход её не касаются вовсе.
            if (go.GetComponentInParent<Piece>() != null) return "piece";

            var mine = go.GetComponent<MineRock>();
            if (mine != null) return DropsOnly(mine.m_dropItems, StoneOnly) ? "stone" : "ore";

            var mine5 = go.GetComponent<MineRock5>();
            if (mine5 != null) return DropsOnly(mine5.m_dropItems, StoneOnly) ? "stone" : "ore";

            if (go.GetComponent<CreatureSpawner>() != null) return "nest";

            if (go.GetComponent<Pickable>() != null)
                return IsBerryBush(go) ? "berries" : "litter";

            if (go.GetComponent<TreeBase>() != null || go.GetComponent<TreeLog>() != null)
            {
                tall = Tall(go);
                return "tree";
            }

            var broken = go.GetComponent<Destructible>();
            if (broken == null) return "other";

            if (broken.m_destructibleType == DestructibleType.Tree)
            {
                tall = Tall(go);
                return "tree";
            }

            if (IsUndergrowth(go, out tall)) return "undergrowth";

            tall = Tall(go);

            // То самое различие, на котором всё и держится: «снос это уберёт» против
            // «это останется стоять». Со снесением выключенным первое тоже остаётся.
            return BreaksDownTo(go, WoodAndStone, 0) ? "bare" : "rock";
        }
    }
}
