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

        // Deck, leg and ramp are all the same two-metre module, which is what makes the
        // whole thing arithmetic rather than fitting: wood_floor is 2x2, and wood_pole2
        // stacks at exactly 2.0 — measured across nine captured blueprints, every one of
        // them a column of y, y+2, y+4.
        private const string DeckPrefab = "wood_floor";
        private const string LegPrefab = "wood_pole2";
        private const string RampPrefab = "wood_stair";
        private const float Module = 2f;

        // Both of these are pivot corrections, and both were measured off a bridge built
        // by hand rather than reasoned about. wood_pole2 hangs from its middle, so a leg
        // put a whole module under the deck stops half a module short of it — which
        // showed up in game as a one metre gap under every leg. The stair sits the same
        // half module down, which is the step its own geometry expects.
        private const float LegTopDrop = Module * 0.5f;
        private const float RampDrop = Module * 0.5f;

        // A stair covers one module of drop over one module of run, so a bank further
        // below the deck than that needs a flight rather than a step. Eight of them walk
        // down sixteen metres, which is past the height any wooden leg can stand anyway.
        private const int MaxRampSteps = 8;

        // Long enough for any crossing worth a tool, short enough that one press does not
        // put six hundred pieces into the world in a single frame.
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
            HideBridgePreview();
            UpdateBridgeHint();
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Отменено");
        }

        internal static void MarkBridgeStart()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            _bridgeStart = player.transform.position;
            _bridgeStarted = true;
            NoteToolStart();
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
                  + $"Жёлтая проекция — устоит,{NEWLINE}красная — нет.{NEWLINE}"
                  + $"ЛКМ или «Построить».{NEWLINE}Esc — отменить."
                : $"Ширина в секциях (1-4),{NEWLINE}подъём настила над берегом.{NEWLINE}"
                  + $"Встань на этом берегу{NEWLINE}и нажми «Начать».";
        }

        // ---------------- survey ----------------

        /// <summary>
        /// What a crossing from the marked start to here would come to: where every
        /// section lands, what is under it, and whether legs can be made to hold it.
        ///
        /// One method behind both the preview and the build, so what the projection
        /// promises and what the button does cannot drift apart.
        /// </summary>
        private sealed class BridgeSurvey
        {
            public readonly List<Vector3> Centres = new List<Vector3>();
            public readonly List<float> Ground = new List<float>();
            public Geometry.BridgePlan Plan;
            public float Deck;
            public float Length;
            public int Width;
            public Quaternion Facing;

            /// <summary>Null when the crossing could be surveyed, a reason when not.</summary>
            public string Problem;
        }

        private static BridgeSurvey Survey(Vector3 from, Vector3 to)
        {
            var survey = new BridgeSurvey();

            var zones = ZoneSystem.instance;
            if (zones == null)
            {
                survey.Problem = "Мир ещё не готов";
                return survey;
            }

            var flat = new Vector3(to.x - from.x, 0f, to.z - from.z);
            survey.Length = flat.magnitude;

            if (survey.Length < Module * 2f)
            {
                survey.Problem = "Точки слишком близко";
                return survey;
            }

            if (survey.Length > MaxBridgeLength)
            {
                survey.Problem = $"Далеко: {survey.Length:F0} м, максимум {MaxBridgeLength:F0}";
                return survey;
            }

            // The deck is level, so it has to sit at the higher of the two marks or it
            // would meet one bank underground. The field lifts it further, over water or
            // over a rise in the middle.
            var lift = Mathf.Clamp(ParseField(BridgeLiftInput, 0f), 0f, 16f);
            survey.Deck = Mathf.Max(from.y, to.y) + lift;
            survey.Width = Mathf.Clamp(Mathf.RoundToInt(ParseField(BridgeWidthInput, 2f)), 1, 4);

            var dir = flat / survey.Length;
            survey.Facing = Quaternion.LookRotation(dir, Vector3.up);

            // Sections land on whole modules and stop short of the far mark rather than
            // past it: deck hanging beyond the bank is deck with nothing under it.
            var sections = Mathf.FloorToInt(survey.Length / Module) + 1;

            for (var i = 0; i < sections; i++)
            {
                var at = from + dir * (i * Module);

                // No terrain collider means the zone is not loaded, and the obvious
                // fallback is the worst one available: putting the deck's own line into
                // the profile reads as ground exactly at deck height, which is full
                // support, which approves a span over nothing at all.
                if (!zones.GetGroundHeight(at, out var height))
                {
                    survey.Problem = $"Земля не прогружена на {i * Module:F0} м{NEWLINE}"
                                     + "пройди вдоль будущего моста";
                    return survey;
                }

                survey.Centres.Add(at);
                survey.Ground.Add(height);
            }

            survey.Plan = Geometry.PlanPiers(survey.Ground, Module, survey.Deck);
            return survey;
        }

        // ---------------- projection ----------------

        private static GameObject _bridgePreview;

        private static LineRenderer _bridgeLine;

        private static bool _bridgePreviewFailed;

        private static float _bridgeSurveyAt;

        private static BridgeSurvey _shownSurvey;

        private static readonly List<Vector3> BridgeOutline = new List<Vector3>();

        private static void HideBridgePreview()
        {
            if (_bridgePreview != null) _bridgePreview.SetActive(false);
            _shownSurvey = null;
        }

        /// <summary>
        /// Draws the bridge that would be built from here, and says whether it stands,
        /// before a single piece is placed. The line runs along the deck and drops to the
        /// ground wherever a leg is planned, so the spikes hanging off it are the legs at
        /// the depth they will really be sunk to.
        ///
        /// Amber when it holds and red when it does not, which turns walking about
        /// looking for a crossing into something readable off the screen instead of
        /// something to press and find out.
        /// </summary>
        internal static void UpdateBridgePreview()
        {
            var player = Player.m_localPlayer;
            if (!_bridgeStarted || player == null || _bridgePreviewFailed)
            {
                HideBridgePreview();
                return;
            }

            if (_bridgeLine == null && !CreateBridgePreview()) return;

            // Surveying costs a raycast per section, so it runs a few times a second
            // rather than every frame. Nothing here moves faster than a walking player.
            if (Time.time - _bridgeSurveyAt > 0.25f)
            {
                _bridgeSurveyAt = Time.time;
                _shownSurvey = Survey(_bridgeStart, player.transform.position);
            }

            var survey = _shownSurvey;
            if (survey == null || survey.Centres.Count < 2)
            {
                _bridgePreview.SetActive(false);
                return;
            }

            BridgeOutline.Clear();
            var piers = survey.Plan != null ? survey.Plan.Piers : null;
            var last = survey.Centres.Count - 1;

            for (var i = 0; i <= last; i++)
            {
                var centre = survey.Centres[i];
                var top = new Vector3(centre.x, survey.Deck + 0.15f, centre.z);
                BridgeOutline.Add(top);

                var leg = i == 0 || i == last || (piers != null && piers.Contains(i));

                // Down and straight back up in the same stroke: one renderer draws one
                // polyline, and a leg walked twice costs two points and reads correctly.
                if (leg && survey.Deck - survey.Ground[i] > Module * 0.5f)
                {
                    BridgeOutline.Add(new Vector3(centre.x, survey.Ground[i], centre.z));
                    BridgeOutline.Add(top);
                }
            }

            var stands = survey.Problem == null && survey.Plan != null && survey.Plan.Stands;
            var colour = stands
                ? new Color(1f, 0.8f, 0.27f, 0.55f)
                : new Color(1f, 0.25f, 0.2f, 0.55f);

            _bridgePreview.SetActive(true);
            _bridgeLine.startColor = colour;
            _bridgeLine.endColor = colour;
            _bridgeLine.widthMultiplier = Mathf.Max(survey.Width * Module * 0.5f, 1f);
            _bridgeLine.positionCount = BridgeOutline.Count;
            for (var i = 0; i < BridgeOutline.Count; i++)
                _bridgeLine.SetPosition(i, BridgeOutline[i]);
        }

        private static bool CreateBridgePreview()
        {
            var shader = Shader.Find("Sprites/Default")
                         ?? Shader.Find("Particles/Standard Unlit")
                         ?? Shader.Find("Unlit/Color");
            if (shader == null)
            {
                // Better a bridge tool with no projection than one that throws each frame.
                _bridgePreviewFailed = true;
                Log.LogWarning("[AstvardServerMod] No shader for the bridge preview.");
                return false;
            }

            _bridgePreview = new GameObject("AstvardBridgePreview");

            _bridgeLine = _bridgePreview.AddComponent<LineRenderer>();
            _bridgeLine.material = new Material(shader);
            _bridgeLine.useWorldSpace = true;
            _bridgeLine.numCapVertices = 2;
            _bridgeLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _bridgeLine.receiveShadows = false;
            return true;
        }

        // ---------------- building ----------------

        /// <summary>
        /// Surveys the crossing once more and puts it in the world only if it stands.
        /// Refusing with a distance is worth more than building something that falls down
        /// half a minute later, leaving nothing behind to explain why.
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

            var scene = ZNetScene.instance;
            if (scene == null) return;

            // Surveyed again rather than trusting what the projection last drew: that is
            // a quarter of a second old at worst, and a quarter of a second of walking is
            // most of a section.
            var survey = Survey(_bridgeStart, player.transform.position);
            if (survey.Problem != null)
            {
                player.Message(MessageHud.MessageType.Center, survey.Problem);
                return;
            }

            if (!survey.Plan.Stands)
            {
                player.Message(MessageHud.MessageType.Center,
                    $"Не устоит: с {survey.Plan.GapFrom * Module:F0} м по "
                    + $"{survey.Plan.GapTo * Module:F0} м{NEWLINE}не на что опереться");
                Log.LogInfo($"[AstvardServerMod] Bridge refused: gap {survey.Plan.GapFrom}.." +
                            $"{survey.Plan.GapTo} of {survey.Centres.Count} sections.");
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

            _bridgeStarted = false;
            HideBridgePreview();
            UpdateBridgeHint();

            var placed = Raise(deckPrefab, legPrefab, scene.GetPrefab(RampPrefab), survey,
                               player.GetPlayerID());

            player.Message(MessageHud.MessageType.Center,
                $"Мост {survey.Length:F0} м, опор {survey.Plan.Piers.Count}, деталей {placed}");
            Log.LogInfo($"[AstvardServerMod] Bridge {survey.Length:F1} m, " +
                        $"{survey.Centres.Count} sections, width {survey.Width}, " +
                        $"deck {survey.Deck:F1}, {survey.Plan.Piers.Count} piers, {placed} pieces.");
        }

        /// <summary>
        /// Puts the whole bridge down in one pass, deliberately without pausing.
        ///
        /// A span outreaches a cantilever only because the game averages support arriving
        /// from both ends, and half a span reaches neither — eight metres is all that
        /// hangs off one bank. Laying this in batches the way blueprints do would drop the
        /// middle before the far end existed. One long frame is the price, and the length
        /// cap is what keeps it to one.
        /// </summary>
        private static int Raise(GameObject deckPrefab, GameObject legPrefab,
                                 GameObject rampPrefab, BridgeSurvey survey, long creator)
        {
            var centres = survey.Centres;
            var width = survey.Width;
            var deck = survey.Deck;
            var facing = survey.Facing;

            var half = (width - 1) * 0.5f * Module;
            var side = facing * Vector3.right;
            var forward = facing * Vector3.forward;
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
            var last = centres.Count - 1;
            var legs = new List<int>(survey.Plan.Piers);
            if (!legs.Contains(0)) legs.Insert(0, 0);
            if (!legs.Contains(last)) legs.Add(last);

            foreach (var index in legs)
                placed += Leg(legPrefab, centres[index], side, deck, survey.Ground[index],
                              width, facing, creator);

            // A deck standing above its bank is a deck nobody can climb onto.
            placed += Ramp(rampPrefab, centres[0], -forward, deck, creator);
            placed += Ramp(rampPrefab, centres[last], forward, deck, creator);

            return placed;
        }

        /// <summary>
        /// Stands one leg: poles a module at a time from just under the deck down past
        /// the ground. The lowest is allowed to sink rather than stop short — a leg that
        /// does not reach carries nothing, and buried wood costs nothing.
        /// </summary>
        private static int Leg(GameObject prefab, Vector3 at, Vector3 side, float deck,
                               float ground, int width, Quaternion facing, long creator)
        {
            var drop = deck - ground;

            // Resting on the bank already: a pole here would push up through the deck.
            if (drop <= Module * 0.5f) return 0;

            var poles = Mathf.CeilToInt(drop / Module);
            var placed = 0;

            // Always a pair, under the two outer edges of the deck rather than down its
            // middle — which is how the same bridge gets built by hand, and it stands a
            // narrow deck on two feet instead of balancing it on one.
            var edge = width * Module * 0.5f;

            foreach (var offset in new[] { -edge, edge })
            {
                for (var p = 0; p < poles; p++)
                {
                    // Middle pivot: the topmost pole hangs half a module under the deck
                    // so its head meets it, and each one below is a module further down.
                    var y = deck - LegTopDrop - p * Module;
                    Spawn(prefab, new Vector3(at.x, y, at.z) + side * offset, facing, creator);
                    placed++;
                }
            }

            return placed;
        }

        /// <summary>
        /// Walks a flight of stairs from the end of the deck down to the ground.
        ///
        /// One stair covers a module of drop, which is all a bank level with the deck
        /// ever needs — and exactly why a single one was not enough: a bank five metres
        /// down got a step hanging in the air with no way up onto the bridge.
        ///
        /// Each tread is sampled against the ground under it rather than against the
        /// bank at the deck, because the flight walks away from the bridge and the
        /// ground goes on changing while it does. It stops at the first tread whose foot
        /// reaches, and that one is allowed to sink the way the legs are.
        /// </summary>
        private static int Ramp(GameObject prefab, Vector3 end, Vector3 outward, float deck,
                                long creator)
        {
            var zones = ZoneSystem.instance;
            if (prefab == null || zones == null) return 0;

            var rotation = Quaternion.LookRotation(outward, Vector3.up);
            var placed = 0;

            for (var step = 0; step < MaxRampSteps; step++)
            {
                var at = end + outward * (Module * (step + 1));
                var y = deck - RampDrop - step * Module;

                // Nothing to walk down onto: the bank is already at deck height here.
                if (step == 0 && zones.GetGroundHeight(at, out var first)
                    && deck - first <= Module * 0.25f) return 0;

                Spawn(prefab, new Vector3(at.x, y, at.z), rotation, creator);
                placed++;

                // The tread hangs from its middle like everything else, so its foot is
                // half a module below the position it was given.
                if (!zones.GetGroundHeight(at, out var ground)) break;
                if (y - Module * 0.5f <= ground) break;
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
