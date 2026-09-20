using System.Collections;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        // Server side: where the site keeps what players may build - the rules for players
        // and who may build each shared template. The token is the lists' own, one secret
        // for one site, and an empty address means «beside the lists».
        private static ConfigEntry<string> _siteBuildsUrl;

        private static ConfigEntry<int> _siteBuildsSeconds;

        // Quick enough that a switch on the site is in the game before the admin has
        // tabbed back to it; an unchanged revision is answered with one word.
        private const int DefaultSiteBuildsSeconds = 15;

        private const int MinSiteBuildsSeconds = 5;

        private const int MaxSiteBuildsSeconds = 3600;

        // What an admin changed in game and the site has not heard yet. Bounded, so a site
        // that stays down for a day does not grow this without end.
        private const int MaxPendingBuildChanges = 200;

        // The pause's key on the site. It is not a PlayerRule - it lives in «Постройки»
        // with an RPC of its own - but to an admin on the site it is one more rule.
        private const string PauseRuleKey = "pause";

        private static readonly List<string> SiteBuildsPending = new List<string>();

        private static float _siteBuildsNextAt;

        private static bool _siteBuildsBusy;

        // The site's revision this server last wrote into its config and files. Not kept
        // over a restart on purpose: a fresh start takes the whole state once.
        private static int _siteBuildsApplied;

        private static bool _siteBuildsHelloSent;

        // The last complaint, so a site that is down fills the log once, not every round.
        private static string _siteBuildsLastProblem;

        internal static void BindSiteBuilds(ConfigFile config)
        {
            _siteBuildsUrl = config.Bind("Сайт", "BuildsUrl", "",
                "Откуда сервер берёт правила построек для игроков и доступ к общим постройкам и куда отправляет "
                + "изменённое в игре. Пусто — рядом со списками: ListsUrl, где «lists» заменено на «builds». "
                + "Токен тот же — ListsToken.");
            _siteBuildsSeconds = config.Bind("Сайт", "BuildsSeconds", DefaultSiteBuildsSeconds,
                "Как часто спрашивать сайт о постройках, в секундах, от 5 до 3600.");
        }

        private static string SiteBuildsUrl()
        {
            return SiteSync.BuildsUrlFrom(_siteBuildsUrl != null ? _siteBuildsUrl.Value : "",
                                          _siteListsUrl != null ? _siteListsUrl.Value : "");
        }

        private static string SiteToken()
        {
            return _siteListsToken != null ? _siteListsToken.Value.Trim() : "";
        }

        /// <summary>From TickSiteLists, every frame on the server: starts a round when one is due.</summary>
        private void TickSiteBuilds()
        {
            var net = ZNet.instance;
            if (net == null || !net.IsServer() || _siteBuildsBusy) return;

            // Часы - первыми: они стоят одно сравнение, а адрес при пустом
            // «BuildsUrl» выводится из «ListsUrl» подстрокой со склейкой, то есть
            // строит две строки. Каждый кадр, чтобы тут же выяснить, что рано.
            var now = Time.realtimeSinceStartup;
            if (now < _siteBuildsNextAt) return;

            var url = SiteBuildsUrl();
            var token = SiteToken();
            if (url.Length == 0 || token.Length == 0) return;

            var seconds = Mathf.Clamp(_siteBuildsSeconds != null ? _siteBuildsSeconds.Value : DefaultSiteBuildsSeconds,
                                      MinSiteBuildsSeconds, MaxSiteBuildsSeconds);
            _siteBuildsNextAt = now + seconds;
            StartCoroutine(SyncSiteBuilds(url, token));
        }

        /// <summary>
        /// One round: tell the site which rules this build has (once per start), send what an
        /// admin changed in game, then take the state and write it in.
        ///
        /// Sending comes before taking, on purpose. A change made in game that the site has
        /// not heard yet is newer than anything the site could answer with; applying the
        /// answer first would put the old value back under the admin's hands.
        /// </summary>
        private IEnumerator SyncSiteBuilds(string url, string token)
        {
            _siteBuildsBusy = true;
            try
            {
                if (!_siteBuildsHelloSent)
                {
                    var hello = new List<string> { SiteSync.KindLine("hello") };
                    hello.AddRange(RuleMetaLines());

                    var heard = false;
                    yield return PostSiteBuilds(url, token, hello, (code, text) => heard = true);
                    if (!heard) yield break;
                    _siteBuildsHelloSent = true;
                }

                if (SiteBuildsPending.Count > 0)
                {
                    var sending = new List<string>(SiteBuildsPending);
                    var change = new List<string> { SiteSync.KindLine("change") };
                    change.AddRange(sending);

                    var sent = false;
                    yield return PostSiteBuilds(url, token, change, (code, text) => sent = true);
                    if (!sent) yield break;

                    // A 409 is a site that was never seeded: the seed that follows carries these
                    // values anyway, since this server has already applied them.
                    foreach (var line in sending) SiteBuildsPending.Remove(line);
                }

                string answer;
                using (var request = UnityWebRequest.Get(url + (url.Contains("?") ? "&" : "?") + "rev=" + _siteBuildsApplied))
                {
                    request.SetRequestHeader("Authorization", "Bearer " + token);
                    request.timeout = 15;
                    yield return request.SendWebRequest();

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        ComplainSiteBuilds($"site builds: {request.responseCode} {request.error}");
                        yield break;
                    }

                    answer = request.downloadHandler.text;
                }

                var pulled = SiteSync.ParsePull(answer);
                if (!pulled.Valid)
                {
                    ComplainSiteBuilds("site builds: the answer is not a builds state, nothing applied");
                    yield break;
                }

                _siteBuildsLastProblem = null;

                if (pulled.SeedNeeded)
                {
                    var seed = new List<string> { SiteSync.KindLine("seed") };
                    seed.AddRange(RuleMetaLines());
                    seed.AddRange(RuleStateLines());
                    seed.AddRange(TemplateAccessLines());

                    var seeded = false;
                    yield return PostSiteBuilds(url, token, seed, (code, text) => seeded = true);
                    if (seeded)
                    {
                        Log.LogInfo("[AstvardServerMod] Site builds: the site had nothing yet; sent this server's rules and template access.");
                        _siteBuildsNextAt = 0f;
                    }
                    yield break;
                }

                if (pulled.Unchanged) yield break;

                // Changed in game while the answer was on its way: sent next round, applied after.
                if (SiteBuildsPending.Count > 0) yield break;

                ApplySiteBuilds(pulled);
                _siteBuildsApplied = pulled.Revision;
            }
            finally
            {
                _siteBuildsBusy = false;
            }
        }

        private static IEnumerator PostSiteBuilds(string url, string token, List<string> lines,
                                                  System.Action<long, string> done)
        {
            var bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "text/plain; charset=utf-8");
                request.SetRequestHeader("Authorization", "Bearer " + token);
                request.timeout = 15;
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success || request.responseCode == 409)
                {
                    done(request.responseCode, request.downloadHandler.text);
                    yield break;
                }

                ComplainSiteBuilds($"site builds: {lines[0].Replace(SiteSync.Tab, ' ')} got {request.responseCode} {request.error}");
            }
        }

        private static void ComplainSiteBuilds(string problem)
        {
            if (problem == _siteBuildsLastProblem) return;
            _siteBuildsLastProblem = problem;
            Log.LogWarning($"[AstvardServerMod] {problem}");
        }

        /// <summary>
        /// Remembers an admin's change in game for the site. A newer change to the same rule
        /// or template replaces the older one, and the next round starts at once rather
        /// than waiting out the pause - the sooner the site hears, the less there is to lose.
        /// </summary>
        internal static void QueueSiteBuildsChange(string line)
        {
            if (string.IsNullOrEmpty(line) || SiteBuildsUrl().Length == 0 || SiteToken().Length == 0) return;

            var identity = SiteSync.IdentityOf(line);
            SiteBuildsPending.RemoveAll(existing => SiteSync.IdentityOf(existing) == identity);
            SiteBuildsPending.Add(line);
            while (SiteBuildsPending.Count > MaxPendingBuildChanges) SiteBuildsPending.RemoveAt(0);

            _siteBuildsNextAt = 0f;
        }

        // ---------------- what this server has ----------------

        private static string RuleGroupName(RuleGroup group)
        {
            switch (group)
            {
                case RuleGroup.Terrain: return "terrain";
                case RuleGroup.Build: return "build";
                default: return "features";
            }
        }

        private static string RuleKindName(RuleKind kind)
        {
            switch (kind)
            {
                case RuleKind.Toggle: return "toggle";
                case RuleKind.Limit: return "limit";
                case RuleKind.Choice: return "choice";
                default: return "choicelimit";
            }
        }

        /// <summary>
        /// The rules as this build knows them. Sent on every start, so the site draws exactly
        /// the rules the running server has: a rule added in a new build shows up without
        /// the site being told about it, and one taken out disappears.
        /// </summary>
        private static List<string> RuleMetaLines()
        {
            var lines = new List<string>();
            foreach (var rule in PlayerRules)
                lines.Add(SiteSync.MetaLine(rule.Key, rule.Title, RuleGroupName(rule.Group), RuleKindName(rule.Kind),
                                            rule.LimitMin, rule.LimitMax, rule.LimitWord, rule.Note ?? rule.ConfigNote));

            lines.Add(SiteSync.MetaLine(PauseRuleKey, "Пауза между постройками", "build", "number",
                                        0, MaxPlayerBuildMinutes, "мин",
                                        "Бесплатное — шаблоны сервера, пол, стена и забор даром — не чаще раза в столько минут. 0 — без паузы."));
            return lines;
        }

        private static SiteSync.RuleState CurrentRuleState(PlayerRule rule)
        {
            return new SiteSync.RuleState
            {
                Key = rule.Key,
                Value = ServerRuleValue(rule),
                Limit = rule.Limit != null ? ClampLimit(rule, rule.Limit.Value) : (int?)null,
            };
        }

        private static List<string> RuleStateLines()
        {
            var lines = new List<string>();
            foreach (var rule in PlayerRules) lines.Add(SiteSync.RuleLine(CurrentRuleState(rule)));
            lines.Add(SiteSync.RuleLine(new SiteSync.RuleState { Key = PauseRuleKey, Value = PauseMinutes }));
            return lines;
        }

        private static List<string> TemplateAccessLines()
        {
            var lines = new List<string>();
            try
            {
                if (!System.IO.Directory.Exists(SharedDir)) return lines;

                foreach (var path in System.IO.Directory.GetFiles(SharedDir, "*.txt"))
                {
                    var template = ReadTemplate(path);
                    if (template == null) continue;

                    lines.Add(SiteSync.TemplateLine(new SiteSync.TemplateAccess
                    {
                        Name = template.Name,
                        ForAll = template.ForPlayers,
                        Players = template.AllowedPlayers,
                    }));
                }
            }
            catch (System.Exception ex)
            {
                Log.LogWarning($"[AstvardServerMod] Site builds: could not list shared templates: {ex.Message}");
            }

            return lines;
        }

        // ---------------- what the site says ----------------

        /// <summary>
        /// Writes the site's state in: rules into the config the way an admin's switch does,
        /// template access into the template's own header. Only what differs is written, and
        /// players hear about it only when something did.
        /// </summary>
        private static void ApplySiteBuilds(SiteSync.Pulled pulled)
        {
            var rulesChanged = false;
            var pauseChanged = false;

            foreach (var state in pulled.Rules)
            {
                if (state.Key == PauseRuleKey)
                {
                    if (_playerBuildPause == null) continue;
                    var minutes = Mathf.Clamp(state.Value, 0, MaxPlayerBuildMinutes);
                    if (minutes == PauseMinutes) continue;
                    _playerBuildPause.Value = minutes;
                    pauseChanged = true;
                    continue;
                }

                // A rule this build does not have: the site still has it from an older one.
                var rule = FindRule(state.Key);
                if (rule == null) continue;

                if (SetRuleValue(rule, state.Value)) rulesChanged = true;
                if (state.Limit.HasValue && SetRuleLimit(rule, state.Limit.Value)) rulesChanged = true;
            }

            var templatesChanged = 0;
            foreach (var access in pulled.Templates)
                if (ApplyTemplateAccess(access)) templatesChanged++;

            RunSiteJobs(pulled.Jobs);

            if (rulesChanged)
                ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcPlayerRules, PackPlayerRules());
            if (pauseChanged)
                ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcBuildRules, PauseMinutes, -1);
            if (templatesChanged > 0)
                BroadcastSharedList();

            if (rulesChanged || pauseChanged || templatesChanged > 0)
                Log.LogInfo($"[AstvardServerMod] Site builds: revision {pulled.Revision} applied - "
                            + $"rules {(rulesChanged ? "changed" : "as they were")}, pause {PauseMinutes} min, "
                            + $"templates rewritten {templatesChanged}.");
        }

        /// <summary>
        /// Что админ попросил с сайта: переименовать постройку, сменить ей категорию или
        /// убрать. Файлы этой папки пишет только мод, и это единственная дорога туда.
        ///
        /// Номер возвращается сайту в любом случае, даже когда делать было нечего: файла
        /// уже нет, имя занято, папка не читается. Поручение, которое не снимается, сайт
        /// будет слать вечно, и в логе это будет выглядеть как работа.
        /// </summary>
        private static void RunSiteJobs(List<SiteSync.Job> jobs)
        {
            if (jobs == null || jobs.Count == 0) return;

            var did = 0;
            foreach (var job in jobs)
            {
                var done = false;
                if (job.Kind == "delete") done = RemoveSharedTemplate(job.Name);
                else if (job.Kind == "rename") done = RenameSharedTemplate(job.Name, job.Value);
                else if (job.Kind == "category") done = SetSharedHeader(job.Name, "category", job.Value);
                else Log.LogWarning($"[AstvardServerMod] Site builds: unknown job '{job.Kind}' — dropped.");

                if (done) did++;
                if (SiteBuildsPending.Count < MaxPendingBuildChanges)
                    SiteBuildsPending.Add(SiteSync.DoneLine(job.Id));
            }

            Log.LogInfo($"[AstvardServerMod] Site builds: {jobs.Count} asked for, {did} done.");
            if (did > 0) BroadcastSharedList();
        }

        private static bool ApplyTemplateAccess(SiteSync.TemplateAccess access)
        {
            var path = SharedPath(access.Name);
            if (!System.IO.File.Exists(path)) return false;

            try
            {
                var updated = SiteSync.WithAccessHeaders(System.IO.File.ReadAllLines(path), access.ForAll, access.Players);
                if (updated == null) return false;

                // Through a temporary file: the site reads these files too, and must never
                // read half of one.
                var temp = path + ".tmp";
                System.IO.File.WriteAllLines(temp, updated);
                System.IO.File.Replace(temp, path, null);

                Log.LogInfo($"[AstvardServerMod] Site builds: '{access.Name}' - "
                            + $"{(access.ForAll ? "open to all players" : "not open to all")}, {access.Players.Count} named.");
                return true;
            }
            catch (System.Exception ex)
            {
                ComplainSiteBuilds($"site builds: could not write access for '{access.Name}': {ex.Message}");
                return false;
            }
        }
    }
}
