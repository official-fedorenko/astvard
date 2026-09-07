using System;
using System.Linq;
using System.Reflection;
string dir = @"C:\Games\steamapps\common\Valheim dedicated server\valheim_server_Data\Managed";
var resolver = new PathAssemblyResolver(Directory.GetFiles(dir, "*.dll"));
using var mlc = new MetadataLoadContext(resolver);
var asm = mlc.LoadFromAssemblyPath(Path.Combine(dir, "assembly_valheim.dll"));
var t = asm.GetTypes().First(x => x.Name == "EnvMan");
foreach (var f in t.GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static))
    if (f.Name.ToLower().Contains("time") || f.Name.ToLower().Contains("debug") || f.Name.ToLower().Contains("day"))
        Console.WriteLine($"EnvMan field: {(f.IsStatic?"static ":"")}{(f.IsPublic?"pub":"prv")} {f.FieldType.Name} {f.Name}");
