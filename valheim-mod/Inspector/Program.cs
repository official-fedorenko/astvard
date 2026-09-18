using System.Reflection;

// Проверяет, на месте ли всё, к чему мод обращается по строковому имени.
//
// Компилятор такие обращения не видит: AccessTools и Harmony ищут член по строке,
// и когда игра его переименовала, вызов возвращает null, а патч тихо не встаёт.
// Ошибки при этом нет нигде — ни в сборке, ни в логе. Отсюда правило в CLAUDE.md
// «обновление игры ломает рефлексию молча»; это его проверка.
//
//   dotnet run --project valheim-mod/Inspector -- <папка Managed> <файл целей>
//
// Файл целей — строки «Тип|член|откуда», его собирает scripts/collect_targets.py.
// Выход: 0 — все на месте, 1 — что-то потерялось.

if (args.Length < 2)
{
    Console.Error.WriteLine("нужно: <папка Managed> <файл целей>");
    return 2;
}

var managed = args[0];
var targetsFile = args[1];

if (!Directory.Exists(managed))
{
    Console.Error.WriteLine($"нет папки {managed}");
    return 2;
}

var files = Directory.GetFiles(managed, "*.dll");
using var mlc = new MetadataLoadContext(new PathAssemblyResolver(files));

// Тип может лежать в любой из сборок игры, а вложенный — внутри другого типа.
var assemblies = files
    .Select(f => { try { return mlc.LoadFromAssemblyPath(f); } catch { return null; } })
    .Where(a => a != null)
    .ToList();

Type? FindType(string name)
{
    var reflectionName = name.Replace('.', '+');

    foreach (var asm in assemblies)
    {
        var type = asm!.GetType(name) ?? asm.GetType(reflectionName);
        if (type != null) return type;
    }

    // Последняя попытка: по короткому имени, если тип лежит в пространстве имён.
    var shortName = name.Split('.').Last();
    foreach (var asm in assemblies)
    {
        var type = asm!.GetTypes().FirstOrDefault(t => t.Name == shortName);
        if (type != null) return type;
    }

    return null;
}

const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
    | BindingFlags.Instance | BindingFlags.Static;

var missingTypes = 0;
var missingMembers = 0;
var inherited = 0;
var checkedCount = 0;

foreach (var line in File.ReadAllLines(targetsFile))
{
    if (string.IsNullOrWhiteSpace(line)) continue;

    var parts = line.Split('|');
    var typeName = parts[0].Trim();
    var member = parts[1].Trim();
    var where = parts.Length > 2 ? parts[2].Trim() : "";

    checkedCount++;

    var type = FindType(typeName);
    if (type == null)
    {
        Console.WriteLine($"  НЕТ ТИПА   {typeName}   ({where})");
        missingTypes++;
        continue;
    }

    // Объявлен прямо в типе — то, что нужно и Harmony, и AccessTools.
    var declared = type.GetMethods(All | BindingFlags.DeclaredOnly).Any(m => m.Name == member)
        || type.GetFields(All | BindingFlags.DeclaredOnly).Any(f => f.Name == member)
        || type.GetProperties(All | BindingFlags.DeclaredOnly).Any(p => p.Name == member);

    if (declared) continue;

    // Достался по наследству: AccessTools найдёт, а Harmony на такой цели
    // пропатчит базовый тип — то есть не то, что задумано. Стоит знать.
    var fromBase = type.GetMethods(All).Any(m => m.Name == member)
        || type.GetFields(All).Any(f => f.Name == member)
        || type.GetProperties(All).Any(p => p.Name == member);

    if (fromBase)
    {
        var owner = type.GetMethods(All).FirstOrDefault(m => m.Name == member)?.DeclaringType?.Name
            ?? type.GetFields(All).FirstOrDefault(f => f.Name == member)?.DeclaringType?.Name;
        Console.WriteLine($"  по наследству  {typeName}.{member} объявлен в {owner}   ({where})");
        inherited++;
        continue;
    }

    Console.WriteLine($"  ПОТЕРЯН    {typeName}.{member}   ({where})");

    // Подсказка: похожие имена в том же типе — переименование видно сразу.
    var similar = type.GetMethods(All | BindingFlags.DeclaredOnly).Select(m => m.Name)
        .Concat(type.GetFields(All | BindingFlags.DeclaredOnly).Select(f => f.Name))
        .Distinct()
        .Where(n => n.Contains(member, StringComparison.OrdinalIgnoreCase)
                 || member.Contains(n, StringComparison.OrdinalIgnoreCase))
        .Take(6)
        .ToList();

    if (similar.Count > 0) Console.WriteLine($"             похожее: {string.Join(", ", similar)}");

    missingMembers++;
}

Console.WriteLine();
Console.WriteLine($"  проверено целей: {checkedCount}");
Console.WriteLine($"  потеряно членов: {missingMembers}, не найдено типов: {missingTypes}, по наследству: {inherited}");

return missingMembers + missingTypes > 0 ? 1 : 0;
