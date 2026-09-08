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
    }
}
