using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        // Server side: where the site hands out the access lists and the token for them.
        // Both empty - the feature is off, and the lists live the way they always did.
        private static ConfigEntry<string> _siteListsUrl;

        private static ConfigEntry<string> _siteListsToken;

        private static ConfigEntry<int> _siteListsSeconds;

        // A minute is quick enough that a grant on the site is felt the same evening and
        // rare enough to cost the site nothing. Faster than ten seconds would outrun the
        // game itself, which re-reads the files at about that pace anyway.
        private const int MinSiteListsSeconds = 10;

        private const int MaxSiteListsSeconds = 3600;

        private static float _siteListsNextAt;

        private static bool _siteListsBusy;

        // The last complaint, so a site that is down fills the log once, not every minute.
        private static string _siteListsLastProblem;

        internal static void BindSiteLists(ConfigFile config)
        {
            _siteListsUrl = config.Bind("Сайт", "ListsUrl", "",
                "Откуда сервер забирает списки доступа, например https://astvard.online/api/game/lists. "
                + "Пусто — не забирать, списки кладутся на сервер как раньше.");
            _siteListsToken = config.Bind("Сайт", "ListsToken", "",
                "Токен для этого адреса — тот же, что GAME_LISTS_TOKEN в .env сайта.");
            _siteListsSeconds = config.Bind("Сайт", "ListsSeconds", 60,
                "Как часто спрашивать сайт, в секундах, от 10 до 3600.");
        }

        /// <summary>
        /// From Update on the server: starts a fetch when one is due. The request runs as a
        /// coroutine, so the frame never waits on the network - a site that is slow must
        /// not become a server that stutters.
        /// </summary>
        private void TickSiteLists()
        {
            var net = ZNet.instance;
            if (net == null || !net.IsServer() || _siteListsBusy) return;

            var url = _siteListsUrl != null ? _siteListsUrl.Value.Trim() : "";
            var token = _siteListsToken != null ? _siteListsToken.Value.Trim() : "";
            if (url.Length == 0 || token.Length == 0) return;

            var now = Time.realtimeSinceStartup;
            if (now < _siteListsNextAt) return;

            var seconds = Mathf.Clamp(_siteListsSeconds != null ? _siteListsSeconds.Value : 60,
                                      MinSiteListsSeconds, MaxSiteListsSeconds);
            _siteListsNextAt = now + seconds;
            StartCoroutine(PullSiteLists(url, token));
        }

        private IEnumerator PullSiteLists(string url, string token)
        {
            _siteListsBusy = true;
            try
            {
                using (var request = UnityWebRequest.Get(url))
                {
                    request.SetRequestHeader("Authorization", "Bearer " + token);
                    request.timeout = 15;
                    yield return request.SendWebRequest();

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        ComplainSiteLists($"site lists: {request.responseCode} {request.error}");
                        yield break;
                    }

                    ApplySiteLists(request.downloadHandler.text);
                }
            }
            finally
            {
                _siteListsBusy = false;
            }
        }

        private static void ComplainSiteLists(string problem)
        {
            if (problem == _siteListsLastProblem) return;
            _siteListsLastProblem = problem;
            Log.LogWarning($"[AstvardServerMod] {problem}");
        }

        /// <summary>
        /// Writes what the site says the lists should be. The answer has two sections,
        /// «[permitted]» and «[admins]», one id per line, already in the form the game
        /// reads.
        ///
        /// An empty permitted list is never written: to the game an empty permittedlist.txt
        /// means "let everyone in", which is the opposite of what a list is for. An answer
        /// without the section at all is not an empty list but a broken answer, and it
        /// changes nothing either.
        /// </summary>
        private static void ApplySiteLists(string text)
        {
            var permitted = new List<string>();
            var admins = new List<string>();
            List<string> section = null;
            var sawPermitted = false;
            var sawAdmins = false;

            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line == "[permitted]") { section = permitted; sawPermitted = true; continue; }
                if (line == "[admins]") { section = admins; sawAdmins = true; continue; }
                section?.Add(line);
            }

            if (!sawPermitted || !sawAdmins)
            {
                ComplainSiteLists("site lists: the answer has no [permitted]/[admins] sections, nothing written");
                return;
            }

            _siteListsLastProblem = null;
            var dir = Utils.GetSaveDataPath(FileHelpers.FileSource.Local);

            if (permitted.Count == 0)
                ComplainSiteLists("site lists: permitted list is empty on the site, permittedlist.txt left as it is");
            else
                WriteListIfChanged(System.IO.Path.Combine(dir, "permittedlist.txt"), permitted);

            WriteListIfChanged(System.IO.Path.Combine(dir, "adminlist.txt"), admins);
        }

        /// <summary>
        /// Replaces a list file only when its entries differ, through a temporary file and a
        /// rename. The rename is what gives the fresh modification time SyncedList.Load waits
        /// for; rewriting an unchanged file every minute would only make the game re-read it
        /// for nothing.
        /// </summary>
        private static void WriteListIfChanged(string path, List<string> entries)
        {
            try
            {
                if (System.IO.File.Exists(path))
                {
                    var current = new List<string>();
                    foreach (var line in System.IO.File.ReadAllLines(path))
                    {
                        var trimmed = line.Trim();
                        // The game writes its own comment line into these files.
                        if (trimmed.Length == 0 || trimmed.StartsWith("//")) continue;
                        current.Add(trimmed);
                    }
                    if (current.Count == entries.Count && !current.Exists(e => !entries.Contains(e))) return;
                }

                var temp = path + ".tmp";
                System.IO.File.WriteAllLines(temp, entries);
                if (System.IO.File.Exists(path)) System.IO.File.Replace(temp, path, null);
                else System.IO.File.Move(temp, path);

                Log.LogInfo($"[AstvardServerMod] Site lists: {System.IO.Path.GetFileName(path)} now has {entries.Count} lines.");
            }
            catch (System.Exception ex)
            {
                ComplainSiteLists($"site lists: could not write {System.IO.Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }
}
