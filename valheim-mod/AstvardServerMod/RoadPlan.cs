using System;
using System.Collections.Generic;

namespace AstvardServerMod
{
    /// <summary>
    /// Куда класть дорогу: поиск пути по цене земли, а не рисование кривой.
    ///
    /// **Зачем это вместо прежнего.** До 23.09.2026 путь между двумя метками рисовался
    /// вслепую - кривой Безье, - а дальше дорога уворачивалась от того, что на ней
    /// оказалось. Все беды двух дней росли из этого одного: разгон изгиба вшестеро длиннее
    /// отступа и не влезает в кусок в 91 м, деревню шириной в семьдесят метров не обойти
    /// никак, склон режется траншеей, а через залив дорога идёт по дну. Уворачиваться от
    /// плана - значит чинить следствие.
    ///
    /// Здесь наоборот: сперва вся земля получает цену - ровное дёшево, крутое дорого, вода
    /// и рудная жила непроходимы, гнездо дорого, но можно, - и путь ищется по ней. Дорога
    /// тогда обходит деревню за сотню метров до неё, потому что дешевле, а не потому, что
    /// кто-то посчитал отступ.
    ///
    /// **Без Unity нарочно**: цену клетки считает вызывающий, здесь только сетка и поиск,
    /// и оттого всё это проверяется тестами за полсекунды, а не заходом в игру.
    /// </summary>
    internal static class RoadPlan
    {
        /// <summary>Непроходимо: вода, жила, стена крипты.</summary>
        internal const float Blocked = float.MaxValue;

        /// <summary>
        /// Цена земли клетками.
        ///
        /// Клетка мельче дороги была бы враньём о точности - дорога шириной четыре метра
        /// не умеет проходить в брешь в полтора, - а много крупнее означала бы, что между
        /// двумя валунами в двадцати метрах друг от друга прохода нет вовсе. Восемь метров
        /// - половина самого узкого, что нас волнует.
        /// </summary>
        internal sealed class Field
        {
            internal Field(int wide, int high, float step, Vec2 origin)
            {
                Wide = wide;
                High = high;
                Step = step;
                Origin = origin;
                Cost = new float[wide * high];
                for (var i = 0; i < Cost.Length; i++) Cost[i] = 1f;
            }

            internal int Wide { get; private set; }

            internal int High { get; private set; }

            internal float Step { get; private set; }

            /// <summary>Мир в середине клетки (0, 0).</summary>
            internal Vec2 Origin { get; private set; }

            internal float[] Cost { get; private set; }

            internal int At(int x, int z)
            {
                return z * Wide + x;
            }

            internal bool Inside(int x, int z)
            {
                return x >= 0 && z >= 0 && x < Wide && z < High;
            }

            internal Vec2 World(int x, int z)
            {
                return new Vec2(Origin.X + x * Step, Origin.Z + z * Step);
            }

            /// <summary>Клетка под точкой; false, если точка вне поля.</summary>
            internal bool Cell(Vec2 at, out int x, out int z)
            {
                x = (int)Math.Round((at.X - Origin.X) / Step);
                z = (int)Math.Round((at.Z - Origin.Z) / Step);
                return Inside(x, z);
            }

            internal float this[int x, int z]
            {
                get { return Cost[At(x, z)]; }
                set { Cost[At(x, z)] = value; }
            }

            /// <summary>
            /// Кладёт цену кругом. Вызывающий говорит, чем именно: непроходимым для жилы,
            /// дорогим для гнезда.
            /// </summary>
            internal void Circle(Vec2 at, float radius, float cost, bool raiseOnly = true)
            {
                if (radius <= 0f) return;

                int cx, cz;
                Cell(at, out cx, out cz);
                var reach = (int)Math.Ceiling(radius / Step) + 1;

                for (var z = cz - reach; z <= cz + reach; z++)
                for (var x = cx - reach; x <= cx + reach; x++)
                {
                    if (!Inside(x, z)) continue;

                    var here = World(x, z);
                    var dx = here.X - at.X;
                    var dz = here.Z - at.Z;
                    if (dx * dx + dz * dz > radius * radius) continue;

                    // **Непроходимое не открывается ничем.** Круг гнезда, наложенный на
                    // воду, воду проходимой не делает; и уже проложенная дорога, которая
                    // дешевит землю вокруг себя, не открывает соседнюю жилу.
                    var i = At(x, z);
                    if (Cost[i] >= Blocked) continue;

                    if (!raiseOnly || cost > Cost[i]) Cost[i] = cost;
                }
            }

            /// <summary>Есть ли хоть одна проходимая клетка. Пустое поле - это отказ, а не пустой путь.</summary>
            internal bool AnyOpen()
            {
                for (var i = 0; i < Cost.Length; i++)
                    if (Cost[i] < Blocked) return true;
                return false;
            }
        }

        /// <summary>Что вышло у поиска.</summary>
        internal sealed class Route
        {
            internal bool Found;

            /// <summary>Середины клеток от начала к концу; пусто, если не нашлось.</summary>
            internal List<Vec2> Path = new List<Vec2>();

            /// <summary>Во что обошлось: сумма цены по шагам, в метрах ровной земли.</summary>
            internal float Cost;

            /// <summary>Длина пути в метрах - не то же самое, что цена.</summary>
            internal float Metres;

            /// <summary>Самая дорогая клетка на пути: по ней видно, чем пришлось пожертвовать.</summary>
            internal float Worst;

            /// <summary>Почему не нашлось, когда не нашлось.</summary>
            internal string Why = "";
        }

        /// <summary>
        /// Путь от точки до точки по цене земли: A* по восьми соседям.
        ///
        /// Цена шага - средняя цена двух клеток на длину шага. Средняя, а не цена той, куда
        /// ступаем: иначе дорога считала бы бесплатным выход из дорогой клетки и жалась бы
        /// к краю непроходимого вместо того, чтобы обходить его по-человечески.
        ///
        /// **Концы всегда проходимы.** Дорога сети начинается и кончается в середине
        /// метки, а метка - это локация, то есть как раз непроходимый круг. Забыть об этом
        /// значит не найти ни одного пути и не понять почему.
        /// </summary>
        internal static Route Find(Field field, Vec2 from, Vec2 to)
        {
            var route = new Route();

            int sx, sz, ex, ez;
            if (!field.Cell(from, out sx, out sz) || !field.Cell(to, out ex, out ez))
            {
                route.Why = "конец вне поля";
                return route;
            }

            var start = field.At(sx, sz);
            var goal = field.At(ex, ez);
            if (start == goal)
            {
                route.Found = true;
                route.Path.Add(field.World(sx, sz));
                return route;
            }

            var cells = field.Wide * field.High;
            var came = new int[cells];
            var best = new float[cells];
            var done = new bool[cells];
            for (var i = 0; i < cells; i++)
            {
                came[i] = -1;
                best[i] = float.MaxValue;
            }

            var heap = new Heap(cells);
            best[start] = 0f;
            heap.Push(start, Guess(field, sx, sz, ex, ez));

            while (heap.Count > 0)
            {
                var cell = heap.Pop();
                if (done[cell]) continue;
                done[cell] = true;
                if (cell == goal) break;

                var cz = cell / field.Wide;
                var cx = cell - cz * field.Wide;
                var here = field.Cost[cell];

                for (var dz = -1; dz <= 1; dz++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;

                    var nx = cx + dx;
                    var nz = cz + dz;
                    if (!field.Inside(nx, nz)) continue;

                    var next = field.At(nx, nz);
                    if (done[next]) continue;

                    var there = field.Cost[next];

                    // Непроходимое остаётся непроходимым, даже если это сам конец: путь
                    // сквозь стену - не путь. Концы вызывающий открывает заранее.
                    if (there >= Blocked || here >= Blocked) continue;

                    // По диагонали между двумя непроходимыми не протиснуться: угол в
                    // четыре метра дорогой не бывает.
                    if (dx != 0 && dz != 0
                        && (field.Cost[field.At(cx + dx, cz)] >= Blocked
                            || field.Cost[field.At(cx, cz + dz)] >= Blocked)) continue;

                    var span = dx != 0 && dz != 0 ? field.Step * 1.4142136f : field.Step;
                    var step = (here + there) * 0.5f * span;
                    var sum = best[cell] + step;
                    if (sum >= best[next]) continue;

                    best[next] = sum;
                    came[next] = cell;
                    heap.Push(next, sum + Guess(field, nx, nz, ex, ez));
                }
            }

            if (came[goal] < 0 && start != goal)
            {
                route.Why = "прохода нет";
                return route;
            }

            var back = new List<int>();
            for (var at = goal; at >= 0; at = came[at]) back.Add(at);
            back.Reverse();

            route.Found = true;
            route.Cost = best[goal];
            foreach (var cell in back)
            {
                var cz = cell / field.Wide;
                var cx = cell - cz * field.Wide;
                route.Path.Add(field.World(cx, cz));
                if (field.Cost[cell] < Blocked && field.Cost[cell] > route.Worst)
                    route.Worst = field.Cost[cell];
            }

            route.Metres = Length(route.Path);
            return route;
        }

        /// <summary>Нижняя оценка остатка: прямая по восьми направлениям при цене единица.</summary>
        private static float Guess(Field field, int x, int z, int ex, int ez)
        {
            var dx = Math.Abs(ex - x);
            var dz = Math.Abs(ez - z);
            var straight = Math.Abs(dx - dz);
            var diagonal = Math.Min(dx, dz);
            return (straight + diagonal * 1.4142136f) * field.Step;
        }

        internal static float Length(IList<Vec2> path)
        {
            var sum = 0f;
            for (var i = 1; i < path.Count; i++)
            {
                var dx = path[i].X - path[i - 1].X;
                var dz = path[i].Z - path[i - 1].Z;
                sum += (float)Math.Sqrt(dx * dx + dz * dz);
            }

            return sum;
        }

        /// <summary>
        /// Выбрасывает точки, которые ничего не говорят: путь по клеткам идёт ступеньками
        /// по восьми направлениям, и прямой участок в километр - это сто двадцать пять
        /// одинаковых поворотов.
        ///
        /// Дуглас и Пейкер: точка остаётся, если без неё ломаная отошла бы от неё дальше
        /// чем на `tolerance`. Концы остаются всегда.
        /// </summary>
        internal static List<Vec2> Simplify(IList<Vec2> path, float tolerance)
        {
            var kept = new List<Vec2>();
            if (path == null || path.Count == 0) return kept;
            if (path.Count <= 2)
            {
                foreach (var point in path) kept.Add(point);
                return kept;
            }

            var keep = new bool[path.Count];
            keep[0] = true;
            keep[path.Count - 1] = true;
            Thin(path, 0, path.Count - 1, tolerance, keep);

            for (var i = 0; i < path.Count; i++)
                if (keep[i]) kept.Add(path[i]);

            return kept;
        }

        private static void Thin(IList<Vec2> path, int first, int last, float tolerance, bool[] keep)
        {
            if (last <= first + 1) return;

            var worst = -1f;
            var where = -1;
            for (var i = first + 1; i < last; i++)
            {
                var away = FromLine(path[i], path[first], path[last]);
                if (away <= worst) continue;
                worst = away;
                where = i;
            }

            if (where < 0 || worst <= tolerance) return;

            keep[where] = true;
            Thin(path, first, where, tolerance, keep);
            Thin(path, where, last, tolerance, keep);
        }

        private static float FromLine(Vec2 point, Vec2 a, Vec2 b)
        {
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;
            var span = dx * dx + dz * dz;
            if (span < 1e-9f)
            {
                var ox = point.X - a.X;
                var oz = point.Z - a.Z;
                return (float)Math.Sqrt(ox * ox + oz * oz);
            }

            var t = ((point.X - a.X) * dx + (point.Z - a.Z) * dz) / span;
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;

            var nx = a.X + t * dx - point.X;
            var nz = a.Z + t * dz - point.Z;
            return (float)Math.Sqrt(nx * nx + nz * nz);
        }

        /// <summary>
        /// Скругляет углы ломаной, не двигая концов: каждая точка съезжает к середине
        /// между соседями, и так несколько раз.
        ///
        /// Дорога по клеткам поворачивает на 45° разом, а такой угол выравнивание
        /// превращает в ступеньку. Своей кривой здесь не надо: те же смягчения потом
        /// сделает укладка, а тут довольно убрать углы сетки.
        /// </summary>
        internal static List<Vec2> Round(IList<Vec2> path, int passes, float pull)
        {
            var now = new List<Vec2>(path);
            if (now.Count < 3) return now;

            for (var pass = 0; pass < passes; pass++)
            {
                var next = new List<Vec2>(now) { [0] = now[0] };
                for (var i = 1; i < now.Count - 1; i++)
                {
                    var middleX = (now[i - 1].X + now[i + 1].X) * 0.5f;
                    var middleZ = (now[i - 1].Z + now[i + 1].Z) * 0.5f;
                    next[i] = new Vec2(now[i].X + (middleX - now[i].X) * pull,
                                       now[i].Z + (middleZ - now[i].Z) * pull);
                }

                now = next;
            }

            return now;
        }

        /// <summary>
        /// Ставит точки через равные промежутки вдоль ломаной.
        ///
        /// Укладке нужен путь с точкой примерно на метр - на этом стоит и счёт кусков, и
        /// покраска, - а поиск выдаёт по точке на клетку.
        /// </summary>
        internal static List<Vec2> Walk(IList<Vec2> path, float step)
        {
            var out_ = new List<Vec2>();
            if (path == null || path.Count == 0 || step <= 0f) return out_;

            out_.Add(path[0]);
            if (path.Count == 1) return out_;

            var carry = 0f;
            for (var i = 1; i < path.Count; i++)
            {
                var ax = path[i - 1].X;
                var az = path[i - 1].Z;
                var dx = path[i].X - ax;
                var dz = path[i].Z - az;
                var span = (float)Math.Sqrt(dx * dx + dz * dz);
                if (span < 1e-6f) continue;

                var at = step - carry;
                while (at <= span)
                {
                    var t = at / span;
                    out_.Add(new Vec2(ax + dx * t, az + dz * t));
                    at += step;
                }

                carry = span - (at - step);
            }

            var last = path[path.Count - 1];
            var tailX = out_[out_.Count - 1].X - last.X;
            var tailZ = out_[out_.Count - 1].Z - last.Z;
            if (tailX * tailX + tailZ * tailZ > 0.01f) out_.Add(last);

            return out_;
        }

        /// <summary>Двоичная куча на массивах: очередь A* по десяткам тысяч клеток.</summary>
        private sealed class Heap
        {
            private int[] _cell;

            private float[] _rank;

            private int _count;

            internal Heap(int room)
            {
                var start = room < 16 ? 16 : room / 4;
                _cell = new int[start];
                _rank = new float[start];
            }

            internal int Count
            {
                get { return _count; }
            }

            internal void Push(int cell, float rank)
            {
                if (_count == _cell.Length)
                {
                    Array.Resize(ref _cell, _count * 2);
                    Array.Resize(ref _rank, _count * 2);
                }

                var at = _count++;
                _cell[at] = cell;
                _rank[at] = rank;

                while (at > 0)
                {
                    var up = (at - 1) / 2;
                    if (_rank[up] <= _rank[at]) break;
                    Swap(up, at);
                    at = up;
                }
            }

            internal int Pop()
            {
                var top = _cell[0];
                _count--;
                if (_count > 0)
                {
                    _cell[0] = _cell[_count];
                    _rank[0] = _rank[_count];

                    var at = 0;
                    while (true)
                    {
                        var left = at * 2 + 1;
                        if (left >= _count) break;

                        var small = left;
                        var right = left + 1;
                        if (right < _count && _rank[right] < _rank[left]) small = right;
                        if (_rank[at] <= _rank[small]) break;

                        Swap(at, small);
                        at = small;
                    }
                }

                return top;
            }

            private void Swap(int a, int b)
            {
                var cell = _cell[a];
                _cell[a] = _cell[b];
                _cell[b] = cell;

                var rank = _rank[a];
                _rank[a] = _rank[b];
                _rank[b] = rank;
            }
        }
    }
}
