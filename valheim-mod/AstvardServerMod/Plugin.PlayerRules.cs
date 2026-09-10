using BepInEx.Configuration;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        private const string RpcPlayerRules = "AstvardPlayerRules";
        private const string RpcPlayerRuleSet = "AstvardPlayerRuleSet";

        // What a choice rule can say.
        private const int ChoiceClosed = 0;
        private const int ChoiceFree = 1;
        private const int ChoicePaid = 2;

        private enum RuleKind
        {
            Toggle,
            Limit,
            Choice,
        }

        /// <summary>The «Настройки» page a rule is set from, and the config section it is kept under.</summary>
        private enum RuleGroup
        {
            Terrain,
            Build,
            Features,
        }

        /// <summary>
        /// One thing the admins decide for players, and how far it goes. The same table
        /// serves both ends: the server keeps the answer in its config, a client keeps what
        /// the server last said. None of it touches an admin.
        /// </summary>
        private sealed class PlayerRule
        {
            public string Key;

            public string ConfigName;

            public string Title;

            public RuleGroup Group;

            public RuleKind Kind;

            /// <summary>Toggle and Limit: 1 open, 0 closed. Choice: ChoiceClosed, ChoiceFree or ChoicePaid.</summary>
            public int Default;

            /// <summary>What the limit measures, in metres; Limit only.</summary>
            public string LimitWord;

            public int LimitMin;

            /// <summary>The tool's own reach, which is an admin's.</summary>
            public int LimitMax;

            public int LimitDefault;

            /// <summary>Said on the rule's own page, under what it is.</summary>
            public string Note;

            /// <summary>The config's description, when the one made up from the title would not read.</summary>
            public string ConfigNote;

            public ConfigEntry<bool> Open;

            public ConfigEntry<int> Mode;

            public ConfigEntry<int> Limit;

            public int PlayersValue;

            public int PlayerLimit;
        }

        private static PlayerRule ToggleRule(string key, string config, string title, RuleGroup group, bool open)
        {
            return new PlayerRule
            {
                Key = key, ConfigName = config, Title = title, Group = group, Kind = RuleKind.Toggle,
                Default = open ? 1 : 0,
            };
        }

        private static PlayerRule LimitRule(string key, string config, string title, RuleGroup group, bool open,
                                            string word, int min, int max, int value)
        {
            return new PlayerRule
            {
                Key = key, ConfigName = config, Title = title, Group = group, Kind = RuleKind.Limit,
                Default = open ? 1 : 0, LimitWord = word, LimitMin = min, LimitMax = max,
                LimitDefault = value, PlayerLimit = value,
            };
        }

        private static PlayerRule ChoiceRule(string key, string config, string title, RuleGroup group, int mode,
                                             string note)
        {
            return new PlayerRule
            {
                Key = key, ConfigName = config, Title = title, Group = group, Kind = RuleKind.Choice,
                Default = mode, Note = note,
            };
        }

        // Notes break their lines with a plain escape, not NEWLINE: that lives in another
        // part of this class, and the order static fields of different parts are set up in
        // is not defined - read too early it would be null, and the line would run on.
        private static readonly PlayerRule[] PlayerRules =
        {
            LimitRule("level", "Level", "Выравнивание", RuleGroup.Terrain, true,
                      "радиус", 1, (int)MaxLevelRadius, (int)MaxLevelRadius),
            LimitRule("road", "Road", "Дорожка", RuleGroup.Terrain, true,
                      "длина", 10, (int)MaxRoadLength, (int)MaxRoadLength),
            LimitRule("area", "Area", "Площадка", RuleGroup.Terrain, true,
                      "радиус", 2, (int)MaxAreaRadius, (int)MaxAreaRadius),
            LimitRule("bridge", "Bridge", "Мост", RuleGroup.Terrain, true,
                      "длина", 10, (int)MaxBridgeLength, (int)MaxBridgeLength),
            ToggleRule("smooth", "Smooth", "Сглаживание", RuleGroup.Terrain, true),
            ToggleRule("torches", "Torches", "Факелы", RuleGroup.Terrain, true),
            new PlayerRule
            {
                Key = "torchpay", ConfigName = "TorchesPaid", Title = "Факелы платные", Group = RuleGroup.Terrain,
                Kind = RuleKind.Toggle, Default = 1,
                ConfigNote = "Факелы вдоль дорожки игрок оплачивает из сумки. false — ставятся даром.",
            },
            // Closed out of the box, as it was before there were rules: what it takes down
            // no undo gives back.
            ToggleRule("clear", "Clear", "Снос", RuleGroup.Terrain, false),
            ToggleRule("undo", "Undo", "Откат", RuleGroup.Terrain, true),

            ToggleRule("snap", "Snap", "Прилипание", RuleGroup.Build, true),
            ChoiceRule("floor", "Floor", "Пол", RuleGroup.Build, ChoicePaid,
                       "Даром — с паузой между\nпостройками, платно — без неё."),
            ChoiceRule("fence", "Fence", "Забор", RuleGroup.Build, ChoicePaid,
                       "Даром — с паузой между\nпостройками, платно — без неё."),
            LimitRule("copy", "Copy", "Копирование", RuleGroup.Build, true, "радиус", 1, 64, 20),
            // Closed until an admin wants players' builds sent in: every one lands in «Общие».
            ToggleRule("share", "Share", "Делиться", RuleGroup.Build, false),

            ChoiceRule("repair", "Repair", "Ремонт", RuleGroup.Features, ChoicePaid,
                       "Чинит всегда даром. Платно —\nтопливо для заправки из сумки."),
            // Closed out of the box: every zone is ground the server keeps loaded for good.
            LimitRule("zone", "Zone", "Зона", RuleGroup.Features, false,
                      "радиус", MinZoneRadius, MaxZoneRadius, MinZoneRadius),
        };

        // Here and not beside the rest of the admin's pages: sized from the table, it has to
        // be set up after it, and only within one file is that order promised.
        internal static readonly GameObject[] PlayerRuleButtons = new GameObject[PlayerRules.Length];

        // Client side: whether the server has said anything yet on this connection. Until
        // it has, a player gets nothing - a tool the admins closed must not be open for the
        // moment it takes the answer to arrive.
        private static bool _playerRulesKnown;

        private static string RuleSection(RuleGroup group)
        {
            switch (group)
            {
                case RuleGroup.Terrain: return "Рельеф для игроков";
                case RuleGroup.Build: return "Постройки для игроков";
                default: return "Функции для игроков";
            }
        }

        internal static void BindPlayerRules(ConfigFile config)
        {
            foreach (var rule in PlayerRules)
            {
                var section = RuleSection(rule.Group);
                if (rule.Kind == RuleKind.Choice)
                {
                    rule.Mode = config.Bind(section, rule.ConfigName, rule.Default,
                        rule.ConfigNote ?? $"«{rule.Title}» для игроков: 0 — нельзя, 1 — даром, 2 — платно. "
                                           + "Админа не касается.");
                    continue;
                }

                rule.Open = config.Bind(section, rule.ConfigName, rule.Default != 0,
                    rule.ConfigNote ?? $"«{rule.Title}»: разрешено ли игрокам. Админа не касается.");
                if (rule.Kind == RuleKind.Limit)
                    rule.Limit = config.Bind(section, rule.ConfigName + "Max", rule.LimitDefault,
                        $"«{rule.Title}»: наибольший {rule.LimitWord} для игрока, м, "
                        + $"от {rule.LimitMin} до {rule.LimitMax}.");
            }
        }

        internal static void RegisterPlayerRuleRpcs(ZRoutedRpc rpc)
        {
            rpc.Register<string>(RpcPlayerRules, OnPlayerRules);
            rpc.Register<string, int>(RpcPlayerRuleSet, OnPlayerRuleSet);
        }

        private static PlayerRule FindRule(string key)
        {
            foreach (var rule in PlayerRules)
                if (rule.Key == key) return rule;
            return null;
        }

        private static int ClampLimit(PlayerRule rule, int value)
        {
            return Mathf.Clamp(value, rule.LimitMin, rule.LimitMax);
        }

        // ---------------- server side ----------------

        private static int ServerRuleValue(PlayerRule rule)
        {
            if (rule.Kind == RuleKind.Choice)
                return rule.Mode != null ? Mathf.Clamp(rule.Mode.Value, ChoiceClosed, ChoicePaid) : ChoiceClosed;
            return rule.Open != null && rule.Open.Value ? 1 : 0;
        }

        private static int ServerRuleValue(string key)
        {
            var rule = FindRule(key);
            return rule != null ? ServerRuleValue(rule) : 0;
        }

        private static int ServerRuleLimit(string key)
        {
            var rule = FindRule(key);
            if (rule == null || rule.Limit == null) return rule != null ? rule.LimitMax : 0;
            return ClampLimit(rule, rule.Limit.Value);
        }

        /// <summary>level=1:64;road=1:200;…;floor=2;…</summary>
        private static string PackPlayerRules()
        {
            var packed = new System.Text.StringBuilder();
            foreach (var rule in PlayerRules)
            {
                if (packed.Length > 0) packed.Append(';');
                packed.Append(rule.Key).Append('=').Append(ServerRuleValue(rule));
                if (rule.Limit != null) packed.Append(':').Append(ClampLimit(rule, rule.Limit.Value));
            }

            return packed.ToString();
        }

        private static void ReplyPlayerRules(long target)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(target, RpcPlayerRules, PackPlayerRules());
        }

        /// <summary>An admin's change: "level" with its value, or "level.max" with a limit.</summary>
        private static void OnPlayerRuleSet(long sender, string key, int value)
        {
            if (!ServerAllows(sender) || string.IsNullOrEmpty(key)) return;

            var limit = key.EndsWith(".max");
            var rule = FindRule(limit ? key.Substring(0, key.Length - 4) : key);
            if (rule == null) return;

            if (limit)
            {
                if (rule.Limit != null) rule.Limit.Value = ClampLimit(rule, value);
            }
            else if (rule.Kind == RuleKind.Choice)
            {
                rule.Mode.Value = Mathf.Clamp(value, ChoiceClosed, ChoicePaid);
            }
            else
            {
                rule.Open.Value = value != 0;
            }

            Log.LogInfo($"[AstvardServerMod] Player rule {key} = {value} by {SenderName(sender)}.");
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcPlayerRules, PackPlayerRules());
        }

        // ---------------- client side ----------------

        private static void OnPlayerRules(long sender, string packed)
        {
            if (string.IsNullOrEmpty(packed)) return;

            foreach (var part in packed.Split(';'))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;

                var rule = FindRule(part.Substring(0, eq));
                if (rule == null) continue;

                var value = part.Substring(eq + 1);
                var colon = value.IndexOf(':');
                if (int.TryParse(colon < 0 ? value : value.Substring(0, colon), out var said))
                    rule.PlayersValue = said;
                if (colon >= 0 && int.TryParse(value.Substring(colon + 1), out var limit))
                    rule.PlayerLimit = ClampLimit(rule, limit);
            }

            _playerRulesKnown = true;
            RefreshMenu();
        }

        /// <summary>
        /// Whether this client may use something the admins rule on: an admin always, a
        /// player when the server has said so. Asked where the buttons are drawn and again
        /// where the tool does its work - a page left open outlives a rule changed meanwhile.
        /// </summary>
        private static bool RuleAllows(string key)
        {
            if (IsAdminUnlocked) return true;

            var rule = FindRule(key);
            return _playerRulesKnown && rule != null && rule.PlayersValue > 0;
        }

        /// <summary>Whether a choice rule makes this client pay: never an admin.</summary>
        private static bool RulePaid(string key)
        {
            if (IsAdminUnlocked) return false;

            var rule = FindRule(key);
            return rule != null && rule.Kind == RuleKind.Choice && rule.PlayersValue == ChoicePaid;
        }

        /// <summary>How far a tool reaches here: its own cap for an admin, the admins' limit for a player.</summary>
        private static float RuleLimit(string key, float cap)
        {
            if (IsAdminUnlocked) return cap;

            var rule = FindRule(key);
            return rule != null ? Mathf.Min(cap, rule.PlayerLimit) : cap;
        }

        /// <summary>The line a player's hint gains when the admins have set a tool shorter than its own reach.</summary>
        private static string RuleLimitNote(string key, float cap)
        {
            var limit = RuleLimit(key, cap);
            return limit < cap ? $"{NEWLINE}Тебе — до {limit:0} м." : "";
        }

        private static bool AnyTerrainAllowed
        {
            get
            {
                foreach (var rule in PlayerRules)
                    if (rule.Group == RuleGroup.Terrain && rule.Key != "torchpay" && RuleAllows(rule.Key))
                        return true;
                return false;
            }
        }

        /// <summary>Whether torches laid here come out of the bag: a player's, while the admins say so.</summary>
        private static bool TorchesPaidHere
        {
            get
            {
                if (IsAdminUnlocked) return false;

                var rule = FindRule("torchpay");
                return rule == null || rule.PlayersValue != 0;
            }
        }

        /// <summary>An admin's press: sent to the server, and shown at once - the broadcast that follows puts right anything guessed.</summary>
        private static void SetPlayerRule(string key, int value)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcPlayerRuleSet, key, value);

            var limit = key.EndsWith(".max");
            var rule = FindRule(limit ? key.Substring(0, key.Length - 4) : key);
            if (rule != null)
            {
                if (limit) rule.PlayerLimit = ClampLimit(rule, value);
                else rule.PlayersValue = value;
            }

            RefreshMenu();
        }

        private static string ChoiceWord(int value)
        {
            return value == ChoicePaid ? "платно" : value == ChoiceFree ? "даром" : "нет";
        }

        private static string RuleLabel(PlayerRule rule)
        {
            if (rule.Key == "torchpay") return rule.PlayersValue != 0 ? "Факелы: платно" : "Факелы: даром";

            switch (rule.Kind)
            {
                case RuleKind.Choice:
                    return $"{rule.Title}: {ChoiceWord(rule.PlayersValue)}";
                case RuleKind.Limit:
                    return rule.PlayersValue > 0 ? $"{rule.Title}: до {rule.PlayerLimit} м" : $"{rule.Title}: нет";
                default:
                    return $"{rule.Title}: {(rule.PlayersValue > 0 ? "да" : "нет")}";
            }
        }
    }
}
