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
        internal static GameObject GodButton;

        internal static GameObject FoodButton;

        internal static GameObject DebugModeButton;

        internal static GameObject TodButton;

        internal static GameObject TodHint;

        internal static GameObject TodInput;

        internal static GameObject TodApplyButton;

        internal static GameObject WeatherButton;

        internal static GameObject WindButton;

        internal static GameObject WindHint;

        internal static GameObject WindAngleInput;

        internal static GameObject WindPowerInput;

        internal static GameObject WindApplyButton;

        internal static GameObject WindFacingButton;

        internal static GameObject WindResetButton;

        internal static GameObject EnvButton;

        internal static GameObject EnvHint;

        internal static GameObject EnvResetButton;

        internal static GameObject RepairButton;

        internal static GameObject RepairHint;

        internal static GameObject RepairRadiusInput;

        internal static GameObject RepairApplyButton;

        internal static GameObject ForceDeleteButton;

        internal static GameObject ForceDeleteHint;

        internal static GameObject ForceDeleteRadiusInput;

        internal static GameObject ForceDeleteApplyButton;

        /// <summary>
        /// Freezes the world clock at the given time, the same way the console's
        /// "tod" command does. Input is 1-10 for convenience; the game wants 0-1.
        /// </summary>
        private static void ApplyTimeOfDay()
        {
            var env = EnvMan.instance;
            if (env == null)
            {
                Log.LogWarning("[AstvardServerMod] EnvMan not ready.");
                return;
            }

            var value = Mathf.Clamp(ParseField(TodInput, 5f), 1f, 10f);
            env.m_debugTimeOfDay = true;
            env.m_debugTime = value / 10f;

            Log.LogInfo($"[AstvardServerMod] Time of day set to {value} ({env.m_debugTime:F2}).");
        }

        /// <summary>What the button says, and the name EnvMan files that weather under.</summary>
        private class WeatherOption
        {
            public WeatherOption(string label, string env)
            {
                Label = label;
                Env = env;
            }

            public string Label { get; }

            public string Env { get; }
        }

        // The handful worth a button, out of the thirty-odd the game carries — most of
        // the rest are boss arenas and cave interiors, which look wrong under open sky.
        // Every name is checked against the running game before its button is shown, so
        // one the build has dropped disappears rather than sitting there doing nothing.
        private static readonly WeatherOption[] WeatherOptions =
        {
            new WeatherOption("Ясно", "Clear"),
            new WeatherOption("Морось", "LightRain"),
            new WeatherOption("Дождь", "Rain"),
            new WeatherOption("Гроза", "ThunderStorm"),
            new WeatherOption("Туман", "Misty"),
            new WeatherOption("Снег", "Snow"),
            new WeatherOption("Метель", "SnowStorm"),
            new WeatherOption("Мгла", "Darklands_dark"),
        };

        internal static readonly GameObject[] WeatherOptionButtons =
            new GameObject[WeatherOptions.Length];

        private static readonly string[] CompassNames =
        {
            "север", "северо-восток", "восток", "юго-восток",
            "юг", "юго-запад", "запад", "северо-запад"
        };

        /// <summary>
        /// Whether this build of the game has that weather at all. m_environments is
        /// filled from game data at load, so asking it is the only honest answer — a
        /// name that was right one update ago can quietly stop existing.
        /// </summary>
        internal static bool KnowsWeather(string name)
        {
            var env = EnvMan.instance;
            if (env == null || env.m_environments == null) return false;

            foreach (var setup in env.m_environments)
                if (setup != null && setup.m_name == name) return true;

            return false;
        }

        /// <summary>
        /// Pins the weather, the way the console's "env" command does. Only this client
        /// sees it: m_forceEnv is an ordinary local field, and everyone else goes on
        /// deriving the weather from world time as usual.
        /// </summary>
        private static void ApplyWeather(WeatherOption option)
        {
            var env = EnvMan.instance;
            if (env == null)
            {
                Log.LogWarning("[AstvardServerMod] EnvMan not ready.");
                return;
            }

            env.SetForceEnvironment(option.Env);

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, $"Погода: {option.Label}");
            Log.LogInfo($"[AstvardServerMod] Forced environment {option.Env}.");
        }

        /// <summary>Hands the weather back to the world clock.</summary>
        private static void ResetWeather()
        {
            var env = EnvMan.instance;
            if (env == null) return;

            // Empty is what the field starts as and what the game reads as "no
            // override"; there is no separate call for clearing it.
            env.SetForceEnvironment("");

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Погода снова обычная");
            Log.LogInfo("[AstvardServerMod] Environment override cleared.");
        }

        /// <summary>
        /// Freezes the wind at a bearing and a strength. The game turns the angle into
        /// (sin, 0, cos), which makes it an ordinary compass bearing pointing the way
        /// the wind blows: 0 north, 90 east. Strength is 1-10 here to match the
        /// time-of-day field next door — the game itself wants 0-1 and clamps to it.
        /// Local like the weather: m_debugWind never leaves this client.
        /// </summary>
        private static void ApplyWind()
        {
            // Repeat rather than Clamp: 370 is the same bearing as 10, and a compass
            // has no ends to bump into.
            SetWind(Mathf.Repeat(ParseField(WindAngleInput, 0f), 360f),
                    ParseField(WindPowerInput, 5f));
        }

        /// <summary>
        /// Points the wind wherever the player is looking. The eye direction carries
        /// pitch, so it is flattened to the ground plane first; look straight down and
        /// there is no bearing left in it, and the body's own facing stands in.
        ///
        /// The bearing is written into the field as well as into the wind. Otherwise
        /// the number on screen would go on describing the previous wind, and the next
        /// press of "Установить" would quietly undo this one.
        /// </summary>
        private static void ApplyWindFromFacing()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            var look = player.GetLookDir();
            var flat = new Vector3(look.x, 0f, look.z);

            // Straight up or straight down: what is left horizontally is noise.
            if (flat.sqrMagnitude < 0.0001f)
            {
                var forward = player.transform.forward;
                flat = new Vector3(forward.x, 0f, forward.z);
            }

            if (flat.sqrMagnitude < 0.0001f) return;

            // The game builds the wind as (sin a, 0, cos a), so the bearing is Atan2
            // with x first — the argument order that reads backwards and is right.
            // Rounded before it is used, so the field and the wind agree to the degree.
            var angle = Mathf.Repeat(Mathf.Round(Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg), 360f);

            SetField(WindAngleInput, ((int)angle).ToString());
            SetWind(angle, ParseField(WindPowerInput, 5f));
        }

        /// <summary>
        /// The one place the wind is actually set, so both buttons clamp the strength
        /// the same way and report it in the same words.
        /// </summary>
        private static void SetWind(float angle, float power)
        {
            var env = EnvMan.instance;
            if (env == null)
            {
                Log.LogWarning("[AstvardServerMod] EnvMan not ready.");
                return;
            }

            power = Mathf.Clamp(power, 0f, 10f);
            env.SetDebugWind(angle, power / 10f);

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                $"Ветер на {CompassNames[(int)Mathf.Repeat(Mathf.Round(angle / 45f), 8f)]}, сила {power:F0}");
            Log.LogInfo($"[AstvardServerMod] Wind set to {angle:F0} deg, {power / 10f:F2}.");
        }

        /// <summary>Hands the wind back to the weather that should be driving it.</summary>
        private static void ResetWind()
        {
            var env = EnvMan.instance;
            if (env == null) return;

            env.ResetDebugWind();

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Ветер снова обычный");
            Log.LogInfo("[AstvardServerMod] Debug wind cleared.");
        }

        // CookingStation and Smelter keep their fuel setter private, unlike
        // Fireplace — both just write the ZDO, so calling them is safe.
        private static readonly System.Reflection.MethodInfo MCookingSetFuel =
            AccessTools.Method(typeof(CookingStation), "SetFuel");

        private static readonly System.Reflection.MethodInfo MSmelterSetFuel =
            AccessTools.Method(typeof(Smelter), "SetFuel");


        /// <summary>How many of each to hand over.</summary>
        private const int FoodServings = 3;

        /// <summary>What the button says, and the three dishes behind it.</summary>
        private sealed class FoodSet
        {
            public FoodSet(string label, params string[] prefabs)
            {
                Label = label;
                Prefabs = prefabs;
            }

            public string Label { get; }

            public string[] Prefabs { get; }
        }

        /// <summary>How many buttons the panel keeps room for.</summary>
        internal const int MaxFoodButtons = 9;

        /// <summary>
        /// A meal per stage of the game, three dishes each.
        ///
        /// This used to read ObjectDB and hand over whatever scored highest on health,
        /// stamina and eitr - which is defensible arithmetic and a useless meal. The
        /// strongest three dishes in the build are all Ashlands dishes, so a player who
        /// had just left the Meadows got food he could not have cooked, and the button
        /// had exactly one answer no matter what it was asked.
        ///
        /// These are the combinations the game's own players settled on: two health
        /// dishes and one stamina dish, since the belly holds three and a third health
        /// dish buys less than the stamina it costs. Mistlands and Ashlands get a second
        /// button for the eitr meal, because from Mistlands on there is a real choice
        /// between hitting things and casting at them, and no single trio serves both.
        ///
        /// Names were checked against the shipped game data rather than typed from
        /// memory - every prefab below was found by scanning the install - and they are
        /// checked again at the moment the button is pressed, so one that a future
        /// update renames goes quiet instead of handing over nothing and saying nothing.
        /// </summary>
        private static readonly FoodSet[] FoodSets =
        {
            new FoodSet("Луга", "CookedDeerMeat", "CookedMeat", "Honey"),
            new FoodSet("Чёрный лес", "DeerStew", "MinceMeatSauce", "CarrotSoup"),
            new FoodSet("Болото", "SerpentStew", "SerpentMeatCooked", "TurnipStew"),
            new FoodSet("Горы", "SerpentStew", "SerpentMeatCooked", "Eyescream"),
            new FoodSet("Равнины", "SerpentStew", "LoxPie", "BloodPudding"),
            new FoodSet("Мистленд", "MisthareSupreme", "MeatPlatter", "FishAndBread"),
            new FoodSet("Мистленд — магия",
                        "SeekerAspic", "YggdrasilPorridge", "MagicallyStuffedShroom"),
            new FoodSet("Пепельные земли", "PiquantPie", "MashedMeat", "RoastedCrustPie"),
            new FoodSet("Пепельные земли — магия",
                        "MarinatedGreens", "SparklingShroomshake", "SizzlingBerryBroth"),
        };

        internal static int FoodSetCount
        {
            get { return FoodSets.Length; }
        }

        internal static string FoodSetLabel(int slot)
        {
            return slot >= 0 && slot < FoodSets.Length ? FoodSets[slot].Label : "";
        }

        /// <summary>
        /// Hands over one meal: three of each dish, one belly slot apiece.
        ///
        /// A dish the build does not know is skipped rather than aborting the rest -
        /// two thirds of a meal beats none - and it is named in the log, which is the
        /// only place anyone would look to find out that the game renamed something.
        /// </summary>
        internal static void GiveFoodSet(int slot)
        {
            var player = Player.m_localPlayer;
            if (player == null || ObjectDB.instance == null) return;
            if (slot < 0 || slot >= FoodSets.Length) return;

            var set = FoodSets[slot];
            var given = new List<string>();
            var missing = new List<string>();
            var full = false;

            foreach (var name in set.Prefabs)
            {
                var prefab = ObjectDB.instance.GetItemPrefab(name);
                if (prefab == null)
                {
                    missing.Add(name);
                    continue;
                }

                if (!player.GetInventory().AddItem(prefab, FoodServings))
                {
                    full = true;
                    continue;
                }

                // The prefab name, not m_shared.m_name: the latter is a token like
                // $item_serpentstew, and Localization lives in an assembly we do not
                // reference. An admin reading a log wants the prefab name anyway.
                given.Add(name);
            }

            var said = given.Count > 0
                ? set.Label + ": " + given.Count + " из " + set.Prefabs.Length
                : (full ? "Некуда положить" : "Еды не нашлось");

            player.Message(MessageHud.MessageType.Center, said);
            Log.LogInfo($"[AstvardServerMod] Food set '{set.Label}': gave "
                        + $"{string.Join(", ", given)}"
                        + (missing.Count > 0
                           ? $"; unknown to this build: {string.Join(", ", missing)}"
                           : "")
                        + (full ? "; inventory full" : "") + ".");
        }

        /// <summary>
        /// Mirrors the game's own "forcedelete" console command, including its list of
        /// protected objects - with one deliberate difference, below.
        /// </summary>
        private static void RunForceDelete()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            // The command caps at 50 and so do we: the sweep walks every GameObject
            // in the scene, and a bigger bite is more damage than anyone can undo.
            var radius = Mathf.Clamp(ParseField(ForceDeleteRadiusInput, 5f), 1f, 50f);
            var origin = player.transform.position;
            var sqrRadius = radius * radius;

            var removed = 0;

            // Sorting the whole scene by instance id would be wasted work here.
            foreach (var obj in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
            {
                // Destroying one object can take its children with it, so re-check.
                if (obj == null) continue;
                if ((obj.transform.position - origin).sqrMagnitude > sqrRadius) continue;
                if (IsProtectedFromDelete(obj)) continue;

                // Only something that exists in the world can be removed from it. That
                // was true before as well - a Destructible takes its view from its own
                // object, and without one DestroyNow does nothing - but it still counted.
                var view = obj.GetComponent<ZNetView>();
                if (view == null || !view.IsValid() || ZNetScene.instance == null) continue;

                // The difference from vanilla. Both removals below act only for the
                // owner: DestroyNow does nothing at all for somebody else's object, and
                // ZNetScene.Destroy erases the world record only for our own - for
                // anything else it deletes the local copy, which stays for every other
                // player and comes back for this one on the next load. The command gets
                // away with that because it is meant for a single player. A panel on a
                // shared server does not, so it takes ownership first.
                view.ClaimOwnership();

                var destructible = obj.GetComponent<Destructible>();
                if (destructible != null) destructible.DestroyNow();
                else ZNetScene.instance.Destroy(obj);

                removed++;
            }

            player.Message(MessageHud.MessageType.Center,
                removed == 0 ? "Сносить нечего" : $"Снесено объектов: {removed}");
            Log.LogInfo($"[AstvardServerMod] ForceDelete r={radius} -> {removed} removed.");
        }

        private static bool IsProtectedFromDelete(GameObject obj)
        {
            // A placement preview is not part of the world, so it must survive the
            // sweep — its pieces sit in the same scene as real ones.
            if (GhostRoot != null && obj.transform.IsChildOf(GhostRoot.transform)) return true;

            if (obj.GetComponentInParent<Game>() != null) return true;
            if (obj.GetComponentInParent<Player>() != null) return true;
            if (obj.GetComponentInParent<Valkyrie>() != null) return true;
            if (obj.GetComponentInParent<LocationProxy>() != null) return true;
            if (obj.GetComponentInParent<Room>() != null) return true;
            if (obj.GetComponentInParent<Vegvisir>() != null) return true;
            if (obj.GetComponentInParent<DungeonGenerator>() != null) return true;

            var path = TransformPath(obj.transform);
            return path.Contains("StartTemple") || path.Contains("BossStone");
        }

        /// <summary>
        /// The command checks the full scene path for a couple of names. The game's own
        /// GetPath() extension lives outside assembly_valheim, so build the path here.
        /// </summary>
        private static string TransformPath(Transform t)
        {
            var path = new System.Text.StringBuilder(t.name);
            for (var parent = t.parent; parent != null; parent = parent.parent)
                path.Insert(0, parent.name + "/");
            return path.ToString();
        }

        /// <summary>
        /// Repairs every damaged structure around the player and tops up
        /// everything that burns fuel. Repair goes through WearNTear's own
        /// Repair(), so the health change is replicated the same way a hammer
        /// swing would do it — no ZDO is written behind the game's back.
        /// Ownership is claimed first: Repair() fires an RPC at the owner, and a
        /// piece nobody owns would otherwise swallow it silently.
        /// </summary>
        private static void RunRepair()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            // The page may have been open when the admins closed it.
            var admin = IsAdminUnlocked;
            if (!RuleAllows("repair"))
            {
                player.Message(MessageHud.MessageType.Center, "Ремонт игрокам сейчас закрыт");
                return;
            }

            var radius = Mathf.Clamp(ParseField(RepairRadiusInput, 20f), 1f, admin ? 200f : PlayerRepairRadius);
            var origin = player.transform.position;
            var sqrRadius = radius * radius;

            var repaired = 0;
            var skipped = 0;

            // The list is live and Repair() can spawn effects, so iterate a copy.
            foreach (var wear in WearNTear.GetAllInstances().ToList())
            {
                if (wear == null) continue;
                if ((wear.transform.position - origin).sqrMagnitude > sqrRadius) continue;

                var nview = wear.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;
                if (wear.GetHealthPercentage() >= 1f) continue;

                // A player mends what the hammer would let them touch.
                if (!admin && !PrivateArea.CheckAccess(wear.transform.position, 0f, false, false)) continue;

                if (!nview.IsOwner()) nview.ClaimOwnership();

                if (wear.Repair()) repaired++;
                else skipped++;
            }

            // Paid, the fuel comes out of the player's bag, as far as it goes.
            // An admin pays for the fuel only while building at their own cost.
            var payer = PaysHere("repair") && !player.NoCostCheat() ? player : null;
            var lacking = new HashSet<string>();
            var filled = RefuelAround(origin, radius, !admin, payer, lacking);

            string message;
            if (repaired == 0 && filled == 0) message = lacking.Count > 0 ? "Всё целое" : "Всё целое и заправлено";
            else if (filled == 0) message = $"Починено построек: {repaired}";
            else if (repaired == 0) message = $"Заправлено: {filled}";
            else message = $"Починено: {repaired}, заправлено: {filled}";
            if (lacking.Count > 0) message += $"{NEWLINE}Не хватило: {string.Join(", ", lacking)}";

            player.Message(MessageHud.MessageType.Center, message);

            Log.LogInfo($"[AstvardServerMod] Repair r={radius} -> {repaired} repaired, " +
                        $"{skipped} skipped, {filled} refuelled.");
        }

        /// <summary>
        /// Tops up everything burning fuel nearby: fireplaces (torches, campfires,
        /// hearths, braziers), fuelled cooking stations and smelters. Only the
        /// fuel is filled — a smelter still needs its own ore, since deciding what
        /// it should be smelting is not ours to make.
        ///
        /// For a player it stops at other people's wards, as the hammer would; and when
        /// <paramref name="payer"/> is given each station takes what it lacks from their bag,
        /// as far as the bag goes, with what ran short named in <paramref name="lacking"/>.
        /// </summary>
        private static int RefuelAround(Vector3 origin, float radius, bool wards, Player payer,
                                        HashSet<string> lacking)
        {
            var pieces = new List<Piece>();
            Piece.GetAllPiecesInRadius(origin, radius, pieces);

            var filled = 0;
            foreach (var piece in pieces)
            {
                if (piece == null) continue;
                if (wards && !PrivateArea.CheckAccess(piece.transform.position, 0f, false, false)) continue;

                var fireplace = piece.GetComponentInChildren<Fireplace>();
                if (fireplace != null && !fireplace.m_infiniteFuel)
                {
                    var view = ClaimForRefuel(fireplace, fireplace.m_maxFuel);
                    var fuel = view != null ? FuelTo(view, fireplace.m_maxFuel, fireplace.m_fuelItem, payer, lacking) : -1f;
                    if (fuel >= 0f)
                    {
                        // Fireplace replicates the change itself, no ZDO poking needed.
                        fireplace.SetFuel(fuel);
                        filled++;
                    }
                }

                var cooking = piece.GetComponentInChildren<CookingStation>();
                if (cooking != null && cooking.m_useFuel &&
                    SetFuelDirect(cooking, MCookingSetFuel, cooking.m_maxFuel, cooking.m_fuelItem, payer, lacking)) filled++;

                var smelter = piece.GetComponentInChildren<Smelter>();
                if (smelter != null && smelter.m_maxFuel > 0 &&
                    SetFuelDirect(smelter, MSmelterSetFuel, smelter.m_maxFuel, smelter.m_fuelItem, payer, lacking)) filled++;
            }

            return filled;
        }

        /// <summary>
        /// The fuel a station is to be left with: full, free; or, paid, what it had plus as
        /// much of what it lacks as the payer carries. -1 when nothing is to be put in.
        /// </summary>
        private static float FuelTo(ZNetView view, float max, ItemDrop fuelItem, Player payer, HashSet<string> lacking)
        {
            if (payer == null) return max;
            if (fuelItem == null) return -1f;

            var current = view.GetZDO().GetFloat(ZDOVars.s_fuel, 0f);
            var need = Mathf.FloorToInt(max - current);
            if (need <= 0) return -1f;

            var inventory = payer.GetInventory();
            var name = fuelItem.m_itemData.m_shared.m_name;
            var give = Mathf.Min(need, inventory.CountItems(name));
            if (give < need) lacking.Add(ItemTitle(fuelItem));
            if (give <= 0) return -1f;

            inventory.RemoveItem(name, give, -1);
            return current + give;
        }

        /// <summary>
        /// Returns the station's view once it is owned locally and actually short
        /// on fuel, or null when there is nothing to do.
        /// </summary>
        private static ZNetView ClaimForRefuel(Component station, float max)
        {
            var nview = station.GetComponentInParent<ZNetView>();
            if (nview == null || !nview.IsValid()) return null;
            if (nview.GetZDO().GetFloat(ZDOVars.s_fuel, 0f) >= max) return null;

            if (!nview.IsOwner()) nview.ClaimOwnership();
            return nview;
        }

        /// <summary>Fills a station whose own SetFuel is private and owner-only.</summary>
        private static bool SetFuelDirect(Component station, System.Reflection.MethodInfo setFuel, float max,
                                          ItemDrop fuelItem, Player payer, HashSet<string> lacking)
        {
            if (setFuel == null) return false;

            var view = ClaimForRefuel(station, max);
            var fuel = view != null ? FuelTo(view, max, fuelItem, payer, lacking) : -1f;
            if (fuel < 0f) return false;

            setFuel.Invoke(station, new object[] { fuel });
            return true;
        }

        private static string _adminRepairHint;

        /// <summary>A player's «Ремонт» says how far it reaches and what it costs; an admin's stays as it was.</summary>
        private static void UpdateRepairHint()
        {
            var label = RepairHint != null ? RepairHint.GetComponentInChildren<UnityEngine.UI.Text>(true) : null;
            if (label == null) return;
            if (_adminRepairHint == null) _adminRepairHint = label.text;

            label.text = IsAdminUnlocked
                ? _adminRepairHint
                : $"Радиус (м), до {PlayerRepairRadius:0}.{NEWLINE}Чинит постройки вокруг, куда{NEWLINE}"
                  + $"пускают обереги, и заправляет{NEWLINE}костры, факелы, печи, плавильни"
                  + (RulePaid("repair") ? $" —{NEWLINE}топливом из твоей сумки." : ".");
        }
    }
}
