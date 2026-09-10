using System.Collections.Generic;
using Jotunn.Managers;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        internal static GameObject BuildUndoButton;

        private const string BuildUndoLabel = "Отменить постройку";

        /// <summary>
        /// The last thing put up from «Постройки» - a blueprint, a copy, a spawner, a
        /// fence, a floor - with every piece it put up, for «Отменить постройку».
        /// </summary>
        private sealed class BuildRecord
        {
            public string Label;

            public readonly List<ZDOID> Pieces = new List<ZDOID>();

            /// <summary>The levelling it came with, if any: undone with it while still the last change to the ground.</summary>
            public TerrainUndoStep Ground;

            public bool Running;

            /// <summary>Asked to stop while going up; the build sees it at its next piece.</summary>
            public bool Cancelled;

            /// <summary>Paid for out of a player's bag: taking it down gives the materials back.</summary>
            public bool Paid;

            /// <summary>The build before, kept only until this one turns out to have put something up.</summary>
            public BuildRecord Previous;
        }

        private static BuildRecord _lastBuild;

        private static float _buildUndoArmedAt;

        private void CreateBuildUndoWidget(GUIManager gui)
        {
            BuildUndoButton = MakeButton(gui, BuildUndoLabel, PressBuildUndo);
        }

        internal static bool CanUndoBuild
        {
            get { return _lastBuild != null && (_lastBuild.Running || _lastBuild.Pieces.Count > 0); }
        }

        internal static bool BuildGoingUp
        {
            get { return _lastBuild != null && _lastBuild.Running; }
        }

        /// <summary>Every page under «Постройки».</summary>
        private static bool IsBuildPage(int state)
        {
            return state == StateBuild || state == StateCopyForm
                   || state == StateTemplates || state == StateTemplateList || state == StateTemplateEdit
                   || state == StateSharedList || state == StateSharedItem
                   || state == StateSpawners || state == StateSpawnerList
                   || state == StateFence || state == StateAreaFill;
        }

        /// <summary>
        /// Starts the record of a build about to go up. One record is kept, so the build
        /// before it can no longer be taken down from here - the hammer still can.
        /// </summary>
        private static BuildRecord BeginBuild(string label)
        {
            // A confirmation given for the old build must not take down the new one.
            DisarmBuildUndo();

            var previous = _lastBuild;
            if (previous != null) previous.Previous = null;
            _lastBuild = new BuildRecord { Label = label, Running = true, Previous = previous };
            return _lastBuild;
        }

        private static void EndBuild(BuildRecord record)
        {
            record.Running = false;

            // A build that put nothing up - every side of a fence blocked, say - hands the
            // button back to the one before, rather than keeping a button with nothing
            // behind it.
            if (record.Pieces.Count == 0 && _lastBuild == record) _lastBuild = record.Previous;
            record.Previous = null;

            RefreshMenu();
        }

        /// <summary>
        /// One build at a time. The button stops the build it knows about, and a second
        /// one started alongside would take its place there, leaving the first going up
        /// with nothing able to stop it.
        /// </summary>
        private static bool RefuseWhileBuilding()
        {
            if (!BuildGoingUp) return false;

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                $"Подожди: ещё строится {_lastBuild.Label}");
            return true;
        }

        /// <summary>
        /// «Отменить постройку». A build still going up stops at once - that is what the
        /// button is for. A finished one asks first: it can be a base of a thousand
        /// pieces, and nothing brings it back.
        /// </summary>
        private static void PressBuildUndo()
        {
            var record = _lastBuild;
            if (record == null) return;

            if (record.Running)
            {
                // Only asked to stop: the build sees it at its next piece and takes down
                // what it had put up itself, so the two never race over the same pieces.
                record.Cancelled = true;
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Останавливаю постройку…");
                return;
            }

            if (!BuildUndoArmed)
            {
                _buildUndoArmedAt = Time.realtimeSinceStartup;
                SetLabel(BuildUndoButton, "Точно? Нажми ещё раз");
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    $"Отменить: {record.Label}? Деталей: {record.Pieces.Count}");
                return;
            }

            DisarmBuildUndo();
            TakeDownBuild(record);
            RefreshMenu();
        }

        private static bool BuildUndoArmed
        {
            get { return _buildUndoArmedAt > 0f && Time.realtimeSinceStartup - _buildUndoArmedAt <= 5f; }
        }

        internal static void DisarmBuildUndo()
        {
            _buildUndoArmedAt = 0f;
            SetLabel(BuildUndoButton, BuildUndoLabel);
        }

        /// <summary>
        /// Takes a build down, and - when the levelling it came with is still the last
        /// change to the ground - puts the ground back with it, through undo, whose step
        /// holds these very pieces. Levelling buried under later work stays: undo only
        /// goes back in order, and jumping the queue would undo someone else's ground.
        /// As everywhere, only what is still loaded here can go.
        /// </summary>
        private static void TakeDownBuild(BuildRecord record)
        {
            if (_lastBuild == record) _lastBuild = null;

            var total = record.Pieces.Count;
            if (record.Ground != null && UndoStack.Count > 0 && UndoStack[UndoStack.Count - 1] == record.Ground)
            {
                UndoTerrain();
                Log.LogInfo($"[AstvardServerMod] Build '{record.Label}' undone with its ground: {total} pieces.");
                return;
            }

            var refund = record.Paid ? new Bill() : null;
            var removed = RemovePieces(record.Pieces, refund);
            RefundBill(refund);
            var note = (record.Paid ? ", материалы под ногами" : "")
                       + (record.Ground != null ? ", выровненная земля осталась" : "");
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                removed == total
                    ? $"Отменено: {record.Label}, деталей убрано {removed}{note}"
                    : $"Отменено: {record.Label}, убрано {removed} из {total} — остальные далеко или уже разобраны{note}");
            Log.LogInfo($"[AstvardServerMod] Build '{record.Label}' undone: {removed} of {total} pieces.");
        }
    }
}
