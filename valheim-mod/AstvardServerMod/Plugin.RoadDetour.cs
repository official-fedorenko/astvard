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
        /// short way round and brings it back on its line before the piece ends.
        ///
        /// Read once per piece, after its zones have built their objects - before that
        /// there is nothing in the scene to find, and a location without its prefab has
        /// no radius to ask for.
        /// </summary>
        private const float DetourClearance = 1f;

        /// <summary>The shortest easing, for a step so small a longer one would be silly.</summary>
        private const float DetourMinRamp = 5f;

        /// <summary>Metres of easing for every metre of the step aside.</summary>
        private const float DetourRampPerStep = 3f;

        /// <summary>
        /// How far the road may stray from its line at all. The job holds the zones
        /// within <see cref="RoadJobMargin"/> of the path it was given, and paint outside
        /// them would be written to compilers nobody loaded; the widest vein and the
        /// average ruin both fit well inside this.
        /// </summary>
        private const float DetourMaxStep = 16f;

        /// <summary>A piece is never cut shorter than this to make room for a way round.</summary>
        private const float DetourLeastPiece = 24f;

        /// <summary>
        /// How far short of the easing a piece is cut when it is cut at all.
        ///
        /// Stopping exactly where the easing would begin gains nothing: the next piece
        /// would start at that same metre, and the way round would need to begin at its
        /// very first point - which is the one point that may not move, because it is
        /// where this piece ended. A few metres of lead-in is what makes the second
        /// attempt work.
        /// </summary>
        private const float DetourCutBack = 8f;

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

        /// <summary>Whatever stands fast in an area, as flat circles.</summary>
        private static List<Geometry.Blocker> RoadBlockers(Rect area, Vector3 from, Vector3 to,
                                                           Dictionary<string, int> found)
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

                // The same question the clearing asks, the other way up: what it would
                // take out needs no going round, and what it would leave does. Asked of
                // the same fields, so the two can never disagree about one boulder.
                var bare = thing.m_destructibleType == DestructibleType.Tree
                           || BreaksDownTo(thing.gameObject, WoodAndStone, 0);
                if (bare) continue;

                // A berry bush goes under the paving now, and a sapling somebody planted
                // is theirs to move - neither is worth bending a road for.
                var go = thing.gameObject;
                if (go.GetComponent<Pickable>() != null || go.GetComponentInParent<Piece>() != null) continue;

                Note(blockers, found, area, go, 0f, "rock");
            }

            // A nest the road ran through would go on sending its greydwarves at whoever
            // walks it. The circle is its own footing, which is small, plus a few metres
            // of room - what matters is not paving over it.
            foreach (var nest in Object.FindObjectsByType<CreatureSpawner>(FindObjectsSortMode.None))
                if (nest != null) Note(blockers, found, area, nest.gameObject, 4f, "nest");

            // And the structures: everything a location covers, read from the location
            // itself now that its prefab is in the scene. The two ends of the whole road
            // are let off - the network's roads run between markers, and a road that
            // went round the one it was going to would never arrive.
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
        /// A piece of road bent round what it must not pave over.
        ///
        /// <paramref name="cutAt"/> comes back positive when something stands too near
        /// the far end of the piece to be gone round inside it: there is no room left to
        /// come back on the line, and the end of a piece may not move, because the next
        /// piece starts from it. The caller cuts the piece short there and meets the
        /// same thing again at the start of the next one, with the whole of it to work
        /// in. Blockers at the near end cannot be helped that way and are left standing,
        /// as they were before any of this.
        /// </summary>
        private static List<Vector3> BendRoadPiece(RoadJob job, List<Vector3> piece,
                                                   Dictionary<string, int> found, out float cutAt)
        {
            cutAt = -1f;
            if (piece.Count < 3) return piece;

            var blockers = RoadBlockers(RoadArea(piece, job.Radius), job.Path[0],
                                        job.Path[job.Path.Count - 1], found);
            if (blockers.Count == 0) return piece;

            var flat = new List<Vec2>(piece.Count);
            foreach (var point in piece) flat.Add(new Vec2(point.x, point.z));

            var plan = Geometry.Detour(flat, blockers, job.Radius, DetourClearance,
                                       DetourMinRamp, DetourRampPerStep, DetourMaxStep);

            foreach (var bend in plan.Bends)
            {
                if (bend.Refused == Geometry.BendRefusal.TooWide) job.TooWide++;
                else if (bend.Refused == Geometry.BendRefusal.PastTheEnd)
                {
                    // Only the far end can be helped by a shorter piece, and only while
                    // what is left is still worth laying.
                    var cut = bend.From - DetourCutBack;
                    if (cut >= DetourLeastPiece && (cutAt < 0f || cut < cutAt)) cutAt = cut;
                    else job.Unbent++;
                }
            }

            if (cutAt > 0f) return piece;

            job.Bends += plan.Taken;
            if (plan.Taken == 0) return piece;

            var bent = new List<Vector3>(piece.Count);
            for (var i = 0; i < piece.Count; i++)
                bent.Add(new Vector3(plan.Path[i].X, piece[i].y, plan.Path[i].Z));

            return bent;
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
            var want = RoadArea(path, reach);
            var kept = !fresh
                       && Time.realtimeSinceStartup - _handBlockersWhen < HandBlockersHold
                       && want.xMin >= _handBlockersFor.xMin && want.xMax <= _handBlockersFor.xMax
                       && want.yMin >= _handBlockersFor.yMin && want.yMax <= _handBlockersFor.yMax;
            if (kept) return HandBlockers;

            var area = Rect.MinMaxRect(want.xMin - HandBlockersSlack, want.yMin - HandBlockersSlack,
                                       want.xMax + HandBlockersSlack, want.yMax + HandBlockersSlack);

            HandFound.Clear();
            HandBlockers.Clear();
            HandBlockers.AddRange(RoadBlockers(area, from, to, HandFound));
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
            if (path.Count < 3 || !RoadClearingActive) return;

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
            if (job.TooWide > 0) note += $", {job.TooWide} too big to get round";
            if (job.Unbent > 0) note += $", {job.Unbent} left standing at a join";
            return note;
        }
    }
}
