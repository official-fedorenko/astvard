using System.Collections.Generic;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        // How finely a square pad is cut for its ward check. A disc per cell stands out
        // past the square by under a metre at this size, and the largest square the
        // levelling tool makes comes to about two thousand of them - nothing, for one
        // press of a button.
        private const float WardCell = 4f;

        /// <summary>
        /// Whether a non-admin may rework the ground under these discs: not if any of them
        /// reaches into a ward that does not let this player build. The ward that says no
        /// flashes, as it does at a pickaxe. An admin is not asked.
        ///
        /// Asked with wardCheck, which is not how the hammer or the pickaxe ask. Without it
        /// the game says yes as soon as one of the wards a disc touches lets the player
        /// in, whatever the others say. That is right for a single stroke made at the
        /// player's own base and a hole for a disc the size of the job: a pad levelled 88 m
        /// out that caught the edge of the player's own ward would have gone through a
        /// neighbour's whole. With wardCheck any ward in the disc that says no is a no,
        /// which is how the game judges the circle of a ward being placed. What it costs
        /// is work where a ward of the player's own overlaps somebody else's, and the game
        /// refuses to place a ward like that in the first place.
        ///
        /// Only wards this client has built can answer. A zone that has just come into
        /// range may still have its objects on the way - ZNetScene builds ten or so a tick
        /// outside a loading screen - and a ward among them goes unseen for those seconds.
        /// </summary>
        private static bool WardsAllowGround(string job, IList<Vector3> centres, float radius)
        {
            if (IsAdminUnlocked) return true;

            foreach (var centre in centres)
            {
                if (PrivateArea.CheckAccess(centre, radius, flash: true, wardCheck: true)) continue;

                Log.LogInfo($"[AstvardServerMod] Ward turned down {job}: r={radius:F1} at {centre}");
                return false;
            }

            return true;
        }

        /// <summary>
        /// The ward check for a stroke of the road tool, or for the single point of a pad
        /// laid round the player, whose work reaches <paramref name="reach"/> from the line.
        /// </summary>
        private static bool WardsAllowStroke(string job, List<Vector3> path, float reach)
        {
            if (IsAdminUnlocked) return true;

            var flat = new List<Vec2>(path.Count);
            foreach (var point in path) flat.Add(new Vec2(point.x, point.z));
            return WardsAllowGround(job, path, Geometry.DiscCover(flat, reach));
        }

        /// <summary>The ward check for a pad BlendLevel is about to level, round or square.</summary>
        private static bool WardsAllowPad(string job, Vector3 target, float reach, bool square)
        {
            if (IsAdminUnlocked) return true;

            // BlendLevel centres the pad on the vertex nearest the target rather than on the
            // target itself, which can be half a cell off along either axis.
            var slack = PaintGridScale(target) * 0.5f;

            if (!square)
                return WardsAllowGround(job, new[] { target }, reach + slack * Mathf.Sqrt(2f));

            var cells = Geometry.SquareCover(new Vec2(target.x, target.z), reach + slack,
                                             WardCell, out var radius);
            var centres = new List<Vector3>(cells.Count);
            foreach (var cell in cells) centres.Add(new Vector3(cell.X, target.y, cell.Z));
            return WardsAllowGround(job, centres, radius);
        }

        /// <summary>
        /// Whether a non-admin may put pieces at these places: each asked exactly as the
        /// hammer asks, at the piece's own position, with no radius and no wardCheck. A
        /// point is never the size of the job, so the hole above does not open here, and a
        /// piece refused where the player could have set it down by hand would be the mod
        /// being stricter than the game for nothing.
        /// </summary>
        private static bool WardsAllowPieces(string job, IList<Vector3> places)
        {
            if (IsAdminUnlocked) return true;

            foreach (var place in places)
            {
                if (PrivateArea.CheckAccess(place, 0f, flash: true, wardCheck: false)) continue;

                Log.LogInfo($"[AstvardServerMod] Ward turned down {job}: piece at {place}");
                return false;
            }

            return true;
        }
    }
}
