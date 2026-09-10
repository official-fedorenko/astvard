using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        private const string RpcSay = "AstvardSay";
        private const string RpcZoneMine = "AstvardZoneMine";
        private const string RpcZoneMineDel = "AstvardZoneMineDel";

        // A player's «Ремонт» reaches this far - their base, not the whole valley.
        private const float PlayerRepairRadius = 32f;

        internal static GameObject PlayerRepairButton;

        internal static GameObject PlayerZoneButton;

        internal static GameObject PlayerZoneHint;

        internal static GameObject PlayerZoneRadiusInput;

        internal static GameObject PlayerZoneSetButton;

        internal static GameObject PlayerZoneRemoveButton;

        /// <summary>What «Функции» gains for a player, each while the admins keep it open.</summary>
        private void CreatePlayerFeatureWidgets(GUIManager gui)
        {
            PlayerRepairButton = MakeButton(gui, "Ремонт", () =>
            {
                MenuState = StateRepair;
                RefreshMenu();
            });

            PlayerZoneButton = MakeButton(gui, "Моя зона", () =>
            {
                MenuState = StatePlayerZone;
                RequestZoneList();
                RefreshMenu();
            });

            PlayerZoneHint = MakeText(gui, "");

            PlayerZoneRadiusInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.IntegerNumber, "радиус, напр. 32", 16, 160f, 32f);
            AddFixedSize(PlayerZoneRadiusInput, 160f, 32f);

            PlayerZoneSetButton = MakeButton(gui, "Поставить здесь", () =>
            {
                var player = Player.m_localPlayer;
                if (player == null) return;

                var radius = Mathf.RoundToInt(ParseField(PlayerZoneRadiusInput, MinZoneRadius));
                var at = player.transform.position;
                ZRoutedRpc.instance?.InvokeRoutedRPC(RpcZoneMine, at.x, at.z, radius);
            });

            PlayerZoneRemoveButton = MakeButton(gui, "Убрать мою зону", () =>
            {
                ZRoutedRpc.instance?.InvokeRoutedRPC(RpcZoneMineDel);
            });
        }

        internal static void RegisterPlayerFeatureRpcs(ZRoutedRpc rpc)
        {
            rpc.Register<string>(RpcSay, OnSay);
            rpc.Register<float, float, int>(RpcZoneMine, OnZoneMine);
            rpc.Register(RpcZoneMineDel, OnZoneMineDel);
        }

        /// <summary>A word from the server to one player, for what only it can know.</summary>
        private static void Say(long target, string text)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(target, RpcSay, text);
        }

        private static void OnSay(long sender, string text)
        {
            if (!string.IsNullOrEmpty(text))
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, text);
        }

        /// <summary>
        /// A player's own kept zone, set here or moved here. One each: the server knows it by
        /// the platform id of the socket, which renaming does not change, and holds the radius
        /// to the admins' limit. An admin's zones are made on their own page and never count.
        /// </summary>
        private static void OnZoneMine(long sender, float x, float z, int radius)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            if (ServerRuleValue("zone") == 0)
            {
                Say(sender, "Зоны игрокам сейчас закрыты");
                return;
            }

            var who = SenderId();
            var zone = new KeptZone
            {
                X = x,
                Z = z,
                Radius = Mathf.Clamp(radius, MinZoneRadius, ServerRuleLimit("zone")),
                Owner = SenderName(sender),
                OwnerId = who,
            };

            var index = Zones.FindIndex(kept => kept.OwnerId == who);
            if (index >= 0) Zones[index] = zone;
            else Zones.Add(zone);

            SaveZones();
            BroadcastZoneList();
            Say(sender, $"Твоя зона: радиус {zone.Radius} м");
            Log.LogInfo($"[AstvardServerMod] Player zone at {x:F0},{z:F0} r={zone.Radius} for {zone.Owner} ({who}).");
        }

        private static void OnZoneMineDel(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            var who = SenderId();
            var removed = Zones.RemoveAll(kept => kept.OwnerId == who);
            if (removed == 0)
            {
                Say(sender, "У тебя нет зоны");
                return;
            }

            SaveZones();
            BroadcastZoneList();
            Say(sender, "Зона убрана");
            Log.LogInfo($"[AstvardServerMod] Player zone removed for {SenderName(sender)} ({who}).");
        }

        private static void RefreshPlayerFeatureVisibility(bool admin)
        {
            SetActive(PlayerRepairButton, !admin && MenuState == StateFeatures && RuleAllows("repair"));
            SetActive(PlayerZoneButton, !admin && MenuState == StateFeatures && RuleAllows("zone"));

            var zonePage = !admin && MenuState == StatePlayerZone && RuleAllows("zone");
            SetActive(PlayerZoneHint, zonePage);
            SetActive(PlayerZoneRadiusInput, zonePage);
            SetActive(PlayerZoneSetButton, zonePage);
            SetActive(PlayerZoneRemoveButton, zonePage);
            if (!zonePage) return;

            // The list comes without ids, so a player's own is found by their name - close
            // enough to say where it is; the server goes by the id when it acts.
            var me = LocalPlayerName();
            var mine = ShownZones.FindIndex(kept => kept.Owner == me);
            var hint = PlayerZoneHint.GetComponentInChildren<Text>(true);
            if (hint != null)
                hint.text = $"Сервер держит землю вокруг{NEWLINE}загруженной и без тебя:{NEWLINE}"
                            + $"плавильни и печи работают.{NEWLINE}Одна на игрока, радиус{NEWLINE}"
                            + $"от {MinZoneRadius} до {RuleLimit("zone", MaxZoneRadius):0} м."
                            + (mine >= 0
                                ? $"{NEWLINE}Твоя: {ShownZones[mine].X:F0}, {ShownZones[mine].Z:F0}, "
                                  + $"{ShownZones[mine].Radius} м."
                                : "");
        }
    }
}
