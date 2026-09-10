using BepInEx.Configuration;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        private const string RpcTerrainRules = "AstvardTerrainRules";
        private const string RpcTerrainRuleSet = "AstvardTerrainRuleSet";

        private const string TerrainRulesSection = "Рельеф для игроков";

        /// <summary>
        /// One thing «Рельеф» lets a player do, and how far the admins let it go. The same
        /// table serves both ends: the server keeps the answer in its config, a client keeps
        /// what the server last said. None of it touches an admin.
        /// </summary>
        private sealed class TerrainRule
        {
            public string Key;

            public string ConfigName;

            public string Title;

            public bool Default;

            /// <summary>What the limit measures, in metres; null when the tool has none.</summary>
            public string LimitWord;

            public int LimitMin;

            /// <summary>The tool's own reach, which is an admin's, and a player's until an admin says less.</summary>
            public int LimitMax;

            public ConfigEntry<bool> Allowed;

            public ConfigEntry<int> Limit;

            public bool PlayersMay;

            public int PlayerLimit;
        }

        private static readonly TerrainRule[] TerrainRules =
        {
            new TerrainRule
            {
                Key = "level", ConfigName = "Level", Title = "Выравнивание", Default = true,
                LimitWord = "радиус", LimitMin = 1, LimitMax = (int)MaxLevelRadius, PlayerLimit = (int)MaxLevelRadius,
            },
            new TerrainRule
            {
                Key = "road", ConfigName = "Road", Title = "Дорожка", Default = true,
                LimitWord = "длина", LimitMin = 10, LimitMax = (int)MaxRoadLength, PlayerLimit = (int)MaxRoadLength,
            },
            new TerrainRule
            {
                Key = "area", ConfigName = "Area", Title = "Площадка", Default = true,
                LimitWord = "радиус", LimitMin = 2, LimitMax = (int)MaxAreaRadius, PlayerLimit = (int)MaxAreaRadius,
            },
            new TerrainRule
            {
                Key = "bridge", ConfigName = "Bridge", Title = "Мост", Default = true,
                LimitWord = "длина", LimitMin = 10, LimitMax = (int)MaxBridgeLength, PlayerLimit = (int)MaxBridgeLength,
            },
            new TerrainRule { Key = "smooth", ConfigName = "Smooth", Title = "Сглаживание", Default = true },
            new TerrainRule { Key = "torches", ConfigName = "Torches", Title = "Факелы", Default = true },
            // Closed out of the box, as it was before there were rules: what it takes down
            // no undo gives back.
            new TerrainRule { Key = "clear", ConfigName = "Clear", Title = "Снос", Default = false },
            new TerrainRule { Key = "undo", ConfigName = "Undo", Title = "Откат", Default = true },
        };

        // Server side: whether a player's torches come out of their bag. They do out of the
        // box; free pieces are printed resources, see Plugin.Torches.cs.
        private static ConfigEntry<bool> _torchesPaid;

        // Client side, as the server last said.
        private static bool _torchesPaidForPlayers = true;

        // Client side: whether the server has said anything yet on this connection. Until
        // it has, a player gets nothing - a tool the admins closed must not be open for the
        // moment it takes the answer to arrive.
        private static bool _terrainRulesKnown;

        internal static GameObject TerrainRulesButton;

        internal static GameObject TerrainRulesHint;

        internal static readonly GameObject[] TerrainRuleButtons = new GameObject[TerrainRules.Length];

        internal static GameObject TorchPayButton;

        internal static GameObject TerrainRuleHint;

        internal static GameObject TerrainRuleToggle;

        internal static GameObject TerrainRuleInput;

        internal static GameObject TerrainRuleApply;

        private static int _editingRule;

        internal static void BindTerrainRules(ConfigFile config)
        {
            foreach (var rule in TerrainRules)
            {
                rule.Allowed = config.Bind(TerrainRulesSection, rule.ConfigName, rule.Default,
                    $"«{rule.Title}» в «Рельефе»: разрешено ли игрокам. Админа не касается.");
                if (rule.LimitWord != null)
                    rule.Limit = config.Bind(TerrainRulesSection, rule.ConfigName + "Max", rule.LimitMax,
                        $"«{rule.Title}»: наибольший {rule.LimitWord} для игрока, м, "
                        + $"от {rule.LimitMin} до {rule.LimitMax}.");
            }

            _torchesPaid = config.Bind(TerrainRulesSection, "TorchesPaid", true,
                "Факелы вдоль дорожки игрок оплачивает из сумки. false — ставятся даром.");
        }

        internal static void RegisterTerrainRuleRpcs(ZRoutedRpc rpc)
        {
            rpc.Register<string>(RpcTerrainRules, OnTerrainRules);
            rpc.Register<string, int>(RpcTerrainRuleSet, OnTerrainRuleSet);
        }

        private static TerrainRule FindTerrainRule(string key)
        {
            foreach (var rule in TerrainRules)
                if (rule.Key == key) return rule;
            return null;
        }

        private static int ClampLimit(TerrainRule rule, int value)
        {
            return Mathf.Clamp(value, rule.LimitMin, rule.LimitMax);
        }

        // ---------------- server side ----------------

        /// <summary>level=1:64;road=1:200;…;smooth=1;…;torchpay=1</summary>
        private static string PackTerrainRules()
        {
            var packed = new System.Text.StringBuilder();
            foreach (var rule in TerrainRules)
            {
                if (packed.Length > 0) packed.Append(';');
                packed.Append(rule.Key).Append('=').Append(rule.Allowed != null && rule.Allowed.Value ? '1' : '0');
                if (rule.Limit != null) packed.Append(':').Append(ClampLimit(rule, rule.Limit.Value));
            }

            packed.Append(";torchpay=").Append(_torchesPaid == null || _torchesPaid.Value ? '1' : '0');
            return packed.ToString();
        }

        private static void ReplyTerrainRules(long target)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(target, RpcTerrainRules, PackTerrainRules());
        }

        /// <summary>
        /// An admin's change: "level" 1/0 opens or closes a tool, "level.max" sets its
        /// limit, "torchpay" 1/0 says whether torches are paid for.
        /// </summary>
        private static void OnTerrainRuleSet(long sender, string key, int value)
        {
            if (!ServerAllows(sender) || string.IsNullOrEmpty(key)) return;

            if (key == "torchpay")
            {
                if (_torchesPaid != null) _torchesPaid.Value = value != 0;
            }
            else
            {
                var limit = key.EndsWith(".max");
                var rule = FindTerrainRule(limit ? key.Substring(0, key.Length - 4) : key);
                if (rule == null) return;

                if (!limit) rule.Allowed.Value = value != 0;
                else if (rule.Limit != null) rule.Limit.Value = ClampLimit(rule, value);
            }

            Log.LogInfo($"[AstvardServerMod] Terrain rule {key} = {value} by {SenderName(sender)}.");
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcTerrainRules, PackTerrainRules());
        }

        // ---------------- client side ----------------

        private static void OnTerrainRules(long sender, string packed)
        {
            if (string.IsNullOrEmpty(packed)) return;

            foreach (var part in packed.Split(';'))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;

                var key = part.Substring(0, eq);
                var value = part.Substring(eq + 1);
                if (key == "torchpay")
                {
                    _torchesPaidForPlayers = value != "0";
                    continue;
                }

                var rule = FindTerrainRule(key);
                if (rule == null) continue;

                var colon = value.IndexOf(':');
                rule.PlayersMay = (colon < 0 ? value : value.Substring(0, colon)) == "1";
                if (colon >= 0 && int.TryParse(value.Substring(colon + 1), out var limit))
                    rule.PlayerLimit = ClampLimit(rule, limit);
            }

            _terrainRulesKnown = true;
            RefreshMenu();
        }

        /// <summary>
        /// Whether this client may use a «Рельеф» tool now: an admin always, a player when
        /// the server has said so. Asked where the buttons are drawn and again where the
        /// tool does its work - a page left open outlives a rule changed meanwhile.
        /// </summary>
        private static bool TerrainAllowed(string key)
        {
            if (IsAdminUnlocked) return true;

            var rule = FindTerrainRule(key);
            return _terrainRulesKnown && rule != null && rule.PlayersMay;
        }

        /// <summary>How far a tool reaches here: its own cap for an admin, the admins' limit for a player.</summary>
        private static float TerrainLimit(string key, float cap)
        {
            if (IsAdminUnlocked) return cap;

            var rule = FindTerrainRule(key);
            return rule != null ? Mathf.Min(cap, rule.PlayerLimit) : cap;
        }

        /// <summary>The line a player's hint gains when the admins have set a tool shorter than its own reach.</summary>
        private static string TerrainLimitNote(string key, float cap)
        {
            var limit = TerrainLimit(key, cap);
            return limit < cap ? $"{NEWLINE}Тебе — до {limit:0} м." : "";
        }

        private static bool AnyTerrainAllowed
        {
            get
            {
                foreach (var rule in TerrainRules)
                    if (TerrainAllowed(rule.Key)) return true;
                return false;
            }
        }

        /// <summary>Whether torches laid here come out of the bag: a player's, while the admins say so.</summary>
        private static bool TorchesPaidHere
        {
            get { return !IsAdminUnlocked && _torchesPaidForPlayers; }
        }

        /// <summary>An admin's press: sent to the server, and shown at once - the broadcast that follows puts right anything guessed.</summary>
        private static void SetTerrainRule(string key, int value)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcTerrainRuleSet, key, value);

            if (key == "torchpay")
            {
                _torchesPaidForPlayers = value != 0;
            }
            else
            {
                var limit = key.EndsWith(".max");
                var rule = FindTerrainRule(limit ? key.Substring(0, key.Length - 4) : key);
                if (rule != null)
                {
                    if (limit) rule.PlayerLimit = ClampLimit(rule, value);
                    else rule.PlayersMay = value != 0;
                }
            }

            RefreshMenu();
        }

        // ---------------- the admin's pages ----------------

        private void CreateTerrainRulesButton(GUIManager gui)
        {
            TerrainRulesButton = MakeButton(gui, "Рельеф для игроков", () =>
            {
                MenuState = StateTerrainRules;
                AskSharedList();
                RefreshMenu();
            });
        }

        private void CreateTerrainRuleWidgets(GUIManager gui)
        {
            TerrainRulesHint = MakeText(gui, "Что игроки могут в «Рельефе».\nАдмина это не касается.\nЧужие обереги игрокам\nмешают всегда.");

            for (var i = 0; i < TerrainRules.Length; i++)
            {
                var index = i;
                TerrainRuleButtons[i] = MakeButton(gui, "", () =>
                {
                    var rule = TerrainRules[index];
                    if (rule.LimitWord == null)
                    {
                        // Nothing to set but yes or no: the button is the switch.
                        SetTerrainRule(rule.Key, rule.PlayersMay ? 0 : 1);
                        return;
                    }

                    _editingRule = index;
                    SetFieldText(TerrainRuleInput, rule.PlayerLimit.ToString());
                    MenuState = StateTerrainRule;
                    RefreshMenu();
                });
            }

            TorchPayButton = MakeButton(gui, "", () => SetTerrainRule("torchpay", _torchesPaidForPlayers ? 0 : 1));

            TerrainRuleHint = MakeText(gui, "");

            TerrainRuleToggle = MakeButton(gui, "", () =>
            {
                var rule = TerrainRules[_editingRule];
                SetTerrainRule(rule.Key, rule.PlayersMay ? 0 : 1);
            });

            TerrainRuleInput = gui.CreateInputField(
                Panel.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                InputField.ContentType.IntegerNumber, "метры", 16, 160f, 32f);
            AddFixedSize(TerrainRuleInput, 160f, 32f);

            TerrainRuleApply = MakeButton(gui, "Применить", () =>
            {
                var rule = TerrainRules[_editingRule];
                var limit = ClampLimit(rule, Mathf.RoundToInt(ParseField(TerrainRuleInput, rule.PlayerLimit)));
                SetTerrainRule(rule.Key + ".max", limit);
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    $"{rule.Title} игрокам: {rule.LimitWord} до {limit} м");
                MenuState = StateTerrainRules;
                RefreshMenu();
            });
        }

        private static string TerrainRuleLabel(TerrainRule rule)
        {
            if (!rule.PlayersMay) return $"{rule.Title}: нет";
            return rule.LimitWord != null ? $"{rule.Title}: до {rule.PlayerLimit} м" : $"{rule.Title}: да";
        }

        private static void RefreshTerrainRuleViews(bool admin)
        {
            for (var i = 0; i < TerrainRules.Length; i++)
                SetLabel(TerrainRuleButtons[i], TerrainRuleLabel(TerrainRules[i]));
            SetLabel(TorchPayButton, _torchesPaidForPlayers ? "Факелы: платно" : "Факелы: даром");

            var rule = TerrainRules[Mathf.Clamp(_editingRule, 0, TerrainRules.Length - 1)];
            SetLabel(TerrainRuleToggle, rule.PlayersMay ? "Игрокам: разрешено" : "Игрокам: запрещено");
            var hint = TerrainRuleHint != null ? TerrainRuleHint.GetComponentInChildren<Text>(true) : null;
            if (hint != null && rule.LimitWord != null)
                hint.text = $"«{rule.Title}» для игроков.{NEWLINE}Наибольший {rule.LimitWord}, м:{NEWLINE}"
                            + $"от {rule.LimitMin} до {rule.LimitMax}.";

            SetActive(TerrainRulesButton, admin && MenuState == StateSettings);
            SetActive(TerrainRulesHint, admin && MenuState == StateTerrainRules);
            foreach (var button in TerrainRuleButtons)
                SetActive(button, admin && MenuState == StateTerrainRules);
            SetActive(TorchPayButton, admin && MenuState == StateTerrainRules);

            var editing = admin && MenuState == StateTerrainRule;
            SetActive(TerrainRuleHint, editing);
            SetActive(TerrainRuleToggle, editing);
            SetActive(TerrainRuleInput, editing);
            SetActive(TerrainRuleApply, editing);
        }
    }
}
