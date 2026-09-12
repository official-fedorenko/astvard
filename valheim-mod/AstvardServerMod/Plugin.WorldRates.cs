using BepInEx.Configuration;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        private const string RpcResourceRateSet = "AstvardResourceRateSet";

        // The game's own bounds would be whatever the world modifier menu offers; the key
        // itself takes any number. A quarter of the vanilla rate at the bottom, ten times it
        // at the top: past that a single tree fills a cart, and most drops hit their stack
        // size anyway (Game.ScaleDrops clamps to it).
        private const int MinResourcePercent = 25;

        private const int MaxResourcePercent = 1000;

        private const int VanillaResourcePercent = 100;

        // Server side: the rate as a percent, as the game stores it - «resourcerate 500» is
        // five times. A config entry, so a restart keeps it and the world is set back to it
        // if anything else changes the key.
        private static ConfigEntry<int> _resourcePercent;

        internal static GameObject ResourceRateButton;

        internal static GameObject ResourceRateHint;

        internal static GameObject ResourceRateInput;

        internal static GameObject ResourceRateApply;

        internal static void BindWorldRates(ConfigFile config)
        {
            _resourcePercent = config.Bind("Мир", "ResourceRatePercent", VanillaResourcePercent,
                "Сколько ресурсов падает, в процентах от обычного: 100 — как в игре, 500 — впятеро. "
                + "От 25 до 1000. Значение не из меню мира делает мир «с читами» для достижений. "
                + "Админ меняет это в игре, в «Настройках».");
        }

        private static int ResourcePercent
        {
            get
            {
                return Mathf.Clamp(_resourcePercent != null ? _resourcePercent.Value : VanillaResourcePercent,
                                   MinResourcePercent, MaxResourcePercent);
            }
        }

        // When the world was last checked against the config. The key lives in the world and
        // is sent to every client by the game itself; this only keeps it equal to the config.
        private static float _worldRatesCheckedAt = -1f;

        private static float _worldRatesSeenAt = -1f;

        // ZoneSystem registers its key RPCs as it starts; a key set before that is dropped
        // without a word. A few seconds after the zone system first shows up is enough.
        private const float WorldRatesSettle = 5f;

        private const float WorldRatesEvery = 30f;

        internal static void RegisterWorldRateRpcs(ZRoutedRpc rpc)
        {
            rpc.Register<int>(RpcResourceRateSet, OnResourceRateSet);
        }

        /// <summary>
        /// From Update on the server: puts the configured rate into the world, and puts it
        /// back if something else took it away - a «resetworldkeys», a preset on the command
        /// line. At 100 percent the key is removed rather than set to 100, so a world run at
        /// the vanilla rate is exactly a vanilla world.
        /// </summary>
        internal static void TickWorldRates()
        {
            var net = ZNet.instance;
            var zones = ZoneSystem.instance;
            if (net == null || !net.IsServer() || zones == null || ZNet.World == null) return;

            var now = Time.realtimeSinceStartup;
            if (_worldRatesSeenAt < 0f) _worldRatesSeenAt = now;
            if (now - _worldRatesSeenAt < WorldRatesSettle) return;
            if (_worldRatesCheckedAt >= 0f && now - _worldRatesCheckedAt < WorldRatesEvery) return;
            _worldRatesCheckedAt = now;

            var want = ResourcePercent;
            var has = zones.GetGlobalKey(GlobalKeys.ResourceRate, out float current);

            if (want == VanillaResourcePercent)
            {
                if (!has) return;
                zones.RemoveGlobalKey(GlobalKeys.ResourceRate);
                Log.LogInfo("[AstvardServerMod] Resource rate: back to the game's own, key removed.");
                return;
            }

            if (has && Mathf.RoundToInt(current) == want) return;
            zones.SetGlobalKey(GlobalKeys.ResourceRate, want);
            Log.LogInfo($"[AstvardServerMod] Resource rate: {want}% (was {(has ? Mathf.RoundToInt(current) + "%" : "the game's own")}).");
        }

        /// <summary>An admin's new rate. Kept in the config and put into the world at once.</summary>
        private static void OnResourceRateSet(long sender, int percent)
        {
            if (!ServerAllows(sender) || _resourcePercent == null) return;

            _resourcePercent.Value = Mathf.Clamp(percent, MinResourcePercent, MaxResourcePercent);
            _worldRatesCheckedAt = -1f;
            Log.LogInfo($"[AstvardServerMod] Resource rate: {ResourcePercent}% set by {SenderName(sender)}.");
            TickWorldRates();
        }

        // ---------------- client side ----------------

        /// <summary>
        /// The rate the world actually runs at, as the game synced it to this client. Read
        /// from the key rather than asked for: the key is already here, and it is what the
        /// drops on this machine are scaled by.
        /// </summary>
        private static float ClientResourceMultiplier()
        {
            var zones = ZoneSystem.instance;
            if (zones != null && zones.GetGlobalKey(GlobalKeys.ResourceRate, out float percent) && percent > 0f)
                return percent / 100f;
            return 1f;
        }

        private static string MultiplierText(float multiplier)
        {
            return "×" + multiplier.ToString(multiplier % 1f == 0f ? "0" : "0.##",
                                              System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void SetResourceRate(float multiplier)
        {
            var percent = Mathf.Clamp(Mathf.RoundToInt(multiplier * 100f), MinResourcePercent, MaxResourcePercent);
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcResourceRateSet, percent);
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                $"Ресурсы: {MultiplierText(percent / 100f)} — сервер применит за пару секунд");
        }

        private void CreateWorldRateWidgets(GUIManager gui)
        {
            ResourceRateButton = MakeButton(gui, "", () =>
            {
                SetFieldText(ResourceRateInput, MultiplierText(ClientResourceMultiplier()).TrimStart('×'));
                OpenRulePage(StateResourceRate);
            });

            ResourceRateHint = MakeText(gui,
                "Во сколько раз больше падает\nресурсов: дерево, камень, руда,\nсобираемое и дроп с мобов.\n"
                + "1 — как в игре, 5 — впятеро,\nот 0.25 до 10. Не из меню\nмира — мир «с читами»\nдля достижений.");

            ResourceRateInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.DecimalNumber, "во сколько раз, напр. 5", 16, 160f, 32f);
            AddFixedSize(ResourceRateInput, 160f, 32f);

            ResourceRateApply = MakeButton(gui, "Применить", () =>
            {
                SetResourceRate(ParseField(ResourceRateInput, ClientResourceMultiplier()));
                MenuState = StateSettings;
                RefreshMenu();
            });
        }
    }
}
