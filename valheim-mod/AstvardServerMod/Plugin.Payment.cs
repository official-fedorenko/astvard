using System.Collections.Generic;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>
        /// What a player pays for a set of pieces, kept as the pieces themselves - by prefab
        /// name - so the part that does not go up can be handed back exactly: a build is
        /// paid for whole before it starts, and whatever it did not put up comes back.
        /// </summary>
        private sealed class Bill
        {
            public readonly Dictionary<string, int> Pieces = new Dictionary<string, int>();

            public void Add(string prefab, int count = 1)
            {
                if (string.IsNullOrEmpty(prefab) || count == 0) return;

                Pieces.TryGetValue(prefab, out var had);
                var now = had + count;
                if (now > 0) Pieces[prefab] = now;
                else Pieces.Remove(prefab);
            }

            public void Add(GameObject prefab, int count = 1)
            {
                if (prefab != null) Add(prefab.name, count);
            }
        }

        private static Bill ClipboardBill()
        {
            var bill = new Bill();
            foreach (var entry in Clipboard) bill.Add(entry.Prefab);
            return bill;
        }

        private static Bill PlacementBill(IEnumerable<PiecePlacement> placements)
        {
            var bill = new Bill();
            foreach (var placement in placements) bill.Add(placement.Prefab);
            return bill;
        }

        /// <summary>
        /// The resources a bill comes to, item by item - as the hammer charges: every
        /// requirement of every piece, and nothing for a piece the world builds free.
        /// </summary>
        private static Dictionary<ItemDrop, int> BillItems(Bill bill)
        {
            var items = new Dictionary<ItemDrop, int>();
            var scene = ZNetScene.instance;
            var zones = ZoneSystem.instance;
            if (scene == null || bill == null) return items;

            foreach (var entry in bill.Pieces)
            {
                var prefab = scene.GetPrefab(entry.Key);
                var piece = prefab != null ? prefab.GetComponent<Piece>() : null;
                if (piece == null) continue;
                if (zones != null && zones.GetGlobalKey(piece.FreeBuildKey())) continue;

                foreach (var requirement in piece.m_resources)
                {
                    if (requirement.m_resItem == null || requirement.m_amount <= 0) continue;

                    items.TryGetValue(requirement.m_resItem, out var had);
                    items[requirement.m_resItem] = had + requirement.m_amount * entry.Value;
                }
            }

            return items;
        }

        private static string ItemTitle(ItemDrop item)
        {
            var name = item.m_itemData.m_shared.m_name;
            return Localization.instance != null ? Localization.instance.Localize(name) : name;
        }

        /// <summary>What the player is short of for this bill, as a line to show them; null when they can pay.</summary>
        private static string BillShortfall(Player player, Bill bill)
        {
            if (player == null || player.NoCostCheat()) return null;

            var inventory = player.GetInventory();
            var missing = new System.Text.StringBuilder();
            foreach (var entry in BillItems(bill))
            {
                var have = inventory.CountItems(entry.Key.m_itemData.m_shared.m_name);
                if (have >= entry.Value) continue;

                if (missing.Length > 0) missing.Append(", ");
                missing.Append(ItemTitle(entry.Key)).Append(' ').Append(entry.Value - have);
            }

            return missing.Length > 0 ? "Не хватает: " + missing : null;
        }

        /// <summary>
        /// Takes a bill out of the player's bag, after BillShortfall has said they can pay.
        /// False when nothing was taken - the no-cost cheat - so nothing is owed back either.
        /// </summary>
        private static bool PayBill(Player player, Bill bill)
        {
            if (player == null || bill == null || player.NoCostCheat()) return false;

            var inventory = player.GetInventory();
            foreach (var entry in BillItems(bill))
                inventory.RemoveItem(entry.Key.m_itemData.m_shared.m_name, entry.Value, -1);
            return true;
        }

        /// <summary>
        /// Hands a bill back at the player's feet, where walking picks it up - the way the
        /// hammer leaves what a piece gives back, and a full bag loses nothing. In whole
        /// stacks, as the game drops them: one drop past a stack comes back overfull.
        /// </summary>
        private static void RefundBill(Bill bill)
        {
            var player = Player.m_localPlayer;
            if (player == null || bill == null || bill.Pieces.Count == 0) return;

            var at = player.transform.position + Vector3.up;
            foreach (var entry in BillItems(bill))
            {
                var stack = Mathf.Max(1, entry.Key.m_itemData.m_shared.m_maxStackSize);
                var left = entry.Value;
                while (left > 0)
                {
                    var amount = Mathf.Min(left, stack);
                    left -= amount;

                    var item = entry.Key.m_itemData.Clone();
                    item.m_dropPrefab = entry.Key.gameObject;
                    ItemDrop.DropItem(item, amount, at, Quaternion.identity);
                }
            }
        }
    }
}
