using System.Collections.Generic;
using Splatform;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        internal static GameObject RoadTorchButton;

        internal static GameObject RoadTorchInput;

        /// <summary>
        /// The torches a road can be lined with, in the order the button steps through
        /// them; the first is none. All are the game's own standing torches, so they burn,
        /// take fuel and come down under the hammer like any built by hand.
        /// </summary>
        private static readonly string[] TorchPrefabs =
        {
            null, "piece_groundtorch_wood", "piece_groundtorch", "piece_groundtorch_green", "piece_groundtorch_blue",
        };

        private static readonly string[] TorchLabels =
        {
            "выкл", "деревянные", "железные", "зелёные", "синие",
        };

        private static readonly string[] TorchChoiceLabels =
        {
            "Без факелов", "Деревянные", "Железные", "Зелёные", "Синие",
        };

        internal static readonly GameObject[] RoadTorchChoiceButtons = new GameObject[TorchPrefabs.Length];

        private static int _roadTorch;

        internal static bool IsRoadTorches
        {
            get { return _roadTorch != 0; }
        }

        // Off the paint, but close enough to read as the road's own.
        private const float TorchMargin = 0.6f;

        // A little into the ground rather than exactly on it. The game stands a piece by
        // touching the surface; a torch left a hair above it would have nothing under it.
        private const float TorchSink = 0.05f;

        // Where to try next when a torch's own spot is taken: along the edge, never
        // further out or in, so the row stays a row.
        private static readonly float[] TorchNudges = { 0f, 1f, -1f, 2f, -2f };

        /// <summary>How far a piece reaches below its pivot and above it, by its solid colliders.</summary>
        private struct PieceSpan
        {
            public float Below;

            public float Above;
        }

        private static readonly Dictionary<string, PieceSpan> PieceSpans = new Dictionary<string, PieceSpan>();

        private static int? _torchBlockers;

        private static void UpdateRoadTorchButtonLabel()
        {
            var label = RoadTorchButton != null
                ? RoadTorchButton.GetComponentInChildren<Text>(true)
                : null;
            if (label != null) label.text = "Факелы: " + TorchLabels[_roadTorch];
        }

        private static void ChooseRoadTorch(int choice)
        {
            _roadTorch = Mathf.Clamp(choice, 0, TorchPrefabs.Length - 1);
            UpdateRoadTorchButtonLabel();
            Log.LogInfo($"[AstvardServerMod] Road torches: {TorchPrefabs[_roadTorch] ?? "off"}");
        }

        private static float TorchSpacing()
        {
            return Mathf.Clamp(ParseField(RoadTorchInput, 10f), 4f, 50f);
        }

        /// <summary>
        /// Everything solid a torch may not stand in: rocks and trunks, other pieces,
        /// carts and ships. Not the ground, and not creatures - a boar wandering past is
        /// no reason to leave a gap. Resolved on first use, because Unity will not look
        /// up layers from a MonoBehaviour's field initialisers.
        /// </summary>
        private static int TorchBlockers
        {
            get
            {
                if (_torchBlockers == null)
                    _torchBlockers = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "vehicle");
                return _torchBlockers.Value;
            }
        }

        /// <summary>What one lining did.</summary>
        private sealed class TorchRun
        {
            public int Placed;

            /// <summary>Nowhere to stand: water, a location, somebody's ward, or no room.</summary>
            public int Skipped;

            /// <summary>Would have stood, but the materials ran out first.</summary>
            public int Unpaid;
        }

        /// <summary>
        /// Lines what was just laid with torches: both edges of a road, or the rim of a
        /// pad, one every so many metres.
        ///
        /// An admin's torches are free and come full of fuel. Anyone else pays for them
        /// the way the hammer would have made them pay, out of their own bag, and gets a
        /// torch lit with what the game gives any new one. That is not thrift. A torch
        /// taken down with the hammer gives back everything it cost, so free torches in a
        /// player's hands would be a way to print iron and resin.
        ///
        /// Every spot is found before anything is built or paid for, and the torches that
        /// go up are written into the undo step of the road they came with, so undoing the
        /// road takes them down again.
        /// </summary>
        private static TorchRun LineWithTorches(List<Vector3> path, float radius, bool ring,
                                                TerrainUndoStep undo)
        {
            var run = new TorchRun();
            var player = Player.m_localPlayer;
            var scene = ZNetScene.instance;
            var zones = ZoneSystem.instance;
            var name = TorchPrefabs[_roadTorch];
            if (player == null || scene == null || zones == null || name == null || path.Count == 0)
                return run;

            var prefab = scene.GetPrefab(name);
            var piece = prefab != null ? prefab.GetComponent<Piece>() : null;
            if (piece == null)
            {
                Log.LogWarning($"[AstvardServerMod] No torch prefab '{name}'.");
                return run;
            }

            var flat = new List<Vec2>(path.Count);
            foreach (var point in path) flat.Add(new Vec2(point.x, point.z));
            var posts = ring
                ? Geometry.RingPosts(flat[0], radius + TorchMargin, TorchSpacing())
                : Geometry.EdgePosts(flat, TorchSpacing(), radius + TorchMargin);

            var spots = new List<Vector3>();
            foreach (var post in posts)
            {
                if (FindTorchSpot(post, out var spot)) spots.Add(spot);
                else run.Skipped++;
            }

            var free = !TorchesPaidHere || player.NoCostCheat()
                       || zones.GetGlobalKey(piece.FreeBuildKey());
            var count = free ? spots.Count : Mathf.Min(spots.Count, Affordable(player, piece));
            run.Unpaid = spots.Count - count;
            if (count == 0) return run;

            var lift = PivotAboveBase(prefab);
            var creator = player.GetPlayerID();
            var platform = PlatformManager.DistributionPlatform.LocalUser.PlatformUserID;
            var built = new List<ZDOID>();

            for (var i = 0; i < count; i++)
            {
                var go = Instantiate(prefab, spots[i] + Vector3.up * (lift - TorchSink), Quaternion.identity);
                var placed = go.GetComponent<Piece>();
                if (placed != null) placed.SetCreator(creator, platform);

                var fire = go.GetComponentInChildren<Fireplace>();
                if (free && fire != null && !fire.m_infiniteFuel) fire.SetFuel(fire.m_maxFuel);

                var view = go.GetComponent<ZNetView>();
                if (view != null && view.IsValid()) built.Add(view.GetZDO().m_uid);
            }

            if (!free) player.ConsumeResources(piece.m_resources, 0, -1, count);

            if (undo != null)
            {
                undo.Pieces = built;
                undo.PiecePrefab = name;
                undo.PiecesPaid = !free;
            }

            run.Placed = count;
            return run;
        }

        /// <summary>What the message after a road says about its torches.</summary>
        private static string TorchNote(TorchRun run)
        {
            if (run.Placed + run.Skipped + run.Unpaid == 0) return "";

            var note = $", факелов: {run.Placed}";
            if (run.Unpaid > 0) note += $", на {run.Unpaid} не хватило материалов";
            if (run.Skipped > 0) note += $", {run.Skipped} некуда поставить";
            return note;
        }

        /// <summary>
        /// Where a torch meant for this post can stand: on the ground, dry, outside any
        /// no-build location and any ward the player may not build in, and clear of
        /// anything solid. Slid a metre or two along the edge before giving up, since
        /// the usual thing in the way is one trunk.
        ///
        /// The ground is a ray against the terrain, not a heightmap read: the heightmap
        /// answers with its nearest vertex, which on a slope can leave a torch floating a
        /// hand's width or buried to the flame.
        /// </summary>
        private static bool FindTorchSpot(Post post, out Vector3 spot)
        {
            spot = Vector3.zero;
            var zones = ZoneSystem.instance;

            foreach (var nudge in TorchNudges)
            {
                var x = post.At.X + post.Along.X * nudge;
                var z = post.At.Z + post.Along.Z * nudge;
                if (!zones.GetGroundHeight(new Vector3(x, 0f, z), out var ground)) continue;
                if (ground < zones.m_waterLevel) continue;

                var at = new Vector3(x, ground, z);
                if (Location.IsInsideNoBuildLocation(at)) continue;
                if (!IsAdminUnlocked && !PrivateArea.CheckAccess(at, 0f, false)) continue;

                // A torch's worth of room, from knee height up, so the ground itself and the
                // grass on it do not count.
                if (Physics.CheckCapsule(at + Vector3.up * 0.5f, at + Vector3.up * 1.5f, 0.3f,
                                         TorchBlockers, QueryTriggerInteraction.Ignore)) continue;

                spot = at;
                return true;
            }

            return false;
        }

        /// <summary>How many whole torches the player's bag can pay for.</summary>
        private static int Affordable(Player player, Piece piece)
        {
            var inventory = player.GetInventory();
            var most = int.MaxValue;
            foreach (var requirement in piece.m_resources)
            {
                if (requirement.m_resItem == null || requirement.m_amount <= 0) continue;
                var have = inventory.CountItems(requirement.m_resItem.m_itemData.m_shared.m_name);
                most = Mathf.Min(most, have / requirement.m_amount);
            }

            return most;
        }

        /// <summary>
        /// Puts what torches cost back into the world at the player's feet, where walking
        /// picks it up - the same way the hammer leaves what a piece gives back, and a full
        /// bag loses nothing. In whole stacks, as the game drops them: one drop carrying
        /// more than a stack would come back as an overfull slot.
        /// </summary>
        private static void GiveBackTorches(string prefabName, int count)
        {
            var player = Player.m_localPlayer;
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
            var piece = prefab != null ? prefab.GetComponent<Piece>() : null;
            if (player == null || piece == null || count <= 0) return;

            var at = player.transform.position + Vector3.up;
            foreach (var requirement in piece.m_resources)
            {
                if (requirement.m_resItem == null || requirement.m_amount <= 0) continue;

                var stack = Mathf.Max(1, requirement.m_resItem.m_itemData.m_shared.m_maxStackSize);
                var left = requirement.m_amount * count;
                while (left > 0)
                {
                    var amount = Mathf.Min(left, stack);
                    left -= amount;

                    var item = requirement.m_resItem.m_itemData.Clone();
                    item.m_dropPrefab = requirement.m_resItem.gameObject;
                    ItemDrop.DropItem(item, amount, at, Quaternion.identity);
                }
            }
        }

        /// <summary>
        /// How far above its lowest solid point a piece keeps its pivot.
        ///
        /// The game stands a piece on the ground by that lowest point, not by its pivot:
        /// the placement ghost is moved until its nearest collider touches the spot the
        /// ray hit. A standing torch keeps its pivot well up the pole - about 0.65 m for
        /// the wooden one, going by the player's own blueprints - so a torch put down by
        /// its pivot would stand buried to the knee.
        /// </summary>
        private static float PivotAboveBase(GameObject prefab)
        {
            return MeasurePiece(prefab).Below;
        }

        /// <summary>How far below its highest solid point a piece keeps its pivot - a floor's top.</summary>
        private static float PivotBelowTop(GameObject prefab)
        {
            return MeasurePiece(prefab).Above;
        }

        /// <summary>
        /// Measured on a copy that never joins the world, once for each kind of piece, and
        /// written to the log - the torches, the fence's stakes and the floor plates all
        /// stand by it.
        /// </summary>
        private static PieceSpan MeasurePiece(GameObject prefab)
        {
            if (PieceSpans.TryGetValue(prefab.name, out var known)) return known;

            var span = new PieceSpan();
            var probeAt = new Vector3(0f, 5000f, 0f);
            GameObject probe = null;

            // As the blueprint ghosts do: without this the copy would register itself as
            // a real object the moment it exists.
            ZNetView.m_forceDisableInit = true;
            try
            {
                probe = Instantiate(prefab, probeAt, Quaternion.identity);
                var lowest = float.MaxValue;
                var highest = float.MinValue;
                foreach (var col in probe.GetComponentsInChildren<Collider>())
                {
                    if (col == null || !col.enabled || col.isTrigger) continue;
                    lowest = Mathf.Min(lowest, col.bounds.min.y);
                    highest = Mathf.Max(highest, col.bounds.max.y);
                }

                if (lowest < float.MaxValue)
                {
                    span.Below = probeAt.y - lowest;
                    span.Above = highest - probeAt.y;
                }
            }
            finally
            {
                ZNetView.m_forceDisableInit = false;
                if (probe != null) Destroy(probe);
            }

            // A reading outside any piece's height is a failed measurement, not a piece.
            if (span.Below < -8f || span.Below > 8f || span.Above < -8f || span.Above > 8f)
            {
                Log.LogWarning($"[AstvardServerMod] {prefab.name}: measured {span.Below:F2} below and "
                               + $"{span.Above:F2} above the pivot, using 0.");
                span = new PieceSpan();
            }

            PieceSpans[prefab.name] = span;
            Log.LogInfo($"[AstvardServerMod] {prefab.name}: pivot {span.Below:F3} m above its base, "
                        + $"{span.Above:F3} m below its top.");
            return span;
        }
    }
}
