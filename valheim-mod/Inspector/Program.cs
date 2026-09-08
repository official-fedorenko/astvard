using System;
using System.Linq;
using System.Reflection;

string vdir = @"C:\Games\steamapps\common\Valheim\valheim_Data\Managed";
var files = Directory.GetFiles(vdir, "*.dll").ToArray();
using var mlc = new MetadataLoadContext(new PathAssemblyResolver(files));
var asm = mlc.LoadFromAssemblyPath(Path.Combine(vdir, "assembly_valheim.dll"));

void Dump(string typeName, Func<MemberInfo, bool>? filter = null)
{
    var t = asm.GetTypes().FirstOrDefault(x => x.Name == typeName);
    if (t == null) { Console.WriteLine($"!! {typeName} not found"); return; }

    Console.WriteLine($"=== {typeName} : fields ===");
    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                                  BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                       .Where(f => filter == null || filter(f)).OrderBy(f => f.Name))
        Console.WriteLine($"  {(f.IsStatic ? "static " : "")}{(f.IsPublic ? "public " : "private ")}{f.FieldType.Name} {f.Name}");

    Console.WriteLine($"=== {typeName} : methods ===");
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                       .Where(m => filter == null || filter(m)).OrderBy(m => m.Name))
        Console.WriteLine($"  {(m.IsStatic ? "static " : "")}{(m.IsPublic ? "public " : "private ")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
    Console.WriteLine();
}

bool Weatherish(MemberInfo m) =>
    m.Name.Contains("Wind", StringComparison.OrdinalIgnoreCase) ||
    m.Name.Contains("Env", StringComparison.OrdinalIgnoreCase) ||
    m.Name.Contains("Weather", StringComparison.OrdinalIgnoreCase) ||
    m.Name.Contains("debug", StringComparison.OrdinalIgnoreCase) ||
    m.Name.Contains("Force", StringComparison.OrdinalIgnoreCase);

Dump("EnvMan", Weatherish);
Dump("EnvSetup", m => m is FieldInfo f && (f.Name == "m_name" || f.Name.Contains("Wind") || f.Name.Contains("m_is")));
