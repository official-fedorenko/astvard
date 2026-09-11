using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        internal static GameObject TerrainButton;

        internal static GameObject LevelCircleButton;

        internal static GameObject LevelSquareButton;

        internal static GameObject TerrainHint;

        internal static GameObject LevelGroundButton;

        internal static GameObject RoadButton;

        internal static GameObject RoadHint;

        internal static GameObject RoadWidthInput;

        internal static GameObject RoadCurveInput;

        internal static GameObject RoadAreaInput;

        internal static GameObject RoadAreaButton;

        internal static GameObject RoadAreaMakeButton;

        internal static GameObject RoadKindButton;

        internal static GameObject RoadWidthButton;

        internal static GameObject RoadBendButton;

        internal static GameObject RoadStoneButton;

        internal static GameObject RoadDirtButton;

        internal static GameObject RoadLeftButton;

        internal static GameObject RoadRightButton;

        internal static GameObject RoadStartButton;

        internal static GameObject RoadEndButton;

        internal static GameObject RoadCancelButton;

        internal static GameObject UndoButton;

        private static bool _terrainSquare;

        internal static bool IsLevelGroundEnabled;

        private static void UpdateLevelGroundButtonLabel()
        {
            var label = LevelGroundButton != null
                ? LevelGroundButton.GetComponentInChildren<Text>(true)
                : null;
            if (label != null)
                label.text = IsLevelGroundEnabled
                    ? "Выравнивать землю: вкл"
                    : "Выравнивать землю: выкл";
        }

        internal static GameObject RoadClearButton;

        internal static GameObject RoadSmoothButton;

        /// <summary>
        /// Whether a road also evens out the ground it runs over, the way a hoe would:
        /// flat across its width, and along its length following the land with the lumps
        /// taken off. Off by default. Unlike clearing, it is everybody's, like the road
        /// and the levelling tool beside it - and unlike clearing, undo takes it back.
        /// </summary>
        internal static bool IsRoadSmoothing;

        private static void UpdateRoadSmoothButtonLabel()
        {
            var label = RoadSmoothButton != null
                ? RoadSmoothButton.GetComponentInChildren<Text>(true)
                : null;
            if (label != null)
                label.text = IsRoadSmoothing ? "Сглаживать: вкл" : "Сглаживать: выкл";
        }

        /// <summary>
        /// Whether a road, or a pad laid around the player, also takes out the trees and
        /// rocks standing in it. Off by default: unlike the paint, what it removes cannot
        /// be undone.
        /// </summary>
        internal static bool IsRoadClearing;

        /// <summary>
        /// Clearing is an admin's unless the admins open it to players, though the road
        /// itself is everybody's. Paint only changes how the ground looks; clearing removes
        /// what other people may have meant to keep, and there is no undo for it.
        ///
        /// The toggle is drawn only where it is allowed, and that is asked again here at
        /// the point of use: the setting outlives the grant - ForgetAdmin clears the admin
        /// bit on disconnect, not every tool's settings - so a player who switched it on
        /// and then lost admin, or had it closed to them, would otherwise keep clearing.
        /// </summary>
        private static bool RoadClearingActive
        {
            get { return IsRoadClearing && RuleAllows("clear"); }
        }

        /// <summary>Smoothing as it will actually happen: switched on, and open to whoever is laying.</summary>
        private static bool RoadSmoothingActive
        {
            get { return IsRoadSmoothing && RuleAllows("smooth"); }
        }

        /// <summary>Torches as they will actually be set: chosen, and open to whoever is laying.</summary>
        private static bool RoadTorchesActive
        {
            get { return IsRoadTorches && RuleAllows("torches"); }
        }

        private static void UpdateRoadClearButtonLabel()
        {
            var label = RoadClearButton != null
                ? RoadClearButton.GetComponentInChildren<Text>(true)
                : null;
            if (label != null)
                label.text = IsRoadClearing ? "Сносить: вкл" : "Сносить: выкл";
        }

        // Long enough for a real stretch of road, short enough that one press does not
        // rewrite the terrain of a dozen zones at once.
        private const float MaxRoadLength = 200f;

        // An admin's reach. The ground a client can change is the 5x5 zones round the
        // player - 130 to 190 m each way - so past 200 m the far end has gone by the time
        // the player gets there. With the end pinned and the player back near the middle,
        // both ends are in reach up to about this; further, and CompsForStamps turns it down.
        private const float AdminMaxRoadLength = 300f;

        private static float RoadMaxLength
        {
            get { return IsAdminUnlocked ? AdminMaxRoadLength : RuleLimit("road", MaxRoadLength); }
        }

        // The heightmap only covers one 64 m zone, so a pad levelled wider than that is
        // clipped at its edge anyway - generous rather than exact.
        private const float MaxLevelRadius = 64f;

        private const float MaxAreaRadius = 32f;

        private static bool _roadPaved = true;

        // Left is what a positive curve used to mean, so this keeps every road already
        // laid by a typed number bending the way it did.
        private static bool _roadBendLeft = true;

        private static bool _roadStarted;
        private static bool _roadLaying;
        private static bool _roadCancelled;

        internal static bool RoadInProgress
        {
            get { return _roadStarted || _roadLaying; }
        }

        /// <summary>A start is marked and the far end is still to be chosen.</summary>
        internal static bool RoadAwaitingEnd
        {
            get { return _roadStarted; }
        }

        /// <summary>
        /// Drops a marked start, and stops a road already going down. Laying happens
        /// over several frames, so a road caught halfway keeps the part already painted
        /// — undoing terrain is not something this can offer, and pretending otherwise
        /// would be worse than stopping where it stands.
        /// </summary>
        internal static void CancelRoad()
        {
            var wasLaying = _roadLaying;

            _roadCancelled = _roadLaying;
            _roadStarted = false;
            _roadPinned = false;
            if (_roadPreview != null) _roadPreview.SetActive(false);
            UpdateRoadHint();

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                wasLaying ? "Укладка остановлена" : "Отменено");
        }

        private static Vector3 _roadStart;

        // Pinned, the far end of a marked road stays where it was left instead of being
        // wherever the player stands; the arrows move it.
        private static bool _roadPinned;

        private static Vector3 _roadPinnedEnd;

        /// <summary>Where a marked road would end: where it was pinned, or the player.</summary>
        private static Vector3 RoadEnd(Player player)
        {
            return _roadPinned ? _roadPinnedEnd : player.transform.position;
        }

        private static void ToggleRoadPin(Player player)
        {
            _roadPinned = !_roadPinned;
            if (_roadPinned) _roadPinnedEnd = player.transform.position;
            UpdateRoadHint();
            SayPinned(_roadPinned);
        }

        /// <summary>The road page, or one of the pages behind its buttons.</summary>
        private static bool IsRoadPage(int state)
        {
            return state == StateRoad || state == StateRoadKind || state == StateRoadWidth
                   || state == StateRoadBend || state == StateRoadTorches || state == StateRoadArea;
        }

        // Where a choice of paving or of torches goes back to: the road it was opened from,
        // or the paving round the player, which shares both with the road.
        private static int _pavingBack = StateRoad;

        private static void OpenPavingSubPage(int state)
        {
            _pavingBack = MenuState == StateRoadArea ? StateRoadArea : StateRoad;
            OpenRoadPage(state);
        }

        private static void OpenRoadPage(int state)
        {
            MenuState = state;
            RefreshMenu();
        }

        /// <summary>
        /// The buttons on the road page say what they are set to, so the page reads as
        /// the road about to be laid.
        /// </summary>
        private static void UpdateRoadLabels()
        {
            SetLabel(RoadKindButton, _roadPaved ? "Кладка: каменная" : "Кладка: земляная");
            SetLabel(RoadWidthButton, $"Ширина: {RoadWidth():0.#} м");

            var curve = RoadCurve();
            SetLabel(RoadBendButton, curve <= 0f
                ? "Изгиб: нет"
                : $"Изгиб: {(_roadBendLeft ? "влево" : "вправо")}, {curve:0.#}");

            UpdateRoadTorchButtonLabel();
        }

        /// <summary>
        /// One hint for all the road's pages, saying what the page in front of the
        /// player is for.
        /// </summary>
        private static void UpdateRoadHint()
        {
            var label = RoadHint != null ? RoadHint.GetComponentInChildren<Text>(true) : null;
            if (label == null) return;

            var smooth = RoadSmoothingActive ? $"{NEWLINE}Землю сгладит." : "";
            var clear = RoadClearingActive
                ? $"{NEWLINE}Деревья и камни на пути снесёт —{NEWLINE}откат их не вернёт."
                : "";
            var torches = !RoadTorchesActive
                ? ""
                : TorchesPaidHere
                    ? $"{NEWLINE}По краям встанут факелы —{NEWLINE}из твоих материалов."
                    : $"{NEWLINE}По краям встанут факелы.";
            var notes = smooth + clear + torches;

            switch (MenuState)
            {
                case StateRoadKind:
                    label.text = "Чем мостить дорожку и землю вокруг.";
                    break;
                case StateRoadWidth:
                    label.text = "Ширина дорожки, от 1 до 8 м.";
                    break;
                case StateRoadBend:
                    label.text = $"Насколько изогнуть: 0 — прямо,{NEWLINE}1 — чуть-чуть, 10 — полукругом.{NEWLINE}"
                                 + "Потом выбери сторону.";
                    break;
                case StateRoadTorches:
                    label.text = $"Шаг между факелами, от 4 до 50 м.{NEWLINE}Потом выбери, какие ставить.";
                    break;
                case StateRoadArea:
                    label.text = $"Мощение вокруг тебя: круг{NEWLINE}радиусом от 2 до {RuleLimit("area", MaxAreaRadius):0} м. «Поставить»{NEWLINE}"
                                 + $"покажет его: ЛКМ — замостить,{NEWLINE}Esc — отменить, P — закрепить,{NEWLINE}"
                                 + $"стрелки — сдвиг. Кладка и прочее{NEWLINE}— общие с дорожкой.{notes}";
                    break;
                default:
                    // Past 200 m only an admin, and only from near the middle: see AdminMaxRoadLength.
                    var longRoad = IsAdminUnlocked
                        ? $"{NEWLINE}До {AdminMaxRoadLength:0} м. Длиннее {MaxRoadLength:0} —{NEWLINE}"
                          + $"закрепи конец (P) и встань{NEWLINE}ближе к середине."
                        : "";
                    label.text = _roadStarted
                        ? $"Начало отмечено — иди в конец{NEWLINE}и нажми ЛКМ или «Закончить».{NEWLINE}"
                          + $"Esc — отменить. P — закрепить{NEWLINE}конец, стрелки — сдвинуть.{notes}{longRoad}"
                        : $"Встань в начало дорожки{NEWLINE}и нажми «Начать».{notes}"
                          + RuleLimitNote("road", MaxRoadLength) + longRoad;
                    break;
            }
        }

        private static readonly List<Vector3> RoadPath = new List<Vector3>();

        private static readonly List<Vector3> PreviewStamps = new List<Vector3>();

        private static GameObject _roadPreview;

        private static LineRenderer _roadLine;

        private static bool _roadPreviewFailed;

        private static readonly System.Reflection.FieldInfo FModifiedPaint =
            AccessTools.Field(typeof(TerrainComp), "m_modifiedPaint");

        private static readonly System.Reflection.FieldInfo FPaintMask =
            AccessTools.Field(typeof(TerrainComp), "m_paintMask");

        private static float RoadWidth()
        {
            return Mathf.Clamp(ParseField(RoadWidthInput, 3f), 1f, 8f);
        }

        /// <summary>
        /// How much the road bends, 0 to 10. The field is a magnitude and the buttons
        /// carry the side. A typed minus used to be the only way to say "the other way",
        /// and the field's own label had that backwards — positive bows left, which
        /// RoadTests pins.
        /// </summary>
        private static float RoadCurve()
        {
            return Mathf.Clamp(Mathf.Abs(ParseField(RoadCurveInput, 0f)), 0f, 10f);
        }

        /// <summary>
        /// How far the road bows out at its middle, in metres. Scaling it by the length
        /// means the typed number describes the shape rather than an absolute distance:
        /// 1 is a gentle bend and 10 puts the bulge at half the chord, a semicircle.
        /// </summary>
        private static float RoadSagitta(float length)
        {
            var curve = RoadCurve();
            return Geometry.Sagitta(_roadBendLeft ? curve : -curve, length);
        }

        /// <summary>
        /// Samples the centreline. A quadratic Bezier only reaches half of its control
        /// offset, so the control point is pushed out twice the bulge we want.
        /// </summary>
        /// <summary>
        /// Samples the centreline. The curve itself lives in <see cref="Geometry"/>,
        /// which has no Unity in it and can therefore be tested without the game;
        /// this only carries the height across, which the paint never looks at.
        /// </summary>
        private static void RoadPoints(Vector3 from, Vector3 to, float sagitta,
                                       float step, List<Vector3> into)
        {
            into.Clear();

            var flat = Geometry.Bezier(new Vec2(from.x, from.z), new Vec2(to.x, to.z),
                                       sagitta, step);
            if (flat.Count == 0) return;

            for (var i = 0; i < flat.Count; i++)
            {
                var t = flat.Count == 1 ? 0f : (float)i / (flat.Count - 1);
                into.Add(new Vector3(flat[i].X, Mathf.Lerp(from.y, to.y, t), flat[i].Z));
            }
        }

        // ---------------- clearing ----------------

        private static readonly string[] StoneOnly = { "Stone" };

        private static readonly string[] WoodAndStone = { "Stone", "Wood" };

        // How far outside the run's own rectangle a thing may stand and still be worth
        // the exact test. Only the position is known before the colliders are read, and
        // a big boulder's centre can sit several metres from the edge it pushes into
        // the road.
        private const float ClearSlack = 8f;

        /// <summary>
        /// Takes out what stands in the paint: trees, stumps, fallen logs, bare rocks and
        /// bushes that give nothing but wood.
        ///
        /// What counts as in the way is decided by what a thing is, never by what it is
        /// called. A tree is anything with a TreeBase, a log a TreeLog, a stump a
        /// Destructible the game itself types as a tree. A rock counts only when all it
        /// would ever drop is stone, down to what it turns into when broken - one rule
        /// that keeps copper, tin, silver and obsidian deposits, muddy scrap piles and the
        /// Mistlands' giant bones where they are, with no list of names to go stale. Left
        /// alone as well: anything somebody built, anything inside a location's radius -
        /// villages, ruins, dolmens, cave mouths - and everything ForceDelete already
        /// protects. Pickables stay; a berry bush or a stone lying on the ground is not an
        /// obstacle.
        ///
        /// The test is flat, like the paint. The path carries no terrain height, only a
        /// straight line between the heights of its two ends, so a vertical window would
        /// miss every tree on the crest of a hill the road goes over.
        ///
        /// Removal claims ownership first and goes through ZNetScene.Destroy. The claim is
        /// what makes it stick: Destroy erases the world record only for an object this
        /// client owns, and for anything else just deletes the local copy - gone here,
        /// still there for everyone else, and back for this player on the next load.
        /// Destroy is also silent, which is the point: Destructible.Destroy would drop the
        /// wood and stone and play its effects, and a hundred metres of forest road would
        /// come out paved with loot. A whole boulder it would not even remove - it would
        /// put the broken one in its place.
        ///
        /// What went and what was seen in the way and left are both logged by name. The
        /// question this gets is always why that one is still standing, and the log
        /// answers it without another trip into the game.
        /// </summary>
        private static int ClearAlongPath(List<Vector3> path, float radius)
        {
            if (path == null || path.Count == 0 || ZNetScene.instance == null) return 0;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var p in path)
            {
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.z < minZ) minZ = p.z;
                if (p.z > maxZ) maxZ = p.z;
            }

            var reach = radius + ClearSlack;
            var area = Rect.MinMaxRect(minX - reach, minZ - reach, maxX + reach, maxZ + reach);
            var run = new ClearingRun(path, radius, area);

            foreach (var tree in Object.FindObjectsByType<TreeBase>(FindObjectsSortMode.None))
                run.Consider(tree, null);

            foreach (var log in Object.FindObjectsByType<TreeLog>(FindObjectsSortMode.None))
                run.Consider(log, null);

            foreach (var rock in Object.FindObjectsByType<MineRock>(FindObjectsSortMode.None))
                if (rock != null)
                    run.Consider(rock, DropsOnly(rock.m_dropItems, StoneOnly) ? null : "drops more than stone");

            foreach (var rock in Object.FindObjectsByType<MineRock5>(FindObjectsSortMode.None))
                if (rock != null)
                    run.Consider(rock, DropsOnly(rock.m_dropItems, StoneOnly) ? null : "drops more than stone");

            foreach (var thing in Object.FindObjectsByType<Destructible>(FindObjectsSortMode.None))
            {
                if (thing == null) continue;

                var bare = thing.m_destructibleType == DestructibleType.Tree
                           || BreaksDownTo(thing.gameObject, WoodAndStone, 0);
                run.Consider(thing, bare ? null : "not bare rock or brush");
            }

            var removed = new Dictionary<string, int>();
            foreach (var view in run.Doomed)
            {
                // Something removed earlier in this loop can take a neighbour with it.
                if (view == null || !view.IsValid()) continue;

                // A player's clearing leaves what stands in someone else's ward, as the
                // hammer would. The road's own ward check covers only its band, and a
                // boulder can stand across the edge of it with its middle in the ward.
                if (!IsAdminUnlocked && !PrivateArea.CheckAccess(view.transform.position, 0f, false, false))
                {
                    Tally(run.Kept, Utils.GetPrefabName(view.gameObject) + " (ward)");
                    continue;
                }

                Tally(removed, Utils.GetPrefabName(view.gameObject));
                view.ClaimOwnership();
                ZNetScene.instance.Destroy(view.gameObject);
            }

            if (removed.Count > 0 || run.Kept.Count > 0)
                Log.LogInfo($"[AstvardServerMod] Clearing removed: {Listing(removed)}; " +
                            $"left in the way: {Listing(run.Kept)}");

            var total = 0;
            foreach (var count in removed.Values) total += count;
            return total;
        }

        /// <summary>One clearing pass: what it will take out, and what it saw in the way and left.</summary>
        private sealed class ClearingRun
        {
            public readonly List<ZNetView> Doomed = new List<ZNetView>();

            public readonly Dictionary<string, int> Kept = new Dictionary<string, int>();

            // A thing can carry more than one of the components the pass looks for; it
            // is judged once, on the first look that finds it in the way.
            private readonly HashSet<ZNetView> _judged = new HashSet<ZNetView>();

            private readonly List<Vector3> _path;

            private readonly float _radius;

            private readonly Rect _area;

            public ClearingRun(List<Vector3> path, float radius, Rect area)
            {
                _path = path;
                _radius = radius;
                _area = area;
            }

            /// <param name="refusal">Why this kind of thing stays, whatever else is true of it; null if it need not.</param>
            public void Consider(Component thing, string refusal)
            {
                if (thing == null) return;

                // The networked root is the thing that exists in the world. A part with
                // no view is scenery the zone rebuilds on every load, and removing it
                // would last until the next one.
                var view = thing.GetComponentInParent<ZNetView>();
                if (view == null || !view.IsValid() || _judged.Contains(view)) return;

                // Only when the tree or rock IS the networked object. The same component
                // on a child would lead up to whatever that child belongs to, and the
                // whole of it would go. Vanilla trees, logs, stumps and rocks all carry
                // theirs on the root, so this costs nothing and rules out a class of
                // accident.
                var go = view.gameObject;
                if (go != thing.gameObject) return;

                var at = go.transform.position;
                if (!_area.Contains(new Vector2(at.x, at.z))) return;

                if (refusal == null)
                {
                    if (go.GetComponent<Pickable>() != null) refusal = "pickable";
                    // A sapling somebody planted is a Piece until it grows up.
                    else if (go.GetComponentInParent<Piece>() != null) refusal = "built";
                    else if (IsProtectedFromDelete(go)) refusal = "protected";
                    else if (Location.IsInsideLocation(at, 0f)) refusal = "inside a location";
                }

                if (FootprintDistance(go, _path) > _radius) return;

                _judged.Add(view);
                if (refusal == null) Doomed.Add(view);
                else Tally(Kept, $"{Utils.GetPrefabName(go)} ({refusal})");
            }
        }

        /// <summary>
        /// Whether a Destructible is bare rock or plain brush all the way down: all it
        /// drops is on the list, and what it turns into when broken is bare rock too.
        ///
        /// The second half is what the boulders need. A whole one drops nothing: the
        /// first hit replaces it with a broken copy - m_spawnWhenDestroyed, one of the
        /// game's "_frac" prefabs - and it is the copy, a MineRock5, that holds the stone.
        /// Judged by that first link alone, a boulder was a thing of unknown use, and it
        /// stayed standing in the middle of a cleared road. Copper and silver deposits are
        /// made the same way, and following the chain is also what keeps them: their copy
        /// drops ore.
        /// </summary>
        private static bool BreaksDownTo(GameObject go, string[] allowed, int depth)
        {
            // The game's chains are one link long; the bound only guards against a
            // prefab that names itself.
            if (go == null || depth > 3) return false;

            // A rock at the end of the chain is held to the same rule as one met
            // already broken: stone and nothing else.
            var mine = go.GetComponent<MineRock>();
            if (mine != null) return DropsOnly(mine.m_dropItems, StoneOnly);

            var mine5 = go.GetComponent<MineRock5>();
            if (mine5 != null) return DropsOnly(mine5.m_dropItems, StoneOnly);

            var destructible = go.GetComponent<Destructible>();
            if (destructible == null) return false;

            var drops = go.GetComponent<DropOnDestroyed>();
            var table = drops != null ? drops.m_dropWhenDestroyed : null;
            var dropsSomething = table != null && table.m_drops != null && table.m_drops.Count > 0;
            if (dropsSomething && !DropsOnly(table, allowed)) return false;

            var next = destructible.m_spawnWhenDestroyed;
            if (next == null) return dropsSomething;

            // Debris flying off is the break itself, not something left behind.
            if (next.GetComponent<Gibber>() != null && next.GetComponent<Destructible>() == null
                && next.GetComponent<MineRock5>() == null && next.GetComponent<MineRock>() == null)
                return dropsSomething;

            return BreaksDownTo(next, allowed, depth + 1);
        }

        /// <summary>
        /// How close a thing comes to the path, measured from its footprint rather than
        /// its centre: a boulder that pushes three metres into the road is in the way
        /// even with its middle beside it. The footprint is the flat box of each solid
        /// collider, the nearest one counting; with none, just where the thing stands.
        ///
        /// Each collider on its own, not one box round all of them. That box also covers
        /// the ground between the pieces of anything built of several, and a road passing
        /// one corner of it would take the whole thing.
        /// </summary>
        private static float FootprintDistance(GameObject go, List<Vector3> path)
        {
            var best = float.MaxValue;
            var solid = false;
            foreach (var col in go.GetComponentsInChildren<Collider>())
            {
                if (col == null || !col.enabled || col.isTrigger) continue;
                solid = true;
                best = Mathf.Min(best, BoxDistanceSq(col.bounds, path));
            }

            if (!solid) best = BoxDistanceSq(new Bounds(go.transform.position, Vector3.zero), path);
            return Mathf.Sqrt(best);
        }

        /// <summary>Squared flat distance from the nearest path point to a box.</summary>
        private static float BoxDistanceSq(Bounds box, List<Vector3> path)
        {
            var best = float.MaxValue;
            foreach (var p in path)
            {
                var dx = Mathf.Max(Mathf.Max(box.min.x - p.x, p.x - box.max.x), 0f);
                var dz = Mathf.Max(Mathf.Max(box.min.z - p.z, p.z - box.max.z), 0f);
                var d = dx * dx + dz * dz;
                if (d < best) best = d;
            }

            return best;
        }

        /// <summary>
        /// Whether everything a table can drop is on the list. An empty or missing table
        /// answers no: not knowing what a thing is, is a reason to leave it standing.
        /// </summary>
        private static bool DropsOnly(DropTable table, string[] allowed)
        {
            if (table == null || table.m_drops == null || table.m_drops.Count == 0) return false;

            foreach (var drop in table.m_drops)
                if (drop.m_item == null || System.Array.IndexOf(allowed, drop.m_item.name) < 0)
                    return false;

            return true;
        }

        private static void Tally(Dictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out var count);
            counts[key] = count + 1;
        }

        private static string Listing(Dictionary<string, int> counts)
        {
            if (counts.Count == 0) return "nothing";

            return string.Join(", ", counts
                .OrderByDescending(entry => entry.Value)
                .Select(entry => entry.Value > 1 ? $"{entry.Key} x{entry.Value}" : entry.Key));
        }

        // ---------------- smoothing ----------------

        /// <summary>
        /// How far to either side of the road the reshaped ground runs out. Wide enough
        /// that a road cut into a slope does not leave a wall at its edge, narrow enough
        /// that it does not go on to reshape the hillside beside it.
        /// </summary>
        private static float SmoothBlend(float radius)
        {
            return Mathf.Clamp(radius * 1.5f, 2f, 6f);
        }

        /// <summary>
        /// The line a ward refusal gains while smoothing is on. The blend band runs out
        /// past the paint the preview draws, so without it a road can be turned down over
        /// ground it does not look as if it touches.
        /// </summary>
        private static string SmoothingNote(float radius)
        {
            return RoadSmoothingActive
                ? $"{NEWLINE}Сглаживание захватывает ещё {SmoothBlend(radius):F0} м по краям"
                : "";
        }

        /// <summary>
        /// How far along the road the averaging reaches, in path points a metre apart.
        /// Two passes make the kernel twice this wide, so a lump shorter than about twice
        /// this is taken off while a hill longer than that is followed. A wider road gets
        /// a longer reach, the way a wider road is laid at a gentler grade.
        /// </summary>
        private static int SmoothHalfWindow(float radius)
        {
            return Mathf.Clamp(Mathf.RoundToInt(radius * 2f + 2f), 4, 10);
        }

        /// <summary>
        /// Evens out the ground along the path, the way a hoe would.
        ///
        /// The ground under each point of the path is read as it is now - the path's own
        /// heights are no use for this, being a straight line between its two ends - and
        /// that profile is smoothed along its length by Geometry.SmoothProfile, which
        /// keeps the ends where they are and leaves an even slope alone. Every vertex
        /// within the road's half-width is then set to the smoothed height of the nearest
        /// place on the path, so the road is flat across; past that it eases back to the
        /// ground over the blend band, as BlendLevel does for a pad.
        ///
        /// A single point, the pad «Вокруг меня» lays, smooths to itself: the pad comes
        /// out level at the height of the ground under the player.
        /// </summary>
        private static void SmoothAlongPath(List<Vector3> path, List<TerrainComp> comps, float radius)
        {
            if (path == null || path.Count == 0 || comps == null || comps.Count == 0) return;

            var heights = new List<float>(path.Count);
            var last = path[0].y;
            foreach (var p in path)
            {
                // A point over ground that is not loaded keeps the last height read, which
                // is the least surprising thing a gap in the profile can be filled with.
                if (Heightmap.GetHeight(p, out var h)) last = h;
                heights.Add(last);
            }

            var profile = Geometry.SmoothProfile(heights, SmoothHalfWindow(radius));

            var flat = new List<Vec2>(path.Count);
            foreach (var p in path) flat.Add(new Vec2(p.x, p.z));

            var blend = SmoothBlend(radius);
            foreach (var comp in comps) LevelAlong(comp, flat, profile, radius, blend);
        }

        /// <summary>
        /// Writes one zone's share of the smoothing into its TerrainComp, with the same
        /// bookkeeping BlendLevel and the game's own LevelTerrain use.
        /// </summary>
        private static void LevelAlong(TerrainComp comp, List<Vec2> flat, float[] profile,
                                       float radius, float blend)
        {
            var hmap = FHmap.GetValue(comp) as Heightmap;
            var levelDelta = FLevelDelta.GetValue(comp) as float[];
            var smoothDelta = FSmoothDelta.GetValue(comp) as float[];
            var modified = FModified.GetValue(comp) as bool[];
            if (hmap == null || levelDelta == null || smoothDelta == null || modified == null) return;

            var width = (int)FWidth.GetValue(comp);
            var size = width + 1;
            var scale = hmap.m_scale;
            if (scale <= 0f || levelDelta.Length < size * size) return;

            // The height grid as Heightmap.WorldToVertex lays it out: a half-width offset
            // of width / 2, the same number the paint grid's (width + 1) / 2 comes to for
            // the game's even widths. Heights are relative to the heightmap's own
            // transform, which is also the frame GetHeight answers in below.
            var half = width / 2;
            var origin = hmap.transform.position;
            var reach = radius + blend;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var p in flat)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Z < minZ) minZ = p.Z;
                if (p.Z > maxZ) maxZ = p.Z;
            }

            var j0 = Mathf.Max(0, Geometry.VertexAt(minX - reach, origin.x, scale, half));
            var j1 = Mathf.Min(size - 1, Geometry.VertexAt(maxX + reach, origin.x, scale, half));
            var i0 = Mathf.Max(0, Geometry.VertexAt(minZ - reach, origin.z, scale, half));
            var i1 = Mathf.Min(size - 1, Geometry.VertexAt(maxZ + reach, origin.z, scale, half));
            if (j0 > j1 || i0 > i1) return;

            // Where in this zone anything moved, for the grass below.
            float tMinX = float.MaxValue, tMaxX = float.MinValue;
            float tMinZ = float.MaxValue, tMaxZ = float.MinValue;

            for (var i = i0; i <= i1; i++)
            {
                var wz = Geometry.WorldAt(i, origin.z, scale, half);
                for (var j = j0; j <= j1; j++)
                {
                    var wx = Geometry.WorldAt(j, origin.x, scale, half);

                    var along = Geometry.NearestOnPath(flat, wx, wz, out var distance);
                    if (distance > reach) continue;

                    var target = Geometry.ProfileAt(profile, along) - origin.y;
                    var current = hmap.GetHeight(j, i);

                    var desired = distance <= radius
                        ? target
                        : Mathf.Lerp(target, current,
                            Mathf.SmoothStep(0f, 1f, (distance - radius) / blend));

                    // Fold in and clear any pending smooth delta, then stay inside the
                    // engine's ±8 m budget - the game's own LevelTerrain does the same.
                    var index = i * size + j;
                    var delta = desired - current + smoothDelta[index];
                    smoothDelta[index] = 0f;
                    levelDelta[index] = Mathf.Clamp(levelDelta[index] + delta, -8f, 8f);
                    modified[index] = true;

                    if (wx < tMinX) tMinX = wx;
                    if (wx > tMaxX) tMaxX = wx;
                    if (wz < tMinZ) tMinZ = wz;
                    if (wz > tMaxZ) tMaxZ = wz;
                }
            }

            if (tMinX > tMaxX) return;

            // Another player's client redraws grass only inside the last operation's
            // circle when the count goes up by exactly one. BlendLevel's circle fits a
            // pad; a road through this zone needs one round everything that moved, or
            // grass would be left standing in the air over the lowered stretches.
            var centre = new Vector3((tMinX + tMaxX) * 0.5f, origin.y, (tMinZ + tMaxZ) * 0.5f);
            var spanX = tMaxX - tMinX;
            var spanZ = tMaxZ - tMinZ;

            FOperations.SetValue(comp, (int)FOperations.GetValue(comp) + 1);
            FLastOpPoint.SetValue(comp, centre);
            FLastOpRadius.SetValue(comp, Mathf.Sqrt(spanX * spanX + spanZ * spanZ) * 0.5f + scale);
        }

        // ---------------- painting ----------------

        /// <summary>
        /// One zone's paint mask, with the scratch a single operation needs. The base
        /// colour is remembered per vertex so that a later, better-covering pass blends
        /// from the ground's original colour instead of compounding its own earlier work.
        /// </summary>
        private sealed class PaintTarget
        {
            public TerrainComp Comp;
            public Heightmap Hmap;
            public bool[] Modified;
            public Color[] Mask;
            public float[] Best;
            public Color[] Base;
            public bool[] Touched;
            public int Size;
            public int Half;
            public float Scale;
            public Vector3 Origin;
        }

        private static PaintTarget MakeTarget(TerrainComp comp)
        {
            var hmap = FHmap != null ? FHmap.GetValue(comp) as Heightmap : null;
            var modified = FModifiedPaint != null ? FModifiedPaint.GetValue(comp) as bool[] : null;
            var mask = FPaintMask != null ? FPaintMask.GetValue(comp) as Color[] : null;
            if (hmap == null || modified == null || mask == null || hmap.m_scale <= 0f) return null;

            var size = hmap.m_width + 1;
            if (modified.Length < size * size || mask.Length < size * size) return null;

            return new PaintTarget
            {
                Comp = comp,
                Hmap = hmap,
                Modified = modified,
                Mask = mask,
                Best = new float[size * size],
                Base = new Color[size * size],
                Touched = new bool[size * size],
                Size = size,
                Half = size / 2,
                Scale = hmap.m_scale,
                Origin = hmap.transform.position
            };
        }

        // Mirrors Heightmap.WorldToVertexMask, and its inverse. Keeping both here means
        // the two can be read against each other instead of trusted separately.
        // Mirrors Heightmap.WorldToVertexMask and its inverse; both are pinned by tests.
        private static int VertexAt(PaintTarget t, float world, float origin)
        {
            return Geometry.VertexAt(world, origin, t.Scale, t.Half);
        }

        private static float WorldAt(PaintTarget t, int vertex, float origin)
        {
            return Geometry.WorldAt(vertex, origin, t.Scale, t.Half);
        }

        private static readonly List<Vec2> FlatPath = new List<Vec2>();

        private static float DistanceToPath(List<Vector3> path, int first, int last, float x, float z)
        {
            // The measurement is the whole reason a painted road is continuous, so it
            // lives with the rest of the tested arithmetic rather than here.
            FlatPath.Clear();
            for (var i = 0; i < path.Count; i++) FlatPath.Add(new Vec2(path[i].x, path[i].z));
            return Geometry.DistanceToPath(FlatPath, first, last, x, z);
        }

        /// <summary>
        /// Paints every vertex within <paramref name="radius"/> of a stretch of the path,
        /// once, using its true distance. The game's own PaintCleared cannot be used for
        /// this: it stamps circles that overwrite each other, and since it reads its base
        /// colour from the rendered heightmap — which only refreshes between batches —
        /// the last stamp to graze a vertex wins with its weakest edge value. That is
        /// what turned a road into a row of blotches.
        /// </summary>
        private static void PaintStretch(PaintTarget t, List<Vector3> path, int first, int last,
                                         float radius, Color paint)
        {
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (var k = first; k <= last; k++)
            {
                var point = path[k];
                if (point.x < minX) minX = point.x;
                if (point.x > maxX) maxX = point.x;
                if (point.z < minZ) minZ = point.z;
                if (point.z > maxZ) maxZ = point.z;
            }

            var j0 = Mathf.Max(0, VertexAt(t, minX - radius, t.Origin.x));
            var j1 = Mathf.Min(t.Size - 1, VertexAt(t, maxX + radius, t.Origin.x));
            var i0 = Mathf.Max(0, VertexAt(t, minZ - radius, t.Origin.z));
            var i1 = Mathf.Min(t.Size - 1, VertexAt(t, maxZ + radius, t.Origin.z));

            for (var i = i0; i <= i1; i++)
            {
                var wz = WorldAt(t, i, t.Origin.z);
                for (var j = j0; j <= j1; j++)
                {
                    var wx = WorldAt(t, j, t.Origin.x);

                    var distance = DistanceToPath(path, first, last, wx, wz);
                    if (distance > radius) continue;

                    var f = Geometry.Falloff(distance, radius);

                    var index = i * t.Size + j;
                    if (f <= t.Best[index]) continue;
                    t.Best[index] = f;

                    if (!t.Touched[index])
                    {
                        t.Touched[index] = true;
                        t.Base[index] = t.Modified[index] ? t.Mask[index] : t.Hmap.GetPaintMask(j, i);
                    }

                    var baseColor = t.Base[index];
                    var blended = Color.Lerp(baseColor, paint, f);
                    // Alpha carries the terrain's own data, not our colour.
                    blended.a = baseColor.a;

                    t.Modified[index] = true;
                    t.Mask[index] = blended;
                }
            }
        }

        private static Color PaintColor()
        {
            return _roadPaved ? Heightmap.m_paintMaskPaved : Heightmap.m_paintMaskDirt;
        }

        // ---------------- the pad's projection ----------------

        // The pad around the player waits as a projection, like everything else that
        // builds: «Поставить» shows its ring, LMB lays it, Esc drops it, P pins it where
        // it stands for the arrows to move.
        private static bool _areaPreviewing;

        private static bool _areaPinned;

        private static Vector3 _areaPinnedAt;

        private static GameObject _areaPreview;

        private static LineRenderer _areaLine;

        private static LineRenderer _areaBlendLine;

        private const int AreaRingPoints = 72;

        internal static bool IsAreaPreviewing
        {
            get { return _areaPreviewing; }
        }

        private static float AreaRadius()
        {
            return Mathf.Clamp(ParseField(RoadAreaInput, 8f), 2f, RuleLimit("area", MaxAreaRadius));
        }

        /// <summary>Where the pad would go: where it was pinned, or round the player - on the ground either way.</summary>
        private static Vector3 AreaCentre(Player player)
        {
            if (!_areaPinned) return player.transform.position;

            var centre = _areaPinnedAt;
            var system = ZoneSystem.instance;
            if (system != null && system.GetGroundHeight(centre, out var ground)) centre.y = ground;
            return centre;
        }

        /// <summary>
        /// Tells a player their radius was cut to the admins' limit. Cut without a word, a pad
        /// smaller than the number typed reads as the tool being broken; and the log line is
        /// what shows afterwards that the limit held.
        /// </summary>
        private static void SayHeldToLimit(string what, float typed, float used)
        {
            if (IsAdminUnlocked || typed <= used + 0.01f) return;

            Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft,
                $"{what}: радиус урезан до {used:0} м — больше игрокам нельзя");
            Log.LogInfo($"[AstvardServerMod] {what}: radius {typed:0.#} held to the players' limit {used:0.#}.");
        }

        private static void StartAreaPreview()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            if (!RuleAllows("area"))
            {
                player.Message(MessageHud.MessageType.Center, "Мощение игрокам сейчас закрыто");
                return;
            }

            // A marked road start answers the same click, so one of them has to go first.
            if (RoadAwaitingEnd)
            {
                player.Message(MessageHud.MessageType.Center, "Сначала закончи или отмени дорожку");
                return;
            }

            // One projection at a time, or one click would answer two of them.
            if (IsPlacing) CancelPlacement();
            if (IsFencePreviewing) CancelFencePreview();

            _areaPreviewing = true;
            _areaPinned = false;
            NoteToolStart();
            UpdateRoadHint();
            InventoryGui.instance?.Hide();
            player.Message(MessageHud.MessageType.Center, "ЛКМ — замостить, Esc — отменить, P — закрепить");
        }

        internal static void CancelAreaPreview()
        {
            if (!_areaPreviewing) return;

            _areaPreviewing = false;
            _areaPinned = false;
            if (_areaPreview != null) _areaPreview.SetActive(false);
            HideTorchMarks("area");
            UpdateRoadHint();
        }

        /// <summary>LMB, Esc, P and the arrows while the pad is shown. True when the key was the pad's.</summary>
        internal static bool HandleAreaPreviewInput()
        {
            if (!_areaPreviewing) return false;

            var player = Player.m_localPlayer;
            if (player == null)
            {
                CancelAreaPreview();
                return false;
            }

            if (InventoryGui.IsVisible() || Chat.instance?.HasFocus() == true) return false;

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                NoteEscapeUsed();
                CancelAreaPreview();
                player.Message(MessageHud.MessageType.Center, "Отменено");
                RefreshMenu();
                return true;
            }

            if (Input.GetKeyDown(PinKey))
            {
                _areaPinned = !_areaPinned;
                if (_areaPinned) _areaPinnedAt = player.transform.position;
                SayPinned(_areaPinned);
                UpdateRoadHint();
                return true;
            }

            if (_areaPinned && PinNudgeThisFrame(out var step))
            {
                _areaPinnedAt += step;
                return true;
            }

            if (Input.GetMouseButtonDown(0) && Time.time - _toolMarkedAt > MarkDeafSeconds)
            {
                // The window every projection's click uses: this click must not also be a
                // swing, and the game's own input can still run later in the same frame.
                _inputHeldUntil = Time.time + 0.3f;

                // Turned down - a ward, ground still loading - the ring stays up, to be moved.
                if (BuildArea(AreaCentre(player))) CancelAreaPreview();
                RefreshMenu();
                return true;
            }

            return false;
        }

        internal static void UpdateAreaPreview()
        {
            var player = Player.m_localPlayer;
            if (!_areaPreviewing || player == null || _roadPreviewFailed)
            {
                if (_areaPreview != null) _areaPreview.SetActive(false);
                HideTorchMarks("area");
                return;
            }

            if (_areaLine == null && !CreateAreaPreview()) return;

            var centre = AreaCentre(player);
            var radius = AreaRadius();
            _areaPreview.SetActive(true);
            DrawGroundRing(_areaLine, centre, radius);

            // Smoothing reaches past the paint; the fainter ring is how far, which is also
            // what a ward refusal is judged on.
            var blend = RoadSmoothingActive ? SmoothBlend(radius) : 0f;
            _areaBlendLine.enabled = blend > 0f;
            if (blend > 0f) DrawGroundRing(_areaBlendLine, centre, radius + blend);

            // Round the rim, where LineWithTorches will stand them.
            if (RoadTorchesActive)
                ShowTorchMarks("area", Geometry.RingPosts(new Vec2(centre.x, centre.z), radius + TorchMargin, TorchSpacing()));
            else
                HideTorchMarks("area");
        }

        private static void DrawGroundRing(LineRenderer line, Vector3 centre, float radius)
        {
            var system = ZoneSystem.instance;
            line.positionCount = AreaRingPoints;
            for (var i = 0; i < AreaRingPoints; i++)
            {
                var angle = i * Mathf.PI * 2f / AreaRingPoints;
                var point = new Vector3(centre.x + Mathf.Cos(angle) * radius, centre.y,
                                        centre.z + Mathf.Sin(angle) * radius);
                if (system != null && system.GetGroundHeight(point, out var ground)) point.y = ground;
                point.y += 0.15f;
                line.SetPosition(i, point);
            }
        }

        private static bool CreateAreaPreview()
        {
            if (PreviewMaterial() == null) return false;

            _areaPreview = new GameObject("AstvardAreaPreview");
            _areaLine = MakeGroundLine(_areaPreview.transform, "Ring", true);
            _areaLine.widthMultiplier = 0.5f;
            _areaLine.startColor = Faded(PreviewGood, 0.8f);
            _areaLine.endColor = _areaLine.startColor;
            _areaBlendLine = MakeGroundLine(_areaPreview.transform, "BlendRing", true);
            _areaBlendLine.widthMultiplier = 0.25f;
            _areaBlendLine.startColor = Faded(PreviewGood, 0.35f);
            _areaBlendLine.endColor = _areaBlendLine.startColor;
            return true;
        }

        internal static void DestroyAreaPreview()
        {
            if (_areaPreview != null) Destroy(_areaPreview);
            if (_torchMarkRoot != null) Destroy(_torchMarkRoot);
        }

        // ---------------- preview ----------------

        // The road about to be laid, drawn on the ground: a faint band for the paint, firm
        // lines along its edges, fainter ones for how far smoothing reaches, a post where
        // each torch will stand - and all of it red while the road is longer than it may be.
        private static LineRenderer _roadLeftEdge;

        private static LineRenderer _roadRightEdge;

        private static LineRenderer _roadLeftBlend;

        private static LineRenderer _roadRightBlend;

        private static string _roadPreviewKey;

        private static Material _previewMaterial;

        private static GameObject _torchMarkRoot;

        private static readonly List<LineRenderer> TorchMarks = new List<LineRenderer>();

        // Whose the torch posts are: the road's or the paving's, one at a time.
        private static string _torchMarksFor;

        private static readonly List<Vec2> PreviewFlat = new List<Vec2>();

        private static readonly Color PreviewGood = new Color(1f, 0.8f, 0.27f);

        private static readonly Color PreviewTooLong = new Color(1f, 0.3f, 0.25f);

        private static readonly Color TorchMarkColour = new Color(1f, 0.55f, 0.15f, 0.95f);

        private static Color Faded(Color colour, float alpha)
        {
            colour.a = alpha;
            return colour;
        }

        private static void UpdateRoadPreview()
        {
            var player = Player.m_localPlayer;
            if (!_roadStarted || player == null || _roadPreviewFailed)
            {
                if (_roadPreview != null) _roadPreview.SetActive(false);
                HideTorchMarks("road");
                _roadPreviewKey = null;
                return;
            }

            if (_roadLine == null && !CreateRoadPreview()) return;

            var to = RoadEnd(player);
            var length = new Vector3(to.x - _roadStart.x, 0f, to.z - _roadStart.z).magnitude;
            var width = RoadWidth();
            // The paint's own half-width, as BuildRoad works it out.
            var half = Mathf.Max(width * 0.5f, PaintGridScale(_roadStart) * 0.75f);
            var blend = RoadSmoothingActive ? SmoothBlend(half) : 0f;
            var torches = RoadTorchesActive;
            var tooLong = length > RoadMaxLength;
            var sagitta = RoadSagitta(length);

            // Drawn again only when something that shapes it has moved: every point is a look
            // at the ground, and a road standing still needs none of them.
            var key = $"{_roadStart}|{Mathf.Round(to.x * 10f)}|{Mathf.Round(to.z * 10f)}|{half}|{sagitta:F2}"
                      + $"|{blend}|{torches}|{TorchSpacing()}|{tooLong}";
            if (key == _roadPreviewKey && _roadPreview.activeSelf) return;
            _roadPreviewKey = key;

            // Half a metre apart, so the band keeps to the ground over every bump.
            RoadPoints(_roadStart, to, sagitta, 0.5f, PreviewStamps);
            if (PreviewStamps.Count < 2)
            {
                _roadPreview.SetActive(false);
                HideTorchMarks("road");
                return;
            }

            _roadPreview.SetActive(true);
            var colour = tooLong ? PreviewTooLong : PreviewGood;
            DrawAlongGround(_roadLine, PreviewStamps, 0f, half * 2f, 0.15f, Faded(colour, 0.3f));
            DrawAlongGround(_roadLeftEdge, PreviewStamps, half, 0.25f, 0.2f, Faded(colour, 0.95f));
            DrawAlongGround(_roadRightEdge, PreviewStamps, -half, 0.25f, 0.2f, Faded(colour, 0.95f));

            _roadLeftBlend.enabled = blend > 0f;
            _roadRightBlend.enabled = blend > 0f;
            if (blend > 0f)
            {
                DrawAlongGround(_roadLeftBlend, PreviewStamps, half + blend, 0.15f, 0.17f, Faded(colour, 0.4f));
                DrawAlongGround(_roadRightBlend, PreviewStamps, -(half + blend), 0.15f, 0.17f, Faded(colour, 0.4f));
            }

            if (torches && !tooLong)
            {
                PreviewFlat.Clear();
                foreach (var point in PreviewStamps) PreviewFlat.Add(new Vec2(point.x, point.z));
                ShowTorchMarks("road", Geometry.EdgePosts(PreviewFlat, TorchSpacing(), half + TorchMargin));
            }
            else
            {
                HideTorchMarks("road");
            }
        }

        /// <summary>
        /// Lays a line on the ground along the path, shifted sideways by
        /// <paramref name="offset"/> - to the left of the way it runs when positive.
        /// </summary>
        private static void DrawAlongGround(LineRenderer line, List<Vector3> path, float offset, float width,
                                            float lift, Color colour)
        {
            var system = ZoneSystem.instance;
            line.widthMultiplier = width;
            line.startColor = colour;
            line.endColor = colour;
            line.positionCount = path.Count;

            for (var i = 0; i < path.Count; i++)
            {
                var point = path[i];
                if (offset != 0f)
                {
                    var before = path[Mathf.Max(0, i - 1)];
                    var after = path[Mathf.Min(path.Count - 1, i + 1)];
                    var along = new Vector3(after.x - before.x, 0f, after.z - before.z).normalized;
                    point += new Vector3(-along.z, 0f, along.x) * offset;
                }

                if (system != null && system.GetGroundHeight(point, out var ground)) point.y = ground;
                point.y += lift;
                line.SetPosition(i, point);
            }
        }

        private static Material PreviewMaterial()
        {
            if (_previewMaterial != null) return _previewMaterial;

            var shader = Shader.Find("Sprites/Default")
                         ?? Shader.Find("Particles/Standard Unlit")
                         ?? Shader.Find("Unlit/Color");
            if (shader == null)
            {
                // Better a tool with no preview than one that throws every frame.
                _roadPreviewFailed = true;
                Log.LogWarning("[AstvardServerMod] No shader for the previews.");
                return null;
            }

            _previewMaterial = new Material(shader);
            return _previewMaterial;
        }

        /// <summary>
        /// A line lying flat on the ground: TransformZ faces it along its object's forward,
        /// which points up here. One LineRenderer to an object, so each line gets its own.
        /// </summary>
        private static LineRenderer MakeGroundLine(Transform parent, string name, bool loop)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            var line = go.AddComponent<LineRenderer>();
            line.material = PreviewMaterial();
            line.useWorldSpace = true;
            line.loop = loop;
            line.numCapVertices = loop ? 0 : 2;
            line.alignment = LineAlignment.TransformZ;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }

        private static bool CreateRoadPreview()
        {
            if (PreviewMaterial() == null) return false;

            _roadPreview = new GameObject("AstvardRoadPreview");
            _roadLine = MakeGroundLine(_roadPreview.transform, "Band", false);
            _roadLeftBlend = MakeGroundLine(_roadPreview.transform, "LeftBlend", false);
            _roadRightBlend = MakeGroundLine(_roadPreview.transform, "RightBlend", false);
            _roadLeftEdge = MakeGroundLine(_roadPreview.transform, "LeftEdge", false);
            _roadRightEdge = MakeGroundLine(_roadPreview.transform, "RightEdge", false);
            return true;
        }

        /// <summary>
        /// A post where each torch will stand, from the same placing the torches themselves
        /// go by. The road and the paving share the posts, one at a time.
        /// </summary>
        private static void ShowTorchMarks(string owner, IList<Post> posts)
        {
            if (PreviewMaterial() == null) return;
            if (_torchMarkRoot == null) _torchMarkRoot = new GameObject("AstvardTorchMarks");

            _torchMarksFor = owner;
            _torchMarkRoot.SetActive(true);

            var system = ZoneSystem.instance;
            for (var i = 0; i < posts.Count; i++)
            {
                if (i == TorchMarks.Count) TorchMarks.Add(MakeTorchMark());

                var mark = TorchMarks[i];
                var at = new Vector3(posts[i].At.X, 0f, posts[i].At.Z);
                if (system != null && system.GetGroundHeight(at, out var ground)) at.y = ground;
                mark.SetPosition(0, at);
                mark.SetPosition(1, at + Vector3.up * 1.5f);
                mark.gameObject.SetActive(true);
            }

            for (var i = posts.Count; i < TorchMarks.Count; i++) TorchMarks[i].gameObject.SetActive(false);
        }

        private static void HideTorchMarks(string owner)
        {
            if (_torchMarkRoot != null && _torchMarksFor == owner) _torchMarkRoot.SetActive(false);
        }

        private static LineRenderer MakeTorchMark()
        {
            var go = new GameObject("TorchMark");
            go.transform.SetParent(_torchMarkRoot.transform, false);

            // Standing up, and turned to the camera from wherever it is seen, like a stake.
            var line = go.AddComponent<LineRenderer>();
            line.material = PreviewMaterial();
            line.startColor = TorchMarkColour;
            line.endColor = TorchMarkColour;
            line.widthMultiplier = 0.15f;
            line.positionCount = 2;
            line.useWorldSpace = true;
            line.alignment = LineAlignment.View;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }

        /// <summary>
        /// Metres between neighbouring vertices of the paint mask — the finest detail
        /// the terrain can hold.
        /// </summary>
        private static float PaintGridScale(Vector3 near)
        {
            var comp = TerrainComp.FindTerrainCompiler(near);
            var hmap = comp != null && FHmap != null ? FHmap.GetValue(comp) as Heightmap : null;
            if (hmap == null) hmap = Heightmap.FindHeightmap(near);
            return hmap != null && hmap.m_scale > 0f ? hmap.m_scale : 1f;
        }

        /// <summary>
        /// Every zone the work can reach, resolved once. Asking find-or-create per point
        /// would query the same zone dozens of times, and a creation landing on a zone
        /// that already has a compiler makes the new one destroy the old — taking the
        /// paint already laid into it along with it. Null when the work has to wait or
        /// cannot be done at all; see <see cref="MayMakeCompiler"/>.
        /// </summary>
        private static List<TerrainComp> CompsForStamps(List<Vector3> points, float radius, float y,
                                                        out bool unloaded)
        {
            var zones = new HashSet<Vector2s>();
            foreach (var point in points)
            {
                zones.Add(ZoneSystem.GetZone(point + new Vector3(-radius, 0f, -radius)));
                zones.Add(ZoneSystem.GetZone(point + new Vector3(radius, 0f, -radius)));
                zones.Add(ZoneSystem.GetZone(point + new Vector3(-radius, 0f, radius)));
                zones.Add(ZoneSystem.GetZone(point + new Vector3(radius, 0f, radius)));
            }

            var found = new List<TerrainComp>();
            var missing = new List<Vector3>();
            foreach (var zone in zones)
            {
                var zoneCenter = ZoneSystem.GetZonePos(zone);
                var at = new Vector3(zoneCenter.x, y, zoneCenter.z);

                var comp = TerrainComp.FindTerrainCompiler(at);
                if (comp != null) found.Add(comp);
                else missing.Add(at);
            }

            return WithMissingCompilers(found, missing, out unloaded);
        }

        // ---------------- the two tools ----------------

        private static void BuildRoad()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            if (!_roadStarted)
            {
                player.Message(MessageHud.MessageType.Center, "Сначала нажми «Начать»");
                return;
            }

            // The start may have been marked before the admins closed roads.
            if (!RuleAllows("road"))
            {
                CancelRoad();
                player.Message(MessageHud.MessageType.Center, "Дорожки игрокам сейчас закрыты");
                return;
            }

            var from = _roadStart;
            var to = RoadEnd(player);
            var length = new Vector3(to.x - from.x, 0f, to.z - from.z).magnitude;

            if (length < 1f)
            {
                player.Message(MessageHud.MessageType.Center, "Точки слишком близко");
                return;
            }

            var maxLength = RoadMaxLength;
            if (length > maxLength)
            {
                player.Message(MessageHud.MessageType.Center,
                    $"Далеко: {length:F0} м, максимум {maxLength:F0}");
                return;
            }

            var width = RoadWidth();
            var scale = PaintGridScale(from);

            // The mask has a vertex every scale metres, so a band narrower than about
            // three quarters of a cell can miss whole rows and come out dotted however
            // carefully it is drawn. The asked-for width still widens it beyond that.
            var radius = Mathf.Max(width * 0.5f, scale * 0.75f);

            // A metre between samples is plenty: the distance is measured to the line
            // itself, so sampling only has to follow the curve, not cover it.
            RoadPoints(from, to, RoadSagitta(length), 1f, RoadPath);
            if (RoadPath.Count == 0) return;

            // Smoothing reaches past the paint into the blend band. So does the ward
            // check, and so does the list of zones: every zone the road writes has to be
            // in it - that is also what undo records.
            var reach = RoadSmoothingActive ? radius + SmoothBlend(radius) : radius;

            // Before any zone is looked up, so a refusal makes no compiler either; and
            // like the refusals below, it keeps the start marked for another end.
            if (!WardsAllowStroke("road", RoadPath, reach))
            {
                player.Message(MessageHud.MessageType.Center,
                    "Дорожка задевает чужой оберег — веди её в обход" + SmoothingNote(radius));
                return;
            }

            var comps = CompsForStamps(RoadPath, reach, from.y, out var unloaded);
            if (comps == null)
            {
                // The start stays marked, so waiting and pressing again is all it takes.
                player.Message(MessageHud.MessageType.Center, unloaded
                    ? (_roadPinned
                        ? "Конец дорожки дальше прогруженной земли — встань ближе к середине"
                        : "Начало дорожки уже не прогружено — закрепи конец (P) и подойди к середине")
                    : "Земля по пути ещё прогружается — подожди пару секунд и нажми снова");
                return;
            }

            if (comps.Count == 0)
            {
                Log.LogWarning("[AstvardServerMod] No TerrainComp for the road.");
                return;
            }

            _roadStarted = false;
            _roadPinned = false;
            UpdateRoadHint();

            Instance?.StartCoroutine(LayPaint(new List<Vector3>(RoadPath), comps, radius,
                PaintColor(), length, width, scale, "road"));
        }

        /// <summary>
        /// A filled circle — a square or a yard rather than a path — where its projection
        /// stands. It is the same painter with a one-point path, so the distance test alone
        /// fills the disc; no ring of stamps is needed. False when it was turned down, and
        /// the projection stays up to be moved or given another radius.
        /// </summary>
        private static bool BuildArea(Vector3 centre)
        {
            var player = Player.m_localPlayer;
            if (player == null) return false;

            if (!RuleAllows("area"))
            {
                player.Message(MessageHud.MessageType.Center, "Мощение игрокам сейчас закрыто");
                return false;
            }

            var area = AreaRadius();
            var scale = PaintGridScale(centre);
            SayHeldToLimit("Мощение", ParseField(RoadAreaInput, 8f), area);

            RoadPath.Clear();
            RoadPath.Add(centre);

            var reach = RoadSmoothingActive ? area + SmoothBlend(area) : area;
            if (!WardsAllowStroke("area", RoadPath, reach))
            {
                player.Message(MessageHud.MessageType.Center,
                    "Мощение задевает чужой оберег — уменьши радиус или отойди" + SmoothingNote(area));
                return false;
            }

            var comps = CompsForStamps(RoadPath, reach, centre.y, out var unloaded);
            if (comps == null)
            {
                player.Message(MessageHud.MessageType.Center, unloaded
                    ? "Мощение выходит за прогруженную землю"
                    : "Земля вокруг ещё прогружается — подожди пару секунд и нажми снова");
                return false;
            }

            if (comps.Count == 0)
            {
                Log.LogWarning("[AstvardServerMod] No TerrainComp for the area.");
                return false;
            }

            Instance?.StartCoroutine(LayPaint(new List<Vector3>(RoadPath), comps, area,
                PaintColor(), area, area * 2f, scale, "area"));
            return true;
        }

        /// <summary>
        /// Lays the paint a stretch at a time so it grows from the marked point onwards.
        /// Each stretch is saved and poked, which is also what makes it visible — the
        /// mask only reaches the rendered ground when its heightmap regenerates.
        /// </summary>
        private static IEnumerator LayPaint(List<Vector3> path, List<TerrainComp> comps,
                                            float radius, Color paint, float length,
                                            float width, float scale, string kind)
        {
            var undo = RecordTerrainUndo(kind == "area" ? "мощение" : "дорожка", comps,
                path[path.Count / 2], length * 0.5f + radius + 16f);

            _roadLaying = true;
            _roadCancelled = false;

            var targets = new List<PaintTarget>();
            foreach (var comp in comps)
            {
                var target = MakeTarget(comp);
                if (target != null) targets.Add(target);
            }

            if (targets.Count == 0)
            {
                Log.LogWarning("[AstvardServerMod] Could not reach the paint mask.");
                _roadLaying = false;
                yield break;
            }

            // Only once it is certain the paint will go down, so a road that fails to
            // reach its ground does not leave a cleared strip behind it. All at once
            // rather than stretch by stretch: the paint takes twenty frames, far too
            // quick for a cancel to land between them.
            var cleared = RoadClearingActive ? ClearAlongPath(path, radius) : 0;

            // Before the paint, so the first stretch's save carries both, and the
            // heightmaps the paint rebuilds show the new ground under it.
            if (RoadSmoothingActive) SmoothAlongPath(path, comps, radius);

            var save = AccessTools.Method(typeof(TerrainComp), "Save");
            var owned = 0;
            foreach (var comp in comps)
            {
                var view = comp.GetComponent<ZNetView>();
                if (view != null && view.IsOwner()) owned++;
            }

            // Twenty stretches at most: each one costs a full serialise of the zone and
            // a heightmap rebuild, so it is the number of them that matters.
            var segments = Mathf.Max(1, path.Count - 1);
            var perStretch = Mathf.Max(1, Mathf.CeilToInt(segments / 20f));

            var stopped = false;
            for (var first = 0; first < Mathf.Max(1, segments); first += perStretch)
            {
                if (_roadCancelled)
                {
                    stopped = true;
                    break;
                }

                var last = Mathf.Min(first + perStretch, path.Count - 1);

                foreach (var target in targets)
                    PaintStretch(target, path, first, last, radius, paint);

                // Save gained an optional paintOnly in 1.0. Reflection does not fill
                // optional parameters - a null argument array throws - so pass the
                // default explicitly; false is the full save the old call made.
                foreach (var comp in comps) save.Invoke(comp, new object[] { false });
                RebuildHeightmaps(path[last], radius + 16f);
                yield return null;
            }

            // One last sweep over the whole run: a stretch near a zone border can leave
            // the neighbour's heightmap holding a version from before the last write.
            RebuildHeightmaps(path[path.Count / 2], length * 0.5f + radius + 16f);

            var painted = 0;
            foreach (var target in targets)
                foreach (var touched in target.Touched)
                    if (touched) painted++;

            // Last, on the finished ground, and a frame on: the rebuilt terrain has to be
            // what a ray hits, not only what is drawn. A road stopped halfway gets none -
            // there is no telling where its edges were meant to run.
            var torches = new TorchRun();
            if (!stopped && RoadTorchesActive)
            {
                yield return null;
                torches = LineWithTorches(path, radius, kind == "area", undo);
            }

            _roadLaying = false;
            _roadCancelled = false;
            if (_roadPreview != null) _roadPreview.SetActive(false);

            var clearedNote = (RoadSmoothingActive ? (kind == "area" ? ", земля сглажена" : ", сглажена") : "")
                              + (cleared > 0 ? $", снесено: {cleared}" : "")
                              + TorchNote(torches);
            if (!stopped)
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    kind == "area"
                        ? $"Мощение радиусом {length:F0} м готово{clearedNote}"
                        : $"Дорожка {length:F0} м, ширина {width:F1} м{clearedNote}");

            Log.LogInfo($"[AstvardServerMod] {kind} {(_roadPaved ? "paved" : "dirt")} " +
                        $"{length:F1} m width {width:F1} brush={radius:F2} grid={scale:F2} " +
                        $"nodes={path.Count} zones={comps.Count} owned={owned} verts={painted} " +
                        $"cleared={cleared} smoothed={RoadSmoothingActive} " +
                        $"torches={torches.Placed}/{torches.Placed + torches.Skipped + torches.Unpaid}");
        }

        /// <summary>
        /// Flattens a pad under a blueprint before it is placed, so a build meant for
        /// level ground does not end up half-buried on a slope. The footprint is taken
        /// from the rotated clipboard, so a build set down at an angle still gets a pad
        /// that covers it. Returns the undo step it recorded, for the build to join, or
        /// null when nothing was levelled.
        /// </summary>
        private static TerrainUndoStep LevelUnderBuild(Vector3 origin, Quaternion rotation)
        {
            if (Clipboard.Count == 0) return null;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var entry in Clipboard)
            {
                var local = rotation * entry.LocalPos;
                if (local.x < minX) minX = local.x;
                if (local.x > maxX) maxX = local.x;
                if (local.z < minZ) minZ = local.z;
                if (local.z > maxZ) maxZ = local.z;
            }

            // A circle has to reach the corners, not the sides: half the diagonal,
            // otherwise a rectangular build would sit with its corners off the pad.
            var spanX = maxX - minX;
            var spanZ = maxZ - minZ;
            var half = Mathf.Sqrt(spanX * spanX + spanZ * spanZ) * 0.5f;
            var radius = Mathf.Clamp(half + 1.5f, 2f, 64f);
            var target = new Vector3(origin.x + (minX + maxX) * 0.5f,
                                     origin.y,
                                     origin.z + (minZ + maxZ) * 0.5f);

            // A short run-out: enough that the pad does not end in a cliff, not so much
            // that setting down a hut reshapes the whole hillside around it.
            var blend = Mathf.Clamp(radius * 0.35f, 2f, 8f);

            // Asked as a round pad, which this always is - see the flag borrowed below.
            // Only an admin can place a blueprint today, and an admin is never refused, so
            // this is for the day blueprints open up; the pieces will want the same
            // question asked then.
            if (!WardsAllowPad("pad under a build", target, radius + blend, false))
            {
                // Like ground not yet loaded: the build still goes down, on the ground as it is.
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    "Землю под постройкой не выровнять — рядом чужой оберег");
                return null;
            }

            var comps = CollectTerrainComps(target, radius + blend, out _);
            if (comps == null)
            {
                // The build goes down regardless, as it always did when there was nothing
                // to level into - only now the player hears why the ground stayed.
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    "Землю под постройкой не выровнять — она ещё не прогружена");
                return null;
            }

            if (comps.Count == 0)
            {
                Log.LogWarning("[AstvardServerMod] No TerrainComp under the build.");
                return null;
            }

            // BlendLevel reads the terrain tool's own square/circle flag. The pad is
            // always round whatever the player last levelled by hand, so the flag is
            // borrowed and put back.
            var undo = RecordTerrainUndo("площадка под постройку", comps, target, radius + blend);

            var wasSquare = _terrainSquare;
            _terrainSquare = false;

            var save = AccessTools.Method(typeof(TerrainComp), "Save");
            foreach (var comp in comps) BlendLevel(comp, target, radius, blend);
            foreach (var comp in comps) save.Invoke(comp, new object[] { false });

            _terrainSquare = wasSquare;

            RebuildHeightmaps(target, radius + blend);

            Log.LogInfo($"[AstvardServerMod] Levelled under build r={radius:F1} " +
                        $"blend={blend:F1} zones={comps.Count} at {target}");
            return undo;
        }

        private static void ApplyTerrainLevel()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            // The page may have been open when the admins closed it.
            if (!RuleAllows("level"))
            {
                player.Message(MessageHud.MessageType.Center, "Выравнивание игрокам сейчас закрыто");
                return;
            }

            var typed = ParseField(RadiusInput, 8f);
            var asked = ParseField(HeightInput, 0f);
            var radius = Mathf.Clamp(typed, 1f, RuleLimit("level", MaxLevelRadius));
            SayHeldToLimit("Выравнивание", typed, radius);

            var playerPos = player.transform.position;

            // An absolute mark, not a step up from where the player stands. "Level this
            // to five above the water" is the thing anyone actually wants, and it is the
            // same number the info page reports — raw Y would read five as twenty-five
            // metres under the sea, since the world floor sits thirty below it.
            //
            // Zero and empty both mean the height being stood at, which is what the field
            // did before and what it is most often used for. The price is that sea level
            // itself cannot be asked for by number; stand at the shore for that.
            var sea = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            var targetY = Mathf.Approximately(asked, 0f) ? playerPos.y : sea + asked;
            var target = new Vector3(playerPos.x, targetY, playerPos.z);

            // The blend band is derived from the radius — a bigger platform gets a
            // longer run-out, so the user only has to pick radius and height.
            var blend = Mathf.Clamp(radius * 0.75f, 4f, 24f);
            var reach = radius + blend;

            // Before any zone is looked up, so a refusal makes no compiler either.
            if (!WardsAllowPad("levelling", target, reach, _terrainSquare))
            {
                player.Message(MessageHud.MessageType.Center,
                    "Выравнивание задевает чужой оберег — уменьши радиус или отойди");
                return;
            }

            // Each zone keeps its own heightmap, so an operation spilling over a
            // zone border has to be handed to every TerrainComp it touches —
            // otherwise the neighbour keeps its old heights and the seam tears open.
            var comps = CollectTerrainComps(target, reach, out var unloaded);
            if (comps == null)
            {
                player.Message(MessageHud.MessageType.Center, unloaded
                    ? "Выравнивание выходит за прогруженную землю — уменьши радиус"
                    : "Земля вокруг ещё прогружается — подожди пару секунд и нажми снова");
                return;
            }

            if (comps.Count == 0)
            {
                Log.LogWarning("[AstvardServerMod] No TerrainComp available for the area.");
                return;
            }

            RecordTerrainUndo("выравнивание", comps, target, reach);

            var save = AccessTools.Method(typeof(TerrainComp), "Save");
            foreach (var c in comps) BlendLevel(c, target, radius, blend);
            foreach (var c in comps) save.Invoke(c, new object[] { false });

            RebuildHeightmaps(target, reach);

            // Said in the size it came out, not the one typed: for a player the admins'
            // limit may have cut it, and a square's radius is half its side.
            var height = Mathf.Approximately(asked, 0f) ? "на уровне игрока" : $"на {asked:0.#} м над водой";
            player.Message(MessageHud.MessageType.Center, _terrainSquare
                ? $"Выровнено квадратом {radius * 2f:0}×{radius * 2f:0} м, {height}"
                : $"Выровнено кругом радиусом {radius:0} м, {height}");

            Log.LogInfo($"[AstvardServerMod] Level {(_terrainSquare ? "square" : "circle")} " +
                        $"r={radius} h={asked:F1} -> y={targetY:F1} blend={blend:F1} " +
                        $"zones={comps.Count} at {target}");
        }

        private static readonly System.Reflection.FieldInfo FHmap = AccessTools.Field(typeof(TerrainComp), "m_hmap");

        private static readonly System.Reflection.FieldInfo FLevelDelta = AccessTools.Field(typeof(TerrainComp), "m_levelDelta");

        private static readonly System.Reflection.FieldInfo FSmoothDelta = AccessTools.Field(typeof(TerrainComp), "m_smoothDelta");

        private static readonly System.Reflection.FieldInfo FModified = AccessTools.Field(typeof(TerrainComp), "m_modifiedHeight");

        private static readonly System.Reflection.FieldInfo FWidth = AccessTools.Field(typeof(TerrainComp), "m_width");

        private static readonly System.Reflection.FieldInfo FOperations = AccessTools.Field(typeof(TerrainComp), "m_operations");

        private static readonly System.Reflection.FieldInfo FLastOpPoint = AccessTools.Field(typeof(TerrainComp), "m_lastOpPoint");

        private static readonly System.Reflection.FieldInfo FLastOpRadius = AccessTools.Field(typeof(TerrainComp), "m_lastOpRadius");

        /// <summary>
        /// Levels the inner disc and blends outwards per vertex: every point in the
        /// outer band is pulled towards the flat height only as far as its distance
        /// allows, ending at the terrain's own height. The engine's LevelTerrain can
        /// only stamp one flat height over a whole radius, which is why the border
        /// had to be faked with rings before — writing the height deltas ourselves
        /// gives a continuous slope that meets whatever is already there.
        /// </summary>
        private static void BlendLevel(TerrainComp comp, Vector3 worldTarget, float radius, float blend)
        {
            var hmap = FHmap.GetValue(comp) as Heightmap;
            var levelDelta = FLevelDelta.GetValue(comp) as float[];
            var smoothDelta = FSmoothDelta.GetValue(comp) as float[];
            var modified = FModified.GetValue(comp) as bool[];
            if (hmap == null || levelDelta == null || smoothDelta == null || modified == null) return;

            var width = (int)FWidth.GetValue(comp);
            var size = width + 1;
            var scale = hmap.m_scale;
            if (scale <= 0f) return;

            hmap.WorldToVertex(worldTarget, out var cx, out var cy);
            // Heights inside a heightmap are relative to its own transform.
            var localTargetY = worldTarget.y - comp.transform.position.y;

            var reach = radius + blend;
            var reachVerts = Mathf.CeilToInt(reach / scale);

            for (var i = cy - reachVerts; i <= cy + reachVerts; i++)
            {
                if (i < 0 || i >= size) continue;
                for (var j = cx - reachVerts; j <= cx + reachVerts; j++)
                {
                    if (j < 0 || j >= size) continue;

                    var dx = j - cx;
                    var dz = i - cy;
                    // Chebyshev distance gives square platforms, Euclidean round ones.
                    var distance = (_terrainSquare
                        ? Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz))
                        : Mathf.Sqrt(dx * dx + dz * dz)) * scale;

                    if (distance > reach) continue;

                    var index = i * size + j;
                    var current = hmap.GetHeight(j, i);

                    float desired;
                    if (distance <= radius)
                    {
                        desired = localTargetY;
                    }
                    else
                    {
                        var t = Mathf.SmoothStep(0f, 1f, (distance - radius) / blend);
                        desired = Mathf.Lerp(localTargetY, current, t);
                    }

                    // Same bookkeeping LevelTerrain does: fold in and clear any
                    // pending smooth delta, then clamp to the engine's ±8 m budget.
                    var delta = desired - current + smoothDelta[index];
                    smoothDelta[index] = 0f;
                    levelDelta[index] = Mathf.Clamp(levelDelta[index] + delta, -8f, 8f);
                    modified[index] = true;
                }
            }

            FOperations.SetValue(comp, (int)FOperations.GetValue(comp) + 1);
            FLastOpPoint.SetValue(comp, worldTarget);
            FLastOpRadius.SetValue(comp, reach);
        }

        /// <summary>
        /// Every TerrainComp whose zone is touched by the given reach, created if missing.
        /// Null when the work has to wait or cannot be done at all; see
        /// <see cref="MayMakeCompiler"/>.
        /// </summary>
        private static List<TerrainComp> CollectTerrainComps(Vector3 center, float reach, out bool unloaded)
        {
            var found = new List<TerrainComp>();
            var missing = new List<Vector3>();
            var seen = new HashSet<Vector2s>();

            // Sample a grid across the affected square; a half-zone step is fine
            // since one sample per 32 m cannot skip over a 64 m zone.
            const float step = 32f;
            for (var dx = -reach; dx <= reach + step; dx += step)
            {
                for (var dz = -reach; dz <= reach + step; dz += step)
                {
                    var probe = center + new Vector3(Mathf.Clamp(dx, -reach, reach), 0f,
                                                     Mathf.Clamp(dz, -reach, reach));
                    var zone = ZoneSystem.GetZone(probe);
                    if (!seen.Add(zone)) continue;

                    var zoneCenter = ZoneSystem.GetZonePos(zone);
                    var probeAtZone = new Vector3(zoneCenter.x, center.y, zoneCenter.z);

                    var comp = TerrainComp.FindTerrainCompiler(probeAtZone);
                    if (comp != null) found.Add(comp);
                    else missing.Add(probeAtZone);
                }
            }

            return WithMissingCompilers(found, missing, out unloaded);
        }

        /// <summary>
        /// Rebuilds every heightmap in range. TerrainComp.DoOperation only pokes its
        /// own, which leaves neighbouring zones rendering stale geometry — the visible
        /// gaps at zone seams. The game does the same sweep in TerrainModifier.PokeHeightmaps.
        /// </summary>
        private static void RebuildHeightmaps(Vector3 center, float reach)
        {
            foreach (var hmap in Heightmap.GetAllHeightmaps())
            {
                // Poke's "delayed" is an update-channel selector now, not a flag:
                // 0 regenerates inside the call, as the old false did. 1 and 2 defer to
                // LateUpdate, which would leave the caller measuring a stale heightmap.
                if (hmap != null && hmap.IsPointInside(center, reach))
                    hmap.Poke(delayed: 0);
            }

            if (ClutterSystem.instance != null)
                ClutterSystem.instance.ResetGrass(center, reach);
        }

        private static readonly int TerrainCompilerHash = "_TerrainCompiler".GetStableHashCode();

        private static readonly int ZoneCtrlHash = "_ZoneCtrl".GetStableHashCode();

        /// <summary>
        /// Whether a new terrain compiler may be made for the zone at a point. Asked only
        /// where no live one was found; <paramref name="unloaded"/> tells the two reasons
        /// for a no apart.
        ///
        /// A zone keeps all its terrain edits in one compiler, and the game allows one
        /// per zone: a compiler waking up where there already is one removes the other,
        /// with everything in it - "Found another terrain compiler in this area, removing
        /// it". So not finding one among the live objects is not enough. The zone's own
        /// can exist in the world and not have been built here yet, and a new one made in
        /// that window is the one it removes when it does wake. The client log has two of
        /// exactly that, straight after a 172 m road on 10.09.2026 made seven compilers.
        ///
        /// The world's records settle it. The server sends terrain records ahead of
        /// ordinary ones - ZDOMan.ServerSendCompare sorts by type - and every generated
        /// zone has a controller, an ordinary record standing on the very spot a compiler
        /// does. Once the controller is here, any compiler of the zone is here too; and a
        /// compiler record that is here but not live is one ZNetScene has still to build.
        /// Both the types and the positions were read out of the world save, not assumed.
        ///
        /// No heightmap means the zone is outside the loaded ground altogether. A compiler
        /// made there finds nothing in its Awake, never joins the registry, and leaves a
        /// record in the world that holds no edits and waits to collide with the real one.
        /// </summary>
        private static bool MayMakeCompiler(Vector3 at, out bool unloaded)
        {
            unloaded = Heightmap.FindHeightmap(at) == null;
            if (unloaded || ZDOMan.instance == null) return false;

            var records = new List<ZDO>();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(at), new SimulationDistance(0, 0), records);

            var known = false;
            foreach (var zdo in records)
            {
                var prefab = zdo.GetPrefab();
                if (prefab == TerrainCompilerHash) return false;
                if (prefab == ZoneCtrlHash) known = true;
            }

            return known;
        }

        /// <summary>
        /// Adds the compilers still missing - for every zone or for none. All of them are
        /// checked before any is made, so a job turned down leaves nothing behind: half a
        /// set of new compilers would be records in the world for work that never
        /// happened. Null when turned down.
        /// </summary>
        private static List<TerrainComp> WithMissingCompilers(List<TerrainComp> found, List<Vector3> missing,
                                                              out bool unloaded)
        {
            unloaded = false;
            foreach (var at in missing)
            {
                if (MayMakeCompiler(at, out unloaded)) continue;

                var zone = ZoneSystem.GetZone(at);
                Log.LogInfo($"[AstvardServerMod] Terrain work turned down: zone {zone.x},{zone.y} " +
                            (unloaded ? "is not loaded" : "may have a compiler still on its way"));
                return null;
            }

            foreach (var at in missing)
            {
                var comp = CreateTerrainCompiler(at);
                if (comp != null) found.Add(comp);
            }

            foreach (var comp in found)
            {
                var nview = comp.GetComponent<ZNetView>();
                if (nview != null && !nview.IsOwner()) nview.ClaimOwnership();
            }

            return found;
        }

        private static TerrainComp CreateTerrainCompiler(Vector3 pos)
        {
            if (ZNetScene.instance == null)
            {
                Log.LogError("[AstvardServerMod] ZNetScene not ready.");
                return null;
            }

            var prefab = ZNetScene.instance.GetPrefab("_TerrainCompiler");
            if (prefab == null)
            {
                var candidates = ZNetScene.instance.GetPrefabNames()
                    .Where(n => n.IndexOf("terrain", System.StringComparison.OrdinalIgnoreCase) >= 0
                             || n.IndexOf("compiler", System.StringComparison.OrdinalIgnoreCase) >= 0);
                Log.LogError("[AstvardServerMod] '_TerrainCompiler' prefab not found. Candidates: "
                             + string.Join(", ", candidates));
                return null;
            }

            var zonePos = ZoneSystem.GetZonePos(ZoneSystem.GetZone(pos));
            var go = UnityEngine.Object.Instantiate(prefab, zonePos, Quaternion.identity);
            var comp = go.GetComponent<TerrainComp>();
            if (comp == null)
                Log.LogError("[AstvardServerMod] Spawned _TerrainCompiler has no TerrainComp component.");
            else
                Log.LogInfo($"[AstvardServerMod] Created TerrainComp for zone at {zonePos}");
            return comp;
        }

        private static void PaintRect(Texture2D tex, int x0, int y0, int x1, int y1,
                                      Color fill, Color edge)
        {
            for (var y = y0; y <= y1; y++)
            {
                if (y < 0 || y >= tex.height) continue;
                for (var x = x0; x <= x1; x++)
                {
                    if (x < 0 || x >= tex.width) continue;
                    var onEdge = x == x0 || x == x1 || y == y0 || y == y1;
                    tex.SetPixel(x, y, onEdge ? edge : fill);
                }
            }
        }
    }
}
