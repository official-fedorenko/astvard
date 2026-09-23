using System.Collections.Generic;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>
        /// Что дорога обходит, а не сносит.
        ///
        /// The clearing takes out what a road may take out - trees, bare rock, brush -
        /// and leaves standing what it must not: an ore vein, a mud pile, the Mistlands'
        /// bones, and everything inside a location, which is villages, ruins, dolmens
        /// and cave mouths. Until now that was the end of it, and the road was laid
        /// straight through whatever stayed: a copper vein came out standing in the
        /// middle of the paving, and a boulder inside a dolmen's radius kept its ground
        /// while the levelling dug the road out from under it and left it overhead.
        ///
        /// So the road goes round instead. What stands fast is read off as flat circles
        /// and handed to <see cref="Geometry.Detour"/>, which steps the road aside by the
        /// short way round and brings it back on its line.
        ///
        /// Read once per piece, after its zones have built their objects - before that
        /// there is nothing in the scene to find, and a location without its prefab has
        /// no radius to ask for. But what is read in a piece bends the whole road ahead
        /// of it, not that piece alone: см. <see cref="BendRoadAhead"/>.
        /// </summary>
        private const float DetourClearance = 1f;

        /// <summary>The shortest easing, for a step so small a longer one would be silly.</summary>
        private const float DetourMinRamp = 5f;

        /// <summary>Metres of easing for every metre of the step aside.</summary>
        private const float DetourRampPerStep = 3f;

        /// <summary>
        /// How far the road may stray from its line at all.
        ///
        /// The job holds the zones within <see cref="RoadJobMargin"/> = 64 m of the path
        /// it was given, and paint outside them would be written to compilers nobody
        /// loaded. The bent road sits at most this far out, and its paint reaches its own
        /// radius plus the smoothing blend beyond that - about 10 m for a road of two -
        /// so the margin is what sets the ceiling, and this is kept inside it.
        ///
        /// Sixteen stood here until 23.09.2026, and twenty things a lay were refused as
        /// too big to get round: dolmens, whose own radius the road must clear, ask for
        /// more than that. Хозяин сказал прямо: такие надо обходить - сперва до 24 м, а
        /// увидев, что деревни и лагеря просят по сорок с лишним, и до пятидесяти:
        /// «если слишком большое пусть тоже обходят, почему нет?».
        ///
        /// Ради этого `RoadJobMargin` поднят с 40 до 64 м - иначе краска согнутой дороги
        /// легла бы за теми зонами, которые задание держит загруженными. Задание от этого
        /// берёт полосу шире (четыре зоны поперёк вместо трёх), то есть укладка сети идёт
        /// дольше; цена принята сознательно.
        /// </summary>
        private const float DetourMaxStep = 50f;

        /// <summary>
        /// How far an end of a whole road may move aside to get round something.
        ///
        /// A road of the network begins and ends in the middle of a mark, and that middle
        /// is a paved circle of `RingMin` at the very least. Beginning a few metres off it
        /// is invisible; what would be visible is the thing still standing in the road,
        /// which is what this buys. Kept well inside the smallest circle so the road's own
        /// paint stays on the disc.
        ///
        /// Before this, 63 things a lay round stood their ground at the ends of pieces
        /// (23.09.2026, and the same 63 in three layings running), and the near end of
        /// the first piece is the mark itself. С того же дня дорога гнётся целиком (см.
        /// <see cref="BendRoadAhead"/>), так что стыков между кусками этот запас больше
        /// не касается - только двух концов самой дороги.
        /// </summary>
        private const float DetourEndSlack = 12f;

        /// <summary>
        /// The ground a road covers, with room to spare: anything outside this cannot be
        /// in its way whichever side it takes.
        /// </summary>
        private static Rect RoadArea(List<Vector3> path, float reach)
        {
            var slack = reach + DetourMaxStep + DetourClearance + 16f;
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var point in path)
            {
                if (point.x < minX) minX = point.x;
                if (point.x > maxX) maxX = point.x;
                if (point.z < minZ) minZ = point.z;
                if (point.z > maxZ) maxZ = point.z;
            }

            return Rect.MinMaxRect(minX - slack, minZ - slack, maxX + slack, maxZ + slack);
        }

        /// <summary>
        /// Whatever stands fast in an area, as flat circles.
        ///
        /// <paramref name="clearing"/> is what makes the answer honest: the question is
        /// not "would the clearing take this out" but "will it". With the clearing off it
        /// takes out nothing but undergrowth, and "the clearing will see to it" becomes
        /// "nobody will see to it" - the boulder stands in the middle of the paving. The
        /// eighth laying left 1325 of them on 15,9 km, one every dozen metres, and the
        /// owner sent a picture of himself standing on one (23.09.2026).
        /// </summary>
        private static List<Geometry.Blocker> RoadBlockers(Rect area, Vector3 from, Vector3 to,
                                                           Dictionary<string, int> found,
                                                           bool clearing, bool structures)
        {
            var blockers = new List<Geometry.Blocker>();
            if (ZNetScene.instance == null) return blockers;

            foreach (var rock in Object.FindObjectsByType<MineRock>(FindObjectsSortMode.None))
                if (rock != null && !DropsOnly(rock.m_dropItems, StoneOnly))
                    Note(blockers, found, area, rock.gameObject, 0f, "ore");

            foreach (var rock in Object.FindObjectsByType<MineRock5>(FindObjectsSortMode.None))
                if (rock != null && !DropsOnly(rock.m_dropItems, StoneOnly))
                    Note(blockers, found, area, rock.gameObject, 0f, "ore");

            foreach (var thing in Object.FindObjectsByType<Destructible>(FindObjectsSortMode.None))
            {
                if (thing == null) continue;

                // Чужой саженец - его хозяину и двигать.
                var go = thing.gameObject;
                if (go.GetComponentInParent<Piece>() != null) continue;

                // Ягодный куст дорога обходит, а не рубит (просьба хозяина 22.09.2026).
                // Их на сети десятки, не тысячи, так что отступ в пару метров ей по силам;
                // с деревьями так нельзя, и почему - в шапке этого файла.
                if (go.GetComponent<Pickable>() != null)
                {
                    Note(blockers, found, area, go, 0f, "berries");
                    continue;
                }

                // Дерево не обходится никогда, чем бы ни кончился снос: их в восьмой
                // укладке осталось стоять 3815 на одной дороге в 15,9 км, и дорога,
                // шагающая в сторону от каждого, дорогой быть перестанет. Его берёт
                // снос или не берёт никто.
                if (thing.m_destructibleType == DestructibleType.Tree) continue;

                // Ровно тот же вопрос, что задаёт снос, и теми же словами - иначе они
                // разойдутся в суждении об одном валуне. Со снесением выключенным
                // спрашивается то, что снос в этом случае и делает: убирает подлесок и
                // больше ничего.
                var goes = clearing
                    ? BreaksDownTo(go, WoodAndStone, 0)
                    : IsUndergrowth(go, out _);
                if (goes) continue;

                Note(blockers, found, area, go, 0f, "rock");
            }

            // A nest the road ran through would go on sending its greydwarves at whoever
            // walks it. The circle is its own footing, which is small, plus a few metres
            // of room - what matters is not paving over it.
            foreach (var nest in Object.FindObjectsByType<CreatureSpawner>(FindObjectsSortMode.None))
                if (nest != null) Note(blockers, found, area, nest.gameObject, 4f, "nest");

            // Структуры: деревни, руины, дольмены, входы в пещеры. Серверной укладке их
            // тут читать поздно - она обходит их заранее и всей дорогой сразу
            // (<see cref="BendRoadRoundLocations"/>), и спрашивать второй раз значило бы
            // посчитать их дважды.
            //
            // А дорожке, которую кладут руками, только так их и прочесть: список локаций
            // мира живёт у сервера, у клиента он пуст - локации порождает не он. Зато и
            // куска у неё нет: она гнётся целиком, и разгону есть где начаться.
            if (!structures) return blockers;

            foreach (var place in Object.FindObjectsByType<Location>(FindObjectsSortMode.None))
            {
                if (place == null) continue;

                var radius = place.m_noBuild && place.m_noBuildRadiusOverride > 0f
                    ? place.m_noBuildRadiusOverride
                    : place.GetMaxRadius();
                if (radius <= 0f) continue;

                var at = place.transform.position;
                if (Flat(at, from) <= radius || Flat(at, to) <= radius) continue;
                if (!area.Contains(new Vector2(at.x, at.z))) continue;

                blockers.Add(new Geometry.Blocker(new Vec2(at.x, at.z), radius));
                Tally(found, "location");
            }

            return blockers;
        }

        /// <summary>
        /// Дорога, согнутая вокруг деревень и крипт - вся разом и до того, как лёг первый
        /// кусок.
        ///
        /// **Это единственный способ обойти большое, и восемь укладок показали почему.**
        /// Разгон изгиба втрое длиннее отступа, значит деревне, просящей 35 м, нужно сто с
        /// лишним метров дороги ПЕРЕД собой, чтобы плавно с линии сойти. А препятствие в
        /// сцене видно только тогда, когда зона над ним встала, то есть когда до него
        /// меньше девяноста метров - и вся дорога позади к этому мигу уже уложена и не
        /// двигается. Сколько ни гни оставшееся, места под разгон взять негде: в логе это
        /// `went round 0 of … location x28` у дороги, прошедшей деревню насквозь.
        ///
        /// Локации этим не связаны. Где они стоят и какой они ширины, `ZoneSystem` знает с
        /// генерации мира: весь список лежит в `m_locationInstances`, и **ни одной зоны
        /// грузить не надо** - тем же словарём сеть находит камни с надписями, не построив
        /// ни клетки. Значит вся дорога может обойти их ещё до укладки, а куски лягут уже
        /// по согнутой линии, и разгону есть где начаться.
        ///
        /// Ширина берётся у самой локации - `m_exteriorRadius`, то, насколько широко
        /// генератор расчищал под неё землю. Не `m_noBuildRadiusOverride`: тот лежит на
        /// префабе, до сцены его не спросить, а запрет стройки бывает шире самой деревни -
        /// уводить дорогу за него значит уводить её дальше, чем нужно.
        ///
        /// Жилы, гнёзда и валуны так не прочитать, их нет нигде, кроме сцены. Им столько
        /// разгона и не нужно: шаг у них метры, а не десятки метров, и он укладывается
        /// внутри куска. Их берёт <see cref="BendRoadAhead"/>.
        /// </summary>
        private static bool BendRoadRoundLocations(RoadJob job, Dictionary<string, int> found)
        {
            var zones = ZoneSystem.instance;
            if (zones == null || job.Path.Count < 3) return false;

            var reach = job.Smooth ? job.Radius + SmoothBlend(job.Radius) : job.Radius;
            var area = RoadArea(job.Path, reach);

            var blockers = new List<Geometry.Blocker>();
            foreach (var pair in zones.m_locationInstances)
            {
                var where = pair.Value;
                if (where.m_location == null) continue;

                var radius = where.m_location.m_exteriorRadius;
                if (radius <= 0f) continue;

                var at = where.m_position;
                if (!area.Contains(new Vector2(at.x, at.z))) continue;

                // Локация, накрывающая конец дороги, - это та метка, к которой дорога и
                // шла. Обойдя её, дорога не пришла бы никуда.
                if (Flat(at, job.MeantA) <= radius || Flat(at, job.MeantB) <= radius) continue;

                blockers.Add(new Geometry.Blocker(new Vec2(at.x, at.z), radius));
                Tally(found, "location");
            }

            if (blockers.Count == 0) return false;

            var flat = new List<Vec2>(job.Path.Count);
            foreach (var point in job.Path) flat.Add(new Vec2(point.x, point.z));

            var slack = job.EndsInRings
                ? Mathf.Max(0f, Mathf.Min(DetourEndSlack, RingMax - job.Radius))
                : 0f;

            var plan = Geometry.Detour(flat, blockers, job.Radius, DetourClearance,
                                       DetourMinRamp, DetourRampPerStep, DetourMaxStep,
                                       slack, slack);

            NoteRefusals(job, plan);

            job.Bends += plan.Taken;
            if (plan.Taken == 0) return false;

            for (var i = 0; i < job.Path.Count; i++)
                job.Path[i] = new Vector3(plan.Path[i].X, job.Path[i].y, plan.Path[i].Z);

            return true;
        }

        /// <summary>Почему изгиб не взяли - в счётчики задания, они же строка лога.</summary>
        private static void NoteRefusals(RoadJob job, Geometry.DetourPlan plan)
        {
            foreach (var bend in plan.Bends)
            {
                if (bend.Refused == Geometry.BendRefusal.None) continue;

                if (bend.Refused == Geometry.BendRefusal.TooWide)
                {
                    job.TooWide++;
                    job.TooWideWidest = Mathf.Max(job.TooWideWidest, Mathf.Abs(bend.Step));
                    continue;
                }

                // Разгон, не уместившийся за началом, упирается в уже уложенное: та вещь
                // стоит позади, и двигать под неё нечего. Не уместившийся за концом
                // упирается в метку, к которой дорога шла.
                job.Unbent++;
                if (bend.From < 0f) job.UnbentNear++;
                else job.UnbentAtRoadEnd++;
                job.UnbentWidest = Mathf.Max(job.UnbentWidest, Mathf.Abs(bend.Step));
            }
        }

        /// <summary>One thing added to the list, measured by its own colliders.</summary>
        private static void Note(List<Geometry.Blocker> blockers, Dictionary<string, int> found,
                                 Rect area, GameObject go, float least, string kind)
        {
            var at = go.transform.position;
            if (!area.Contains(new Vector2(at.x, at.z))) return;

            var radius = Mathf.Max(least, FlatRadius(go));
            if (radius <= 0f) return;

            blockers.Add(new Geometry.Blocker(new Vec2(at.x, at.z), radius));
            Tally(found, kind);
        }

        /// <summary>
        /// How wide a thing is on the ground, measured from where it stands.
        ///
        /// From the colliders, not from a number per kind: veins, boulders and bones are
        /// all different and the game gives no width. Measured out from the object's own
        /// middle rather than from the middle of its box, because that is the point the
        /// circle is centred on - a boulder whose pivot sits at one end would otherwise
        /// be given a circle that misses half of it.
        /// </summary>
        private static float FlatRadius(GameObject go)
        {
            var at = go.transform.position;
            var widest = 0f;

            foreach (var col in go.GetComponentsInChildren<Collider>())
            {
                if (col == null || !col.enabled || col.isTrigger) continue;

                var box = col.bounds;
                var dx = Mathf.Max(Mathf.Abs(box.max.x - at.x), Mathf.Abs(at.x - box.min.x));
                var dz = Mathf.Max(Mathf.Abs(box.max.z - at.z), Mathf.Abs(at.z - box.min.z));
                var reach = Mathf.Sqrt(dx * dx + dz * dz);
                if (reach > widest) widest = reach;
            }

            return widest;
        }

        /// <summary>
        /// Читается кусок, а гнётся вся дорога от него и до конца.
        ///
        /// Так с 23.09.2026, и вот почему. Разгон изгиба - три метра на каждый метр
        /// отступа, значит весь изгиб занимает вшестеро больше отступа плюс саму вещь: у
        /// деревни, просящей 35 м, это четверть километра. Кусок дороги - 91 м, и оба его
        /// конца прибиты намертво, потому что с них начинается соседний. В такой кусок
        /// помещается отступ метров до четырнадцати, и всё, что шире, отказывалось - в
        /// восьмой укладке `went round 0 of … location x28` стояло у дороги, прошедшей
        /// деревню насквозь, а в отказах `widest wanted 41,5 m`. Ни один предел тут ни
        /// при чём: мешала длина куска.
        ///
        /// Теперь изгиб пишется в `job.Path` целиком, а кладётся по-прежнему кусками:
        /// нынешний кусок получает начало разгона, остальное достаётся тем, что придут
        /// следом. Резать кусок ради места больше не нужно, и той машинерии тут больше
        /// нет.
        ///
        /// **Читать при этом можно только нынешний кусок** - только над его землёй зоны
        /// встали и в сцене есть объекты. Следующий кусок прочтёт своё и согнёт ещё раз,
        /// уже поверх согнутого; вещь, которую обошли, к тому времени стоит в стороне от
        /// дороги, шаг для неё выходит нулевым, и второго изгиба не будет.
        ///
        /// Не двигается ровно одна точка - та, где кончился предыдущий кусок: ступенька
        /// в мощении видна, а начало дороги в трёх метрах от середины метки - нет.
        /// </summary>
        /// <param name="piece">Кусок, над чьей землёй зоны уже встали: только его и читаем.</param>
        /// <param name="first">Где этот кусок начинается в пути: дальше него всё вольно двигаться.</param>
        /// <returns>Изменился ли путь.</returns>
        private static bool BendRoadAhead(RoadJob job, List<Vector3> piece, int first,
                                          Dictionary<string, int> found)
        {
            if (piece.Count < 3 || job.Path.Count - first < 3) return false;

            var blockers = RoadBlockers(RoadArea(piece, job.Radius), job.MeantA, job.MeantB,
                                        found, job.Clear, false);
            if (blockers.Count == 0) return false;

            var rest = job.Path.GetRange(first, job.Path.Count - first);
            var flat = new List<Vec2>(rest.Count);
            foreach (var point in rest) flat.Add(new Vec2(point.x, point.z));

            // Сдвинуть конец дороги можно настолько, насколько круг метки сможет за ним
            // дорасти: круг читает, куда отошли её дороги, и накрывает их (23.09.2026,
            // решение хозяина). Потолок у круга `RingMax`, дальше его не пускают зоны
            // задания.
            //
            // Двенадцать метров выбраны по логу шестой укладки: нужные шаги шли от 3,2 до
            // 15 м с медианой 10, и 36 из 40 укладываются в двенадцать. Запас в шесть,
            // стоявший тут до того, брал четыре из сорока.
            var slack = job.EndsInRings
                ? Mathf.Max(0f, Mathf.Min(DetourEndSlack, RingMax - job.Radius))
                : 0f;

            // Начало пути свободно только у первого куска: у прочих это стык с уложенным.
            var plan = Geometry.Detour(flat, blockers, job.Radius, DetourClearance,
                                       DetourMinRamp, DetourRampPerStep, DetourMaxStep,
                                       first == 0 ? slack : 0f, slack);

            NoteRefusals(job, plan);

            job.Bends += plan.Taken;
            if (plan.Taken == 0) return false;

            for (var i = 0; i < rest.Count; i++)
                job.Path[first + i] = new Vector3(plan.Path[i].X, job.Path[first + i].y,
                                                  plan.Path[i].Z);

            return true;
        }

        // ---------------- дорожка, которую кладут руками ----------------

        /// <summary>
        /// Препятствия рядом с проекцией дорожки, снятые не чаще, чем нужно.
        ///
        /// The projection is redrawn whenever the player moves a tenth of a metre, and
        /// reading what stands fast is five sweeps of the whole scene - at that rate it
        /// would be the most expensive thing in the mod. None of it moves, so the answer
        /// is kept: read over a good deal more ground than the road covers, and read
        /// again only when the road leaves that ground or the answer has stood a while.
        /// </summary>
        private static readonly List<Geometry.Blocker> HandBlockers = new List<Geometry.Blocker>();

        private static readonly Dictionary<string, int> HandFound = new Dictionary<string, int>();

        private static Rect _handBlockersFor;

        private static float _handBlockersWhen = float.MinValue;

        /// <summary>
        /// Со снесением или без него было прочитано то, что лежит в <see cref="HandBlockers"/>.
        ///
        /// Ответ от этого меняется целиком - выключенный снос добавляет в препятствия все
        /// валуны, - так что переключатель обязан читаться заново, а не ждать своей
        /// секунды.
        /// </summary>
        private static bool _handBlockersClearing;

        /// <summary>
        /// How long a reading stands before it is taken again.
        ///
        /// Long, because nothing it reads moves on its own and the sweep is not cheap.
        /// What it can miss is a vein somebody mined out or a zone that finished loading
        /// while the projection was up, and both are put right by the reading the laying
        /// itself takes, which never uses what is kept here.
        /// </summary>
        private const float HandBlockersHold = 8f;

        /// <summary>How much more ground is read than the road needs, so a step does not read it again.</summary>
        private const float HandBlockersSlack = 32f;

        /// <summary>What the road in hand goes round, for the word at the end of it.</summary>
        private static int _handBends;

        private static int _handRefused;

        private static List<Geometry.Blocker> HandBlockersFor(List<Vector3> path, float reach,
                                                              Vector3 from, Vector3 to, bool fresh)
        {
            var clearing = RoadClearingActive;
            var want = RoadArea(path, reach);
            var kept = !fresh
                       && clearing == _handBlockersClearing
                       && Time.realtimeSinceStartup - _handBlockersWhen < HandBlockersHold
                       && want.xMin >= _handBlockersFor.xMin && want.xMax <= _handBlockersFor.xMax
                       && want.yMin >= _handBlockersFor.yMin && want.yMax <= _handBlockersFor.yMax;
            if (kept) return HandBlockers;

            var area = Rect.MinMaxRect(want.xMin - HandBlockersSlack, want.yMin - HandBlockersSlack,
                                       want.xMax + HandBlockersSlack, want.yMax + HandBlockersSlack);

            HandFound.Clear();
            HandBlockers.Clear();
            HandBlockers.AddRange(RoadBlockers(area, from, to, HandFound, clearing, true));
            _handBlockersClearing = clearing;
            _handBlockersFor = area;
            _handBlockersWhen = Time.realtimeSinceStartup;
            return HandBlockers;
        }

        /// <summary>
        /// A road laid by hand bent round what it must not pave over - the same going
        /// round the server does for a long one.
        ///
        /// Called from the projection and from the laying both, and that is the point of
        /// it. A road that quietly went round something the projection had drawn running
        /// straight through would be worse than one that paved it over: the straight one
        /// is at least the road that was shown. The two are the same curve at two
        /// samplings - half a metre for the projection, a metre for the paint - and since
        /// the going round is worked out in metres along the road, they come out the same
        /// shape.
        ///
        /// A pad is a single point and comes back untouched, which is right: a disc has
        /// no line to step aside from.
        /// </summary>
        /// <param name="fresh">The laying reads the ground again; the projection may use what is kept.</param>
        private static void BendRoadByHand(List<Vector3> path, float radius, bool fresh)
        {
            _handBends = 0;
            _handRefused = 0;
            if (path.Count < 3 || !IsRoadDetour) return;

            var reach = RoadSmoothingActive ? radius + SmoothBlend(radius) : radius;
            var blockers = HandBlockersFor(path, reach, path[0], path[path.Count - 1], fresh);
            if (blockers.Count == 0) return;

            var flat = new List<Vec2>(path.Count);
            foreach (var point in path) flat.Add(new Vec2(point.x, point.z));

            var plan = Geometry.Detour(flat, blockers, radius, DetourClearance,
                                       DetourMinRamp, DetourRampPerStep, DetourMaxStep);

            _handBends = plan.Taken;
            foreach (var bend in plan.Bends)
                if (bend.Refused != Geometry.BendRefusal.None) _handRefused++;

            if (plan.Taken == 0) return;

            for (var i = 0; i < path.Count; i++)
                path[i] = new Vector3(plan.Path[i].X, path[i].y, plan.Path[i].Z);
        }

        /// <summary>What a hand-laid road says about going round, at the end of it.</summary>
        private static string HandDetourNote()
        {
            if (_handBends == 0 && _handRefused == 0) return "";

            var note = _handBends > 0 ? $", обойдено: {_handBends}" : "";
            if (_handRefused > 0) note += $", обойти не вышло: {_handRefused}";
            return note;
        }

        /// <summary>What the going round came to, for the line the job writes when it ends.</summary>
        private static string DetourNote(RoadJob job, Dictionary<string, int> found)
        {
            if (job.Bends == 0 && job.TooWide == 0 && job.Unbent == 0 && found.Count == 0) return "";

            var note = $", went round {job.Bends}";
            if (found.Count > 0) note += $" of {Listing(found)}";
            if (job.TooWide > 0)
                note += $", {job.TooWide} too big to get round (widest wanted {job.TooWideWidest:F1} m)";
            if (job.Unbent > 0)
            {
                note += $", {job.Unbent} left standing at an end of the road";
                note += $" ({job.UnbentNear} behind what was already laid, {job.UnbentAtRoadEnd}"
                        + $" past the far end, widest wanted {job.UnbentWidest:F1} m)";
            }

            return note;
        }
    }
}
