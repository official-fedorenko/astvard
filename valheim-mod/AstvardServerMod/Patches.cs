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

    public class AdminUnlockCommand : ConsoleCommand
    {
        public override string Name => "astvardadmin";
        public override string Help => "Astvard: разблокировать админ-кнопки в меню (только для админов сервера)";

        public override void Run(string[] args)
        {
            // This used to be OnlyServer, on the assumption that the flag was the admin
            // check. It is not: Terminal.ConsoleCommand.IsValid answers OnlyServer with
            // ZNet.IsServer(), which is false on every client, so the command was simply
            // refused - it only ever ran because ServerDevcommands relayed it to the
            // server. The rights question is now asked where it belongs, over our own
            // RPC, against adminlist.txt.
            Plugin.RequestAdmin();
            Chat.instance?.AddString("Astvard: спрашиваю сервер…");
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

            // Vanilla spawns the jars one at a time and scales each through
            // Game.ScaleDrops, so a world with the Resources modifier raised pays more
            // than the hive's level. Handing the raw level to the chest quietly ignored
            // that: on double resources a level four hive paid four into a chest and
            // eight onto the ground. Rates below one are unaffected either way, since
            // ScaleDrops floors each jar at one.
            var amount = level * Game.instance.ScaleDrops(__instance.m_honeyItem.m_itemData, 1);

            if (!Plugin.TryStoreNearby(__instance.transform.position,
                    __instance.m_honeyItem.gameObject, amount)) return true;

            __instance.m_spawnEffect.Create(__instance.m_spawnPoint.position, Quaternion.identity);
            view.GetZDO().Set(ZDOVars.s_level, 0);
            return false;
        }
    }

    /// <summary>
    /// Makes the client admit that cheats are on for a confirmed admin.
    ///
    /// This is the piece that was missing, and it is why the panel's Debugmode button
    /// logged a cheerful "true" and did nothing. Player.m_debugMode has exactly one
    /// reader in the whole game:
    ///
    ///     if (m_debugMode &amp;&amp; Console.instance.IsCheatsEnabled())
    ///
    /// and IsCheatsEnabled answers ZNet.IsServer(), which is false on every client no
    /// matter what devcommands was told. So the flag was set and then ignored. The same
    /// gate hides every isCheat command from IsValid, which is the other half of why
    /// none of this worked away from a host.
    ///
    /// Being an admin is enough here; we do not also demand devcommands first. The
    /// button lives behind a panel the server itself unlocked, and every control in
    /// that panel is a cheat by definition - asking the player to type a second
    /// incantation would add friction and no safety. God mode needs none of this, by
    /// the way: Character reads InGodMode() directly, which is why that one worked.
    ///
    /// Console inherits this method rather than overriding it, so patching Terminal
    /// covers Console.instance too.
    /// </summary>
    [HarmonyPatch(typeof(Terminal), "IsCheatsEnabled")]
    public static class AdminCheatsEnabled
    {
        private static void Postfix(ref bool __result)
        {
            if (!__result && Plugin.IsAdminUnlocked) __result = true;
        }
    }

    /// <summary>
    /// Puts a few purely local commands back within reach of a confirmed admin.
    ///
    /// The game refuses them on a client twice over, and both refusals are about where
    /// the code runs rather than who is asking: IsCheatsEnabled() returns
    /// ZNet.IsServer() no matter what devcommands was told, and OnlyServer then asks
    /// the same question again. Yet debugmode only flips Player.m_debugMode, fly calls
    /// ToggleDebugFly on the local player, exploremap talks to Minimap.instance - none
    /// of them leaves the machine. Relaying them to a headless server would do nothing
    /// at all, because there is no player there to fly.
    ///
    /// So they are allowed here instead, and only for someone the server has already
    /// confirmed - IsAdminUnlocked is set by the server's answer, never by the client.
    /// Which commands is a setting, because "local" is a judgement about each one.
    /// </summary>
    [HarmonyPatch(typeof(Terminal.ConsoleCommand), "IsValid")]
    public static class LocalAdminCommandsPatch
    {
        private static void Postfix(Terminal.ConsoleCommand __instance, Terminal context,
            ref bool __result)
        {
            if (__result || !Plugin.IsAdminUnlocked) return;

            // Never touch a command the game already relays. TryRunCommand asks IsValid
            // FIRST and only sends the line to the server when the answer is no, so
            // saying yes here would hijack the relay and run it on this client instead:
            // "sleep" would pass the night for us alone and leave the server at dusk.
            if (__instance.RemoteCommand) return;

            // Only ever lift the OnlyServer refusal. IsValid folds four conditions into
            // one bool, so an unconditional "true" here overrode all of them - including
            // ones that had nothing to do with us.
            if (!__instance.OnlyServer) return;

            // And only in the console. Chat overrides isAllowedCommand to refuse every
            // cheat command outright, which is a deliberate rule of the game's, not an
            // accident of where the code runs - the blunt version above was quietly
            // opening cheats in the chat window too.
            if (!(context is Console)) return;

            if (Plugin.IsLocalAdminCommand(__instance.Command)) __result = true;
        }
    }

    /// <summary>
    /// Mead. DelayedTap is the pour that follows the tap animation, and it is the only
    /// place the bottles come into existence.
    /// </summary>
    [HarmonyPatch(typeof(Fermenter), "DelayedTap")]
    public static class FermenterTapToChest
    {
        // 1.0 turned m_delayedTapItem from the item's name into the stable hash of
        // that name, and an empty fermenter reads 0. Declaring it string here does not
        // fail to compile - Harmony throws when this field initialiser runs, taking the
        // whole patch class with it.
        private static readonly AccessTools.FieldRef<Fermenter, int> TapItem =
            AccessTools.FieldRefAccess<Fermenter, int>("m_delayedTapItem");

        private static bool Prefix(Fermenter __instance)
        {
            var content = TapItem(__instance);
            if (content == 0) return true;

            // The same match the game's own GetItemConversion makes: it hashes the
            // prefab name rather than comparing it, so we hash too.
            Fermenter.ItemConversion conversion = null;
            foreach (var candidate in __instance.m_conversion)
            {
                if (candidate == null || candidate.m_from == null) continue;
                if (candidate.m_from.gameObject.name.GetStableHashCode() != content) continue;
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

    /// <summary>
    /// Admin lasts as long as the connection that granted it, and no longer.
    ///
    /// ZNet.OnDestroy runs on a clean logout, a kick and a dropped link alike, which is
    /// why the hook hangs there rather than on a disconnect message. It fires on the
    /// headless server too, where there is no panel - hence no RefreshMenu call here.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), "OnDestroy")]
    public static class ForgetAdminOnDisconnect
    {
        private static void Postfix()
        {
            Plugin.ForgetAdmin();
        }
    }

    /// <summary>
    /// Escape takes back a projection or a marked start - and used to open the pause
    /// menu with the same press. The game's Menu reads the key in its own Update, in the
    /// same frame as ours, and nothing told it the press was spoken for. While one of our
    /// tools holds the key, or has just used it, the closed menu skips that frame; with
    /// nothing of ours going Escape opens it as it always did, and an open menu is never
    /// touched.
    /// </summary>
    [HarmonyPatch(typeof(Menu), "Update")]
    public static class KeepMenuShutForToolEscape
    {
        private static bool Prefix()
        {
            if (Menu.IsVisible()) return true;
            if (!ZInput.GetKeyDown(KeyCode.Escape, false)) return true;
            return !Plugin.EscapeBelongsToTool;
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "Show")]
    public static class InventoryShowPatch
    {
        private static void Postfix()
        {
            if (Plugin.Panel != null) Plugin.Panel.SetActive(true);
            // Coming back to the panel means the trip to the chest was abandoned. The
            // flag used to survive it - and everything else short of quitting the game -
            // so the next chest opened for any reason was swallowed and quietly turned
            // into a collect chest instead. A real assignment never reaches here,
            // because the prefix suppresses the very Interact that would open it.
            Plugin.PendingChestAssign = null;
            // A player's panel needs the server's word on what they may build before it
            // can offer «Постройки»; once per connection, broadcasts carry the rest.
            Plugin.AskSharedListOnce();
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
