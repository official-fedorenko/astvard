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

        internal static GameObject AutoCollectHint;

        internal static GameObject AutoCollectButton;

        internal static GameObject AssignChestButton;

        internal static GameObject UnassignChestButton;

        internal static GameObject FillCategoryButton;

        internal static GameObject CollectCategoryButton;

        internal static GameObject FillHint;

        internal static GameObject FillButton;

        internal static GameObject FillAssignButton;

        internal static GameObject FillUnassignButton;

        // null = not armed, true = arm assign, false = arm unassign.
        internal static bool? PendingChestAssign;

        // Which role the armed button is about: supply chest or collection chest.
        internal static bool PendingChestSupply;

        internal static bool IsAutoCollectEnabled
        {
            get { return _autoCollect != null && _autoCollect.Value; }
            set { if (_autoCollect != null) _autoCollect.Value = value; }
        }

        internal static bool IsAutoFillEnabled
        {
            get { return _autoFill != null && _autoFill.Value; }
            set { if (_autoFill != null) _autoFill.Value = value; }
        }

        private static void UpdateAutoCollectButtonLabel()
        {
            var label = AutoCollectButton != null ? AutoCollectButton.GetComponentInChildren<Text>() : null;
            if (label != null) label.text = IsAutoCollectEnabled ? "Сбор в сундук: вкл" : "Сбор в сундук: выкл";
        }

        private static void UpdateFillButtonLabel()
        {
            var label = FillButton != null ? FillButton.GetComponentInChildren<Text>() : null;
            if (label != null) label.text = IsAutoFillEnabled ? "Наполнение: вкл" : "Наполнение: выкл";
        }

        private const float AutoCollectRadius = 8f;

        // An assigned chest is a deliberate choice, so it reaches further than the
        // "whatever is closest" fallback.
        private const float AssignedChestRadius = 24f;

        // Lives in the chest's own ZDO, so the assignment is part of the world: it
        // survives a relog and every player sees the same chest, not only whoever set it.
        private const string CollectChestKey = "astvard_collect";

        private const string SupplyChestKey = "astvard_supply";

        private const float HarvestScanRadius = 64f;

        internal static bool IsCollectChest(Container container)
        {
            return HasChestFlag(container, CollectChestKey);
        }

        internal static bool IsSupplyChest(Container container)
        {
            return HasChestFlag(container, SupplyChestKey);
        }

        private static bool HasChestFlag(Container container, string key)
        {
            if (container == null) return false;
            var view = container.GetComponent<ZNetView>();
            return view != null && view.IsValid() && view.GetZDO().GetBool(key);
        }

        /// <summary>
        /// Gives a chest one of its two roles, or takes it away. Returns false when the
        /// chest cannot be written to, so the caller can let it open as usual.
        /// A chest may hold both roles at once — nothing stops a barrel of coal from
        /// also being where the coal ends up.
        /// </summary>
        internal static bool SetChestRole(Container container, bool supply, bool enabled)
        {
            PendingChestAssign = null;

            var view = container != null ? container.GetComponent<ZNetView>() : null;
            if (view == null || !view.IsValid()) return false;

            // The flag only sticks if the owner writes it, same as the inventory itself.
            if (!view.IsOwner()) view.ClaimOwnership();

            var key = supply ? SupplyChestKey : CollectChestKey;
            var was = view.GetZDO().GetBool(key);
            view.GetZDO().Set(key, enabled);

            string message;
            if (was == enabled)
                message = supply
                    ? (enabled ? "Из этого сундука уже берётся сырьё" : "Из этого сундука и так не берут")
                    : (enabled ? "Этот сундук уже для сбора" : "Этот сундук и так не для сбора");
            else
                message = supply
                    ? (enabled ? "Сундук назначен на подачу" : "Подача с сундука снята")
                    : (enabled ? "Сундук назначен для сбора" : "Сундук отвязан");

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, message);
            Log.LogInfo("[AstvardServerMod] Chest " + (supply ? "supply " : "collect ")
                        + (enabled ? "set" : "cleared") + ".");
            return true;
        }

        /// <summary>
        /// Puts a produced item into a chest near <paramref name="origin"/>. Returns
        /// false when nothing could take it, so every caller can fall back to the
        /// vanilla behaviour of dropping it on the ground.
        /// </summary>
        internal static bool TryStoreNearby(Vector3 origin, GameObject prefab, int amount)
        {
            if (prefab == null || amount <= 0) return false;

            // An assigned chest is a decision made in the world, so it works for
            // whoever happens to be nearby. The personal toggle only governs the
            // guess-the-nearest-chest fallback.
            var target = FindChest(origin, AssignedChestRadius, prefab, amount, true)
                         ?? (IsAutoCollectEnabled
                             ? FindChest(origin, AutoCollectRadius, prefab, amount, false)
                             : null);
            if (target == null) return false;

            // A container saves itself to its ZDO only from the owner's side, so adding
            // to one we do not own would live in local memory and vanish on reload.
            var targetView = target.GetComponent<ZNetView>();
            if (!targetView.IsOwner()) targetView.ClaimOwnership();

            return target.GetInventory().AddItem(prefab, amount);
        }

        private static readonly System.Reflection.MethodInfo MSpawnProcessed =
            AccessTools.Method(typeof(Smelter), "SpawnProcessed");

        /// <summary>
        /// Tips out what the smelter is still holding back.
        ///
        /// A stacking smelter does not hand over each bar as it is made. QueueProcessed
        /// counts them up in its own ZDO and only calls Spawn once the count reaches the
        /// item's max stack size - fifty coal, say - so the patch that routes Spawn into
        /// a chest never sees the remainder. It sits inside the kiln looking like nothing
        /// was produced, which is exactly what "it does not take everything" means.
        ///
        /// SpawnProcessed is the game's own way of emptying that counter, and it goes
        /// through Spawn, so calling it here needs no new path into the chest: whatever
        /// was being held simply arrives one sweep later.
        /// </summary>
        private static void FlushSmelter(Smelter smelter)
        {
            if (MSpawnProcessed == null || !smelter.m_spawnStack) return;

            var view = smelter.GetComponent<ZNetView>();
            if (view == null || !view.IsValid()) return;

            // The counter lives in the ZDO, and only its owner may write there.
            if (!view.IsOwner()) return;
            if (view.GetZDO().GetInt(ZDOVars.s_spawnAmount) <= 0) return;

            MSpawnProcessed.Invoke(smelter, null);
        }

        /// <summary>
        /// Routes a smelter's finished product into a nearby chest. Covers smelters,
        /// blast furnaces and charcoal kilns alike — the game gives them all the same
        /// Smelter component.
        /// </summary>
        internal static bool TryCollectToChest(Smelter smelter, string ore, int stack)
        {
            if (smelter == null || stack <= 0) return false;

            var product = FindProduct(smelter, ore);
            if (!TryStoreNearby(smelter.transform.position, product, stack)) return false;

            // Spawn() plays this before dropping the item; keep the cue so the player
            // still gets the usual feedback that something came out.
            smelter.m_produceEffects.Create(smelter.transform.position, smelter.transform.rotation);
            return true;
        }

        private static Container FindChest(Vector3 origin, float radius, GameObject product,
                                           int stack, bool assignedOnly)
        {
            var pieces = new List<Piece>();
            Piece.GetAllPiecesInRadius(origin, radius, pieces);

            Container best = null;
            var bestSqr = float.MaxValue;

            foreach (var piece in pieces)
            {
                if (piece == null) continue;

                var container = piece.GetComponentInChildren<Container>();
                if (container == null) continue;
                if (assignedOnly != IsCollectChest(container)) continue;

                // Writing into a chest somebody has open is a reliable way to desync it.
                if (container.IsInUse()) continue;

                var view = container.GetComponent<ZNetView>();
                if (view == null || !view.IsValid()) continue;
                if (!container.GetInventory().CanAddItem(product, stack)) continue;

                // Sorting the same goods together beats raw proximity: with a chest
                // by the kiln and another by the kitchen, coal and food stop mixing.
                var sqr = (container.transform.position - origin).sqrMagnitude;
                if (!AlreadyHolds(container, product)) sqr += SortingBias;
                if (sqr >= bestSqr) continue;

                best = container;
                bestSqr = sqr;
            }

            return best;
        }

        // Far enough to outweigh any distance inside the search radius.
        private const float SortingBias = 1e6f;

        private static bool AlreadyHolds(Container container, GameObject prefab)
        {
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null) return false;
            return container.GetInventory().HaveItem(drop.m_itemData.m_shared.m_name);
        }

        private static readonly System.Reflection.MethodInfo MCookingFreeSlot =
            AccessTools.Method(typeof(CookingStation), "GetFreeSlot");

        private static readonly List<string> AcceptScratch = new List<string>();

        /// <summary>
        /// One sweep a second drives both halves: producers that only hand their goods
        /// over when asked, and stations waiting to be fed. Each vanilla "take" routine
        /// already refuses to act when nothing is ready, so they can be asked blindly.
        /// </summary>
        private static IEnumerator AutomationLoop()
        {
            var pieces = new List<Piece>();
            var collectSpots = new List<Vector3>();
            var supplyChests = new List<Container>();
            var anyChests = new List<Container>();

            while (true)
            {
                yield return new WaitForSeconds(1f);

                var player = Player.m_localPlayer;
                if (player == null) continue;

                pieces.Clear();
                Piece.GetAllPiecesInRadius(player.transform.position, HarvestScanRadius, pieces);

                // Sort the chests out of the same sweep. Asking per station would mean
                // re-walking every loaded piece once for each of them.
                collectSpots.Clear();
                supplyChests.Clear();
                anyChests.Clear();
                foreach (var piece in pieces)
                {
                    if (piece == null) continue;
                    var container = piece.GetComponentInChildren<Container>();
                    if (container == null) continue;

                    anyChests.Add(container);
                    if (IsCollectChest(container)) collectSpots.Add(container.transform.position);
                    if (IsSupplyChest(container)) supplyChests.Add(container);
                }

                // With no supply chest and the toggle off there is nothing to feed
                // from, so skip the reads and reflection the feeding half would do.
                var feeding = IsAutoFillEnabled || supplyChests.Count > 0;

                foreach (var piece in pieces)
                {
                    if (piece == null) continue;

                    var cooking = piece.GetComponentInChildren<CookingStation>();
                    if (cooking != null)
                    {
                        if (MayHarvest(cooking, collectSpots))
                            cooking.GetComponent<ZNetView>()
                                .InvokeRPC("RPC_RemoveDoneItem", player.transform.position, 1);
                        if (feeding) FillCooking(cooking, supplyChests, anyChests);
                    }

                    var beehive = piece.GetComponentInChildren<Beehive>();
                    if (beehive != null && MayHarvest(beehive, collectSpots))
                        beehive.GetComponent<ZNetView>().InvokeRPC("RPC_Extract");

                    var fermenter = piece.GetComponentInChildren<Fermenter>();
                    if (fermenter != null)
                    {
                        if (MayHarvest(fermenter, collectSpots))
                            fermenter.GetComponent<ZNetView>().InvokeRPC("RPC_Tap");
                        if (feeding) FillFermenter(fermenter, supplyChests, anyChests);
                    }

                    var smelter = piece.GetComponentInChildren<Smelter>();
                    if (smelter != null)
                    {
                        if (feeding) FillSmelter(smelter, supplyChests, anyChests);
                        FlushSmelter(smelter);
                    }

                    var fireplace = piece.GetComponentInChildren<Fireplace>();
                    if (fireplace != null && feeding) FillFireplace(fireplace, supplyChests, anyChests);
                }
            }
        }

        /// <summary>
        /// Only the owner runs a producer's logic. The chest check matters too: without
        /// somewhere to put the goods, harvesting unattended would just tip them onto
        /// the ground, which is worse than leaving them where they are.
        /// </summary>
        private static bool MayHarvest(Component producer, List<Vector3> assignedChests)
        {
            if (!OwnedAndValid(producer)) return false;
            if (IsAutoCollectEnabled) return true;

            var origin = producer.transform.position;
            var range = AssignedChestRadius * AssignedChestRadius;
            foreach (var chest in assignedChests)
                if ((chest - origin).sqrMagnitude <= range) return true;

            return false;
        }

        private static bool OwnedAndValid(Component producer)
        {
            var view = producer.GetComponent<ZNetView>();
            return view != null && view.IsValid() && view.IsOwner();
        }

        // ---------------- feeding ----------------

        /// <summary>
        /// Removes one unit of anything the station accepts from a chest, and reports
        /// which prefab it was. A chest explicitly put on supply duty is tried first and
        /// reaches further; the personal toggle only enables the guess-the-nearest pass.
        /// </summary>
        private static string TakeSupply(Vector3 origin, List<Container> supplyChests,
                                         List<Container> anyChests, List<string> accepted)
        {
            if (accepted.Count == 0) return null;

            return TakeFrom(origin, supplyChests, AssignedChestRadius, accepted)
                   ?? (IsAutoFillEnabled ? TakeFrom(origin, anyChests, AutoCollectRadius, accepted) : null);
        }

        private static string TakeFrom(Vector3 origin, List<Container> chests,
                                       float radius, List<string> accepted)
        {
            var range = radius * radius;
            Container bestChest = null;
            ItemDrop.ItemData bestItem = null;
            var bestSqr = float.MaxValue;

            foreach (var container in chests)
            {
                if (container == null || container.IsInUse()) continue;

                var sqr = (container.transform.position - origin).sqrMagnitude;
                if (sqr > range || sqr >= bestSqr) continue;

                var view = container.GetComponent<ZNetView>();
                if (view == null || !view.IsValid()) continue;

                foreach (var item in container.GetInventory().GetAllItems())
                {
                    if (item == null || item.m_dropPrefab == null) continue;
                    if (!accepted.Contains(item.m_dropPrefab.name)) continue;

                    bestChest = container;
                    bestItem = item;
                    bestSqr = sqr;
                    break;
                }
            }

            if (bestChest == null) return null;

            // Same rule as storing: only the owner's write reaches the ZDO.
            var bestView = bestChest.GetComponent<ZNetView>();
            if (!bestView.IsOwner()) bestView.ClaimOwnership();

            var name = bestItem.m_dropPrefab.name;
            return bestChest.GetInventory().RemoveItem(bestItem, 1) ? name : null;
        }

        private static void FillSmelter(Smelter smelter, List<Container> supply, List<Container> any)
        {
            if (!OwnedAndValid(smelter)) return;

            var view = smelter.GetComponent<ZNetView>();
            var zdo = view.GetZDO();
            var origin = smelter.transform.position;

            if (smelter.m_maxFuel > 0 && smelter.m_fuelItem != null &&
                zdo.GetFloat(ZDOVars.s_fuel) <= smelter.m_maxFuel - 1)
            {
                AcceptScratch.Clear();
                AcceptScratch.Add(smelter.m_fuelItem.gameObject.name);
                if (TakeSupply(origin, supply, any, AcceptScratch) != null)
                    view.InvokeRPC("RPC_AddFuel");
            }

            if (zdo.GetInt(ZDOVars.s_queued) < smelter.m_maxOre)
            {
                AcceptScratch.Clear();
                foreach (var conversion in smelter.m_conversion)
                    if (conversion != null && conversion.m_from != null)
                        AcceptScratch.Add(conversion.m_from.gameObject.name);

                var ore = TakeSupply(origin, supply, any, AcceptScratch);
                // 1.0 added a trailing cheated flag to RPC_AddOre. Sending the old
                // single argument made the receiver read a bool past the end of the
                // package: an EndOfStreamException thrown straight into this coroutine,
                // which killed every automation sweep for the rest of the session.
                // The ore came out of a chest, so it is not cheated.
                if (ore != null) view.InvokeRPC("RPC_AddOre", ore, false);
            }
        }

        private static void FillCooking(CookingStation cooking, List<Container> supply, List<Container> any)
        {
            if (!OwnedAndValid(cooking)) return;

            var view = cooking.GetComponent<ZNetView>();
            var origin = cooking.transform.position;

            if (cooking.m_useFuel && cooking.m_fuelItem != null &&
                view.GetZDO().GetFloat(ZDOVars.s_fuel) <= cooking.m_maxFuel - 1)
            {
                AcceptScratch.Clear();
                AcceptScratch.Add(cooking.m_fuelItem.gameObject.name);
                if (TakeSupply(origin, supply, any, AcceptScratch) != null)
                    view.InvokeRPC("RPC_AddFuel");
            }

            if (MCookingFreeSlot == null) return;
            if ((int)MCookingFreeSlot.Invoke(cooking, null) == -1) return;

            AcceptScratch.Clear();
            foreach (var conversion in cooking.m_conversion)
                if (conversion != null && conversion.m_from != null)
                    AcceptScratch.Add(conversion.m_from.gameObject.name);

            var raw = TakeSupply(origin, supply, any, AcceptScratch);
            // Same trailing cheated flag as the smelter, same crash without it.
            if (raw != null) view.InvokeRPC("RPC_AddItem", raw, false);
        }

        private static void FillFermenter(Fermenter fermenter, List<Container> supply, List<Container> any)
        {
            if (!OwnedAndValid(fermenter)) return;

            var view = fermenter.GetComponent<ZNetView>();
            // Status.Empty is exactly "no content stored", so the ZDO answers this
            // without reaching for the private enum. 1.0 stores that content as the
            // stable hash of the prefab name rather than the name, and ZDO keeps ints
            // and strings in separate stores - so the old GetString returned "" for
            // every fermenter, full or empty, and this guard never once fired.
            if (view.GetZDO().GetInt(ZDOVars.s_content) != 0) return;

            AcceptScratch.Clear();
            foreach (var conversion in fermenter.m_conversion)
                if (conversion != null && conversion.m_from != null)
                    AcceptScratch.Add(conversion.m_from.gameObject.name);

            var brew = TakeSupply(fermenter.transform.position, supply, any, AcceptScratch);
            // Register<int, bool> in 1.0, not <string>. Sending the name made the
            // receiver read four bytes of UTF-8 as a hash, reject the unknown item,
            // and leave the brew destroyed - TakeSupply had already removed it from
            // the chest. Hash it the way the game does, and say it is not cheated.
            if (brew != null) view.InvokeRPC("RPC_AddItem", brew.GetStableHashCode(), false);
        }

        private static void FillFireplace(Fireplace fireplace, List<Container> supply, List<Container> any)
        {
            if (!OwnedAndValid(fireplace) || fireplace.m_fuelItem == null) return;
            if (fireplace.m_infiniteFuel) return;

            var view = fireplace.GetComponent<ZNetView>();
            if (view.GetZDO().GetFloat(ZDOVars.s_fuel) > fireplace.m_maxFuel - 1f) return;

            AcceptScratch.Clear();
            AcceptScratch.Add(fireplace.m_fuelItem.gameObject.name);
            if (TakeSupply(fireplace.transform.position, supply, any, AcceptScratch) != null)
                view.InvokeRPC("RPC_AddFuel");
        }

        private static GameObject FindProduct(Smelter smelter, string ore)
        {
            foreach (var conversion in smelter.m_conversion)
            {
                if (conversion == null || conversion.m_from == null) continue;
                if (conversion.m_from.gameObject.name != ore) continue;
                return conversion.m_to != null ? conversion.m_to.gameObject : null;
            }
            return null;
        }

        /// <summary>Reads the preview's own snap points, in root-local space.</summary>
        private static void CollectGhostSnapPoints()
        {
            GhostSnapLocal.Clear();
            SnapGrid.Clear();
            _snapCacheTime = float.NegativeInfinity;
            _ghostRadius = 1f;
            if (GhostRoot == null) return;

            var root = GhostRoot.transform;
            var points = new List<Transform>();

            foreach (var ghost in Ghosts)
            {
                if (ghost == null) continue;

                // Piece.GetSnapPoints only walks tagged child transforms, so it
                // still works on a preview whose components are all switched off.
                var piece = ghost.GetComponent<Piece>();
                if (piece != null)
                {
                    points.Clear();
                    piece.GetSnapPoints(points);
                    foreach (var point in points)
                        if (point != null) GhostSnapLocal.Add(root.InverseTransformPoint(point.position));
                }

                _ghostRadius = Mathf.Max(_ghostRadius, ghost.transform.localPosition.magnitude);
            }

            DedupePoints(GhostSnapLocal);
            Log.LogInfo($"[AstvardServerMod] Preview snap points: {GhostSnapLocal.Count}, radius {_ghostRadius:F1}");
        }
    }
}
