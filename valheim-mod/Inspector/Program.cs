using System;
using System.Linq;
using System.Reflection;

string vdir = @"C:\Games\steamapps\common\Valheim\valheim_Data\Managed";
string jotunn = @"C:\Users\offic\Downloads\ValheimModding-Jotunn-2.29.2\plugins\Jotunn.dll";
var files = Directory.GetFiles(vdir, "*.dll").Append(jotunn).ToArray();
var resolver = new PathAssemblyResolver(files);
using var mlc = new MetadataLoadContext(resolver);
var asm = mlc.LoadFromAssemblyPath(jotunn);

var gm = asm.GetTypes().First(x => x.Name == "GUIManager");
foreach (var m in gm.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
    if (m.Name is "CreateButton" or "CreateText")
        Console.WriteLine($"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");

// Which Text type does the game use for buttons at all?
var uasm = mlc.LoadFromAssemblyPath(Path.Combine(vdir, "assembly_valheim.dll"));
var pc = uasm.GetTypes().First(x => x.Name == "Piece");
Console.WriteLine("\nPhysics buffer size check — Piece static fields:");
foreach (var f in pc.GetFields(BindingFlags.NonPublic | BindingFlags.Static))
    Console.WriteLine($"  {f.FieldType.Name} {f.Name}");
