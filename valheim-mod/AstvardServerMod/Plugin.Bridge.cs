using System.Collections.Generic;
using Splatform;
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

        internal static GameObject BridgeCoverButton;

        // The wooden set. Stone and iron reach much further — iron spans 78 m where wood
        // spans 16 — but each carries its own support table, so another material means
        // another set of numbers in Geometry, not just another list of prefab names.
        private const string DeckPrefab = "wood_floor";
        private const string LegPrefab = "wood_pole2";
        private const string RampPrefab = "wood_stair";
        private const string BeamPrefab = "wood_beam";
        private const string RailPostPrefab = "wood_pole";
        private const string RidgePrefab = "wood_roof_top";
        private const string GablePrefab = "wood_wall_roof";
        private const float Module = 2f;

        // Legs stand on the seams between deck pieces, which are half a module apart, so
        // the ground is probed at that spacing and a leg is placed by probe rather than
        // by deck section.
        private const float ProbeStep = Module * 0.5f;

        // Both of these are pivot corrections, and both were measured off a bridge built
        // by hand rather than reasoned about. wood_pole2 hangs from its middle, so a leg
        // put a whole module under the deck stops half a module short of it — which
        // showed up in game as a one metre gap under every leg. The stair sits the same
        // half module down, which is the step its own geometry expects.
        private const float LegTopDrop = Module * 0.5f;
        private const float RampDrop = Module * 0.5f;

        // Every rise here was measured off the covered bridge saved in game, whose deck
        // sits at -0.5. They chain because the pivots are central: the deck meets a one
        // metre post, the post meets the beam, and a two metre post carries on from the
        // beam to the ridge with nothing left over.
        private const float BeamRise = 1f;
        private const float RailPostRise = 0.5f;
        private const float RoofPostRise = 2f;
        private const float RidgeRise = 3f;

        // Measured across three blueprints built by hand, every one of them agreeing to
        // the millimetre: a stair covers one metre of drop over 1.974 of run. Assuming a
        // whole module of drop is what left the treads a metre apart with daylight
        // between them, which no amount of extra treads would have closed.
        private const float RampRise = 1f;
        private const float RampRun = 1.974f;

        // Sixteen treads walk down sixteen metres, past the height any wooden leg stands.
        private const int MaxRampSteps = 16;

        // Long enough for any crossing worth a tool, short enough that one press does not
        // put six hundred pieces into the world in a single frame.
        private const float MaxBridgeLength = 120f;

        /// <summary>Whether a bridge gets its railing and roof, or is left open.</summary>
        internal static bool IsBridgeCovered = true;

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
            ClearBridgeGhost();
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
                  + $"Проекция белая — устоит,{NEWLINE}красная — нет.{NEWLINE}"
                  + $"ЛКМ или «Построить». Esc — отменить."
                : $"Ширина в секциях (1-4),{NEWLINE}подъём настила над берегом.{NEWLINE}"
                  + $"Встань на этом берегу{NEWLINE}и нажми «Начать».";
        }

        private static void UpdateBridgeCoverLabel()
        {
            var label = BridgeCoverButton != null
                ? BridgeCoverButton.GetComponentInChildren<Text>()
                : null;
            if (label != null) label.text = IsBridgeCovered ? "Крыша: вкл" : "Крыша: выкл";
        }

        // ---------------- survey ----------------

        /// <summary>
        /// What a crossing from the marked start to here would come to: where every
        /// section lands, what is under it, and whether legs can be made to hold it.
        /// </summary>
        private sealed class BridgeSurvey
        {
            /// <summary>Ground under every probe, half a module apart. See Survey.</summary>
            public readonly List<float> Ground = new List<float>();

            public int Sections;
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
            survey.Sections = Mathf.FloorToInt(survey.Length / Module) + 1;

            // Probed every half module, so the seams between deck pieces are real points
            // in the profile and a leg can stand on one. A leg on a seam touches the two
            // pieces it sits between and feeds both from below, where the loss is 0.125 a
            // metre; under a piece's middle it feeds one, and the neighbour has to take
            // its share sideways at 0.2. Measured across a range of depths, that is about
            // a fifth more support in the weakest deck piece, and two more metres of
            // spacing before a leg runs out.
            var probes = survey.Sections * 2 - 1;

            for (var k = 0; k < probes; k++)
            {
                var at = from + dir * (k * ProbeStep);

                // No terrain collider means the zone is not loaded, and the obvious
                // fallback is the worst one available: putting the deck's own line into
                // the profile reads as ground exactly at deck height, which is full
                // support, which approves a span over nothing at all.
                if (!zones.GetGroundHeight(at, out var height))
                {
                    survey.Problem = $"Земля не прогружена на {k * ProbeStep:F0} м{NEWLINE}"
                                     + "пройди вдоль будущего моста";
                    return survey;
                }

                // Even probes are the middles of deck pieces. Rather than teach the
                // planner which of its samples may carry a leg, they are handed to it as
                // ground far below anything a leg reaches, so it passes over them on its
                // own. The two ends stay real: those are the banks.
                var middle = k % 2 == 0 && k > 0 && k < probes - 1;
                survey.Ground.Add(middle ? survey.Deck - 1000f : height);
            }

            survey.Plan = Geometry.PlanPiers(survey.Ground, ProbeStep, survey.Deck);
            return survey;
        }

        // ---------------- layout ----------------

        /// <summary>
        /// Every piece of the bridge, in the bridge's own frame: X across, Y measured
        /// from the start mark, Z along. Exactly the shape a blueprint is kept in, and
        /// for the same reason — a list of pieces relative to one origin can be shown as
        /// a ghost and then built without either half working out its own geometry.
        ///
        /// That mattered here more than it does for a blueprint. The projection and the
        /// build used to compute their own positions, and a bridge is not a fixed shape:
        /// it changes with every step the player takes. Two answers to a moving question
        /// drift apart, and the drift is invisible until something lands wrong.
        /// </summary>
        private static void Layout(BridgeSurvey survey, List<CopiedPiece> into)
        {
            into.Clear();
            if (survey.Problem != null || survey.Plan == null) return;

            var sections = survey.Sections;
            var last = sections - 1;
            var probes = survey.Ground.Count;
            var width = survey.Width;
            var rise = survey.Deck - _bridgeStart.y;
            var half = (width - 1) * 0.5f * Module;
            var edge = width * Module * 0.5f;

            var straight = Quaternion.identity;
            var alongBridge = Quaternion.Euler(0f, 90f, 0f);
            var acrossBridge = Quaternion.Euler(0f, -90f, 0f);

            for (var i = 0; i < sections; i++)
                for (var w = 0; w < width; w++)
                    Add(into, DeckPrefab, w * Module - half, rise, i * Module, straight);

            // The banks carry legs too. A deck lifted over its own bank stands on one
            // exactly as it stands on anything else, and leaving those out is how a
            // bridge ends up hanging from its middle.
            var legs = new List<int>(survey.Plan.Piers);
            if (!legs.Contains(0)) legs.Insert(0, 0);
            if (!legs.Contains(probes - 1)) legs.Add(probes - 1);

            foreach (var probe in legs)
            {
                var drop = survey.Deck - survey.Ground[probe];

                // Resting on the bank already: a pole here would push up through the deck.
                if (drop <= Module * 0.5f) continue;

                var poles = Mathf.CeilToInt(drop / Module);

                // Always a pair, under the two outer edges of the deck rather than down
                // its middle — which is how the same bridge gets built by hand, and it
                // stands a narrow deck on two feet instead of balancing it on one.
                foreach (var offset in new[] { -edge, edge })
                    for (var p = 0; p < poles; p++)
                        Add(into, LegPrefab, offset, rise - LegTopDrop - p * Module,
                            probe * ProbeStep, straight);
            }

            var nearSteps = RampDown(survey, into, 0f, -1f, rise);
            var farSteps = RampDown(survey, into, last * Module, 1f, rise);

            if (!IsBridgeCovered) return;

            // A continuous beam down both edges, one piece per deck section.
            for (var i = 0; i < sections; i++)
                foreach (var offset in new[] { -edge, edge })
                    Add(into, BeamPrefab, offset, rise + BeamRise, i * Module, alongBridge);

            // Posts only where a leg already stands, so what they carry has somewhere to
            // put it down — which now means on the seams, exactly where the hand-built
            // bridge puts its frames.
            foreach (var probe in legs)
                foreach (var offset in new[] { -edge, edge })
                {
                    Add(into, RailPostPrefab, offset, rise + RailPostRise,
                        probe * ProbeStep, straight);
                    Add(into, LegPrefab, offset, rise + RoofPostRise,
                        probe * ProbeStep, straight);
                }

            // A ridge over every two metre lane, not one down the middle. One line roofs
            // the deck it was measured on and leaves half of a wider one open to the sky,
            // which is exactly what a two-wide bridge came out looking like.
            //
            // The overhang matches the flight beneath it, tread for tread. A stair is
            // 1.974 long against the ridge's 2, so one ridge per tread covers it with a
            // little to spare; a fixed one-module overhang covered the top step and left
            // a long flight walking out from under its own roof.
            //
            // The roof stays level while the stairs go down, so the headroom grows as
            // they descend. Stepping it down with them would want its own measurements.
            for (var i = -nearSteps; i <= last + farSteps; i++)
                for (var w = 0; w < width; w++)
                    Add(into, RidgePrefab, w * Module - half, rise + RidgeRise,
                        i * Module, acrossBridge);

            // Gables close the two ends. They are mirrored, which is why the near and far
            // ones do not share a facing.
            foreach (var offset in new[] { -edge, edge })
            {
                Add(into, GablePrefab, offset, rise, -Module, acrossBridge);
                Add(into, GablePrefab, offset, rise, (last + 1) * Module, alongBridge);
            }
        }

        /// <summary>
        /// Walks a flight of stairs from one end of the deck down to the ground.
        ///
        /// A flight is always laid, even where the bank is level with the deck. Skipping
        /// it there looked reasonable and was not: the bank is level at the mark, and the
        /// stair lands a module further out where it need not be.
        ///
        /// The ground is asked about under each tread's foot, a module further out than
        /// the tread's own position — asking under its middle is what left treads hanging
        /// over a bank that keeps falling away, and no number of extra steps would have
        /// fixed that, only moved where they hung.
        ///
        /// One tread past the one that reaches, always. The last tread is what a person
        /// steps off onto, and it is better buried than a hand's breadth short.
        ///
        /// How far the flight goes is measured once, down the centreline, and every lane
        /// gets that many treads. Measuring each lane against its own ground would leave
        /// a wide flight ragged, one lane ending a step above its neighbour.
        /// </summary>
        /// <returns>How many treads went down, so the roof knows how far to reach.</returns>
        private static int RampDown(BridgeSurvey survey, List<CopiedPiece> into, float from,
                                    float sense, float rise)
        {
            var zones = ZoneSystem.instance;
            if (zones == null) return 0;

            var end = _bridgeStart + survey.Facing * new Vector3(0f, 0f, from);
            var outward = survey.Facing * new Vector3(0f, 0f, sense);
            var turn = sense > 0f ? Quaternion.identity : Quaternion.Euler(0f, 180f, 0f);
            var width = survey.Width;
            var half = (width - 1) * 0.5f * Module;
            var landed = false;
            var treads = 0;

            for (var step = 0; step < MaxRampSteps; step++)
            {
                // The first tread sits a whole module past the deck, the way the
                // hand-built one does; every tread after it follows the stair's own run.
                var reach = Module + step * RampRun;
                var drop = RampDrop + step * RampRise;

                // A stair in every lane, so the flight is as wide as the deck it leaves.
                // One down the middle of a four metre bridge is a plank, not a way down.
                var along = from + sense * reach;
                for (var w = 0; w < width; w++)
                    Add(into, RampPrefab, w * Module - half, rise - drop, along, turn);
                treads++;

                // This iteration laid the spare, so the flight is done.
                if (landed) break;

                // A tread meets the ground at its foot, half its run further out and half
                // its rise lower. Asking under its middle is what left treads hanging
                // over a bank that keeps falling away.
                var foot = end + outward * (reach + RampRun * 0.5f);
                if (!zones.GetGroundHeight(foot, out var ground)) break;
                if (survey.Deck - drop - RampRise * 0.5f > ground) continue;

                // A flight of one is a step onto a bank a metre down and it has already
                // arrived; a spare below that is a stair buried in the bank for nothing.
                // Longer flights get one, because the ground under their last tread is
                // further out and less certain.
                if (step == 0) break;
                landed = true;
            }

            return treads;
        }

        private static void Add(List<CopiedPiece> into, string prefab, float across, float up,
                                float along, Quaternion turn)
        {
            into.Add(new CopiedPiece
            {
                Prefab = prefab,
                LocalPos = new Vector3(across, up, along),
                LocalRot = turn
            });
        }

        // ---------------- projection ----------------

        private static readonly List<CopiedPiece> BridgePlanned = new List<CopiedPiece>();

        private static readonly List<CopiedPiece> BridgeShown = new List<CopiedPiece>();

        private static readonly List<GameObject> BridgeGhosts = new List<GameObject>();

        private static GameObject BridgeGhostRoot;

        private static float _bridgeSurveyAt;

        private static void ClearBridgeGhost()
        {
            foreach (var ghost in BridgeGhosts)
                if (ghost != null) Destroy(ghost);
            BridgeGhosts.Clear();
            BridgeShown.Clear();

            if (BridgeGhostRoot != null) Destroy(BridgeGhostRoot);
            BridgeGhostRoot = null;
        }

        /// <summary>
        /// Shows the bridge that would be built from here, as the pieces themselves.
        ///
        /// The ghost is rebuilt only when the layout changes, and moved every frame,
        /// which is what makes this affordable: walking sideways turns the whole thing on
        /// its root, and only walking further along adds a section. Rebuilding it four
        /// times a second would be hundreds of instantiations a second for nothing.
        /// </summary>
        internal static void UpdateBridgePreview()
        {
            var player = Player.m_localPlayer;
            if (!_bridgeStarted || player == null)
            {
                ClearBridgeGhost();
                return;
            }

            // Aiming is every frame and costs a subtraction: the ghost has to swing with
            // the player or it lags a quarter second behind its own preview.
            if (BridgeGhostRoot != null)
            {
                var flat = player.transform.position - _bridgeStart;
                flat.y = 0f;
                if (flat.sqrMagnitude > 0.01f)
                    BridgeGhostRoot.transform.SetPositionAndRotation(
                        _bridgeStart, Quaternion.LookRotation(flat.normalized, Vector3.up));
            }

            // Surveying costs a raycast per section, so it runs a few times a second
            // rather than every frame. Nothing here changes faster than a walking player.
            if (Time.time - _bridgeSurveyAt <= 0.25f) return;
            _bridgeSurveyAt = Time.time;

            var plan = Survey(_bridgeStart, player.transform.position);
            Layout(plan, BridgePlanned);

            if (!SameLayout(BridgePlanned, BridgeShown)) RaiseGhost(plan);
        }

        private static bool SameLayout(List<CopiedPiece> a, List<CopiedPiece> b)
        {
            if (a.Count != b.Count) return false;

            for (var i = 0; i < a.Count; i++)
            {
                if (a[i].Prefab != b[i].Prefab) return false;
                if ((a[i].LocalPos - b[i].LocalPos).sqrMagnitude > 0.0001f) return false;
            }

            return true;
        }

        private static void RaiseGhost(BridgeSurvey survey)
        {
            ClearBridgeGhost();
            if (ZNetScene.instance == null || BridgePlanned.Count == 0) return;

            BridgeGhostRoot = new GameObject("AstvardBridgeGhost");
            BridgeGhostRoot.transform.SetPositionAndRotation(_bridgeStart, survey.Facing);

            var stands = survey.Problem == null && survey.Plan != null && survey.Plan.Stands;

            // Red for a crossing that will not stand, so looking for somewhere to build
            // is something to read off the screen rather than press and find out.
            var tint = stands ? new Color(1f, 1f, 1f, 0.5f) : new Color(1f, 0.3f, 0.25f, 0.5f);

            // Without this the ghosts would register themselves as real networked objects
            // the moment they are instantiated.
            ZNetView.m_forceDisableInit = true;
            try
            {
                foreach (var piece in BridgePlanned)
                {
                    var prefab = ZNetScene.instance.GetPrefab(piece.Prefab);
                    if (prefab == null) continue;

                    var ghost = Instantiate(prefab, BridgeGhostRoot.transform);
                    ghost.transform.localPosition = piece.LocalPos;
                    ghost.transform.localRotation = piece.LocalRot;

                    foreach (var collider in ghost.GetComponentsInChildren<Collider>())
                        collider.enabled = false;
                    foreach (var behaviour in ghost.GetComponentsInChildren<MonoBehaviour>())
                        behaviour.enabled = false;
                    foreach (var renderer in ghost.GetComponentsInChildren<Renderer>())
                        foreach (var material in renderer.materials)
                            if (material.HasProperty("_Color")) material.color = tint;

                    BridgeGhosts.Add(ghost);
                }
            }
            finally
            {
                ZNetView.m_forceDisableInit = false;
            }

            BridgeShown.Clear();
            BridgeShown.AddRange(BridgePlanned);
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

            if (ZNetScene.instance == null) return;

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
                    $"Не устоит: с {survey.Plan.GapFrom * ProbeStep:F0} м по "
                    + $"{survey.Plan.GapTo * ProbeStep:F0} м{NEWLINE}не на что опереться");
                Log.LogInfo($"[AstvardServerMod] Bridge refused: gap " +
                            $"{survey.Plan.GapFrom * ProbeStep:F0}..{survey.Plan.GapTo * ProbeStep:F0} m " +
                            $"of {survey.Sections} sections.");
                return;
            }

            Layout(survey, BridgePlanned);

            _bridgeStarted = false;
            UpdateBridgeHint();

            var placed = Raise(BridgePlanned, survey.Facing, player.GetPlayerID());
            ClearBridgeGhost();

            player.Message(MessageHud.MessageType.Center,
                $"Мост {survey.Length:F0} м, опор {survey.Plan.Piers.Count}, деталей {placed}");
            Log.LogInfo($"[AstvardServerMod] Bridge {survey.Length:F1} m, " +
                        $"{survey.Sections} sections, width {survey.Width}, " +
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
        private static int Raise(List<CopiedPiece> pieces, Quaternion facing, long creator)
        {
            var scene = ZNetScene.instance;
            var placed = 0;

            // Read once rather than per piece: this is a property chain through the
            // distribution platform. It does NOT avoid the linear scan of the world's
            // player history that SetCreator itself runs for every piece - that cost
            // is inside the call and stays.
            var creatorPlatform = PlatformManager.DistributionPlatform.LocalUser.PlatformUserID;

            foreach (var piece in pieces)
            {
                var prefab = scene.GetPrefab(piece.Prefab);
                if (prefab == null)
                {
                    Log.LogWarning($"[AstvardServerMod] Unknown prefab '{piece.Prefab}', skipped.");
                    continue;
                }

                var go = Instantiate(prefab, _bridgeStart + facing * piece.LocalPos,
                                     facing * piece.LocalRot);
                var built = go.GetComponent<Piece>();
                if (built != null) built.SetCreator(creator, creatorPlatform);
                placed++;
            }

            return placed;
        }
    }
}
