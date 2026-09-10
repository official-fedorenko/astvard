using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>
        /// One zone's terrain, exactly as it was. These five arrays are the whole of
        /// what a TerrainComp stores, so a copy of them is a complete record — undoing
        /// is a write-back, not a reconstruction, and it cannot drift.
        /// </summary>
        private sealed class ZoneSnapshot
        {
            public TerrainComp Comp;
            public bool[] ModifiedHeight;
            public float[] LevelDelta;
            public float[] SmoothDelta;
            public bool[] ModifiedPaint;
            public Color[] PaintMask;
        }

        private sealed class TerrainUndoStep
        {
            public string Label;
            public Vector3 Centre;
            public float Reach;
            public List<ZoneSnapshot> Zones;
        }

        // Deep enough to walk back a few mistakes, shallow enough that the copies stay
        // small: one zone is roughly a hundred kilobytes.
        private const int UndoDepth = 5;

        private static readonly List<TerrainUndoStep> UndoStack = new List<TerrainUndoStep>();

        internal static bool CanUndoTerrain
        {
            get { return UndoStack.Count > 0; }
        }

        internal static string NextUndoLabel
        {
            get { return UndoStack.Count == 0 ? "" : UndoStack[UndoStack.Count - 1].Label; }
        }

        /// <summary>
        /// Records the zones an operation is about to touch. Called before the change,
        /// which is the only moment the old state still exists anywhere.
        /// </summary>
        internal static void RecordTerrainUndo(string label, List<TerrainComp> comps,
                                               Vector3 centre, float reach)
        {
            if (comps == null || comps.Count == 0) return;

            var step = new TerrainUndoStep
            {
                Label = label,
                Centre = centre,
                Reach = reach,
                Zones = new List<ZoneSnapshot>(),
            };

            foreach (var comp in comps)
            {
                var snapshot = Capture(comp);
                if (snapshot != null) step.Zones.Add(snapshot);
            }

            if (step.Zones.Count == 0) return;

            UndoStack.Add(step);
            while (UndoStack.Count > UndoDepth) UndoStack.RemoveAt(0);

            Log.LogInfo($"[AstvardServerMod] Undo recorded: {label}, {step.Zones.Count} zones "
                        + $"({UndoStack.Count}/{UndoDepth} kept).");
        }

        private static ZoneSnapshot Capture(TerrainComp comp)
        {
            if (comp == null) return null;

            var modifiedHeight = FModified != null ? FModified.GetValue(comp) as bool[] : null;
            var levelDelta = FLevelDelta != null ? FLevelDelta.GetValue(comp) as float[] : null;
            var smoothDelta = FSmoothDelta != null ? FSmoothDelta.GetValue(comp) as float[] : null;
            var modifiedPaint = FModifiedPaint != null ? FModifiedPaint.GetValue(comp) as bool[] : null;
            var paintMask = FPaintMask != null ? FPaintMask.GetValue(comp) as Color[] : null;

            if (modifiedHeight == null || levelDelta == null || smoothDelta == null
                || modifiedPaint == null || paintMask == null) return null;

            return new ZoneSnapshot
            {
                Comp = comp,
                ModifiedHeight = (bool[])modifiedHeight.Clone(),
                LevelDelta = (float[])levelDelta.Clone(),
                SmoothDelta = (float[])smoothDelta.Clone(),
                ModifiedPaint = (bool[])modifiedPaint.Clone(),
                PaintMask = (Color[])paintMask.Clone(),
            };
        }

        internal static void UpdateUndoButtonLabel()
        {
            var label = UndoButton != null
                ? UndoButton.GetComponentInChildren<UnityEngine.UI.Text>(true)
                : null;
            // Naming what will be undone matters more here than elsewhere: the change
            // may be behind the player, or off in another zone entirely.
            if (label != null) label.text = "Откатить: " + NextUndoLabel;
        }

        internal static void UndoTerrain()
        {
            if (UndoStack.Count == 0)
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Отменять нечего");
                return;
            }

            var step = UndoStack[UndoStack.Count - 1];
            UndoStack.RemoveAt(UndoStack.Count - 1);

            var save = AccessTools.Method(typeof(TerrainComp), "Save");
            var restored = 0;

            foreach (var zone in step.Zones)
            {
                if (zone.Comp == null) continue;

                // A zone unloaded since the snapshot was taken has a fresh compiler with
                // its own arrays; writing ours back would be writing into a dead object.
                var current = FModified != null ? FModified.GetValue(zone.Comp) as bool[] : null;
                if (current == null || current.Length != zone.ModifiedHeight.Length) continue;

                FModified.SetValue(zone.Comp, zone.ModifiedHeight.Clone());
                FLevelDelta.SetValue(zone.Comp, zone.LevelDelta.Clone());
                FSmoothDelta.SetValue(zone.Comp, zone.SmoothDelta.Clone());
                FModifiedPaint.SetValue(zone.Comp, zone.ModifiedPaint.Clone());
                FPaintMask.SetValue(zone.Comp, zone.PaintMask.Clone());

                var view = zone.Comp.GetComponent<ZNetView>();
                if (view != null && view.IsValid() && !view.IsOwner()) view.ClaimOwnership();

                if (save != null) save.Invoke(zone.Comp, new object[] { false });
                restored++;
            }

            RebuildHeightmaps(step.Centre, step.Reach);

            // Say when only part of it came back. A step covers every zone the tool
            // touched, and a zone that has unloaded since is skipped - which used to be
            // reported as a clean undo, leaving the far half of a long road painted and
            // its snapshot gone. The snapshot cannot be kept for a later attempt either:
            // it holds a TerrainComp, and a zone that reloads gets a new one, so the
            // reference would stay dead however long the step waited. Being honest about
            // it is the whole fix available without redesigning the snapshot.
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                restored == 0
                    ? "Не удалось откатить: зоны выгружены"
                    : restored < step.Zones.Count
                        ? $"Откат: {step.Label} — {restored} из {step.Zones.Count} зон, "
                          + "остальные выгружены"
                        : $"Откат: {step.Label}");

            Log.LogInfo($"[AstvardServerMod] Undo '{step.Label}': {restored} of "
                        + $"{step.Zones.Count} zones restored, {UndoStack.Count} steps left.");
        }
    }
}
