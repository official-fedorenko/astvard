using System;
using System.Linq;
using System.Reflection;
string dir = @"C:\Games\steamapps\common\Valheim dedicated server\valheim_server_Data\Managed";
var resolver = new PathAssemblyResolver(Directory.GetFiles(dir, "*.dll"));
using var mlc = new MetadataLoadContext(resolver);
var asm = mlc.LoadFromAssemblyPath(Path.Combine(dir, "assembly_valheim.dll"));
var hm = asm.GetTypes().First(x => x.Name == "Heightmap");
foreach (var n in new[]{"WorldToVertex","GetHeight","GetWorldHeight","m_scale","m_width"})
{
    var m = hm.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).FirstOrDefault(x=>x.Name==n);
    if (m != null) Console.WriteLine($"method {n}: public={m.IsPublic} ({string.Join(",", m.GetParameters().Select(p=>p.ParameterType.Name+" "+p.Name))}) -> {m.ReturnType.Name}");
    var f = hm.GetField(n, BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);
    if (f != null) Console.WriteLine($"field {n}: public={f.IsPublic} type={f.FieldType.Name}");
}
