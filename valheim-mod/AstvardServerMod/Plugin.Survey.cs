using System.Collections;
using System.Collections.Generic;
using System.Text;
using BepInEx;
using Jotunn.Managers;
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

        private const string RpcSurveyPins = "AstvardSurveyPins";

        /// <summary>
        /// Число в файл - всегда с точкой, чем бы ни была локаль сервера.
        ///
        /// У нашего она русская, и первая же перепись вышла с запятыми (`-67,3`): её
        /// собственный рисовальщик прочитал ноль строк и не пожаловался. Файл с данными
        /// читает не человек, а следующий инструмент, и он всегда ждёт точку.
        /// </summary>
        private static string Num(float value)
        {
            return value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Что стоит отметить на карте игрока: то, вокруг чего дорога гнётся.</summary>
        private struct SurveyMark
        {
            public string Kind;

            public string Name;

            public float X;

            public float Z;
        }

        /// <summary>
        /// Потолок меток. Деревьев в километре вокруг спавна 45 209, и ими карту не
        /// метят вовсе; обходимого там около трёх тысяч, так что это защита от нелепого,
        /// а не от обычного.
        /// </summary>
        private const int SurveyMarksMost = 6000;

        private const int SurveyMarksPerPacket = 800;

        /// <summary>Файл переписи: рядом с прочими файлами мода, у сервера.</summary>
        private const string SurveyFile = "astvard-survey.txt";

        /// <summary>Сколько зон поднимать разом. Столько же примерно держит кусок дороги.</summary>
        private const int SurveyZonesAtOnce = 24;

        internal static GameObject SurveyButton;

        internal static GameObject SurveyClearButton;

        private static bool _surveying;

        private static string SurveyPath
        {
            get { return System.IO.Path.Combine(Paths.ConfigPath, SurveyFile); }
        }

        internal static void RegisterSurveyRpcs(ZRoutedRpc rpc)
        {
            rpc.Register<float, float>(RpcSurveyAsk, OnSurveyAsk);
            rpc.Register<ZPackage>(RpcSurveyPins, OnSurveyPins);
        }

        // ---------------- клиент ----------------

        internal static void AskSurvey()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            // Откуда стоим, тот материк и переписываем - так же, как сеть обводит тот
            // материк, на котором стоит админ. Круг вокруг спавна, стоявший тут сперва,
            // хозяин отверг: «пусть только переписывает материк на котором я стою».
            var at = player.transform.position;
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcSurveyAsk, at.x, at.z);
            player.Message(MessageHud.MessageType.Center,
                "Перепись материка, на котором стоишь — жди, это минуты");
        }

        /// <summary>Метки, поставленные переписью. Не сохраняются: их ставят заново.</summary>
        private static readonly List<Minimap.PinData> SurveyPins = new List<Minimap.PinData>();

        internal static int SurveyPinCount
        {
            get { return SurveyPins.Count; }
        }

        internal static void ClearSurveyPins()
        {
            var map = Minimap.instance;
            if (map != null)
                foreach (var pin in SurveyPins)
                    if (pin != null) map.RemovePin(pin);

            SurveyPins.Clear();
            RefreshMenu();
        }

        /// <summary>
        /// Метки переписи на карту.
        ///
        /// Значок берётся по роду, чтобы карту можно было читать глазами, а не наведением:
        /// у `Icon3` уже живут зоны сортировки, так что переписи он не достаётся.
        /// </summary>
        private static void OnSurveyPins(long sender, ZPackage pkg)
        {
            if (GUIManager.IsHeadless()) return;

            bool first;
            int count;
            try
            {
                first = pkg.ReadBool();
                count = pkg.ReadInt();
            }
            catch (System.Exception e)
            {
                Log.LogWarning($"[AstvardServerMod] Survey pins unreadable: {e.Message}");
                return;
            }

            if (first) ClearSurveyPins();

            var map = Minimap.instance;
            if (map == null) return;

            for (var i = 0; i < count; i++)
            {
                string kind, name;
                float x, z;
                try
                {
                    kind = pkg.ReadString();
                    name = pkg.ReadString();
                    x = pkg.ReadSingle();
                    z = pkg.ReadSingle();
                }
                catch (System.Exception e)
                {
                    Log.LogWarning($"[AstvardServerMod] Survey pin {i} unreadable: {e.Message}");
                    break;
                }

                SurveyPins.Add(map.AddPin(new Vector3(x, 0f, z), PinFor(kind),
                    $"{MarkTitle(kind)}: {name}", false, false));
            }

            Log.LogInfo($"[AstvardServerMod] Survey pins: {SurveyPins.Count} on the map.");
            RefreshMenu();
        }

        private static Minimap.PinType PinFor(string kind)
        {
            if (kind == "location") return Minimap.PinType.Icon0;
            if (kind == "ore") return Minimap.PinType.Icon1;
            if (kind == "rock") return Minimap.PinType.Icon2;
            return Minimap.PinType.Icon4;
        }

        private static string MarkTitle(string kind)
        {
            if (kind == "location") return "Локация";
            if (kind == "ore") return "Руда";
            if (kind == "rock") return "Не сносится";
            if (kind == "nest") return "Гнездо";
            if (kind == "berries") return "Ягодник";
            return kind;
        }

        // ---------------- сервер ----------------

        private static void OnSurveyAsk(long sender, float x, float z)
        {
            if (!ServerAllows(sender)) return;

            var zones = ZoneSystem.instance;
            var world = WorldGenerator.instance;
            if (zones == null || world == null || !zones.LocationsGenerated)
            {
                SayAboutZone(sender, "Мир ещё не готов — попробуй через несколько секунд.");
                return;
            }

            if (_surveying || _roadJob != null)
            {
                SayAboutZone(sender, "Сервер уже занят землёй — дождись, пока он освободится.");
                return;
            }

            // Тот же обвод материка, которым сеть находит свои камни: высота спрашивается
            // у генератора, значит ни одной зоны грузить не надо и ответ одинаков для
            // любой точки мира.
            var land = Continent(world, zones.m_waterLevel, x, z);
            if (land.Count == 0)
            {
                SayAboutZone(sender, "Ты стоишь не на суше — материк отсюда не обвести.");
                return;
            }

            _surveying = true;
            Instance.StartCoroutine(RunSurvey(sender, new Vector3(x, 0f, z), land));
        }

        /// <summary>
        /// Клетки зон, накрывающие материк, от игрока наружу.
        ///
        /// Наружу - не ради вида: перепись материка это минуты, и оборванная на середине
        /// должна рассказывать про то место, где стоят, а не про случайный его угол.
        /// </summary>
        private static List<Vector2s> SurveyZonesOf(HashSet<long> land, Vector3 from)
        {
            var zones = new HashSet<Vector2s>();
            foreach (var cell in land)
            {
                var cx = (int)(cell >> 32);
                var cz = (int)(cell & 0xFFFFFFFF);
                zones.Add(ZoneSystem.GetZone(new Vector3(cx * LandStep, 0f, cz * LandStep)));
            }

            var list = new List<Vector2s>(zones);
            list.Sort((a, b) => Flat(ZoneSystem.GetZonePos(a), from)
                .CompareTo(Flat(ZoneSystem.GetZonePos(b), from)));
            return list;
        }

        /// <summary>
        /// Обход материка зонами, пачка за пачкой, с записью найденного в файл.
        ///
        /// Пишется он по ходу дела, а не в конце: перепись материка - это сотни тысяч
        /// строк и минуты работы, а сервер, поднятый из Claude, умирает вместе с
        /// приложением. Оборванная на середине перепись всё равно чего-то стоит;
        /// несохранённая не стоит ничего.
        /// </summary>
        private static IEnumerator RunSurvey(long sender, Vector3 centre, HashSet<long> land)
        {
            var job = new RoadJob { Id = Random.Range(1, int.MaxValue), Sender = sender };
            var began = Time.realtimeSinceStartup;
            var batches = 0;
            var things = 0;
            var seen = new HashSet<int>();

            // Метки для карты игрока. Деревьями карту не метят - их 45 209 на километр
            // вокруг спавна, и хозяин сказал прямо: «только то что мы обносим кругом и
            // обходим дорожкой». Значит локации и то, что дорога не сносит.
            var marks = new List<SurveyMark>();

            var file = new System.IO.StreamWriter(SurveyPath, false, new UTF8Encoding(false));
            try
            {
                _roadJob = job;

                var sweep = SurveyZonesOf(land, centre);
                var area = land.Count * LandStep * LandStep / 1000000f;

                file.WriteLine($"# перепись материка от {Num(centre.x)} {Num(centre.z)}, "
                               + $"{area:0.0} км², зон {sweep.Count}");
                file.WriteLine("# род\tимя\tx\tz\tрадиус\tрост");

                SayAboutZone(sender, $"Материк {area:0.0} км², зон {sweep.Count} — "
                                     + $"это примерно {sweep.Count * 155 / 888 / 60 + 1} мин.");

                // Локации - первыми и без единой зоны: мир знает их с генерации. Это те
                // самые круги, вокруг которых дорога гнётся заранее, и увидеть их надо
                // именно так, как их видит она.
                var places = 0;
                foreach (var pair in ZoneSystem.instance.m_locationInstances)
                {
                    var where = pair.Value;
                    if (where.m_location == null) continue;
                    if (!OnLand(land, where.m_position.x, where.m_position.z)) continue;

                    file.WriteLine($"location\t{where.m_location.m_name}\t{Num(where.m_position.x)}\t"
                                   + $"{Num(where.m_position.z)}\t{Num(where.m_location.m_exteriorRadius)}\t0");
                    places++;

                    if (marks.Count < SurveyMarksMost)
                        marks.Add(new SurveyMark
                        {
                            Kind = "location",
                            Name = where.m_location.m_name,
                            X = where.m_position.x,
                            Z = where.m_position.z,
                        });
                }

                file.Flush();
                Log.LogInfo($"[AstvardServerMod] Survey {job.Id}: {area:0.0} km2 of land, "
                            + $"{sweep.Count} zones, {places} locations on it — before a "
                            + "single zone was built.");

                var batch = new List<Vector2s>();
                foreach (var zone in sweep)
                {
                    batch.Add(zone);
                    if (batch.Count < SurveyZonesAtOnce) continue;

                    yield return SurveyBatch(job, batch, land, file, seen, marks,
                                             count => things += count);
                    batches++;
                    batch.Clear();

                    // Своим каналом, а не каналом дорожки: клиент принимает вести о
                    // задании, только если сам его заказывал, а номера переписи он не
                    // знает. Первая же перепись оттого прошла молча от начала до конца,
                    // и хозяин сказал «запустил, но пока что ничего не происходит».
                    if (batches % 6 == 0)
                        SayAboutZone(sender, $"Перепись: {things} объектов, "
                                             + $"зон {batches * SurveyZonesAtOnce}.");

                    if (job.Stop || ZNet.instance == null) break;
                }

                if (batch.Count > 0 && !job.Stop && ZNet.instance != null)
                {
                    yield return SurveyBatch(job, batch, land, file, seen, marks,
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
                        + $"batches, {Time.realtimeSinceStartup - began:F0} s, written to {SurveyFile}"
                        + $", {marks.Count} marks for the map.");

            // Сказать, что метки урезаны, обязательно: карта, на которой отмечена половина
            // материка, врёт молча, а число в сообщении - единственное, что это ловит.
            var capped = marks.Count >= SurveyMarksMost
                ? $" (предел меток {SurveyMarksMost} — дальний край материка не отмечен)"
                : "";

            SayAboutZone(sender, $"Перепись готова: {things} объектов на материке, "
                                 + $"на карте отмечено {marks.Count}{capped}. Файл {SurveyFile}.");
            SendSurveyPins(sender, marks);
        }

        /// <summary>
        /// Метки уезжают игроку пачками, потому что их тысячи, а не десятки.
        ///
        /// Пакет на восемь сотен - это килобайтов двадцать; одним куском ушло бы всё
        /// разом, и на большом радиусе это был бы пакет, которого никто не ждал.
        /// </summary>
        private static void SendSurveyPins(long sender, List<SurveyMark> marks)
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null) return;

            var target = ReplyTarget(sender);
            var sent = 0;
            while (sent < marks.Count || sent == 0)
            {
                var take = Mathf.Min(SurveyMarksPerPacket, marks.Count - sent);
                var pkg = new ZPackage();

                // Первая пачка снимает прежние метки: перепись показывает то, что нашла
                // сейчас, а не сумму всех прошлых.
                pkg.Write(sent == 0);
                pkg.Write(take);
                for (var i = 0; i < take; i++)
                {
                    var mark = marks[sent + i];
                    pkg.Write(mark.Kind);
                    pkg.Write(mark.Name ?? "");
                    pkg.Write(mark.X);
                    pkg.Write(mark.Z);
                }

                rpc.InvokeRoutedRPC(target, RpcSurveyPins, pkg);
                sent += take;
                if (take == 0) break;
            }
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
        private static IEnumerator SurveyBatch(RoadJob job, List<Vector2s> batch, HashSet<long> land,
                                               System.IO.TextWriter file,
                                               HashSet<int> seen, List<SurveyMark> marks,
                                               System.Action<int> counted)
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

            counted(WriteSurvey(job, land, file, seen, marks));
            file.Flush();
        }

        /// <summary>
        /// Всё, что стоит в зонах этой пачки, одной строкой каждое.
        ///
        /// Сцена обходится один раз на пачку, а не на зону: `FindObjectsByType` идёт по
        /// всем объектам мира, и звать её на каждую из семисот зон значило бы семьсот
        /// полных обходов.
        /// </summary>
        private static int WriteSurvey(RoadJob job, HashSet<long> land,
                                       System.IO.TextWriter file, HashSet<int> seen,
                                       List<SurveyMark> marks)
        {
            var wrote = 0;

            foreach (var thing in Object.FindObjectsByType<Destructible>(FindObjectsSortMode.None))
                if (SurveyOne(job, land, file, seen, marks, thing == null ? null : thing.gameObject))
                    wrote++;

            foreach (var tree in Object.FindObjectsByType<TreeBase>(FindObjectsSortMode.None))
                if (SurveyOne(job, land, file, seen, marks, tree == null ? null : tree.gameObject))
                    wrote++;

            foreach (var log in Object.FindObjectsByType<TreeLog>(FindObjectsSortMode.None))
                if (SurveyOne(job, land, file, seen, marks, log == null ? null : log.gameObject))
                    wrote++;

            foreach (var rock in Object.FindObjectsByType<MineRock>(FindObjectsSortMode.None))
                if (SurveyOne(job, land, file, seen, marks, rock == null ? null : rock.gameObject))
                    wrote++;

            foreach (var rock in Object.FindObjectsByType<MineRock5>(FindObjectsSortMode.None))
                if (SurveyOne(job, land, file, seen, marks, rock == null ? null : rock.gameObject))
                    wrote++;

            foreach (var pick in Object.FindObjectsByType<Pickable>(FindObjectsSortMode.None))
                if (SurveyOne(job, land, file, seen, marks, pick == null ? null : pick.gameObject))
                    wrote++;

            foreach (var nest in Object.FindObjectsByType<CreatureSpawner>(FindObjectsSortMode.None))
                if (SurveyOne(job, land, file, seen, marks, nest == null ? null : nest.gameObject))
                    wrote++;

            return wrote;
        }

        private static bool SurveyOne(RoadJob job, HashSet<long> land,
                                      System.IO.TextWriter file, HashSet<int> seen,
                                      List<SurveyMark> marks, GameObject go)
        {
            if (go == null) return false;

            // Только зоны этой пачки: соседние тоже стоят в сцене, и без этого каждая
            // вещь попала бы в перепись столько раз, сколько пачек её застали.
            var at = go.transform.position;
            if (!job.Zones.Contains(ZoneSystem.GetZone(at))) return false;
            if (!OnLand(land, at.x, at.z)) return false;
            if (!seen.Add(go.GetInstanceID())) return false;

            float tall;
            var kind = SurveyKind(go, out tall);
            var name = Utils.GetPrefabName(go);
            file.WriteLine($"{kind}\t{name}\t{Num(at.x)}\t{Num(at.z)}\t"
                           + $"{Num(FlatRadius(go))}\t{tall:F0}");

            if (WorthAMark(kind) && marks.Count < SurveyMarksMost)
                marks.Add(new SurveyMark { Kind = kind, Name = name, X = at.x, Z = at.z });

            return true;
        }

        /// <summary>
        /// Метить ли это на карте игрока.
        ///
        /// Ровно то, что просил хозяин, и ровно то, что делает дорога: круг вокруг метки
        /// и обход того, что не снести. Деревья, подлесок и мелочь на землю карты не
        /// идут - их за километр вокруг спавна восемьдесят пять тысяч, и карта из них
        /// была бы зелёным пятном, а не ответом.
        ///
        /// `bare` сюда не попадает нарочно, хотя со снесением выключенным дорога его
        /// обходит: таких камней там двадцать тысяч, и это отдельный разговор, а не
        /// метка.
        /// </summary>
        private static bool WorthAMark(string kind)
        {
            return kind == "ore" || kind == "rock" || kind == "nest" || kind == "berries";
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
