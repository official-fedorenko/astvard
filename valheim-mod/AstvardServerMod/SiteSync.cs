using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AstvardServerMod
{
    /// <summary>
    /// The text the mod and the site exchange about what players may build: lines of
    /// tab-separated fields, the same shape in both directions.
    ///
    /// Kept out of the Plugin so the tests can compile it on their own, as they do
    /// Geometry.cs. This is the part where one wrong split quietly closes a template to
    /// everyone or opens it to all, and none of it needs the game to be checked.
    ///
    /// The site answers a pull with
    ///
    ///     revision  12
    ///     rule      floor   2
    ///     rule      copy    2    20
    ///     tpl       Верстачня   0   76561198000000001,76561198000000002
    ///
    /// or with «unchanged» when the mod already has that revision, or with «seed needed»
    /// when the site has never been told anything and wants what the server holds.
    /// </summary>
    public static class SiteSync
    {
        public const char Tab = '\t';

        /// <summary>The header a shared template carries while every player may build it.</summary>
        public const string PlayersHeader = "#players yes";

        /// <summary>The header naming the players who may build it besides: «#allow id,id».</summary>
        public const string AllowHeader = "#allow";

        // SteamID64 is seventeen digits; the bounds only keep out what is plainly not an id.
        private const int MinIdLength = 5;

        private const int MaxIdLength = 20;

        // A template open to more people than this is open to everyone, and the header
        // line would be longer than the rest of the file.
        public const int MaxPlayersPerTemplate = 500;

        public sealed class RuleState
        {
            public string Key;

            public int Value;

            public int? Limit;
        }

        public sealed class TemplateAccess
        {
            public string Name;

            public bool ForAll;

            public List<string> Players = new List<string>();
        }

        public sealed class Pulled
        {
            /// <summary>False when the answer is not a builds state at all: an error page, a proxy's HTML.</summary>
            public bool Valid;

            public int Revision;

            public bool SeedNeeded;

            public bool Unchanged;

            public readonly List<RuleState> Rules = new List<RuleState>();

            public readonly List<TemplateAccess> Templates = new List<TemplateAccess>();
        }

        public static Pulled ParsePull(string text)
        {
            var pulled = new Pulled();
            if (string.IsNullOrEmpty(text)) return pulled;

            var lines = text.Replace("\r", "").Split('\n');
            var first = lines[0].Split(Tab);
            if (first.Length != 2 || first[0] != "revision" || !TryInt(first[1], out var revision) || revision < 0)
                return pulled;

            pulled.Valid = true;
            pulled.Revision = revision;

            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;

                var fields = lines[i].Split(Tab);
                if (fields[0] == "seed") pulled.SeedNeeded = true;
                else if (fields[0] == "unchanged") pulled.Unchanged = true;
                else if (fields[0] == "rule") AddRule(pulled, fields);
                else if (fields[0] == "tpl") AddTemplate(pulled, fields);
            }

            return pulled;
        }

        // A line that does not read is skipped rather than failing the whole answer: one
        // rule this build does not understand must not stop every other rule arriving.
        private static void AddRule(Pulled pulled, string[] fields)
        {
            if (fields.Length < 3 || !IsKey(fields[1]) || !TryInt(fields[2], out var value)) return;

            int? limit = null;
            if (fields.Length > 3 && fields[3].Length > 0)
            {
                if (!TryInt(fields[3], out var parsed)) return;
                limit = parsed;
            }

            pulled.Rules.Add(new RuleState { Key = fields[1], Value = value, Limit = limit });
        }

        private static void AddTemplate(Pulled pulled, string[] fields)
        {
            if (fields.Length < 3 || fields[1].Length == 0) return;
            if (fields[2] != "0" && fields[2] != "1") return;

            pulled.Templates.Add(new TemplateAccess
            {
                Name = fields[1],
                ForAll = fields[2] == "1",
                Players = ParseIds(fields.Length > 3 ? fields[3] : ""),
            });
        }

        public static bool IsKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 24) return false;
            foreach (var ch in key)
                if (ch < 'a' || ch > 'z') return false;
            return true;
        }

        public static bool TryInt(string text, out int value)
        {
            return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Player ids from a comma list, as a template header or the site carries them:
        /// digits only, each once, in a fixed order so two lists compare as text.
        /// </summary>
        public static List<string> ParseIds(string csv)
        {
            var ids = new SortedSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(csv)) return new List<string>();

            foreach (var raw in csv.Split(','))
            {
                var id = raw.Trim();
                if (id.Length < MinIdLength || id.Length > MaxIdLength) continue;

                var digits = true;
                foreach (var ch in id)
                    if (ch < '0' || ch > '9') { digits = false; break; }
                if (!digits) continue;

                ids.Add(id);
                if (ids.Count >= MaxPlayersPerTemplate) break;
            }

            return new List<string>(ids);
        }

        public static string JoinIds(IEnumerable<string> ids)
        {
            var list = new List<string>();
            foreach (var id in ids ?? new string[0]) list.Add(id);
            return string.Join(",", ParseIds(string.Join(",", list)));
        }

        /// <summary>A name as a line can carry it: a tab or a line break inside would split the record.</summary>
        public static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace(Tab, ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        public static string KindLine(string kind)
        {
            return "kind" + Tab + kind;
        }

        public static string RuleLine(RuleState rule)
        {
            return "rule" + Tab + rule.Key + Tab + rule.Value.ToString(CultureInfo.InvariantCulture) + Tab
                   + (rule.Limit.HasValue ? rule.Limit.Value.ToString(CultureInfo.InvariantCulture) : "");
        }

        public static string TemplateLine(TemplateAccess access)
        {
            return "tpl" + Tab + Clean(access.Name) + Tab + (access.ForAll ? "1" : "0") + Tab + JoinIds(access.Players);
        }

        /// <summary>An admin's switch in game: open to all or not, the named players left as they are.</summary>
        public static string TemplateAllLine(string name, bool forAll)
        {
            return "tplall" + Tab + Clean(name) + Tab + (forAll ? "1" : "0");
        }

        public static string MetaLine(string key, string title, string group, string kind,
                                      int min, int max, string word, string note)
        {
            return "meta" + Tab + key + Tab + Clean(title) + Tab + group + Tab + kind + Tab
                   + min.ToString(CultureInfo.InvariantCulture) + Tab + max.ToString(CultureInfo.InvariantCulture) + Tab
                   + Clean(word) + Tab + Clean(note);
        }

        /// <summary>
        /// What a queued change is about - «rule floor», «tplall Верстачня» - so a newer
        /// change to the same thing replaces the older one instead of both being sent.
        /// </summary>
        public static string IdentityOf(string line)
        {
            if (string.IsNullOrEmpty(line)) return "";
            var fields = line.Split(Tab);
            return fields.Length > 1 ? fields[0] + Tab + fields[1] : fields[0];
        }

        /// <summary>The revision a push was answered with, or -1.</summary>
        public static int ParseRevision(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            var first = text.Replace("\r", "").Split('\n')[0].Split(Tab);
            return first.Length == 2 && first[0] == "revision" && TryInt(first[1], out var revision) && revision >= 0
                ? revision
                : -1;
        }

        /// <summary>
        /// Where to talk about builds: the address given for it, or else the one beside the
        /// access lists - «…/api/game/lists» becomes «…/api/game/builds» - so a server that
        /// already fetches its lists needs no new line in its config.
        /// </summary>
        public static string BuildsUrlFrom(string buildsUrl, string listsUrl)
        {
            var own = (buildsUrl ?? "").Trim();
            if (own.Length > 0) return own;

            var lists = (listsUrl ?? "").Trim();
            const string tail = "/lists";
            return lists.EndsWith(tail, StringComparison.Ordinal)
                ? lists.Substring(0, lists.Length - tail.Length) + "/builds"
                : "";
        }

        public static bool HasAccessHeader(string line, out bool forAll, out List<string> players)
        {
            forAll = false;
            players = null;

            var trimmed = (line ?? "").Trim();
            if (trimmed.StartsWith("#players", StringComparison.OrdinalIgnoreCase))
            {
                // The mod reads «#players» only as «yes»; anything else is a closed template.
                forAll = trimmed == PlayersHeader;
                return true;
            }

            if (trimmed.StartsWith(AllowHeader, StringComparison.OrdinalIgnoreCase)
                && (trimmed.Length == AllowHeader.Length || trimmed[AllowHeader.Length] == ' '))
            {
                players = ParseIds(trimmed.Substring(AllowHeader.Length));
                return true;
            }

            return false;
        }

        /// <summary>
        /// A template file with its access headers set to what is wanted, or null when the
        /// file already says exactly that - so a file is rewritten only when something in it
        /// changes, and the pull every few seconds does not touch the disk.
        ///
        /// The headers go in right after the first line, where the mod has always put
        /// «#players yes»; every other line keeps its place.
        /// </summary>
        public static List<string> WithAccessHeaders(IList<string> lines, bool forAll, List<string> players)
        {
            var wanted = ParseIds(JoinIds(players));

            var hasAll = false;
            var hasPlayers = new List<string>();
            var kept = new List<string>();
            foreach (var line in lines ?? new string[0])
            {
                if (HasAccessHeader(line, out var lineAll, out var linePlayers))
                {
                    hasAll |= lineAll;
                    if (linePlayers != null) hasPlayers.AddRange(linePlayers);
                    continue;
                }

                kept.Add(line);
            }

            if (hasAll == forAll && JoinIds(hasPlayers) == string.Join(",", wanted)) return null;

            var headers = new List<string>();
            if (forAll) headers.Add(PlayersHeader);
            if (wanted.Count > 0) headers.Add(AllowHeader + " " + string.Join(",", wanted));

            kept.InsertRange(kept.Count > 0 ? 1 : 0, headers);
            return kept;
        }
    }
}
