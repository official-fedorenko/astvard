using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        private const string RpcTplBuild = "AstvardTplBuild";
        private const string RpcBuildAsk = "AstvardBuildAsk";
        private const string RpcBuildAnswer = "AstvardBuildAnswer";
        private const string RpcBuildRules = "AstvardBuildRules";
        private const string RpcBuildRulesSet = "AstvardBuildRulesSet";

        private const int DefaultPlayerBuildMinutes = 5;

        // A day. Longer than that the pause is a ban, and closing the template says so better.
        private const int MaxPlayerBuildMinutes = 1440;

        // Long enough for any lag worth playing through. After it the ghost is the player's
        // again, and a yes that comes later still counts against the pause.
        private const float BuildAskTimeout = 10f;

        // Server side: the pause itself - a config entry, so a restart keeps it.
        private static ConfigEntry<int> _playerBuildPause;

        // Server side: when each player last put up a build of theirs, by platform id. Kept
        // in memory only: a server restart forgives everyone, which is rare and harmless.
        private static readonly Dictionary<string, float> LastPlayerBuild = new Dictionary<string, float>();

        // Client side: the pause as the server last reported it, and when this player may
        // build next as far as this client knows. Both are only for showing.
        private static int _playerBuildMinutes = DefaultPlayerBuildMinutes;

        private static float _nextPlayerBuildAt;

        // Client side: whether this placement is a player's pick from the server, which
        // one, and the click still waiting on the server's word.
        private static bool _playerPlacement;

        private static string _playerPlacementName;

        // Client side: this placement is a player's own copy or template, paid for from
        // their bag when it goes up; and the build now starting is to be paid for.
        private static bool _playerPaidPlacement;

        private static bool _buildPays;

        private static string _buildAsk;

        private static float _buildAskedAt;

        // What the click does once the server has answered: build, give the projection back
        // to wait the pause out, or put it away for good.
        private static System.Action _buildAskGo;

        private static System.Action _buildAskResume;

        private static System.Action _buildAskClosed;

        private static float _playerHintTickAt;

        internal static void BindBuildPause(ConfigFile config)
        {
            _playerBuildPause = config.Bind("Постройки", "PlayerBuildPauseMinutes", DefaultPlayerBuildMinutes,
                "Как часто игрок может ставить постройки, которые ему разрешил админ, в минутах. "
                + "0 — без паузы. Читает только сервер; админ меняет это в игре, в «Настройках».");
        }

        internal static void RegisterBuildPauseRpcs(ZRoutedRpc rpc)
        {
            rpc.Register<string>(RpcTplBuild, OnTemplateBuild);
            rpc.Register<string>(RpcBuildAsk, OnBuildAsk);
            rpc.Register<string, int, int>(RpcBuildAnswer, OnBuildAnswer);
            rpc.Register<int, int>(RpcBuildRules, OnBuildRules);
            rpc.Register<int>(RpcBuildRulesSet, OnBuildRulesSet);
        }

        // ---------------- server side ----------------

        private static int PauseMinutes
        {
            get { return _playerBuildPause != null ? Mathf.Clamp(_playerBuildPause.Value, 0, MaxPlayerBuildMinutes) : 0; }
        }

        /// <summary>
        /// Who a routed RPC came from, as the pause keys it: the platform id of the socket it
        /// arrived on, which renaming or switching characters does not change. This
        /// machine's own calls are "local".
        /// </summary>
        private static string SenderId()
        {
            var rpc = RoutedSenderTracker.Current;
            if (rpc == null) return "local";

            var peer = MPeerByRpc != null
                ? MPeerByRpc.Invoke(ZNet.instance, new object[] { rpc }) as ZNetPeer
                : null;
            return peer != null && peer.m_socket != null ? peer.m_socket.GetHostName() : "?";
        }

        private static int WaitFor(string who)
        {
            var pause = PauseMinutes * 60;
            if (pause <= 0 || !LastPlayerBuild.TryGetValue(who, out var at)) return 0;
            return Mathf.Max(0, Mathf.CeilToInt(at + pause - Time.realtimeSinceStartup));
        }

        private static void Answer(long target, string name, int wait)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(target, RpcBuildAnswer, name, wait, PauseMinutes);
        }

        /// <summary>
        /// A player's pick: the pieces if they may build now, else how long is left. Nothing
        /// is counted yet - picking one and changing your mind costs no pause.
        /// </summary>
        private static void OnTemplateBuild(long sender, string name)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            var template = ReadTemplate(SharedPath(name));
            if (template == null || !template.ForPlayers)
            {
                Answer(sender, name, -1);
                return;
            }

            // The rule over all of them, above whether this one is open: shut, nothing is
            // handed over; paid, the bag is the price and there is no pause to wait out.
            var mode = ServerRuleValue("tpl");
            if (mode == ChoiceClosed)
            {
                Answer(sender, name, -1);
                return;
            }

            if (mode == ChoiceFree)
            {
                var wait = WaitFor(SenderId());
                if (wait > 0)
                {
                    Answer(sender, name, wait);
                    return;
                }
            }

            ZRoutedRpc.instance?.InvokeRoutedRPC(sender, RpcTplBody,
                template.Name, template.Category, template.Author,
                string.Join("\n", template.Lines));
        }

        /// <summary>
        /// The click that puts a player's build up, answered and counted in one step here -
        /// so two clicks in flight cannot both be told yes.
        /// </summary>
        private static void OnBuildAsk(long sender, string name)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            // A name starting with «#» is a rule, not a template: a floor, a wall, a fence, a
            // copy or a bridge has no file to look at, only what the admins allow. Closed says
            // no, paid says yes without a count - a paid build prints nothing, so it has no
            // pause to keep - and free goes on to the pause like a template. A rule this
            // server does not know reads as closed.
            if (!string.IsNullOrEmpty(name) && name[0] == '#')
            {
                // Only a rule that can say «даром» may be built against: anything else named
                // this way - a switch, a limit, a word this server has never heard - is a no.
                var rule = FindRule(name.Substring(1));
                var mode = rule != null && (rule.Kind == RuleKind.Choice || rule.Kind == RuleKind.ChoiceLimit)
                    ? ServerRuleValue(rule)
                    : ChoiceClosed;
                if (mode != ChoiceFree)
                {
                    Answer(sender, name, mode == ChoicePaid ? 0 : -1);
                    return;
                }
            }
            else
            {
                var template = ReadTemplate(SharedPath(name));
                if (template == null || !template.ForPlayers)
                {
                    Answer(sender, name, -1);
                    return;
                }

                var mode = ServerRuleValue("tpl");
                if (mode != ChoiceFree)
                {
                    Answer(sender, name, mode == ChoicePaid ? 0 : -1);
                    return;
                }
            }

            var who = SenderId();
            var wait = WaitFor(who);
            if (wait == 0) LastPlayerBuild[who] = Time.realtimeSinceStartup;
            Answer(sender, name, wait);

            if (wait == 0)
                Log.LogInfo($"[AstvardServerMod] Player build '{name}' by {SenderName(sender)} ({who}).");
        }

        private static void OnBuildRulesSet(long sender, int minutes)
        {
            if (!ServerAllows(sender) || _playerBuildPause == null) return;

            _playerBuildPause.Value = Mathf.Clamp(minutes, 0, MaxPlayerBuildMinutes);
            Log.LogInfo($"[AstvardServerMod] Player build pause set to {PauseMinutes} min by {SenderName(sender)}.");

            // To everyone; -1 leaves each player's own countdown as it stands.
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcBuildRules, PauseMinutes, -1);
        }

        /// <summary>The pause, and how long the asker has left of it; goes out with every list.</summary>
        private static void ReplyBuildRules(long target)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(target, RpcBuildRules, PauseMinutes, WaitFor(SenderId()));
        }

        // ---------------- client side ----------------

        private static void OnBuildRules(long sender, int minutes, int wait)
        {
            _playerBuildMinutes = Mathf.Max(0, minutes);
            if (wait >= 0) _nextPlayerBuildAt = wait > 0 ? Time.realtimeSinceStartup + wait : 0f;
            RefreshMenu();
        }

        private static void OnBuildAnswer(long sender, string name, int wait, int minutes)
        {
            _playerBuildMinutes = Mathf.Max(0, minutes);

            // The click on a projection.
            if (_buildAsk != null && _buildAsk == name)
            {
                var go = _buildAskGo;
                var resume = _buildAskResume;
                var closed = _buildAskClosed;
                ClearBuildAsk();

                if (wait == 0)
                {
                    NotePlayerBuild();
                    go?.Invoke();
                    return;
                }

                if (wait < 0)
                {
                    closed?.Invoke();
                    SayClosedToPlayers(name);
                    return;
                }

                // Back in hand: to wait the pause out, or to put away with Esc.
                resume?.Invoke();
                SayWait(wait);
                return;
            }

            // The pick from «Постройки»: refused before any ghost was shown.
            if (_awaitedBuild != null && _awaitedBuild == name)
            {
                _awaitedBuild = null;
                if (wait < 0) SayClosedToPlayers(name);
                else SayWait(wait);
            }
        }

        private static void SayWait(int wait)
        {
            _nextPlayerBuildAt = Time.realtimeSinceStartup + wait;
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                $"Строить можно раз в {_playerBuildMinutes} мин — следующая через {FormatWait(wait)}");
            RefreshMenu();
        }

        private static void SayClosedToPlayers(string name)
        {
            var what = name == "#floor" ? "Пол" : name == "#wall" ? "Стену" : name == "#fence" ? "Забор"
                : name == "#copy" ? "Копию" : name == "#bridge" ? "Мост" : $"«{name}»";
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, $"{what} админ больше не разрешает");
            AskSharedList();
        }

        private static void NotePlayerBuild()
        {
            _nextPlayerBuildAt = _playerBuildMinutes > 0
                ? Time.realtimeSinceStartup + _playerBuildMinutes * 60f
                : 0f;
        }

        private static void ClearBuildAsk()
        {
            _buildAsk = null;
            _buildAskGo = null;
            _buildAskResume = null;
            _buildAskClosed = null;
        }

        /// <summary>A placed ghost back in the player's hand.</summary>
        private static void ResumePlacement()
        {
            if (GhostRoot != null) IsPlacing = true;
        }

        /// <summary>
        /// The click on a free build of a player's. The projection holds still where it was
        /// clicked and goes up once the server says the pause is over - or comes back to hand
        /// if it is not. <paramref name="key"/> is a template's name, or #floor or #fence.
        /// </summary>
        private static void AskToBuild(string key, System.Action go, System.Action resume, System.Action closed)
        {
            _buildAsk = key;
            _buildAskGo = go;
            _buildAskResume = resume;
            _buildAskClosed = closed;
            _buildAskedAt = Time.time;
            IsPlacing = false;
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcBuildAsk, key);
        }

        private static void BuildOnServerWord()
        {
            var player = Player.m_localPlayer;
            if (player == null || GhostRoot == null || BuildInProgress) return;

            _playerPlacement = false;
            _ghostPinned = false;
            Instance?.StartCoroutine(BuildFromGhost(player));
        }

        /// <summary>No word from the server in time - it lagged, or went away: the ghost is the player's again.</summary>
        internal static void CheckBuildAskTimeout()
        {
            if (_buildAsk == null || Time.time - _buildAskedAt < BuildAskTimeout) return;

            var resume = _buildAskResume;
            ClearBuildAsk();
            resume?.Invoke();
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Сервер не ответил — нажми ещё раз");
        }

        /// <summary>Keeps the countdown on a player's «Постройки» running while the page is open.</summary>
        internal static void TickPlayerBuildHint()
        {
            if (_nextPlayerBuildAt <= 0f) return;
            if (MenuState != StatePlayerBuild && MenuState != StatePlayerTemplates) return;
            if (Panel == null || !Panel.activeInHierarchy) return;
            if (Time.realtimeSinceStartup < _playerHintTickAt) return;

            _playerHintTickAt = Time.realtimeSinceStartup + 1f;
            RebuildPlayerBuildViews();
        }

        /// <summary>Seconds this player still has to wait, as far as this client knows.</summary>
        private static int PlayerBuildWait
        {
            get
            {
                if (_nextPlayerBuildAt <= 0f) return 0;

                var wait = Mathf.CeilToInt(_nextPlayerBuildAt - Time.realtimeSinceStartup);
                if (wait > 0) return wait;

                _nextPlayerBuildAt = 0f;
                return 0;
            }
        }

        private static string FormatWait(int seconds)
        {
            if (seconds < 60) return $"{seconds} с";

            var rest = seconds % 60;
            return rest == 0 ? $"{seconds / 60} мин" : $"{seconds / 60} мин {rest} с";
        }

        /// <summary>The admin's «Применить»: the server keeps it and tells everyone.</summary>
        private static void SetPlayerBuildPause(int minutes)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcBuildRulesSet, minutes);
            _playerBuildMinutes = minutes;
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, minutes > 0
                ? $"Игроки строят не чаще раза в {minutes} мин"
                : "Игроки строят без паузы");
        }
    }
}
