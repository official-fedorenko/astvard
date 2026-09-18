using System;
using System.Collections.Generic;
using System.Text;

namespace AstvardServerMod
{
    /// <summary>
    /// Ведомость рун: у кого сколько, одним списком и в панели игры, и на сайте.
    ///
    /// Two halves that meet only here. The game server owns the balances - it counts the
    /// hours and it will bill a purchase - and knows a player by the platform id the socket
    /// reported and by the character name it last saw. The site owns the nicknames people
    /// chose for themselves and knows everyone who ever registered, including those who
    /// have not come to the server yet. Both halves are keyed by the same SteamID64: it is
    /// literally the number in the purse file and in users.steam_id, so the join costs
    /// nothing and needs no table of its own.
    ///
    /// Kept out of the Plugin, like Geometry.cs and SiteSync.cs, so the tests can hold it
    /// to its edges with no game installed: which name wins, who stands where in the list,
    /// what the search finds, and what a grant may do to a balance. The site repeats these
    /// same rules in its own language - it cannot call this file - so a change here is a
    /// change there too.
    /// </summary>
    public static class Roster
    {
        public const char Tab = '\t';

        public const char RowBreak = '\n';

        /// <summary>
        /// The most one press may move a balance. A rune is an hour of play, so this is
        /// already far past generous; it is here to make a typo an odd number rather than
        /// a balance nobody can undo.
        /// </summary>
        public const int MaxGrant = 10000;

        /// <summary>Above this a balance is not a balance but an accident.</summary>
        public const int MaxBalance = 1000000;

        /// <summary>
        /// One line of the ledger. Half of it comes from the server, half from the site,
        /// and either half may be missing: a player who never registered has no Site, one
        /// who registered and never came has no Character and no balance.
        /// </summary>
        public sealed class Row
        {
            /// <summary>SteamID64, the key both halves are keyed by.</summary>
            public string Id;

            /// <summary>The nickname on the site, empty when there is no account.</summary>
            public string Site;

            /// <summary>The character name the server saw last, empty when never on.</summary>
            public string Character;

            public int Balance;

            /// <summary>Has a purse: has been on the server at least once.</summary>
            public bool Played;

            public bool Online;
        }

        /// <summary>
        /// What to call this player. The nickname from the site wins because the person
        /// chose it themselves and it is the name the admin sees everywhere else; the
        /// character name is what is left when there is no account, and the bare id is
        /// what is left when there is neither.
        /// </summary>
        public static string Display(Row row)
        {
            if (row == null) return "";
            if (!string.IsNullOrEmpty(row.Site)) return row.Site;
            if (!string.IsNullOrEmpty(row.Character)) return row.Character;
            return row.Id ?? "";
        }

        /// <summary>
        /// Puts the two halves together by id and sorts them for the admin.
        ///
        /// Rows are matched on Id alone: a purse carries Id, Character and Balance, an
        /// account carries Id and Site. A row that arrives from both sides keeps both
        /// names. Anything with no id at all is dropped - it cannot be paid and cannot be
        /// found - and a repeated id is merged instead of doubled.
        /// </summary>
        public static List<Row> Merge(IEnumerable<Row> purses, IEnumerable<Row> accounts,
            IEnumerable<string> online)
        {
            var byId = new Dictionary<string, Row>();

            Take(byId, purses, true);
            Take(byId, accounts, false);

            if (online != null)
            {
                foreach (var id in online)
                {
                    var key = Clean(id);
                    if (key.Length == 0) continue;
                    // Somebody on the server right now who is in neither half still belongs
                    // in the list: that is exactly the person an admin is about to pay.
                    if (!byId.TryGetValue(key, out var row))
                    {
                        row = new Row { Id = key, Site = "", Character = "" };
                        byId[key] = row;
                    }

                    row.Online = true;
                }
            }

            var rows = new List<Row>(byId.Values);
            Sort(rows);
            return rows;
        }

        private static void Take(Dictionary<string, Row> byId, IEnumerable<Row> rows, bool fromServer)
        {
            if (rows == null) return;

            foreach (var incoming in rows)
            {
                if (incoming == null) continue;

                var key = Clean(incoming.Id);
                if (key.Length == 0) continue;

                if (!byId.TryGetValue(key, out var row))
                {
                    row = new Row { Id = key, Site = "", Character = "" };
                    byId[key] = row;
                }

                var site = Clean(incoming.Site);
                var character = Clean(incoming.Character);

                if (site.Length > 0) row.Site = site;
                if (character.Length > 0) row.Character = character;
                if (incoming.Online) row.Online = true;

                // The balance is the server's word and only the server's: an account half
                // carries no balance, and reading a zero from it would wipe a real purse.
                if (fromServer)
                {
                    row.Balance = ClampBalance(incoming.Balance);
                    row.Played = true;
                }
            }
        }

        /// <summary>
        /// Who stands where: the people on the server right now, then everyone who has ever
        /// played, then those who only registered. Inside a group - by name, and by id when
        /// the names are equal, so the list never reshuffles under the admin's finger.
        /// </summary>
        public static void Sort(List<Row> rows)
        {
            if (rows == null) return;

            rows.Sort((a, b) =>
            {
                var group = Group(a).CompareTo(Group(b));
                if (group != 0) return group;

                var byName = string.Compare(Fold(Display(a)), Fold(Display(b)), StringComparison.Ordinal);
                if (byName != 0) return byName;

                return string.Compare(a.Id ?? "", b.Id ?? "", StringComparison.Ordinal);
            });
        }

        private static int Group(Row row)
        {
            if (row == null) return 3;
            if (row.Online) return 0;
            return row.Played ? 1 : 2;
        }

        /// <summary>
        /// Does this row answer to what was typed. An empty query matches everything, so a
        /// cleared field shows the whole list again. The id is searched too: the admin who
        /// was given a number rather than a name has nothing else to type.
        /// </summary>
        public static bool Matches(Row row, string query)
        {
            if (row == null) return false;

            var needle = Fold(query);
            if (needle.Length == 0) return true;

            return Fold(row.Site).Contains(needle)
                || Fold(row.Character).Contains(needle)
                || Fold(row.Id).Contains(needle);
        }

        /// <summary>The rows worth showing, in the order they are shown.</summary>
        public static List<Row> Search(IEnumerable<Row> rows, string query)
        {
            var found = new List<Row>();
            if (rows == null) return found;

            foreach (var row in rows)
                if (Matches(row, query)) found.Add(row);

            Sort(found);
            return found;
        }

        /// <summary>
        /// Case and «ё» folded away. Somebody looking for «Пётр» types «Петр» about as
        /// often, and a search that answers one and not the other reads as a broken list
        /// rather than as a spelling.
        /// </summary>
        public static string Fold(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Trim().ToLowerInvariant().Replace('ё', 'е');
        }

        /// <summary>Whatever cannot travel in a tab-separated line, gone before it is written.</summary>
        public static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace(Tab, ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
        }

        /// <summary>A hand-typed number held to something a balance can survive.</summary>
        public static int ClampGrant(int amount)
        {
            if (amount > MaxGrant) return MaxGrant;
            if (amount < -MaxGrant) return -MaxGrant;
            return amount;
        }

        public static int ClampBalance(int balance)
        {
            if (balance < 0) return 0;
            return balance > MaxBalance ? MaxBalance : balance;
        }

        /// <summary>
        /// What a grant leaves behind. Taking away more than there is empties the purse
        /// instead of turning it over: a negative balance would be a debt nobody agreed to,
        /// and the first thing it would do is make the next earned rune invisible.
        /// </summary>
        public static int ApplyGrant(int balance, int amount)
        {
            return ClampBalance(ClampBalance(balance) + ClampGrant(amount));
        }

        /// <summary>
        /// The ledger as it travels from the server to an admin's panel: one row a line,
        /// fields tab-separated, in the order they are read back.
        /// </summary>
        public static string Pack(IEnumerable<Row> rows)
        {
            var packed = new StringBuilder();
            if (rows == null) return "";

            foreach (var row in rows)
            {
                if (row == null || string.IsNullOrEmpty(Clean(row.Id))) continue;

                if (packed.Length > 0) packed.Append(RowBreak);
                packed.Append(Clean(row.Id)).Append(Tab)
                      .Append(ClampBalance(row.Balance)).Append(Tab)
                      .Append(row.Played ? '1' : '0').Append(Tab)
                      .Append(row.Online ? '1' : '0').Append(Tab)
                      .Append(Clean(row.Site)).Append(Tab)
                      .Append(Clean(row.Character));
            }

            return packed.ToString();
        }

        /// <summary>
        /// Reads back what Pack wrote. A line with fewer fields than it should have is
        /// dropped rather than guessed at, and extra fields at the end are ignored on
        /// purpose: a newer server may say more about a player than this client knows to
        /// ask, and that is not a reason to show an empty list.
        /// </summary>
        public static List<Row> Parse(string packed)
        {
            var rows = new List<Row>();
            if (string.IsNullOrEmpty(packed)) return rows;

            foreach (var line in packed.Split('\n'))
            {
                var text = line.Trim('\r');
                if (text.Length == 0) continue;

                var parts = text.Split(Tab);
                if (parts.Length < 4) continue;

                var id = Clean(parts[0]);
                if (id.Length == 0) continue;

                int.TryParse(parts[1], out var balance);

                rows.Add(new Row
                {
                    Id = id,
                    Balance = ClampBalance(balance),
                    Played = parts[2] == "1",
                    Online = parts[3] == "1",
                    Site = parts.Length > 4 ? Clean(parts[4]) : "",
                    Character = parts.Length > 5 ? Clean(parts[5]) : "",
                });
            }

            Sort(rows);
            return rows;
        }
    }
}
