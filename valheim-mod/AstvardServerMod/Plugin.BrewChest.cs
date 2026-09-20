using System.Collections.Generic;
using Jotunn.Managers;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        internal static GameObject BrewChestButton;

        /// <summary>
        /// Сундук с составляющими для тех медовух, которые этот персонаж уже открыл.
        ///
        /// The brewing half cannot be tried at all without a shelf of honey and berries,
        /// and gathering those by hand takes longer than the thing being tested. What goes
        /// in is not written here: it is read off the recipes of the brews the character
        /// actually knows, so a fresh character gets what a fresh character can brew, and a
        /// veteran gets his own list. A list written in the mod would have been a guess
        /// about somebody else's progress.
        ///
        /// Оставляем непомеченным, как и «Сундук для проверки»: помеченный сундук — это
        /// приёмник, а нам нужен источник. Сортировщик разнесёт добро по полкам сам, и
        /// варка возьмёт его уже оттуда.
        /// </summary>
        private const int BrewChestBarrels = 5;

        private void CreateBrewChestWidget(GUIManager gui)
        {
            BrewChestButton = MakeButton(gui, "Сундук для медовух", GiveBrewChest);
        }

        internal static void GiveBrewChest()
        {
            var player = Player.m_localPlayer;
            var scene = ZNetScene.instance;
            if (player == null || scene == null || ObjectDB.instance == null) return;

            var brews = KnownBrews();
            if (brews.Count == 0)
            {
                player.Message(MessageHud.MessageType.Center,
                    "Этот персонаж ещё не открыл ни одной основы");
                return;
            }

            // Складываем потребность всех известных основ в одну: у медовух общий мёд, и
            // десять отдельных стопок мёда заняли бы сундук вместо ягод.
            var need = new Dictionary<string, int>();
            foreach (var brew in brews)
            {
                if (brew == null || brew.Recipe == null || brew.Recipe.m_resources == null) continue;

                foreach (var res in brew.Recipe.m_resources)
                {
                    if (res == null || res.m_resItem == null || res.m_amount <= 0) continue;

                    var name = res.m_resItem.gameObject.name;
                    int had;
                    need[name] = (need.TryGetValue(name, out had) ? had : 0)
                                 + res.m_amount * BrewChestBarrels;
                }
            }

            if (need.Count == 0)
            {
                player.Message(MessageHud.MessageType.Center, "У этих основ нет состава — смотри лог");
                Log.LogWarning("[AstvardServerMod] Brew chest: the known brews have no resources.");
                return;
            }

            GameObject prefab = null;
            foreach (var name in TestChestPrefabs)
            {
                prefab = scene.GetPrefab(name);
                if (prefab != null) break;
            }

            if (prefab == null)
            {
                player.Message(MessageHud.MessageType.Center, "Сундук не нашёлся — смотри лог");
                Log.LogWarning($"[AstvardServerMod] Brew chest: none of {string.Join(", ", TestChestPrefabs)} exists.");
                return;
            }

            var where = player.transform.position + player.transform.forward * 2f;
            var chest = Object.Instantiate(prefab, where, player.transform.rotation);

            var view = chest.GetComponent<ZNetView>();
            if (view != null && view.IsValid() && !view.IsOwner()) view.ClaimOwnership();

            var container = chest.GetComponentInChildren<Container>();
            if (container == null)
            {
                Log.LogWarning("[AstvardServerMod] Brew chest: the prefab has no Container.");
                return;
            }

            var inventory = container.GetInventory();
            var put = new List<string>();
            var missing = new List<string>();
            var full = new List<string>();

            foreach (var pair in need)
            {
                var item = ObjectDB.instance.GetItemPrefab(pair.Key);
                if (item == null)
                {
                    missing.Add(pair.Key);
                    continue;
                }

                // Спрошено до укладки: сундук, кончившийся на полпути, должен сказать об
                // этом, а не тихо выдать меньше, чем нужно на проверку.
                if (!inventory.CanAddItem(item, pair.Value))
                {
                    full.Add(pair.Key);
                    continue;
                }

                if (inventory.AddItem(item, pair.Value)) put.Add($"{pair.Key} x{pair.Value}");
                else full.Add(pair.Key);
            }

            Log.LogInfo($"[AstvardServerMod] Brew chest ({prefab.name}): {brews.Count} brews known, "
                        + $"{BrewChestBarrels} barrels each; put {string.Join(", ", put.ToArray())}"
                        + (missing.Count > 0 ? $"; unknown: {string.Join(", ", missing.ToArray())}" : "")
                        + (full.Count > 0 ? $"; did not fit: {string.Join(", ", full.ToArray())}" : ""));

            player.Message(MessageHud.MessageType.Center,
                full.Count > 0 || missing.Count > 0
                    ? $"Сундук поставлен: {put.Count} видов, {full.Count + missing.Count} не влезло"
                    : $"Сундук поставлен: состав на {BrewChestBarrels} бочек каждой из {brews.Count}");

            InventoryGui.instance?.Hide();
        }
    }
}
