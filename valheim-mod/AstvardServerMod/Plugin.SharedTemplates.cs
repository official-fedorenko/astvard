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
        }

        private const string SharedFolder = "astvard-templates-shared";

        private const string RpcTplPush = "AstvardTplPush";
        private const string RpcTplDelete = "AstvardTplDelete";
        private const string RpcTplQuery = "AstvardTplQuery";
        private const string RpcTplList = "AstvardTplList";
        private const string RpcTplGet = "AstvardTplGet";
        private const string RpcTplBody = "AstvardTplBody";

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

                var lines = new List<string>
                {
                    "# astvard shared template",
                    "#name " + name,
                    "#category " + category,
                };
                if (!string.IsNullOrEmpty(author)) lines.Add("#author " + author);
                lines.AddRange(body.Split('\n'));

                System.IO.File.WriteAllLines(SharedPath(name), lines);
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

        private static void OnTemplateQuery(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            ReplySharedList(sender);
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
                          .Append(template.Pieces);
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
            // The shared library is an admin tool - the buttons for it are drawn only
            // for an admin. This is the one shared payload that is never broadcast, so
            // without a check here a modded client could read a name off the freely
            // broadcast list and pull the whole blueprint down.
            if (!ServerAllows(sender)) return;

            var template = ReadTemplate(SharedPath(name));
            if (template == null) return;

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
                    });
                }
            }

            RefreshMenu();
        }

        private static void OnTemplateBody(long sender, string name, string category,
                                           string author, string body)
        {
            if (string.IsNullOrEmpty(body)) return;

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

        internal static void PushTemplate(BlueprintTemplate template)
        {
            if (template == null) return;

            ZRoutedRpc.instance?.InvokeRoutedRPC(RpcTplPush,
                template.Name, template.Category,
                string.IsNullOrEmpty(template.Author) ? LocalPlayerName() : template.Author,
                string.Join("\n", template.Lines));

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
