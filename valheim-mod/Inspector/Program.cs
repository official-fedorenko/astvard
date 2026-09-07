using System;
using System.Linq;
using System.Reflection;
string dir = @"C:\Games\steamapps\common\Valheim dedicated server\valheim_server_Data\Managed";
var resolver = new PathAssemblyResolver(Directory.GetFiles(dir, "*.dll"));
using var mlc = new MetadataLoadContext(resolver);
var asm = mlc.LoadFromAssemblyPath(Path.Combine(dir, "assembly_valheim.dll"));
var zs = asm.GetTypes().First(x => x.Name == "ZoneSystem");
foreach (var m in zs.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static)
    .Where(m => m.Name.Contains("Zone") && (m.Name.Contains("Get") || m.Name.Contains("Pos"))))
    Console.WriteLine($"ZoneSystem.{(m.IsStatic?"static ":"")}{m.ReturnType.Name} {m.Name}({string.Join(",", m.GetParameters().Select(p=>p.ParameterType.Name))}) public={m.IsPublic}");
