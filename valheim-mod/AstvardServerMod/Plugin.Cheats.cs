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

        /// <summary>
        /// Everything that is the input of some recipe: raw meat, dough, anything the
        /// player is meant to put on a fire first.
        ///
        /// Asked of the stations themselves rather than listed here. A cooking station
        /// and a fermenter each carry their conversions as from-to pairs, so anything
        /// appearing as a "from" has a better version of itself one step away and is by
        /// definition not what you hand somebody to eat.
        /// </summary>
        private static HashSet<string> Uncooked()
        {
            var raw = new HashSet<string>();
            if (ZNetScene.instance == null) return raw;

            foreach (var prefab in ZNetScene.instance.m_prefabs)
            {
                if (prefab == null) continue;

                var cooking = prefab.GetComponent<CookingStation>();
                if (cooking != null && cooking.m_conversion != null)
                    foreach (var step in cooking.m_conversion)
                        if (step != null && step.m_from != null)
                            raw.Add(step.m_from.gameObject.name);

                var fermenting = prefab.GetComponent<Fermenter>();
                if (fermenting != null && fermenting.m_conversion != null)
                    foreach (var step in fermenting.m_conversion)
                        if (step != null && step.m_from != null)
                            raw.Add(step.m_from.gameObject.name);
            }

            return raw;
        }

        /// <summary>
        /// Three plates, one per belly slot, each the best of its kind this build knows.
        ///
        /// The roster is read out of ObjectDB rather than written down here. A list of
        /// names would go stale on the next update the way our spawner names nearly did,
        /// and worse, it would encode one person's opinion of "good food" instead of the
        /// game's own numbers. Whatever the strongest health, stamina and eitr dishes
        /// are, that is what arrives - so the answer improves by itself when the game
        /// adds a better one.
        ///
        /// Three of the same axis is not a balanced meal, so each pick is taken from the
        /// dishes the earlier ones did not already claim.
        /// </summary>
        internal static void GiveFood()
        {
            var player = Player.m_localPlayer;
            if (player == null || ObjectDB.instance == null) return;

            var uncooked = Uncooked();

            var edible = new List<ItemDrop>();
            foreach (var prefab in ObjectDB.instance.m_items)
            {
                var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                var shared = drop != null && drop.m_itemData != null ? drop.m_itemData.m_shared : null;
                if (shared == null) continue;
                if (shared.m_food <= 0f && shared.m_foodStamina <= 0f && shared.m_foodEitr <= 0f)
                    continue;

                // Raw meat and its like feed a starving man, so they carry food values
                // and slipped through - but handing someone a plate of them is handing
                // them a job. Anything a station turns into something else is dropped.
                if (uncooked.Contains(prefab.name)) continue;

                edible.Add(drop);
            }

            var picks = new List<ItemDrop>();
            var axes = new System.Func<ItemDrop.ItemData.SharedData, float>[]
            {
                s => s.m_food,
                s => s.m_foodStamina,
                s => s.m_foodEitr,
            };

            foreach (var axis in axes)
            {
                ItemDrop best = null;
                foreach (var candidate in edible)
                {
                    if (picks.Contains(candidate)) continue;
                    if (axis(candidate.m_itemData.m_shared) <= 0f) continue;
                    if (best == null ||
                        axis(candidate.m_itemData.m_shared) > axis(best.m_itemData.m_shared))
                        best = candidate;
                }

                if (best != null) picks.Add(best);
            }

            if (picks.Count == 0)
            {
                player.Message(MessageHud.MessageType.Center, "Еды не нашлось");
                return;
            }

            var given = new List<string>();
            foreach (var pick in picks)
            {
                if (!player.GetInventory().AddItem(pick.gameObject, FoodServings)) continue;
                // The prefab name, not m_shared.m_name: the latter is a token like
                // $item_serpentstew, and Localization lives in an assembly we do not
                // reference. An admin reading a log wants the prefab name anyway.
                given.Add(pick.gameObject.name);
            }

            player.Message(MessageHud.MessageType.Center,
                given.Count == 0 ? "Некуда положить" : string.Join(", ", given));
            Log.LogInfo($"[AstvardServerMod] Food given: {string.Join(", ", given)}.");
        }

        /// <summary>
        /// Mirrors the game's own "forcedelete" console command, including its list of
        /// protected objects, so the button does exactly what the command does.
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

                var destructible = obj.GetComponent<Destructible>();
                if (destructible != null)
                {
                    destructible.DestroyNow();
                    removed++;
                }
                else if (obj.GetComponent<ZNetView>() != null && ZNetScene.instance != null)
                {
                    ZNetScene.instance.Destroy(obj);
                    removed++;
                }
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

            var radius = Mathf.Clamp(ParseField(RepairRadiusInput, 20f), 1f, 200f);
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

                if (!nview.IsOwner()) nview.ClaimOwnership();

                if (wear.Repair()) repaired++;
                else skipped++;
            }

            var filled = RefuelAround(origin, radius);

            string message;
            if (repaired == 0 && filled == 0) message = "Всё целое и заправлено";
            else if (filled == 0) message = $"Починено построек: {repaired}";
            else if (repaired == 0) message = $"Заправлено: {filled}";
            else message = $"Починено: {repaired}, заправлено: {filled}";

            player.Message(MessageHud.MessageType.Center, message);

            Log.LogInfo($"[AstvardServerMod] Repair r={radius} -> {repaired} repaired, " +
                        $"{skipped} skipped, {filled} refuelled.");
        }

        /// <summary>
        /// Tops up everything burning fuel nearby: fireplaces (torches, campfires,
        /// hearths, braziers), fuelled cooking stations and smelters. Only the
        /// fuel is filled — a smelter still needs its own ore, since deciding what
        /// it should be smelting is not ours to make.
        /// </summary>
        private static int RefuelAround(Vector3 origin, float radius)
        {
            var pieces = new List<Piece>();
            Piece.GetAllPiecesInRadius(origin, radius, pieces);

            var filled = 0;
            foreach (var piece in pieces)
            {
                if (piece == null) continue;

                var fireplace = piece.GetComponentInChildren<Fireplace>();
                if (fireplace != null && !fireplace.m_infiniteFuel &&
                    ClaimForRefuel(fireplace, fireplace.m_maxFuel) != null)
                {
                    // Fireplace replicates the change itself, no ZDO poking needed.
                    fireplace.SetFuel(fireplace.m_maxFuel);
                    filled++;
                }

                var cooking = piece.GetComponentInChildren<CookingStation>();
                if (cooking != null && cooking.m_useFuel &&
                    SetFuelDirect(cooking, MCookingSetFuel, cooking.m_maxFuel)) filled++;

                var smelter = piece.GetComponentInChildren<Smelter>();
                if (smelter != null && smelter.m_maxFuel > 0 &&
                    SetFuelDirect(smelter, MSmelterSetFuel, smelter.m_maxFuel)) filled++;
            }

            return filled;
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
        private static bool SetFuelDirect(Component station, System.Reflection.MethodInfo setFuel, float max)
        {
            if (setFuel == null || ClaimForRefuel(station, max) == null) return false;
            setFuel.Invoke(station, new object[] { max });
            return true;
        }
    }
}
