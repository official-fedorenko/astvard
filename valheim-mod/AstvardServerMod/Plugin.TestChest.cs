using System.Collections.Generic;
using Jotunn.Managers;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        internal static GameObject TestChestButton;

        /// <summary>
        /// Сундук с разным добром — чтобы было на чём проверить сортировку.
        ///
        /// Filling a chest by hand to see whether the sorter divides it properly takes
        /// longer than the thing being tested, and a hand-filled chest is different every
        /// time, so two runs cannot be compared. This one is always the same: piles worth a
        /// chest of their own, ores worth a slot, a little food and a trophy - enough for
        /// every rule in the plan to have something to do.
        ///
        /// It is left unmarked on purpose: an unmarked chest is a source, so standing it in
        /// a zone is the whole test.
        /// </summary>
        private static readonly string[] TestChestPrefabs =
        {
            // The iron one first: it has the slots for all of this. The others are there in
            // case a build of the game calls it something else.
            "piece_chest", "piece_chest_private", "piece_chest_wood"
        };

        /// <summary>
        /// What goes in, and how much. The amounts are chosen against the threshold: wood and
        /// stone are piles that earn a chest each, the ores are a slot apiece and belong with
        /// the odds and ends, and the rest is there to land in other categories.
        /// </summary>
        private static readonly KeyValuePair<string, int>[] TestChestItems =
        {
            new KeyValuePair<string, int>("Wood", 500),
            new KeyValuePair<string, int>("Stone", 200),
            new KeyValuePair<string, int>("CopperOre", 30),
            new KeyValuePair<string, int>("TinOre", 2),
            new KeyValuePair<string, int>("Coal", 40),
            new KeyValuePair<string, int>("Mushroom", 20),
            new KeyValuePair<string, int>("Raspberry", 15),
            new KeyValuePair<string, int>("ArrowWood", 40),
            new KeyValuePair<string, int>("TrophyDeer", 1),
        };

        private void CreateTestChestWidget(GUIManager gui)
        {
            TestChestButton = MakeButton(gui, "Сундук для проверки", GiveTestChest);
        }

        /// <summary>
        /// Puts the chest down two metres ahead and fills it. Anything the game does not
        /// know by that name is skipped and named in the log - the same way the food sets
        /// do it, and for the same reason: two thirds of a test beats none, and the log is
        /// the only place anybody would find out the game had renamed something.
        /// </summary>
        internal static void GiveTestChest()
        {
            var player = Player.m_localPlayer;
            var scene = ZNetScene.instance;
            if (player == null || scene == null || ObjectDB.instance == null) return;

            GameObject prefab = null;
            foreach (var name in TestChestPrefabs)
            {
                prefab = scene.GetPrefab(name);
                if (prefab != null) break;
            }

            if (prefab == null)
            {
                player.Message(MessageHud.MessageType.Center, "Сундук не нашёлся — смотри лог");
                Log.LogWarning($"[AstvardServerMod] Test chest: none of {string.Join(", ", TestChestPrefabs)} exists.");
                return;
            }

            var where = player.transform.position + player.transform.forward * 2f;
            var chest = Object.Instantiate(prefab, where, player.transform.rotation);

            var view = chest.GetComponent<ZNetView>();
            if (view != null && view.IsValid() && !view.IsOwner()) view.ClaimOwnership();

            var container = chest.GetComponentInChildren<Container>();
            if (container == null)
            {
                Log.LogWarning("[AstvardServerMod] Test chest: the prefab has no Container.");
                return;
            }

            var inventory = container.GetInventory();
            var put = new List<string>();
            var missing = new List<string>();
            var full = new List<string>();

            foreach (var pair in TestChestItems)
            {
                var item = ObjectDB.instance.GetItemPrefab(pair.Key);
                if (item == null)
                {
                    missing.Add(pair.Key);
                    continue;
                }

                // Asked before adding: a chest that runs out halfway should say so rather
                // than quietly hand over less than the test needs.
                if (!inventory.CanAddItem(item, pair.Value))
                {
                    full.Add(pair.Key);
                    continue;
                }

                if (inventory.AddItem(item, pair.Value)) put.Add($"{pair.Key} x{pair.Value}");
                else full.Add(pair.Key);
            }

            Log.LogInfo($"[AstvardServerMod] Test chest ({prefab.name}): put {string.Join(", ", put.ToArray())}"
                        + (missing.Count > 0 ? $"; unknown: {string.Join(", ", missing.ToArray())}" : "")
                        + (full.Count > 0 ? $"; did not fit: {string.Join(", ", full.ToArray())}" : ""));

            player.Message(MessageHud.MessageType.Center,
                full.Count > 0 || missing.Count > 0
                    ? $"Сундук поставлен: {put.Count} видов, {full.Count + missing.Count} не влезло"
                    : $"Сундук поставлен: {put.Count} видов добра");

            InventoryGui.instance?.Hide();
        }
    }
}
