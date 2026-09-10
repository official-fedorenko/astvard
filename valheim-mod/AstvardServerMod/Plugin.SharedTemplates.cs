using System.Collections.Generic;
using BepInEx;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>
        /// A template held by the server. Only the description travels with the listing;
        /// the pieces are fetched when somebody actually wants a copy, so opening the
        /// list costs a few hundred bytes rather than every build at once.
        /// </summary>
        internal sealed class SharedTemplate
        {
            public string Name;
            public string Category;
            public string Author;
            public int Pieces;

            /// <summary>An admin has opened it to players: it shows in their «Постройки».</summary>
            public bool ForPlayers;
        }

        private const string SharedFolder = "astvard-templates-shared";

        private const string RpcTplPush = "AstvardTplPush";
        private const string RpcTplDelete = "AstvardTplDelete";
        private const string RpcTplQuery = "AstvardTplQuery";
        private const string RpcTplList = "AstvardTplList";
        private const string RpcTplGet = "AstvardTplGet";
        private const string RpcTplBody = "AstvardTplBody";
        private const string RpcTplPlayers = "AstvardTplPlayers";

        // The mark a shared template carries while players may build it. A header line,
        // so the file stays the whole record and a restart keeps what was opened.
        private const string PlayersHeader = "#players yes";

        // Records are newline separated, fields tab separated; both are stripped from
        // anything a player can type, so a name cannot split its own record.
        private const char FieldSeparator = '\t';

        internal static readonly List<SharedTemplate> SharedTemplates = new List<SharedTemplate>();

        private static string SharedDir
        {
            get { return System.IO.Path.Combine(Paths.ConfigPath, SharedFolder); }
        }

        internal static void RegisterTemplateRpcs()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null) return;

            rpc.Register<string, string, string, string>(RpcTplPush, OnTemplatePush);
            rpc.Register<string>(RpcTplDelete, OnTemplateDelete);
            rpc.Register(RpcTplQuery, OnTemplateQuery);
            rpc.Register<string>(RpcTplList, OnTemplateList);
            rpc.Register<string>(RpcTplGet, OnTemplateGet);
            rpc.Register<string, string, string, string>(RpcTplBody, OnTemplateBody);
            rpc.Register<string, bool>(RpcTplPlayers, OnTemplatePlayers);
            RegisterBuildPauseRpcs(rpc);
        }

        // ---------------- server side ----------------

        private static string SharedPath(string name)
        {
            return System.IO.Path.Combine(SharedDir, SafeFileName(name) + ".txt");
        }

        private static void OnTemplatePush(long sender, string name, string category,
                                           string author, string body)
        {
            if (!ServerAllows(sender)) return;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(body)) return;

            try
            {
                System.IO.Directory.CreateDirectory(SharedDir);

                // A newer version keeps what the old one was open to: fixing a build the
                // players already have should not quietly take it away from them.
                var path = SharedPath(name);
                var before = System.IO.File.Exists(path) ? ReadTemplate(path) : null;

                var lines = new List<string>
                {
                    "# astvard shared template",
                    "#name " + name,
                    "#category " + category,
                };
                if (!string.IsNullOrEmpty(author)) lines.Add("#author " + author);
                if (before != null && before.ForPlayers) lines.Add(PlayersHeader);
                lines.AddRange(body.Split('\n'));

                System.IO.File.WriteAllLines(path, lines);
                Log.LogInfo($"[AstvardServerMod] Shared template '{name}' received from {sender}.");
            }
            catch (System.Exception ex)
            {
                Log.LogError($"[AstvardServerMod] Could not store shared template: {ex.Message}");
                return;
            }

            BroadcastSharedList();
        }

        private static void OnTemplateDelete(long sender, string name)
        {
            if (!ServerAllows(sender)) return;

            try
            {
                var path = SharedPath(name);
                if (!System.IO.File.Exists(path)) return;

                // Same rule as locally: moved aside, not destroyed.
                var bin = System.IO.Path.Combine(SharedDir, "deleted");
                System.IO.Directory.CreateDirectory(bin);

                var target = System.IO.Path.Combine(bin, System.IO.Path.GetFileName(path));
                if (System.IO.File.Exists(target)) System.IO.File.Delete(target);
                System.IO.File.Move(path, target);

                Log.LogInfo($"[AstvardServerMod] Shared template '{name}' removed.");
            }
            catch (System.Exception ex)
            {
                Log.LogError($"[AstvardServerMod] Could not remove shared template: {ex.Message}");
                return;
            }

            BroadcastSharedList();
        }

        /// <summary>Opens a shared template to players, or closes it to them again.</summary>
        private static void OnTemplatePlayers(long sender, string name, bool allow)
        {
            if (!ServerAllows(sender)) return;

            try
            {
                var path = SharedPath(name);
                if (!System.IO.File.Exists(path))
                {
                    Log.LogWarning($"[AstvardServerMod] No shared template '{name}' to open or close to players.");
                    return;
                }

                var lines = new List<string>(System.IO.File.ReadAllLines(path));
                lines.RemoveAll(line => line.Trim().StartsWith("#players", System.StringComparison.OrdinalIgnoreCase));
                if (allow) lines.Insert(lines.Count > 0 ? 1 : 0, PlayersHeader);
                System.IO.File.WriteAllLines(path, lines);

                Log.LogInfo($"[AstvardServerMod] Shared template '{name}' {(allow ? "opened" : "closed")} to players.");
            }
            catch (System.Exception ex)
            {
                Log.LogError($"[AstvardServerMod] Could not change who may build '{name}': {ex.Message}");
                return;
            }

            BroadcastSharedList();
        }

        private static void OnTemplateQuery(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            ReplySharedList(sender);
            ReplyBuildRules(sender);
        }

        private static void BroadcastSharedList()
        {
            ReplySharedList(0L);
        }

        private static void ReplySharedList(long target)
        {
            var packed = new System.Text.StringBuilder();

            try
            {
                System.IO.Directory.CreateDirectory(SharedDir);
                foreach (var path in System.IO.Directory.GetFiles(SharedDir, "*.txt"))
                {
                    var template = ReadTemplate(path);
                    if (template == null) continue;

                    if (packed.Length > 0) packed.Append('\n');
                    packed.Append(template.Name).Append(FieldSeparator)
                          .Append(template.Category).Append(FieldSeparator)
                          .Append(template.Author).Append(FieldSeparator)
                          .Append(template.Pieces).Append(FieldSeparator)
                          .Append(template.ForPlayers ? "1" : "0");
                }
            }
            catch (System.Exception ex)
            {
                Log.LogError($"[AstvardServerMod] Could not list shared templates: {ex.Message}");
                return;
            }

            ZRoutedRpc.instance?.InvokeRoutedRPC(target, RpcTplList, packed.ToString());
        }

        private static void OnTemplateGet(long sender, string name)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            var template = ReadTemplate(SharedPath(name));
            if (template == null) return;

            // The shared library is an admin tool - the buttons for it are drawn only
            // for an admin. This is the one shared payload that is never broadcast, so
            // without a check here a modded client could read a name off the freely
            // broadcast list and pull the whole blueprint down. Players get what was
            // opened to them through AstvardTplBuild, which also keeps their pause.
            if (!ServerAllows(sender)) return;

            ZRoutedRpc.instance?.InvokeRoutedRPC(sender, RpcTplBody,
                template.Name, template.Category, template.Author,
                string.Join("\n", template.Lines));
        }

        // ---------------- client side ----------------

        private static void OnTemplateList(long sender, string packed)
        {
            SharedTemplates.Clear();

            if (!string.IsNullOrEmpty(packed))
            {
                foreach (var record in packed.Split('\n'))
                {
                    var parts = record.Split(FieldSeparator);
                    if (parts.Length < 4) continue;

                    int pieces;
                    int.TryParse(parts[3], out pieces);

                    SharedTemplates.Add(new SharedTemplate
                    {
                        Name = parts[0],
                        Category = parts[1],
                        Author = parts[2],
                        Pieces = pieces,
                        ForPlayers = parts.Length > 4 && parts[4] == "1",
                    });
                }
            }

            // The page open on a server template keeps showing it, with what is true now.
            if (_selectedShared != null) _selectedShared = FindShared(_selectedShared.Name) ?? _selectedShared;
            RebuildPlayerTemplates();
            RefreshMenu();
        }

        private static void OnTemplateBody(long sender, string name, string category,
                                           string author, string body)
        {
            if (string.IsNullOrEmpty(body)) return;

            // Asked for to build, not to keep: a player's pick from their «Постройки».
            if (_awaitedBuild != null && _awaitedBuild == name)
            {
                _awaitedBuild = null;
                StartPlayerBuild(name, body);
                return;
            }

            // Only a body we asked for.
            //
            // A routed RPC is delivered to whatever peer the sender names, and the relay
            // does not care who sent it or what it is - so this handler, which writes a
            // file and takes a name from the packet, was reachable by any modded client
            // on any other client. Sent in a loop it fills the templates folder with
            // attacker-chosen content. Nothing was overwritten, because the loop below
            // walks to a free name, but junk that arrives faster than you can delete it
            // is damage enough.
            //
            // The same shape as the admin grant: remember what was asked for, and drop
            // anything else. It closes the hole without a rights check, which would be
            // wrong here - the server answers this one to an admin who asked.
            if (_awaitedShared == null || _awaitedShared != name)
            {
                Log.LogWarning($"[AstvardServerMod] Unsolicited template '{name}' "
                               + $"from {sender}; ignored.");
                return;
            }

            _awaitedShared = null;

            var path = System.IO.Path.Combine(TemplatesDir, SafeFileName(name) + ".txt");
            // Two people can easily name a build the same thing, and a copy taken from
            // the server must not quietly replace something of your own.
            var attempt = 2;
            while (System.IO.File.Exists(path))
            {
                path = System.IO.Path.Combine(TemplatesDir, SafeFileName(name) + " " + attempt + ".txt");
                attempt++;
            }

            var taken = new BlueprintTemplate
            {
                Name = name,
                Category = category,
                Author = author,
                Path = path,
                Lines = body.Split('\n'),
            };
            taken.Pieces = taken.Lines.Length;

            if (!WriteTemplate(taken)) return;

            ReloadTemplates();
            RefreshMenu();

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, $"Забрано: {name}");
        }

        internal static void RequestSharedList()
        {
            SharedTemplates.Clear();
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcTplQuery);
        }

        /// <summary>Asks for the list without emptying the one shown in the meantime.</summary>
        internal static void AskSharedList()
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcTplQuery);
        }

        internal static void PushTemplate(BlueprintTemplate template, bool say = true)
        {
            if (template == null) return;

            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcTplPush,
                template.Name, template.Category,
                string.IsNullOrEmpty(template.Author) ? LocalPlayerName() : template.Author,
                string.Join("\n", template.Lines));

            if (say)
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    $"Отправлено: {template.Name}");
        }

        /// <summary>What we last asked the server for; see OnTemplateBody.</summary>
        private static string _awaitedShared;

        internal static void TakeSharedTemplate(SharedTemplate template)
        {
            if (template == null) return;

            _awaitedShared = template.Name;
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcTplGet, template.Name);
        }

        internal static void DeleteSharedTemplate(SharedTemplate template)
        {
            if (template == null) return;
            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcTplDelete, template.Name);
        }
    }
}
