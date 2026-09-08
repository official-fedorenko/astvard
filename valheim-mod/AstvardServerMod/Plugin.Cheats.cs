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
            var env = EnvMan.instance;
            if (env == null)
            {
                Log.LogWarning("[AstvardServerMod] EnvMan not ready.");
                return;
            }

            // Repeat rather than Clamp: 370 is the same bearing as 10, and a compass
            // has no ends to bump into.
            var angle = Mathf.Repeat(ParseField(WindAngleInput, 0f), 360f);
            var power = Mathf.Clamp(ParseField(WindPowerInput, 5f), 0f, 10f);

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
