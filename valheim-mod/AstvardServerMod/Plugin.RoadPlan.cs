using System.Collections.Generic;
using UnityEngine;

namespace AstvardServerMod
{
    /// <summary>
    /// Цена земли для прокладки: то, по чему <see cref="RoadPlan"/> ищет дорогу.
    ///
    /// Порядок работы, и он же просьба хозяина 23.09.2026: «сначала пусть скрипт найдёт
    /// всё это что находит, потом начинает от спавна и сначала анализирует путь потом
    /// думает как правильно проложить и обойти что нужно потом проверяет и потом рисует
    /// саму дорожку».
    ///
    ///   1. **Найти** — перепись (<see cref="RunSurvey"/>) выписывает материк целиком и
    ///      оставляет серверу <see cref="SurveyKnown"/>: локации, руду, валуны, гнёзда,
    ///      ягодники. Высоты и вода не стоят ничего и берутся у генератора.
    ///   2. **Разобрать** — эта сетка: ровное дёшево, крутое дорого, вода и жила
    ///      непроходимы.
    ///   3. **Проложить** — A* по цене. Дорога обходит деревню за сотню метров до неё,
    ///      потому что так дешевле, а не потому, что кто-то посчитал отступ.
    ///   4. **Проверить** — <see cref="CheckRoute"/>: уклон, зазор до непроходимого,
    ///      крюк против прямой. Числа уходят в отчёт до того, как что-то ляжет.
    ///   5. **Положить** — прежней укладкой, ей путь безразличен.
    ///
    /// **Чем это лучше прежнего.** Раньше путь рисовался вслепую кривой Безье, а дорога
    /// уворачивалась от встреченного. Оттуда росли все беды: разгон изгиба вшестеро
    /// длиннее отступа и не влезает в кусок 91 м, деревню шириной семьдесят метров не
    /// обойти никак, склон режется траншеей, через залив дорога идёт по дну. Здесь ничего
    /// этого нет по построению.
    /// </summary>
    public partial class Plugin
    {
        /// <summary>
        /// Клетка сетки. Половина самого узкого, что нас волнует: дорога в четыре метра
        /// не проходит в брешь в полтора, а клетка много крупнее сказала бы, что между
        /// двумя валунами в двадцати метрах прохода нет.
        /// </summary>
        private const float PlanStep = 8f;

        /// <summary>Дальше этого уклона - обрыв, а не дорога; по нему не ходят вовсе.</summary>
        private const float PlanCliff = 1.0f;

        /// <summary>Уклон, с которого начинает дорожать: тот же, что держит `LimitGrade`.</summary>
        private const float PlanEasyGrade = 0.35f;

        /// <summary>Во сколько раз дороже земля на пределе уклона.</summary>
        private const float PlanSteepCost = 9f;

        /// <summary>Гнездо проходимо, но дорого: дорога через него будет слать двергров на идущего.</summary>
        private const float PlanNestCost = 20f;

        /// <summary>Ягодник дешевле гнезда: его жалко, но он не опасен.</summary>
        private const float PlanBerryCost = 4f;

        /// <summary>
        /// Цена клетки, по которой уже прошла дорога.
        ///
        /// Меньше единицы нарочно: следующая дорога будет к ней прижиматься, и выйдет
        /// сеть с общими участками, как у настоящих дорог, а не полсотни отдельных линий.
        /// </summary>
        private const float PlanRoadCost = 0.45f;

        /// <summary>
        /// Сколько раз сглаживать найденный путь.
        ///
        /// **Число намерено, а не взято с потолка.** На точках через метр прямой угол
        /// срезается на 5,6 м при двухстах проходах, а срез растёт как корень: шестьсот
        /// дают 9,7 м, то есть дугу радиусом метров двадцать пять. Это поворот, который
        /// видно поворотом; прежние три прохода по редкой ломаной не давали ничего, и
        /// хозяин увидел это на первой же сети - «от круга бывают сильно резкие дорожки».
        ///
        /// Дороже это почти ничего не стоит: дорога сети - метров двести, то есть двести
        /// точек, и весь счёт на восемь десятков дорог укладывается в десяток миллионов
        /// сложений один раз, до укладки.
        /// </summary>
        private const int PlanEasePasses = 600;

        /// <summary>
        /// Насколько точка съезжает к середине между соседями за один проход.
        ///
        /// Половина, и больше нельзя: на единице точка встаёт ровно в середину, а такой
        /// шаг не гасит дрожь через точку - путь, вильнувший на метр туда-сюда, так и
        /// будет вилять, сколько проходов ни делай.
        /// </summary>
        private const float PlanEasePull = 0.5f;

        /// <summary>
        /// Что перепись нашла на материке - остаётся у сервера для прокладки.
        ///
        /// Держится оно здесь, а не перечитывается из файла: файл писан для человека и
        /// для рисовальщика, а прокладке нужен тот же самый ответ, что ушёл на карту.
        /// </summary>
        private static readonly List<SurveyMark> SurveyKnown = new List<SurveyMark>();

        /// <summary>Материк, который перепись обошла: по нему и строится сетка цены.</summary>
        private static HashSet<long> SurveyLand;

        internal static int SurveyKnownCount
        {
            get { return SurveyKnown.Count; }
        }

        /// <summary>Есть ли чем прокладывать: без переписи знания о материке нет.</summary>
        internal static bool PlanReady
        {
            get { return SurveyLand != null && SurveyLand.Count > 0; }
        }

        /// <summary>
        /// Сетка цены над материком.
        ///
        /// Вода спрашивается у генератора, а не у загруженной земли: земля есть только
        /// там, где кто-то был, а генератор отвечает одинаково про любую точку мира. По
        /// той же причине и уклон - он считается по четырём соседям той же сетки.
        /// </summary>
        private static RoadPlan.Field PlanField(HashSet<long> land, float halfWidth)
        {
            var world = WorldGenerator.instance;
            var zones = ZoneSystem.instance;
            if (world == null || zones == null) return null;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var cell in land)
            {
                var cx = (int)(cell >> 32) * LandStep;
                var cz = (int)(cell & 0xFFFFFFFF) * LandStep;
                if (cx < minX) minX = cx;
                if (cx > maxX) maxX = cx;
                if (cz < minZ) minZ = cz;
                if (cz > maxZ) maxZ = cz;
            }

            // Поля на клетку шире материка с каждой стороны: дороге бывает нужно пройти
            // по самому берегу, а клетка на краю поля соседей не имеет.
            var slack = LandStep;
            minX -= slack; maxX += slack; minZ -= slack; maxZ += slack;

            var wide = Mathf.CeilToInt((maxX - minX) / PlanStep) + 1;
            var high = Mathf.CeilToInt((maxZ - minZ) / PlanStep) + 1;
            var field = new RoadPlan.Field(wide, high, PlanStep, new Vec2(minX, minZ));

            // Высоты берём один раз: они нужны и для воды, и для уклона.
            var water = zones.m_waterLevel;
            PlanWet.Clear();
            var height = new float[wide * high];
            for (var z = 0; z < high; z++)
            for (var x = 0; x < wide; x++)
            {
                var at = field.World(x, z);
                height[field.At(x, z)] = world.GetHeight(at.X, at.Z);
            }

            for (var z = 0; z < high; z++)
            for (var x = 0; x < wide; x++)
            {
                var i = field.At(x, z);
                if (height[i] <= water)
                {
                    field.Cost[i] = RoadPlan.Blocked;
                    PlanWet.Add(i);
                    continue;
                }

                var grade = Grade(field, height, x, z);
                if (grade >= PlanCliff)
                {
                    field.Cost[i] = RoadPlan.Blocked;
                    continue;
                }

                // Квадратично: пологое почти не дорожает, а у предела цена растёт быстро,
                // и дорога сама выбирает обойти холм, а не врезаться в него.
                var over = grade / PlanEasyGrade;
                field.Cost[i] = 1f + over * over * PlanSteepCost;
            }

            PlanBlockers(field, halfWidth);
            return field;
        }

        /// <summary>Самый крутой склон из четырёх соседей - в долях подъёма на метр.</summary>
        private static float Grade(RoadPlan.Field field, float[] height, int x, int z)
        {
            var here = height[field.At(x, z)];
            var worst = 0f;

            for (var side = 0; side < 4; side++)
            {
                var nx = x + (side == 0 ? 1 : side == 1 ? -1 : 0);
                var nz = z + (side == 2 ? 1 : side == 3 ? -1 : 0);
                if (!field.Inside(nx, nz)) continue;

                var drop = Mathf.Abs(height[field.At(nx, nz)] - here) / field.Step;
                if (drop > worst) worst = drop;
            }

            return worst;
        }

        /// <summary>Всё, что перепись нашла, - кругами цены на той же сетке.</summary>
        private static void PlanBlockers(RoadPlan.Field field, float halfWidth)
        {
            // Дорога занимает свою ширину, и ещё метр - чтобы её край не лёг вплотную.
            var keep = halfWidth + 1f;

            foreach (var mark in SurveyKnown)
            {
                var radius = mark.Radius;
                var at = new Vec2(mark.X, mark.Z);

                if (mark.Kind == "nest")
                {
                    field.Circle(at, radius + keep, PlanNestCost);
                    continue;
                }

                if (mark.Kind == "berries")
                {
                    field.Circle(at, radius + keep, PlanBerryCost);
                    continue;
                }

                // Локация, жила и валун - насквозь не ходят.
                field.Circle(at, radius + keep, RoadPlan.Blocked);
            }
        }

        /// <summary>
        /// Открывает проход к концу дороги.
        ///
        /// Дорога сети начинается и кончается в середине метки, а метка - это локация, то
        /// есть непроходимый круг. Не открыв его, не найдёшь ни одного пути и не поймёшь
        /// почему: поиск честно ответит «прохода нет».
        ///
        /// Открывается ровно круг метки, не больше: остальное непроходимое остаётся.
        ///
        /// **И закрывается обратно, когда дорога проложена.** Сетка цены одна на всю сеть,
        /// а открытое ничем не закрывалось: каждая из восьмидесяти дорог пробивала у своих
        /// концов по дыре метров в двенадцать, и дыры эти оставались навсегда. Валун или
        /// жила, стоявшие рядом с камнем, переставали быть препятствием для всех следующих
        /// дорог - и сеть спокойно шла сквозь них. Поэтому вызывающий получает список
        /// того, что открыл, и обязан вернуть его на место.
        /// </summary>
        private static void OpenEnd(RoadPlan.Field field, Vector3 at, float radius,
                                    List<KeyValuePair<int, float>> was)
        {
            int cx, cz;
            field.Cell(new Vec2(at.x, at.z), out cx, out cz);
            var reach = Mathf.CeilToInt(radius / field.Step) + 1;

            for (var z = cz - reach; z <= cz + reach; z++)
            for (var x = cx - reach; x <= cx + reach; x++)
            {
                if (!field.Inside(x, z)) continue;

                var here = field.World(x, z);
                var dx = here.X - at.x;
                var dz = here.Z - at.z;
                if (dx * dx + dz * dz > radius * radius) continue;

                var i = field.At(x, z);

                // Воду не открываем даже у метки: храм на берегу не повод класть дорогу
                // по дну.
                if (field.Cost[i] < RoadPlan.Blocked || Wet(field, x, z)) continue;

                was.Add(new KeyValuePair<int, float>(i, field.Cost[i]));
                field.Cost[i] = 2f;
            }
        }

        /// <summary>Память о том, что было непроходимо из-за воды, а не из-за вещи.</summary>
        private static readonly HashSet<int> PlanWet = new HashSet<int>();

        private static bool Wet(RoadPlan.Field field, int x, int z)
        {
            return PlanWet.Contains(field.At(x, z));
        }

        /// <summary>Что проверка сказала о найденном пути.</summary>
        private struct RouteCheck
        {
            public bool Good;

            public float Steepest;

            public float Clearance;

            public float Detour;

            public string Say;
        }

        /// <summary>
        /// Разбор найденного пути - до того, как что-то ляжет.
        ///
        /// Проверяется не то, что мы задумали, а то, что вышло: самый крутой склон вдоль
        /// дороги, самый тесный зазор до непроходимого и крюк против прямой. Число здесь
        /// стоит дороже слова «готово»: восемь укладок подряд писали «готово» и клали
        /// дорогу сквозь деревню.
        /// </summary>
        private static RouteCheck CheckRoute(RoadPlan.Field field, IList<Vec2> path)
        {
            var check = new RouteCheck { Good = true, Clearance = float.MaxValue };
            var world = WorldGenerator.instance;
            if (path.Count < 2) return check;

            for (var i = 1; i < path.Count; i++)
            {
                var span = Mathf.Sqrt((path[i].X - path[i - 1].X) * (path[i].X - path[i - 1].X)
                                      + (path[i].Z - path[i - 1].Z) * (path[i].Z - path[i - 1].Z));
                if (span < 0.01f || world == null) continue;

                var rise = Mathf.Abs(world.GetHeight(path[i].X, path[i].Z)
                                     - world.GetHeight(path[i - 1].X, path[i - 1].Z));
                var grade = rise / span;
                if (grade > check.Steepest) check.Steepest = grade;
            }

            foreach (var point in path)
            {
                int x, z;
                if (!field.Cell(point, out x, out z)) continue;
                var away = ToBlocked(field, x, z);
                if (away < check.Clearance) check.Clearance = away;
            }

            var straight = Mathf.Sqrt(
                (path[path.Count - 1].X - path[0].X) * (path[path.Count - 1].X - path[0].X)
                + (path[path.Count - 1].Z - path[0].Z) * (path[path.Count - 1].Z - path[0].Z));
            check.Detour = straight > 1f ? RoadPlan.Length(path) / straight : 1f;

            check.Good = check.Steepest <= PlanCliff && check.Clearance > 0f;
            check.Say = $"уклон до {check.Steepest * 100f:F0}%, зазор {check.Clearance:F0} м, "
                        + $"крюк {check.Detour:F2}";
            return check;
        }

        /// <summary>Сколько метров от клетки до ближайшей непроходимой, не дальше трёх клеток.</summary>
        private static float ToBlocked(RoadPlan.Field field, int x, int z)
        {
            if (field.Cost[field.At(x, z)] >= RoadPlan.Blocked) return 0f;

            const int look = 3;
            var best = (look + 1) * field.Step;
            for (var dz = -look; dz <= look; dz++)
            for (var dx = -look; dx <= look; dx++)
            {
                if (!field.Inside(x + dx, z + dz)) continue;
                if (field.Cost[field.At(x + dx, z + dz)] < RoadPlan.Blocked) continue;

                var away = Mathf.Sqrt(dx * dx + dz * dz) * field.Step;
                if (away < best) best = away;
            }

            return best;
        }

        /// <summary>
        /// Проложенная дорога дешевеет для следующих: так выходит сеть, а не пучок линий.
        /// </summary>
        private static void RememberRoad(RoadPlan.Field field, IList<Vec2> path, float halfWidth)
        {
            foreach (var point in path)
                field.Circle(point, halfWidth, PlanRoadCost, false);
        }

        /// <summary>
        /// Весь порядок для одной дороги: открыть концы, проложить, проверить, подменить путь.
        ///
        /// Возвращает false, когда прохода не нашлось: тогда у дороги остаётся путь,
        /// нарисованный кривой, то есть ровно то, что было до 23.09.2026. Отказ молчаливым
        /// не бывает - он уходит в лог с причиной.
        /// </summary>
        private static bool PlanRoad(RoadPlan.Field field, RoadJob road, Vector3 from, Vector3 to,
                                     float fromRing, float toRing)
        {
            // Концы дороги - середины меток, а метка это непроходимый круг. Открываем
            // ровно их, иначе поиск честно ответит «прохода нет» у каждой дороги сети.
            // Что открыли - вернём: сетка одна на всю сеть, и дыра в ней живёт до конца.
            var opened = new List<KeyValuePair<int, float>>();
            try
            {
                OpenEnd(field, from, fromRing + road.Radius + 2f, opened);
                OpenEnd(field, to, toRing + road.Radius + 2f, opened);

                var route = RoadPlan.Find(field, new Vec2(from.x, from.z), new Vec2(to.x, to.z));
                if (!route.Found)
                {
                    Log.LogWarning($"[AstvardServerMod] Road plan {road.Id}: {route.Why} from "
                                   + $"{from.x:F0} {from.z:F0} to {to.x:F0} {to.z:F0} — laying it straight.");
                    return false;
                }

                // Ступеньки сетки - не повороты дороги: путь по восьми направлениям на
                // километр даёт сто двадцать пять одинаковых углов. Убираем лишнее,
                // расставляем точки через метр, как ждёт укладка, и только на них
                // скругляем: на редкой ломаной то же сглаживание не скругляет, а двигает.
                var thin = RoadPlan.Simplify(route.Path, PlanStep * 0.35f);
                var walk = RoadPlan.Walk(thin, 1f);
                var easy = RoadPlan.Ease(field, walk, PlanEasePasses, PlanEasePull);

                var check = CheckRoute(field, easy);

                var world = WorldGenerator.instance;
                var path = new List<Vector3>(easy.Count);
                foreach (var point in easy)
                    path.Add(new Vector3(point.X,
                        world != null ? world.GetHeight(point.X, point.Z) : 0f, point.Z));

                // Концы обязаны остаться в серединах меток: круг там и ляжет.
                path[0] = from;
                path[path.Count - 1] = to;

                road.Path = path;
                RememberRoad(field, easy, road.Radius);

                Log.LogInfo($"[AstvardServerMod] Road plan {road.Id}: {route.Metres:F0} m over "
                            + $"{route.Cost:F0} of cost, {check.Say}"
                            + (check.Good ? "." : " — ПРОВЕРКА НЕ ПРОШЛА."));
                return true;
            }
            finally
            {
                // Строго после `RememberRoad`: та дешевит клетки под дорогой, и клетки
                // внутри круга метки она тоже успевает задеть, пока они открыты.
                foreach (var was in opened) field.Cost[was.Key] = was.Value;
            }
        }
    }
}
