using System;
using System.Linq;
using System.Reflection;

string vdir = @"C:\Games\steamapps\common\Valheim\valheim_Data\Managed";
string jotunn = @"C:\Users\offic\Downloads\ValheimModding-Jotunn-2.29.2\plugins\Jotunn.dll";
var files = Directory.GetFiles(vdir, "*.dll").Append(jotunn).ToArray();
using var mlc = new MetadataLoadContext(new PathAssemblyResolver(files));
var asm = mlc.LoadFromAssemblyPath(jotunn);

var gm = asm.GetTypes().First(x => x.Name == "GUIManager");
Console.WriteLine("=== GUIManager: ввод и фокус ===");
foreach (var m in gm.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name.Contains("Block") || m.Name.Contains("Input") || m.Name.Contains("Focus"))
                    .OrderBy(m => m.Name))
    Console.WriteLine($"  {(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
