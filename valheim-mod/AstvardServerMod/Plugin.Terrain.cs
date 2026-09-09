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
                ? LevelGroundButton.GetComponentInChildren<Text>()
                : null;
            if (label != null)
                label.text = IsLevelGroundEnabled
                    ? "Выравнивать землю: вкл"
                    : "Выравнивать землю: выкл";
        }

        // Long enough for a real stretch of road, short enough that one press does not
        // rewrite the terrain of a dozen zones at once.
        private const float MaxRoadLength = 200f;

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
            if (_roadPreview != null) _roadPreview.SetActive(false);
            UpdateRoadHint();

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                wasLaying ? "Укладка остановлена" : "Отменено");
        }

        private static Vector3 _roadStart;

        private static void UpdateRoadHint()
        {
            var label = RoadHint != null ? RoadHint.GetComponentInChildren<Text>() : null;
            if (label == null) return;

            var kind = _roadPaved ? "каменная" : "земляная";
            var bend = _roadBendLeft ? "влево" : "вправо";
            label.text = _roadStarted
                ? $"Кладка: {kind}, изгиб {bend}.{NEWLINE}Начало отмечено — иди в конец{NEWLINE}"
                  + $"и нажми ЛКМ или «Закончить».{NEWLINE}Esc — отменить."
                : $"Кладка: {kind}, изгиб {bend}.{NEWLINE}Встань в начало дорожки{NEWLINE}"
                  + $"и нажми «Начать».";
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
        /// How far the road bows out at its middle, in metres. Scaling it by the length
        /// means the typed number describes the shape rather than an absolute distance:
        /// 1 is a gentle bend and 10 puts the bulge at half the chord, a semicircle.
        /// </summary>
        private static float RoadSagitta(float length)
        {
            // The field is a magnitude and the buttons carry the side. A typed minus
            // used to be the only way to say "the other way", and the field's own
            // label had that backwards — positive bows left, which RoadTests pins.
            var curve = Mathf.Clamp(Mathf.Abs(ParseField(RoadCurveInput, 0f)), 0f, 10f);
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

        // ---------------- preview ----------------

        private static void UpdateRoadPreview()
        {
            var player = Player.m_localPlayer;
            if (!_roadStarted || player == null || _roadPreviewFailed)
            {
                if (_roadPreview != null) _roadPreview.SetActive(false);
                return;
            }

            if (_roadLine == null && !CreateRoadPreview()) return;

            var to = player.transform.position;
            var length = new Vector3(to.x - _roadStart.x, 0f, to.z - _roadStart.z).magnitude;
            var width = RoadWidth();

            RoadPoints(_roadStart, to, RoadSagitta(length), Mathf.Max(width * 0.5f, 1f), PreviewStamps);
            if (PreviewStamps.Count < 2)
            {
                _roadPreview.SetActive(false);
                return;
            }

            _roadPreview.SetActive(true);
            _roadLine.widthMultiplier = width;
            _roadLine.positionCount = PreviewStamps.Count;

            var system = ZoneSystem.instance;
            for (var i = 0; i < PreviewStamps.Count; i++)
            {
                var point = PreviewStamps[i];
                if (system != null && system.GetGroundHeight(point, out var ground)) point.y = ground;
                point.y += 0.15f;
                _roadLine.SetPosition(i, point);
            }
        }

        private static bool CreateRoadPreview()
        {
            var shader = Shader.Find("Sprites/Default")
                         ?? Shader.Find("Particles/Standard Unlit")
                         ?? Shader.Find("Unlit/Color");
            if (shader == null)
            {
                // Better a road tool with no preview than one that throws every frame.
                _roadPreviewFailed = true;
                Log.LogWarning("[AstvardServerMod] No shader for the road preview.");
                return false;
            }

            _roadPreview = new GameObject("AstvardRoadPreview");
            _roadPreview.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            _roadLine = _roadPreview.AddComponent<LineRenderer>();
            _roadLine.material = new Material(shader);
            _roadLine.startColor = new Color(1f, 0.8f, 0.27f, 0.55f);
            _roadLine.endColor = _roadLine.startColor;
            _roadLine.useWorldSpace = true;
            _roadLine.numCapVertices = 2;
            _roadLine.alignment = LineAlignment.TransformZ;
            _roadLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _roadLine.receiveShadows = false;
            return true;
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
        /// paint already laid into it along with it.
        /// </summary>
        private static List<TerrainComp> CompsForStamps(List<Vector3> points, float radius, float y)
        {
            var zones = new HashSet<Vector2s>();
            foreach (var point in points)
            {
                zones.Add(ZoneSystem.GetZone(point + new Vector3(-radius, 0f, -radius)));
                zones.Add(ZoneSystem.GetZone(point + new Vector3(radius, 0f, -radius)));
                zones.Add(ZoneSystem.GetZone(point + new Vector3(-radius, 0f, radius)));
                zones.Add(ZoneSystem.GetZone(point + new Vector3(radius, 0f, radius)));
            }

            var comps = new List<TerrainComp>();
            foreach (var zone in zones)
            {
                var zoneCenter = ZoneSystem.GetZonePos(zone);
                var at = new Vector3(zoneCenter.x, y, zoneCenter.z);

                var comp = TerrainComp.FindTerrainCompiler(at) ?? CreateTerrainCompiler(at);
                if (comp == null) continue;

                var nview = comp.GetComponent<ZNetView>();
                if (nview != null && !nview.IsOwner()) nview.ClaimOwnership();
                comps.Add(comp);
            }

            return comps;
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

            var from = _roadStart;
            var to = player.transform.position;
            var length = new Vector3(to.x - from.x, 0f, to.z - from.z).magnitude;

            if (length < 1f)
            {
                player.Message(MessageHud.MessageType.Center, "Точки слишком близко");
                return;
            }

            if (length > MaxRoadLength)
            {
                player.Message(MessageHud.MessageType.Center,
                    $"Далеко: {length:F0} м, максимум {MaxRoadLength:F0}");
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

            var comps = CompsForStamps(RoadPath, radius, from.y);
            if (comps.Count == 0)
            {
                Log.LogWarning("[AstvardServerMod] No TerrainComp for the road.");
                return;
            }

            _roadStarted = false;
            UpdateRoadHint();

            Instance?.StartCoroutine(LayPaint(new List<Vector3>(RoadPath), comps, radius,
                PaintColor(), length, width, scale, "road"));
        }

        /// <summary>
        /// A filled circle around the player — a square or a yard rather than a path.
        /// It is the same painter with a one-point path, so the distance test alone
        /// fills the disc; no ring of stamps is needed.
        /// </summary>
        private static void BuildArea()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            var area = Mathf.Clamp(ParseField(RoadAreaInput, 8f), 2f, 32f);
            var centre = player.transform.position;
            var scale = PaintGridScale(centre);

            RoadPath.Clear();
            RoadPath.Add(centre);

            var comps = CompsForStamps(RoadPath, area, centre.y);
            if (comps.Count == 0)
            {
                Log.LogWarning("[AstvardServerMod] No TerrainComp for the area.");
                return;
            }

            Instance?.StartCoroutine(LayPaint(new List<Vector3>(RoadPath), comps, area,
                PaintColor(), area, area * 2f, scale, "area"));
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
            RecordTerrainUndo(kind == "area" ? "площадка" : "дорожка", comps,
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

            _roadLaying = false;
            _roadCancelled = false;
            if (_roadPreview != null) _roadPreview.SetActive(false);

            if (!stopped)
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    kind == "area"
                        ? $"Площадка радиусом {length:F0} м"
                        : $"Дорожка {length:F0} м, ширина {width:F1} м");

            Log.LogInfo($"[AstvardServerMod] {kind} {(_roadPaved ? "paved" : "dirt")} " +
                        $"{length:F1} m width {width:F1} brush={radius:F2} grid={scale:F2} " +
                        $"nodes={path.Count} zones={comps.Count} owned={owned} verts={painted}");
        }

        /// <summary>
        /// Flattens a pad under a blueprint before it is placed, so a build meant for
        /// level ground does not end up half-buried on a slope. The footprint is taken
        /// from the rotated clipboard, so a build set down at an angle still gets a pad
        /// that covers it.
        /// </summary>
        private static void LevelUnderBuild(Vector3 origin, Quaternion rotation)
        {
            if (Clipboard.Count == 0) return;

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

            var comps = CollectTerrainComps(target, radius + blend);
            if (comps.Count == 0)
            {
                Log.LogWarning("[AstvardServerMod] No TerrainComp under the build.");
                return;
            }

            // BlendLevel reads the terrain tool's own square/circle flag. The pad is
            // always round whatever the player last levelled by hand, so the flag is
            // borrowed and put back.
            RecordTerrainUndo("площадка под постройку", comps, target, radius + blend);

            var wasSquare = _terrainSquare;
            _terrainSquare = false;

            var save = AccessTools.Method(typeof(TerrainComp), "Save");
            foreach (var comp in comps) BlendLevel(comp, target, radius, blend);
            foreach (var comp in comps) save.Invoke(comp, new object[] { false });

            _terrainSquare = wasSquare;

            RebuildHeightmaps(target, radius + blend);

            Log.LogInfo($"[AstvardServerMod] Levelled under build r={radius:F1} " +
                        $"blend={blend:F1} zones={comps.Count} at {target}");
        }

        private static void ApplyTerrainLevel()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            var radius = ParseField(RadiusInput, 8f);
            var asked = ParseField(HeightInput, 0f);
            // The heightmap only covers one 64 m zone, so anything past its edge is
            // silently clipped — the cap is generous rather than exact.
            radius = Mathf.Clamp(radius, 1f, 64f);

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

            // Each zone keeps its own heightmap, so an operation spilling over a
            // zone border has to be handed to every TerrainComp it touches —
            // otherwise the neighbour keeps its old heights and the seam tears open.
            var comps = CollectTerrainComps(target, reach);
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

        /// <summary>Every TerrainComp whose zone is touched by the given reach, created if missing.</summary>
        private static List<TerrainComp> CollectTerrainComps(Vector3 center, float reach)
        {
            var comps = new List<TerrainComp>();
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

                    var comp = TerrainComp.FindTerrainCompiler(probeAtZone)
                               ?? CreateTerrainCompiler(probeAtZone);
                    if (comp == null) continue;

                    var nview = comp.GetComponent<ZNetView>();
                    if (nview != null && !nview.IsOwner()) nview.ClaimOwnership();
                    comps.Add(comp);
                }
            }

            return comps;
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
