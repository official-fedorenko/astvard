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

    public class HiCommand : ConsoleCommand
    {
        public override string Name => "hi";
        public override string Help => "Astvard: печатает приветствие";
        public override bool OnlyServer => true;

        public override void Run(string[] args)
        {
            Plugin.Log.LogInfo("[AstvardServerMod] hi command executed");
            Chat.instance?.AddString("Приветас мир");
        }
    }

    public class AdminUnlockCommand : ConsoleCommand
    {
        public override string Name => "astvardadmin";
        public override string Help => "Astvard: разблокировать админ-кнопки в меню (только для админов сервера)";
        public override bool OnlyServer => true;

        public override void Run(string[] args)
        {
            // OnlyServer commands only reach here if the server's own admin check
            // (adminlist.txt) let it through — same gate as devcommands/kick.
            Plugin.IsAdminUnlocked = true;
            Plugin.RefreshMenu();
            Plugin.Log.LogInfo("[AstvardServerMod] Admin unlocked via astvardadmin command");
            Chat.instance?.AddString("Astvard: админ-кнопки разблокированы.");
        }
    }

    /// <summary>
    /// Swallows the attack input while a ghost is being placed — otherwise the same
    /// left click that commits the building also swings whatever is in hand.
    /// </summary>
    /// <summary>
    /// Smelter.Spawn is the single point where a finished product materialises, for
    /// smelters, blast furnaces and charcoal kilns alike. Skipping it means the item
    /// never hits the ground, so there is nothing to clean up afterwards.
    /// </summary>
    [HarmonyPatch(typeof(Smelter), "Spawn")]
    public static class SmelterSpawnToChest
    {
        private static bool Prefix(Smelter __instance, string ore, int stack)
        {
            return !Plugin.TryCollectToChest(__instance, ore, stack);
        }
    }

    /// <summary>
    /// While assignment is armed, opening a chest marks it instead. Returning false
    /// keeps the chest closed, so the click that assigns does not also open the UI.
    /// </summary>
    [HarmonyPatch(typeof(Container), nameof(Container.Interact))]
    public static class ContainerAssignPatch
    {
        private static bool Prefix(Container __instance, Humanoid character, bool hold, ref bool __result)
        {
            if (Plugin.PendingChestAssign == null || hold) return true;
            if (character == null || character != Player.m_localPlayer) return true;

            __result = Plugin.SetChestRole(__instance, Plugin.PendingChestSupply,
                Plugin.PendingChestAssign.Value);
            return false;
        }
    }

    /// <summary>
    /// Marks a collection chest right where the player already looks, instead of
    /// floating a label in the world. The vanilla text is already localised and uses
    /// rich text, so the marker just rides along on the name line.
    /// </summary>
    [HarmonyPatch(typeof(Container), nameof(Container.GetHoverText))]
    public static class ContainerCollectHoverPatch
    {
        private static void Postfix(Container __instance, ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;

            var collect = Plugin.IsCollectChest(__instance);
            var supply = Plugin.IsSupplyChest(__instance);
            if (!collect && !supply) return;

            var roles = collect && supply ? "сбор · подача" : (collect ? "сбор" : "подача");
            var marker = " <color=#FFCC44>· " + roles + "</color>";
            var lineEnd = __result.IndexOf('\n');
            __result = lineEnd < 0
                ? __result + marker
                : __result.Substring(0, lineEnd) + marker + __result.Substring(lineEnd);
        }
    }

    /// <summary>
    /// Cooked food, burnt included: taking the burnt piece too keeps the slot free so
    /// the station carries on working while nobody is watching.
    /// </summary>
    [HarmonyPatch(typeof(CookingStation), "SpawnItem")]
    public static class CookingStationSpawnToChest
    {
        private static bool Prefix(CookingStation __instance, string name)
        {
            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(name) : null;
            return !Plugin.TryStoreNearby(__instance.transform.position, prefab, 1);
        }
    }

    /// <summary>
    /// Honey. The vanilla routine spawns the jars inline rather than through a helper,
    /// so this replaces it wholesale and clears the hive itself.
    /// </summary>
    [HarmonyPatch(typeof(Beehive), "RPC_Extract")]
    public static class BeehiveExtractToChest
    {
        private static bool Prefix(Beehive __instance)
        {
            var view = __instance.GetComponent<ZNetView>();
            if (view == null || !view.IsValid() || __instance.m_honeyItem == null) return true;

            var level = view.GetZDO().GetInt(ZDOVars.s_level);
            if (level <= 0) return true;

            if (!Plugin.TryStoreNearby(__instance.transform.position,
                    __instance.m_honeyItem.gameObject, level)) return true;

            __instance.m_spawnEffect.Create(__instance.m_spawnPoint.position, Quaternion.identity);
            view.GetZDO().Set(ZDOVars.s_level, 0);
            return false;
        }
    }

    /// <summary>
    /// Mead. DelayedTap is the pour that follows the tap animation, and it is the only
    /// place the bottles come into existence.
    /// </summary>
    [HarmonyPatch(typeof(Fermenter), "DelayedTap")]
    public static class FermenterTapToChest
    {
        private static readonly AccessTools.FieldRef<Fermenter, string> TapItem =
            AccessTools.FieldRefAccess<Fermenter, string>("m_delayedTapItem");

        private static bool Prefix(Fermenter __instance)
        {
            var content = TapItem(__instance);
            if (string.IsNullOrEmpty(content)) return true;

            Fermenter.ItemConversion conversion = null;
            foreach (var candidate in __instance.m_conversion)
            {
                if (candidate == null || candidate.m_from == null) continue;
                if (candidate.m_from.gameObject.name != content) continue;
                conversion = candidate;
                break;
            }

            if (conversion == null || conversion.m_to == null) return true;
            if (!Plugin.TryStoreNearby(__instance.transform.position,
                    conversion.m_to.gameObject, conversion.m_producedItems)) return true;

            __instance.m_spawnEffects.Create(__instance.m_outputPoint.position, Quaternion.identity);
            return false;
        }
    }

    [HarmonyPatch(typeof(Game), "Start")]
    public static class RegisterZoneRpcsOnStart
    {
        private static void Postfix() => Plugin.RegisterZoneRpcs();
    }

    /// <summary>
    /// Records which socket a routed RPC physically arrived on. The sender id carried
    /// inside the packet is written by the sender and can claim to be anyone, including
    /// the server; the socket cannot be forged. A null value means the call was raised
    /// on this machine rather than received.
    /// </summary>
    [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
    public static class RoutedSenderTracker
    {
        internal static ZRpc Current;

        private static void Prefix(ZRpc rpc) => Current = rpc;

        // A finalizer rather than a postfix: a handler that throws would otherwise
        // leave the last sender's socket standing as the answer for local calls.
        private static void Finalizer() => Current = null;
    }

    [HarmonyPatch(typeof(ZoneSystem), "Update")]
    public static class KeepZoneTerrainAlive
    {
        private static void Postfix() => Plugin.PokeKeptZones();
    }

    [HarmonyPatch(typeof(ZNetScene), "CreateObjects")]
    public static class KeepZoneObjectsLoaded
    {
        private static void Prefix(List<ZDO> currentNearObjects)
            => Plugin.AppendKeptZoneObjects(currentNearObjects);
    }

    /// <summary>
    /// Everything the player's input turns into goes through one call, so this is the
    /// place to take pieces of it away while a preview is up.
    ///
    /// The two earlier patches — on PlayerAttackInput and StartAttack — did not hold.
    /// The click that commits a build clears IsPlacing in our own Update, and MonoBehaviour
    /// order is not fixed: the game's input often ran afterwards in the same frame, saw
    /// placement already finished, and swung the axe. Hence the short window below, which
    /// outlives the frame the click happened in.
    ///
    /// Auto-run was never covered at all. Q is bound to it, and Shift+Q is our height
    /// control, so lowering a preview sent the character jogging off.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.SetControls))]
    public static class SuppressControlsWhilePlacing
    {
        private static void Prefix(Player __instance, ref bool attack, ref bool attackHold,
                                   ref bool secondaryAttack, ref bool secondaryAttackHold,
                                   ref bool autoRun)
        {
            if (__instance != Player.m_localPlayer) return;
            if (!Plugin.PlacementHoldsInput) return;

            attack = false;
            attackHold = false;
            secondaryAttack = false;
            secondaryAttackHold = false;
            autoRun = false;
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "Show")]
    public static class InventoryShowPatch
    {
        private static void Postfix()
        {
            if (Plugin.Panel != null) Plugin.Panel.SetActive(true);
            Plugin.RefreshMenu();
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "Hide")]
    public static class InventoryHidePatch
    {
        private static void Postfix()
        {
            if (Plugin.Panel != null) Plugin.Panel.SetActive(false);
            // next open starts collapsed at the root menu
            Plugin.MenuState = 0;
            Plugin.IsInfoShown = false;
            Plugin.RefreshMenu();
        }
    }
}
