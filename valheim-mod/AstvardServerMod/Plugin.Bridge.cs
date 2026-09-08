using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        internal static GameObject BridgeButton;

        internal static GameObject BridgeHint;

        internal static GameObject BridgeWidthInput;

        internal static GameObject BridgeLiftInput;

        internal static GameObject BridgeStartButton;

        internal static GameObject BridgeEndButton;

        internal static GameObject BridgeCancelButton;

        // Deck and leg are the same two-metre module, which is what makes the whole
        // thing arithmetic rather than fitting: wood_floor is 2x2, and wood_pole2
        // stacks at exactly 2.0 — measured across nine captured blueprints, every one
        // of them a column of y, y+2, y+4.
        private const string DeckPrefab = "wood_floor";
        private const string LegPrefab = "wood_pole2";
        private const float Module = 2f;

        // Long enough for any crossing worth a tool, short enough that one press does
        // not put six hundred pieces into the world in a single frame.
        private const float MaxBridgeLength = 120f;

        private static Vector3 _bridgeStart;

        private static bool _bridgeStarted;

        internal static bool BridgeInProgress
        {
            get { return _bridgeStarted; }
        }

        internal static void CancelBridge()
        {
            if (!_bridgeStarted) return;

            _bridgeStarted = false;
            UpdateBridgeHint();
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Отменено");
        }

        internal static void MarkBridgeStart()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            _bridgeStart = player.transform.position;
            _bridgeStarted = true;
            UpdateBridgeHint();
            InventoryGui.instance?.Hide();
            player.Message(MessageHud.MessageType.Center, "Начало отмечено");
        }

        private static void UpdateBridgeHint()
        {
            var label = BridgeHint != null ? BridgeHint.GetComponentInChildren<Text>() : null;
            if (label == null) return;

            label.text = _bridgeStarted
                ? $"Ширина в секциях (1-4),{NEWLINE}подъём настила над берегом.{NEWLINE}"
                  + $"Начало отмечено — иди на тот{NEWLINE}берег и нажми «Построить».{NEWLINE}Esc — отменить."
                : $"Ширина в секциях (1-4),{NEWLINE}подъём настила над берегом.{NEWLINE}"
                  + $"Встань на этом берегу{NEWLINE}и нажми «Начать».";
        }

        /// <summary>
        /// Measures the crossing, asks <see cref="Geometry.PlanPiers"/> whether it can
        /// stand, and only then puts anything in the world. Refusing with a distance is
        /// worth more than building something that falls down half a minute later,
        /// leaving nothing behind to explain why.
        /// </summary>
        private static void BuildBridge()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            if (!_bridgeStarted)
            {
                player.Message(MessageHud.MessageType.Center, "Сначала нажми «Начать»");
                return;
            }

            var zones = ZoneSystem.instance;
            var scene = ZNetScene.instance;
            if (zones == null || scene == null) return;

            var from = _bridgeStart;
            var to = player.transform.position;
            var flat = new Vector3(to.x - from.x, 0f, to.z - from.z);
            var length = flat.magnitude;

            if (length < Module * 2f)
            {
                player.Message(MessageHud.MessageType.Center, "Точки слишком близко");
                return;
            }

            if (length > MaxBridgeLength)
            {
                player.Message(MessageHud.MessageType.Center,
                    $"Далеко: {length:F0} м, максимум {MaxBridgeLength:F0}");
                return;
            }

            var deckPrefab = scene.GetPrefab(DeckPrefab);
            var legPrefab = scene.GetPrefab(LegPrefab);
            if (deckPrefab == null || legPrefab == null)
            {
                Log.LogWarning("[AstvardServerMod] Bridge prefabs missing from the scene.");
                player.Message(MessageHud.MessageType.Center, "Нет деталей для моста");
                return;
            }

            // The deck is level, so it has to sit at the higher of the two marks or it
            // would meet one bank underground. The field lifts it further, over water
            // or over a rise in the middle.
            var lift = Mathf.Clamp(ParseField(BridgeLiftInput, 0f), 0f, 16f);
            var deck = Mathf.Max(from.y, to.y) + lift;
            var width = Mathf.Clamp(Mathf.RoundToInt(ParseField(BridgeWidthInput, 2f)), 1, 4);

            var dir = flat / length;

            // Sections land on whole modules and stop short of the far mark rather than
            // past it: deck hanging beyond the bank is deck with nothing under it.
            var sections = Mathf.FloorToInt(length / Module) + 1;

            var centres = new List<Vector3>(sections);
            var ground = new List<float>(sections);
            for (var i = 0; i < sections; i++)
            {
                var at = from + dir * (i * Module);

                // No terrain collider means the zone is not loaded, and the obvious
                // fallback is the worst one available: putting the deck's own line into
                // the profile reads as ground exactly at deck height, which is full
                // support, which approves a span over nothing at all.
                if (!zones.GetGroundHeight(at, out var height))
                {
                    player.Message(MessageHud.MessageType.Center,
                        $"Земля не прогружена на {i * Module:F0} м{NEWLINE}пройди вдоль будущего моста");
                    Log.LogWarning($"[AstvardServerMod] No ground at section {i}, bridge refused.");
                    return;
                }

                centres.Add(at);
                ground.Add(height);
            }

            var plan = Geometry.PlanPiers(ground, Module, deck);
            if (!plan.Stands)
            {
                player.Message(MessageHud.MessageType.Center,
                    $"Не устоит: с {plan.GapFrom * Module:F0} м по {plan.GapTo * Module:F0} м"
                    + $"{NEWLINE}не на что опереться");
                Log.LogInfo($"[AstvardServerMod] Bridge refused: gap {plan.GapFrom}..{plan.GapTo} " +
                            $"of {sections} sections, deck {deck:F1}.");
                return;
            }

            _bridgeStarted = false;
            UpdateBridgeHint();

            var placed = Raise(deckPrefab, legPrefab, centres, ground, plan, deck, width,
                               Quaternion.LookRotation(dir, Vector3.up), player.GetPlayerID());

            player.Message(MessageHud.MessageType.Center,
                $"Мост {length:F0} м, опор {plan.Piers.Count}, деталей {placed}");
            Log.LogInfo($"[AstvardServerMod] Bridge {length:F1} m, {sections} sections, " +
                        $"width {width}, deck {deck:F1}, {plan.Piers.Count} piers, {placed} pieces.");
        }

        /// <summary>
        /// Puts the whole bridge down in one pass, deliberately without pausing.
        ///
        /// A span outreaches a cantilever only because the game averages support
        /// arriving from both ends, and half a span reaches neither — eight metres is
        /// all that hangs off one bank. Laying this in batches the way blueprints do
        /// would drop the middle before the far end existed. One long frame is the
        /// price, and the length cap is what keeps it to one.
        /// </summary>
        private static int Raise(GameObject deckPrefab, GameObject legPrefab,
                                 List<Vector3> centres, List<float> ground,
                                 Geometry.BridgePlan plan, float deck, int width,
                                 Quaternion facing, long creator)
        {
            var half = (width - 1) * 0.5f * Module;
            var side = facing * Vector3.right;
            var placed = 0;

            for (var i = 0; i < centres.Count; i++)
            {
                for (var w = 0; w < width; w++)
                {
                    var at = new Vector3(centres[i].x, deck, centres[i].z)
                             + side * (w * Module - half);
                    Spawn(deckPrefab, at, facing, creator);
                    placed++;
                }
            }

            // The banks carry legs too. A deck lifted over its own bank stands on one
            // exactly as it stands on anything else, and leaving those out is how a
            // bridge ends up hanging from its middle.
            var legs = new List<int>(plan.Piers);
            if (!legs.Contains(0)) legs.Insert(0, 0);
            if (!legs.Contains(centres.Count - 1)) legs.Add(centres.Count - 1);

            foreach (var index in legs)
                placed += Leg(legPrefab, centres[index], side, half, deck, ground[index],
                              width, facing, creator);

            return placed;
        }

        /// <summary>
        /// Stands one leg: poles two metres at a time from just under the deck down
        /// past the ground. The lowest is allowed to sink rather than stop short — a
        /// leg that does not reach carries nothing, and buried wood costs nothing.
        /// </summary>
        private static int Leg(GameObject prefab, Vector3 at, Vector3 side, float half,
                               float deck, float ground, int width, Quaternion facing,
                               long creator)
        {
            var drop = deck - ground;

            // Resting on the bank already: a pole here would push up through the deck.
            if (drop <= Module * 0.5f) return 0;

            var poles = Mathf.CeilToInt(drop / Module);
            var placed = 0;

            // A wide deck gets a leg under each edge rather than being balanced on a
            // single line of poles down the middle.
            var offsets = width > 1 ? new[] { -half, half } : new[] { 0f };

            foreach (var offset in offsets)
            {
                for (var p = 1; p <= poles; p++)
                {
                    var pos = new Vector3(at.x, deck - p * Module, at.z) + side * offset;
                    Spawn(prefab, pos, facing, creator);
                    placed++;
                }
            }

            return placed;
        }

        private static void Spawn(GameObject prefab, Vector3 at, Quaternion rotation, long creator)
        {
            var go = Instantiate(prefab, at, rotation);
            var piece = go.GetComponent<Piece>();
            if (piece != null) piece.SetCreator(creator);
        }
    }
}
