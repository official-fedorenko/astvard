using System;
using System.Collections.Generic;

namespace AstvardServerMod
{
    /// <summary>A point on the ground. Height never takes part in this arithmetic.</summary>
    internal struct Vec2
    {
        public float X;
        public float Z;

        public Vec2(float x, float z)
        {
            X = x;
            Z = z;
        }
    }

    /// <summary>A place for a post beside a road, and the way the road runs there.</summary>
    internal struct Post
    {
        public Vec2 At;

        /// <summary>Unit vector along the road at the post, for sliding it along the edge.</summary>
        public Vec2 Along;

        /// <summary>
        /// Metres along the road to the post's station, both posts of a pair alike; 0 round
        /// a pad. A road the server lays piece by piece hands each piece the posts whose
        /// stations fall in it, so the step runs on across the joins.
        /// </summary>
        public float Station;

        public Post(Vec2 at, Vec2 along, float station = 0f)
        {
            At = at;
            Along = along;
            Station = station;
        }
    }

    /// <summary>One stake of a ring fence: where its middle stands, and which way it faces.</summary>
    internal struct Stake
    {
        public Vec2 At;

        /// <summary>
        /// Degrees from +Z round towards +X to the outward face - the yaw Unity turns a
        /// piece by, so a stake of the player's own «30м.» fence on its northern side
        /// has 0 here and stands unrotated.
        /// </summary>
        public float Yaw;

        public Stake(Vec2 at, float yaw)
        {
            At = at;
            Yaw = yaw;
        }
    }

    /// <summary>A ring of palisade round a centre, laid out as a regular polygon.</summary>
    internal sealed class FencePlan
    {
        public int Sides;

        public int PerSide;

        /// <summary>Metres between the middles of neighbouring stakes on one side.</summary>
        public float Spacing;

        public readonly List<Stake> Stakes = new List<Stake>();

        /// <summary>Where the sides meet, in order round the ring.</summary>
        public readonly List<Vec2> Corners = new List<Vec2>();
    }

    /// <summary>What a flood over a floor grid finds in one cell.</summary>
    internal enum FloorCell
    {
        Free,

        /// <summary>Something standing there that closes the space: a wall, a beam, a fence.</summary>
        Wall,

        /// <summary>A floor already lies there. The flood goes on across it; nothing is laid on it.</summary>
        Floored,
    }

    /// <summary>A space found by FloodFloor, or the way out of one that was not closed.</summary>
    internal sealed class FloorRegion
    {
        public bool Closed;

        /// <summary>Stopped because the space outgrew the limit, rather than reaching the edge.</summary>
        public bool TooBig;

        public readonly HashSet<long> Free = new HashSet<long>();

        public readonly HashSet<long> Floored = new HashSet<long>();

        /// <summary>
        /// When the flood got out: the shortest way from the start to where it did, which is
        /// what runs through the gap.
        /// </summary>
        public readonly List<GridCell> WayOut = new List<GridCell>();
    }

    internal struct GridCell
    {
        public int I;

        public int J;

        public GridCell(int i, int j)
        {
            I = i;
            J = j;
        }
    }

    /// <summary>
    /// The arithmetic behind the terrain, zone and blueprint tools, kept free of Unity
    /// and of the game's own types so it can be exercised without either.
    ///
    /// It lives apart for a reason: every expensive bug in these tools was a units or
    /// rounding mistake in here, not a mistake in the engine glue around it. A paint
    /// brush measured in grid cells but passed metres; a zone radius counted in whole
    /// cells so a base got clipped; a blueprint whose yaws fell into two families and
    /// came out rotated. None of those needed a running game to catch.
    /// </summary>
    internal static class Geometry
    {
        // ---------------- road curve ----------------

        /// <summary>
        /// How far a road bows out at its middle, in metres. Scaling by the length means
        /// the number describes a shape rather than a distance: 1 is a gentle bend and
        /// 10 puts the bulge at half the chord, which reads as a semicircle.
        /// </summary>
        public static float Sagitta(float curve, float length)
        {
            return curve * length * 0.05f;
        }

        /// <summary>
        /// Samples the centreline as a quadratic Bezier. The control point is pushed out
        /// twice the wanted bulge because the curve only reaches half of it, and the
        /// sample count follows the bulge as well — a strong curve sampled for its chord
        /// alone comes out dotted.
        /// </summary>
        public static List<Vec2> Bezier(Vec2 from, Vec2 to, float sagitta, float step)
        {
            var points = new List<Vec2>();

            var chordX = to.X - from.X;
            var chordZ = to.Z - from.Z;
            var length = (float)Math.Sqrt(chordX * chordX + chordZ * chordZ);
            if (length < 0.01f) return points;

            var sideX = -chordZ / length;
            var sideZ = chordX / length;

            var midX = (from.X + to.X) * 0.5f + sideX * sagitta * 2f;
            var midZ = (from.Z + to.Z) * 0.5f + sideZ * sagitta * 2f;

            var span = length + Math.Abs(sagitta) * 2f;
            var count = Math.Max(1, (int)Math.Ceiling(span / Math.Max(step, 0.1f)));

            for (var i = 0; i <= count; i++)
            {
                var t = (float)i / count;
                var inv = 1f - t;
                points.Add(new Vec2(
                    inv * inv * from.X + 2f * inv * t * midX + t * t * to.X,
                    inv * inv * from.Z + 2f * inv * t * midZ + t * t * to.Z));
            }

            return points;
        }

        /// <summary>
        /// Distance from a point to a stretch of polyline. Measuring to the line rather
        /// than to sampled circles is what makes a painted road continuous: stamps
        /// overwrite one another, a distance does not.
        /// </summary>
        public static float DistanceToPath(IList<Vec2> path, int first, int last, float x, float z)
        {
            if (path.Count == 0) return float.MaxValue;

            if (last <= first)
            {
                var only = path[first];
                return (float)Math.Sqrt((x - only.X) * (x - only.X) + (z - only.Z) * (z - only.Z));
            }

            var best = float.MaxValue;
            for (var k = first; k < last; k++)
            {
                var a = path[k];
                var b = path[k + 1];

                var abx = b.X - a.X;
                var abz = b.Z - a.Z;
                var lenSq = abx * abx + abz * abz;

                var t = 0f;
                if (lenSq > 1e-6f)
                {
                    t = ((x - a.X) * abx + (z - a.Z) * abz) / lenSq;
                    if (t < 0f) t = 0f;
                    else if (t > 1f) t = 1f;
                }

                var dx = x - (a.X + abx * t);
                var dz = z - (a.Z + abz * t);
                var d = dx * dx + dz * dz;
                if (d < best) best = d;
            }

            return (float)Math.Sqrt(best);
        }

        /// <summary>
        /// Metres along the path to each of its points: 0 for the first, the whole length for
        /// the last. The same measure <see cref="EdgePosts"/> gives its stations in, so a post
        /// can be matched to the stretch of path it stands by.
        /// </summary>
        public static float[] Distances(IList<Vec2> path)
        {
            var along = new float[path.Count];
            for (var i = 1; i < path.Count; i++)
            {
                var dx = path[i].X - path[i - 1].X;
                var dz = path[i].Z - path[i - 1].Z;
                along[i] = along[i - 1] + (float)Math.Sqrt(dx * dx + dz * dz);
            }

            return along;
        }

        // ---------------- posts along a road ----------------

        /// <summary>
        /// Places for posts along both edges of a road: pairs facing each other across
        /// it, one every <paramref name="spacing"/> metres of its length, each
        /// <paramref name="offset"/> metres out from the centre line.
        ///
        /// The step is measured along the road itself, not along the chord, so a bend
        /// keeps the step of a straight - the curve's own samples are not evenly spaced
        /// and cannot be counted instead. The row is centred on the length: whatever the
        /// step does not divide is shared between the two ends, so a road a whole number
        /// of steps long gets a pair right at either end, and one shorter than a step
        /// gets a single pair in its middle.
        /// </summary>
        public static List<Post> EdgePosts(IList<Vec2> path, float spacing, float offset)
        {
            var posts = new List<Post>();

            // Points on top of one another have no direction to take a side from.
            var points = new List<Vec2>();
            foreach (var point in path)
            {
                if (points.Count > 0)
                {
                    var last = points[points.Count - 1];
                    if (Math.Abs(point.X - last.X) < 1e-4f && Math.Abs(point.Z - last.Z) < 1e-4f) continue;
                }

                points.Add(point);
            }

            if (points.Count < 2) return posts;

            var along = new float[points.Count];
            for (var i = 1; i < points.Count; i++)
            {
                var dx = points[i].X - points[i - 1].X;
                var dz = points[i].Z - points[i - 1].Z;
                along[i] = along[i - 1] + (float)Math.Sqrt(dx * dx + dz * dz);
            }

            var length = along[points.Count - 1];
            spacing = Math.Max(spacing, 0.5f);
            var stations = (int)Math.Floor(length / spacing + 1e-4) + 1;
            var first = (length - (stations - 1) * spacing) * 0.5f;

            var k = 0;
            for (var s = 0; s < stations; s++)
            {
                var at = first + s * spacing;
                while (k < points.Count - 2 && along[k + 1] < at) k++;

                var a = points[k];
                var b = points[k + 1];
                var segment = along[k + 1] - along[k];
                var t = Math.Max(0f, Math.Min(1f, (at - along[k]) / segment));

                var dirX = (b.X - a.X) / segment;
                var dirZ = (b.Z - a.Z) / segment;
                var x = a.X + (b.X - a.X) * t;
                var z = a.Z + (b.Z - a.Z) * t;
                var direction = new Vec2(dirX, dirZ);

                // Left of the travel is (-dz, dx), the same side a positive curve bows to.
                posts.Add(new Post(new Vec2(x - dirZ * offset, z + dirX * offset), direction, at));
                posts.Add(new Post(new Vec2(x + dirZ * offset, z - dirX * offset), direction, at));
            }

            return posts;
        }

        /// <summary>
        /// Places for posts round the rim of a round pad: as many as the step fits into
        /// the circumference, never fewer than three, evenly shared out.
        /// </summary>
        public static List<Post> RingPosts(Vec2 centre, float radius, float spacing)
        {
            var posts = new List<Post>();
            if (radius <= 0f) return posts;

            var count = Math.Max(3, (int)Math.Round(2.0 * Math.PI * radius / Math.Max(spacing, 0.5f)));
            for (var i = 0; i < count; i++)
            {
                var angle = 2.0 * Math.PI * i / count;
                var cos = (float)Math.Cos(angle);
                var sin = (float)Math.Sin(angle);

                // Along the rim is the tangent, a quarter turn from the radius.
                posts.Add(new Post(new Vec2(centre.X + cos * radius, centre.Z + sin * radius),
                                   new Vec2(-sin, cos)));
            }

            return posts;
        }

        // ---------------- ring fence ----------------

        /// <summary>
        /// A palisade closing a ring round the origin, <paramref name="radius"/> metres
        /// out to the line of its stakes: a regular polygon of straight sides, none of
        /// them more than <paramref name="maxPerSide"/> stakes long.
        ///
        /// Sixteen sides at the least. That is how the player built «30м.» and «40м.»
        /// by hand - one side for each of the hammer's sixteen turns - and it keeps even a
        /// small ring round rather than square. Past about thirty metres sixteen sides
        /// would each need more stakes than allowed, so there are more of them.
        ///
        /// The radius is kept exactly, whatever it is. A side is then rarely a whole
        /// number of stakes long, so it gets the next whole number and they stand a
        /// little closer than their own width; the palisade comes out a touch denser, and
        /// never with a gap. The last stake of each side reaches the corner, so the ring
        /// is closed there too.
        /// </summary>
        public static FencePlan FenceRing(float radius, int maxPerSide, float stakeWidth)
        {
            var plan = new FencePlan();
            if (radius <= 0f || maxPerSide < 1 || stakeWidth <= 0f) return plan;

            var halfLongest = maxPerSide * stakeWidth * 0.5;
            var sides = Math.Max(16, (int)Math.Ceiling(Math.PI / Math.Atan(halfLongest / radius) - 1e-6));
            var half = Math.PI / sides;
            var length = (float)(2.0 * radius * Math.Tan(half));
            var perSide = Math.Max(1, (int)Math.Ceiling(length / stakeWidth - 1e-4));
            var spacing = length / perSide;

            plan.Sides = sides;
            plan.PerSide = perSide;
            plan.Spacing = spacing;

            var corner = radius / Math.Cos(half);
            for (var i = 0; i < sides; i++)
            {
                var angle = 2.0 * Math.PI * i / sides;
                var outX = (float)Math.Sin(angle);
                var outZ = (float)Math.Cos(angle);
                var yaw = (float)(360.0 * i / sides);

                // Along the side, a quarter turn clockwise from the outward direction:
                // on the northern side that is +X, the way the stakes of «30м.» run.
                for (var j = 0; j < perSide; j++)
                {
                    var along = (j + 0.5f) * spacing - length * 0.5f;
                    plan.Stakes.Add(new Stake(
                        new Vec2(outX * radius + outZ * along, outZ * radius - outX * along), yaw));
                }

                var between = angle + half;
                plan.Corners.Add(new Vec2((float)(Math.Sin(between) * corner),
                                          (float)(Math.Cos(between) * corner)));
            }

            return plan;
        }

        /// <summary>
        /// Where along one side of a ring the walkway's floors and the roof's panels go,
        /// from the side's middle. They are two metres wide and stand two metres apart,
        /// as in the player's «Секция 6шт.», one for each stake: laid at the stakes' own
        /// closer step they would overlap flat on flat and flicker. So they keep their
        /// own step and run a little past a short side's corners, where the next side's
        /// panels meet them at an angle instead.
        /// </summary>
        public static float[] SectionPanels(int perSide)
        {
            var panels = new float[Math.Max(0, perSide)];
            for (var j = 0; j < panels.Length; j++) panels[j] = (j + 0.5f) * 2f - perSide;
            return panels;
        }

        /// <summary>
        /// Where along one side the roof's posts stand. «Секция 6шт.» has two, four metres
        /// out either way from the middle - a panel in from each end - and a shorter side
        /// keeps them a panel in; a side too short for two gets one in the middle.
        /// </summary>
        public static float[] SectionPosts(int perSide)
        {
            var inset = perSide - 2;
            return inset > 0 ? new[] { -(float)inset, (float)inset } : new[] { 0f };
        }

        // ---------------- filling a closed space ----------------

        public static long CellKey(int i, int j)
        {
            return ((long)i << 32) | (uint)j;
        }

        public static GridCell CellOf(long key)
        {
            return new GridCell((int)(key >> 32), (int)(uint)key);
        }

        /// <summary>
        /// Floods a grid from the start cells the way a paint bucket fills: across free
        /// cells and cells already floored, four ways from each, never into a cell that
        /// is wall and never across a passage <paramref name="closed"/> says is shut.
        /// Reaching <paramref name="reach"/> cells out from the origin means the space is
        /// not closed; more than <paramref name="maxCells"/> inside means it is not, or is
        /// too big to fill. Either way the region comes back open, with the way out - the
        /// shortest path from the start to where the flood stopped - which runs through
        /// the gap.
        ///
        /// Walls are mostly passages, not cells. A floor snapped to a wall runs under it
        /// to its middle, so a wall stands on the line between two cells and both of them
        /// are floor; asked as cells, they would both have been wall and the floor would
        /// have stopped half a metre short of every wall. A passage is asked before the
        /// cell behind it, and a shut one does not mark that cell as seen: it may still be
        /// reached from another side.
        ///
        /// A start cell that is wall is no start; with none left the region is empty.
        /// </summary>
        public static FloorRegion FloodFloor(IList<GridCell> start, Func<int, int, FloorCell> classify,
                                             Func<GridCell, GridCell, bool> closed, int reach, int maxCells)
        {
            var region = new FloorRegion();
            var queue = new Queue<GridCell>();
            var from = new Dictionary<long, long>();

            foreach (var cell in start)
            {
                var key = CellKey(cell.I, cell.J);
                if (from.ContainsKey(key)) continue;

                var kind = classify(cell.I, cell.J);
                if (kind == FloorCell.Wall) continue;

                from[key] = key;
                if (kind == FloorCell.Floored) region.Floored.Add(key);
                else region.Free.Add(key);
                queue.Enqueue(cell);
            }

            if (queue.Count == 0) return region;

            var seen = new HashSet<long>(from.Keys);
            var steps = new[] { new GridCell(1, 0), new GridCell(-1, 0), new GridCell(0, 1), new GridCell(0, -1) };

            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                var key = CellKey(cell.I, cell.J);

                foreach (var step in steps)
                {
                    var next = new GridCell(cell.I + step.I, cell.J + step.J);
                    var nextKey = CellKey(next.I, next.J);
                    if (seen.Contains(nextKey)) continue;
                    if (closed != null && closed(cell, next)) continue;

                    if (Math.Abs(next.I) > reach || Math.Abs(next.J) > reach)
                    {
                        TraceWayOut(region, from, key);
                        return region;
                    }

                    seen.Add(nextKey);

                    var kind = classify(next.I, next.J);
                    if (kind == FloorCell.Wall) continue;

                    from[nextKey] = key;
                    if (kind == FloorCell.Floored) region.Floored.Add(nextKey);
                    else region.Free.Add(nextKey);

                    if (region.Free.Count + region.Floored.Count > maxCells)
                    {
                        region.TooBig = true;
                        TraceWayOut(region, from, nextKey);
                        return region;
                    }

                    queue.Enqueue(next);
                }
            }

            region.Closed = true;
            return region;
        }

        private static void TraceWayOut(FloorRegion region, Dictionary<long, long> from, long last)
        {
            var key = last;
            while (true)
            {
                region.WayOut.Add(CellOf(key));
                var back = from[key];
                if (back == key) break;
                key = back;
            }

            region.WayOut.Reverse();
        }

        /// <summary>
        /// Covers the free cells of a region with plates: two by two metres wherever one
        /// fits whole, then one by one wherever that does, none over a cell already
        /// covered. Cells are half a metre, on the grid of the plate the flood started
        /// from, so a plate at (a, b) covers cells 4a-2 .. 4a+1 across and 4b-2 .. 4b+1
        /// along - the start plate is (0, 0) - and a small one at (c, d) cells 2c .. 2c+1
        /// and 2d .. 2d+1, the same lines. A room built on the start plate's grid comes
        /// out covered exactly; an odd corner keeps its half-metre slivers rather than a
        /// plate that would poke through the wall.
        /// </summary>
        public static void FloorTiles(HashSet<long> free, List<GridCell> big, List<GridCell> small)
        {
            big.Clear();
            small.Clear();
            if (free.Count == 0) return;

            int minI = int.MaxValue, maxI = int.MinValue, minJ = int.MaxValue, maxJ = int.MinValue;
            foreach (var key in free)
            {
                var cell = CellOf(key);
                if (cell.I < minI) minI = cell.I;
                if (cell.I > maxI) maxI = cell.I;
                if (cell.J < minJ) minJ = cell.J;
                if (cell.J > maxJ) maxJ = cell.J;
            }

            var covered = new HashSet<long>();

            var a0 = (int)Math.Floor((minI + 2) / 4.0);
            var a1 = (int)Math.Floor((maxI + 2) / 4.0);
            var b0 = (int)Math.Floor((minJ + 2) / 4.0);
            var b1 = (int)Math.Floor((maxJ + 2) / 4.0);
            for (var a = a0; a <= a1; a++)
                for (var b = b0; b <= b1; b++)
                    if (Lay(free, covered, 4 * a - 2, 4 * b - 2, 4)) big.Add(new GridCell(a, b));

            var c0 = (int)Math.Floor(minI / 2.0);
            var c1 = (int)Math.Floor(maxI / 2.0);
            var d0 = (int)Math.Floor(minJ / 2.0);
            var d1 = (int)Math.Floor(maxJ / 2.0);
            for (var c = c0; c <= c1; c++)
                for (var d = d0; d <= d1; d++)
                    if (Lay(free, covered, 2 * c, 2 * d, 2)) small.Add(new GridCell(c, d));
        }

        private static bool Lay(HashSet<long> free, HashSet<long> covered, int i0, int j0, int size)
        {
            for (var i = i0; i < i0 + size; i++)
                for (var j = j0; j < j0 + size; j++)
                {
                    var key = CellKey(i, j);
                    if (!free.Contains(key) || covered.Contains(key)) return false;
                }

            for (var i = i0; i < i0 + size; i++)
                for (var j = j0; j < j0 + size; j++)
                    covered.Add(CellKey(i, j));

            return true;
        }

        // ---------------- grids ----------------

        /// <summary>
        /// Mirrors the game's world-to-zone mapping: cell i spans [size*i - size/2,
        /// size*i + size/2).
        /// </summary>
        public static int ZoneOf(float world, float zoneSize)
        {
            return (int)Math.Floor((world + zoneSize * 0.5) / zoneSize);
        }

        /// <summary>
        /// The cells a metre radius covers. The far edge is half-open: a cell owns
        /// [centre-half, centre+half), so treating it as inclusive would drag in the
        /// next cell on every axis and quadruple an area that fits in one.
        /// </summary>
        public static void ZoneRange(float x, float z, float radius, float zoneSize,
                                     out int minX, out int minZ, out int maxX, out int maxZ)
        {
            const float edge = 0.01f;
            minX = ZoneOf(x - radius, zoneSize);
            minZ = ZoneOf(z - radius, zoneSize);
            maxX = ZoneOf(x + radius - edge, zoneSize);
            maxZ = ZoneOf(z + radius - edge, zoneSize);
        }

        /// <summary>
        /// A height profile smoothed along its length, for a road that follows the land
        /// without its bumps.
        ///
        /// Two passes of a centred moving average, which together make a triangular
        /// kernel - no kink left where a lump used to be. The window shrinks towards
        /// either end, down to nothing at the ends themselves, and that is what makes the
        /// road meet the ground exactly where it starts and where it stops. And since
        /// every window is symmetric, a profile that is already a straight slope comes
        /// out as it went in: a road up an even hillside is left alone, and only the
        /// lumps on it are taken off.
        /// </summary>
        public static float[] SmoothProfile(IList<float> heights, int halfWindow)
        {
            var n = heights.Count;
            var result = new float[n];
            for (var i = 0; i < n; i++) result[i] = heights[i];
            if (n < 3 || halfWindow < 1) return result;

            var source = new float[n];
            for (var pass = 0; pass < 2; pass++)
            {
                Array.Copy(result, source, n);
                for (var i = 0; i < n; i++)
                {
                    var h = Math.Min(halfWindow, Math.Min(i, n - 1 - i));
                    var sum = 0.0;
                    for (var k = i - h; k <= i + h; k++) sum += source[k];
                    result[i] = (float)(sum / (2 * h + 1));
                }
            }

            return result;
        }

        /// <summary>
        /// Where along a polyline a point is nearest, as a fractional index - 2.5 is half
        /// way from the third point to the fourth - together with the distance to it.
        /// </summary>
        public static float NearestOnPath(IList<Vec2> path, float x, float z, out float distance)
        {
            distance = float.MaxValue;
            if (path.Count == 0) return 0f;

            if (path.Count == 1)
            {
                var only = path[0];
                distance = (float)Math.Sqrt((x - only.X) * (x - only.X) + (z - only.Z) * (z - only.Z));
                return 0f;
            }

            var best = float.MaxValue;
            var bestAlong = 0f;
            for (var k = 0; k < path.Count - 1; k++)
            {
                var a = path[k];
                var b = path[k + 1];

                var abx = b.X - a.X;
                var abz = b.Z - a.Z;
                var lenSq = abx * abx + abz * abz;

                var t = 0f;
                if (lenSq > 1e-6f)
                {
                    t = ((x - a.X) * abx + (z - a.Z) * abz) / lenSq;
                    if (t < 0f) t = 0f;
                    else if (t > 1f) t = 1f;
                }

                var dx = x - (a.X + abx * t);
                var dz = z - (a.Z + abz * t);
                var d = dx * dx + dz * dz;
                if (d < best)
                {
                    best = d;
                    bestAlong = k + t;
                }
            }

            distance = (float)Math.Sqrt(best);
            return bestAlong;
        }

        /// <summary>A profile read at a fractional index, in a straight line between its points.</summary>
        public static float ProfileAt(IList<float> profile, float index)
        {
            if (profile.Count == 0) return 0f;

            var last = profile.Count - 1;
            if (index <= 0f) return profile[0];
            if (index >= last) return profile[last];

            var k = (int)Math.Floor(index);
            var f = index - k;
            return profile[k] + (profile[k + 1] - profile[k]) * f;
        }

        /// <summary>Mirrors Heightmap.WorldToVertexMask for one axis.</summary>
        public static int VertexAt(float world, float origin, float scale, int half)
        {
            return (int)Math.Floor((world - origin) / scale + 0.5) + half;
        }

        /// <summary>The exact inverse of <see cref="VertexAt"/>.</summary>
        public static float WorldAt(int vertex, float origin, float scale, int half)
        {
            return origin + (vertex - half) * scale;
        }

        /// <summary>
        /// The game's brush shape: solid across nearly the whole radius with the fade
        /// squeezed into the last sliver, so a painted strip has a clean edge.
        /// </summary>
        public static float Falloff(float distance, float radius)
        {
            if (radius <= 0f) return 0f;
            var t = 1f - distance / radius;
            if (t <= 0f) return 0f;
            if (t > 1f) t = 1f;
            return (float)Math.Pow(t, 0.1);
        }

        // ---------------- covering with discs ----------------

        /// <summary>
        /// How wide a disc round every point of a polyline must be for the discs between
        /// them to cover everything within <paramref name="reach"/> of the line.
        ///
        /// The samples are not the line. Between two of them it runs on, and a place
        /// beside the middle of that step is further from both samples than from the
        /// line - with samples a metre apart and a reach of one, 1.12 m from each. Every
        /// point of a step lies within half its length of one end, so half the longest
        /// step is all the discs need on top of the reach. It is measured rather than
        /// assumed: a curve's samples are not evenly spaced.
        /// </summary>
        public static float DiscCover(IList<Vec2> path, float reach)
        {
            var longest = 0f;
            for (var i = 1; i < path.Count; i++)
            {
                var dx = path[i].X - path[i - 1].X;
                var dz = path[i].Z - path[i - 1].Z;
                longest = Math.Max(longest, (float)Math.Sqrt(dx * dx + dz * dz));
            }

            return reach + longest * 0.5f;
        }

        /// <summary>
        /// Centres of discs that between them cover an axis-aligned square: the square cut
        /// into equal cells no wider than <paramref name="cell"/>, with a disc round each
        /// cell reaching its corners.
        ///
        /// One disc round the whole square has to reach its corners too, and then stands
        /// out past the middle of every side by two fifths of the half-width - over thirty
        /// metres on the largest pad the levelling tool makes. A disc per cell stands out
        /// by a fifth of a cell at most.
        /// </summary>
        public static List<Vec2> SquareCover(Vec2 centre, float half, float cell, out float radius)
        {
            half = Math.Max(half, 0f);
            var count = Math.Max(1, (int)Math.Ceiling(2.0 * half / Math.Max(cell, 0.1f)));
            var side = 2f * half / count;

            // Half a cell's diagonal: the furthest any point of it is from its middle.
            radius = side * (float)Math.Sqrt(0.5);

            var centres = new List<Vec2>(count * count);
            for (var i = 0; i < count; i++)
            for (var j = 0; j < count; j++)
                centres.Add(new Vec2(centre.X - half + side * (j + 0.5f),
                                     centre.Z - half + side * (i + 0.5f)));

            return centres;
        }

        // ---------------- blueprint alignment ----------------

        /// <summary>Yaw in degrees from the Y component of a rotation quaternion.</summary>
        public static float YawOf(float qy, float qw)
        {
            var yaw = (float)(2.0 * Math.Atan2(qy, qw) * (180.0 / Math.PI));
            yaw %= 360f;
            return yaw < 0f ? yaw + 360f : yaw;
        }

        private static float OffGrid(float yaw, float baseYaw, float grid)
        {
            var d = (yaw - baseYaw) % grid;
            if (d < 0f) d += grid;
            return Math.Min(d, grid - d);
        }

        /// <summary>
        /// The single rotation that squares a whole build to a yaw grid. Searched near
        /// zero only: a base a quarter-turn away fits just as well but silently turns
        /// the template sideways.
        /// </summary>
        public static float BestBaseYaw(IList<float> yaws, float grid, float searchDegrees)
        {
            var best = 0f;
            var bestCost = float.MaxValue;

            var steps = (int)Math.Round(searchDegrees * 100);
            for (var i = -steps; i <= steps; i++)
            {
                var candidate = i / 100f;
                var cost = 0f;
                for (var k = 0; k < yaws.Count; k++) cost += OffGrid(yaws[k], candidate, grid);

                if (cost >= bestCost) continue;
                bestCost = cost;
                best = candidate;
            }

            var normalised = best % 360f;
            return normalised < 0f ? normalised + 360f : normalised;
        }

        /// <summary>The worst correction any single piece has to take at that base.</summary>
        public static float WorstYawError(IList<float> yaws, float baseYaw, float grid)
        {
            var worst = 0f;
            for (var i = 0; i < yaws.Count; i++)
                worst = Math.Max(worst, OffGrid(yaws[i], baseYaw, grid));
            return worst;
        }

        public static float SnapYaw(float yaw, float baseYaw, float grid)
        {
            var relative = (yaw - baseYaw) % 360f;
            if (relative < 0f) relative += 360f;
            var snapped = (float)Math.Round(relative / grid) * grid % 360f;
            return snapped < 0f ? snapped + 360f : snapped;
        }

        /// <summary>Rotates about the Y axis, the way the game's left-handed space does.</summary>
        public static Vec2 RotateXZ(Vec2 point, float degrees)
        {
            var a = degrees * Math.PI / 180.0;
            var cos = (float)Math.Cos(a);
            var sin = (float)Math.Sin(a);
            return new Vec2(point.X * cos + point.Z * sin, -point.X * sin + point.Z * cos);
        }

        /// <summary>
        /// The shift that puts a set of coordinates back on a grid. A plain average
        /// would be pulled apart by values sitting either side of a cell boundary, so
        /// this picks the residual with the least total circular distance to the rest.
        /// </summary>
        public static float BestGridShift(IList<float> values, float grid)
        {
            if (values.Count == 0) return 0f;

            var best = 0f;
            var bestCost = float.MaxValue;

            for (var i = 0; i < values.Count; i++)
            {
                var candidate = values[i] % grid;
                if (candidate < 0f) candidate += grid;

                var cost = 0f;
                for (var k = 0; k < values.Count; k++)
                {
                    var r = values[k] % grid;
                    if (r < 0f) r += grid;
                    var d = Math.Abs(r - candidate);
                    cost += Math.Min(d, grid - d);
                }

                if (cost >= bestCost) continue;
                bestCost = cost;
                best = candidate;
            }

            return -best;
        }

        // ---------------- bridge piers ----------------

        /// <summary>
        /// The longest stretch of wooden deck that can hang between two supports with
        /// nothing under it, in metres.
        ///
        /// Read out of WearNTear rather than guessed. Support starts at 100 where a
        /// piece touches ground, each joint multiplies it by (1 - loss * distance) with a
        /// horizontal loss of 0.2 per metre for wood, and a piece dies below 10. A run
        /// held at both ends also gets the game's two-sided rule, which averages the
        /// two arriving values when the supports lie more than 100 degrees apart — that
        /// rule is the only reason a span longer than a cantilever stands at all.
        /// Eight deck pieces come out at 11.4, nine at 8.35.
        /// </summary>
        public const float MaxFreeSpan = 16f;

        /// <summary>
        /// The tallest wooden leg worth building, in metres.
        ///
        /// A column of poles decays vertically at 0.125 per metre: eight poles reach
        /// 11.87 and still stand, nine reach 8.75 and fall. But a leg at that limit has
        /// nothing left to give away — at 16 m no deck hangs off it at any spacing, so
        /// the last useful height is 14.
        /// </summary>
        public const float MaxPierHeight = 14f;

        // How far apart legs may stand, by how tall they are. Measured on an 80 m deck,
        // long enough that the legs are the only thing holding the middle up: a shorter
        // test bridge stands on its own banks and reports whatever spacing it was asked
        // about. The step at 6-8 m is real and not a rounding artefact — it is where the
        // two-sided rule stops being able to make up the difference.
        private static readonly float[] PierHeightSteps = { 2f, 4f, 6f, 8f, 10f, 12f, 14f };
        private static readonly float[] PierSpanSteps = { 16f, 14f, 10f, 10f, 6f, 4f, 2f };

        /// <summary>
        /// How far apart two supports may stand when the taller of them rises
        /// <paramref name="pierHeight"/> metres off the ground. Zero when nothing can be
        /// carried at that height.
        ///
        /// Heights round up to the next measured step rather than interpolating between
        /// them: promising a span that was never measured is how a bridge falls down
        /// after it is built.
        /// </summary>
        public static float MaxPierSpacing(float pierHeight)
        {
            // A leg of no height is the bank itself, and the bank has full support.
            if (pierHeight <= 0f) return MaxFreeSpan;

            for (var i = 0; i < PierHeightSteps.Length; i++)
                if (pierHeight <= PierHeightSteps[i]) return PierSpanSteps[i];

            return 0f;
        }

        /// <summary>
        /// How far apart a bridge will leave its legs when it has the choice, in metres.
        ///
        /// Strength alone is a bad judge of this. The support rules let a shallow
        /// crossing hold with legs sixteen metres apart, and what that builds is a deck
        /// with almost nothing under it, planted wherever the riverbed happened to allow
        /// rather than at any spacing a person would choose. Both bridges built by hand
        /// put a frame every six to eight.
        ///
        /// Kept in metres rather than in a count of sections, because metres are what the
        /// walk actually compares and a count would have to be restated the moment a
        /// section stops being two metres wide. Six is three of today's sections.
        ///
        /// It is a preference, not a rule: where nothing within reach can carry a leg it
        /// gives way rather than refusing a crossing that would have stood.
        /// </summary>
        public const float MaxMetresBetweenLegs = 6f;

        /// <summary>
        /// How far apart legs may stand under a roofed deck of this width.
        ///
        /// The deck is not what fails first once there is a roof on it. Support arrives
        /// at the ridge through beam, post and slope, four connections above the floor,
        /// and each one takes its share: simulated on the shipped 1.0 rules, a three
        /// wide covered bridge on six metre spacing leaves its ridge at 9.79 against a
        /// collapse threshold of 10, and a five wide one at 6.58. The deck underneath
        /// is fine in every one of those cases, which is why this was invisible until
        /// the superstructure was simulated on its own.
        ///
        /// Four metres puts every width tested back over the line; five wide only just,
        /// at 10.53, so it gets two.
        /// </summary>
        public static float LegSpacingFor(bool covered, int width)
        {
            // Only an odd deck carries a ridge, and the ridge is the piece that fails:
            // it hangs a step above the slopes with nothing under its own centre. Even
            // decks let their two inner slopes meet instead, and hold at six metres -
            // three wide comes out at 9.79 there against a threshold of 10, while four
            // wide, one lane wider and heavier, sits comfortably at 11.26.
            if (!covered || width < 3) return MaxMetresBetweenLegs;

            // Straight off the simulation of the finished gable, at six metres:
            //   3 -> 9.79   4 -> 9.46   5 -> 6.29   6 -> 6.08   all under the ten
            // Four carries three and four (17.00, 15.43). Five and six need two: five
            // comes out at 10.92 on four, which is inside this model's error rather
            // than a margin, and six frankly fails there at 9.91.
            //
            // Seven does not stand at any spacing we can offer - 8.19 even at two - so
            // its number here is a formality and the roof is what has to give.
            return width >= 5 ? 2f : 4f;
        }

        /// <summary>
        /// Where a bridge stands its legs, and whether what is left between them holds.
        /// </summary>
        public sealed class BridgePlan
        {
            /// <summary>Profile indices carrying a leg. The two banks are not in here.</summary>
            public readonly List<int> Piers = new List<int>();

            /// <summary>False when a stretch has nothing to stand on and is too wide to span.</summary>
            public bool Stands;

            /// <summary>The stretch that defeated it, as profile indices. Both -1 when it stands.</summary>
            public int GapFrom = -1;

            public int GapTo = -1;
        }

        /// <summary>
        /// Walks the ground under a planned deck and decides where the legs go.
        ///
        /// The two ends are the banks and are assumed to meet the ground, so they carry
        /// full support and never appear as legs. Between them the walk is greedy: from
        /// each support it reaches for the furthest next one it is allowed to reach, so
        /// the bridge gets the fewest legs that hold rather than the most that fit.
        ///
        /// Reaching further is not simply a question of distance. Each end of a stretch
        /// caps how far it may span, and the cap comes from the height of that leg, so a
        /// distant shallow spot can be reachable where a nearer deep one is not. That is
        /// why every candidate is tried rather than the walk stopping at the first
        /// failure.
        /// </summary>
        /// <param name="ground">Ground height at evenly spaced samples along the centreline.</param>
        /// <param name="step">Distance between two samples, in metres.</param>
        /// <param name="deck">Height the deck will sit at.</param>
        public static BridgePlan PlanPiers(IList<float> ground, float step, float deck,
                                          float maxBetweenLegs = MaxMetresBetweenLegs)
        {
            var plan = new BridgePlan();

            if (ground == null || ground.Count < 2 || step <= 0f)
            {
                plan.Stands = ground != null && ground.Count > 0;
                return plan;
            }

            var last = ground.Count - 1;
            var at = 0;

            while (at < last)
            {
                // Two answers: the furthest that keeps to the preferred spacing, and
                // the furthest that stands at all. The first is what gets used, which
                // makes the legs land on a rhythm instead of wherever the ground last
                // permitted; the second is the fallback when nothing near enough can
                // carry one.
                var reach = -1;
                var stretch = -1;

                for (var j = at + 1; j <= last; j++)
                {
                    // Anywhere but the far bank has to be somewhere a leg can stand.
                    if (j != last && PierAt(ground, deck, j) > MaxPierHeight) continue;

                    var allowed = Math.Min(MaxPierSpacing(PierAt(ground, deck, at)),
                                           MaxPierSpacing(PierAt(ground, deck, j)));
                    if ((j - at) * step > allowed) continue;

                    stretch = j;
                    if ((j - at) * step <= maxBetweenLegs) reach = j;
                }

                if (reach < 0) reach = stretch;

                if (reach < 0)
                {
                    plan.GapFrom = at;

                    // The far bank is where the bridge ends, not where the trouble
                    // does. Reporting it makes a hole in the middle of an otherwise
                    // fine crossing read as the whole crossing being impossible, so
                    // name the first place a leg could stand again instead.
                    plan.GapTo = last;
                    for (var j = at + 1; j <= last; j++)
                    {
                        if (j != last && PierAt(ground, deck, j) > MaxPierHeight) continue;
                        plan.GapTo = j;
                        break;
                    }

                    plan.Stands = false;
                    return plan;
                }

                at = reach;
                if (at != last) plan.Piers.Add(at);
            }

            plan.Stands = true;
            return plan;
        }

        /// <summary>
        /// How tall a leg at this sample would be. Ground standing above the deck counts
        /// as no leg at all rather than a negative one: the deck is resting on it.
        /// </summary>
        private static float PierAt(IList<float> ground, float deck, int index)
        {
            var height = deck - ground[index];
            return height > 0f ? height : 0f;
        }
    }
}
