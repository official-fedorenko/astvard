using System;
using System.Linq;
using System.Reflection;

string vdir = @"C:\Games\steamapps\common\Valheim\valheim_Data\Managed";
string jotunn = @"C:\Users\offic\Downloads\ValheimModding-Jotunn-2.29.2\plugins\Jotunn.dll";
var files = Directory.GetFiles(vdir, "*.dll").Append(jotunn).ToArray();
var resolver = new PathAssemblyResolver(files);
using var mlc = new MetadataLoadContext(resolver);
var asm = mlc.LoadFromAssemblyPath(jotunn);

var mm = asm.GetTypes().FirstOrDefault(x => x.Name == "MinimapManager");
if (mm == null)
{
    Console.WriteLine("no MinimapManager in Jotunn");
}
else
{
    Console.WriteLine("=== MinimapManager public API ===");
    foreach (var m in mm.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                        .OrderBy(m => m.Name))
        Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");

    foreach (var t in mm.GetNestedTypes(BindingFlags.Public))
    {
        Console.WriteLine($"\n=== nested {t.Name} ===");
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Console.WriteLine($"  prop {p.PropertyType.Name} {p.Name}");
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            Console.WriteLine($"  field {f.FieldType.Name} {f.Name}");
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(x => x.ParameterType.Name))})");
    }
}
