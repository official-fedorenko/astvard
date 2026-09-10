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
