using System.Collections.Generic;
using System.Linq;
using BepInEx;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>
        /// A saved build. The file is the whole record — name, category and author ride
        /// in its header — so a template can be added, renamed or handed to someone else
        /// without the mod being rebuilt. That was the point: every template added
        /// before this went through a compile and a redeploy.
        /// </summary>
        internal sealed class BlueprintTemplate
        {
            public string Name;
            public string Category;
            public string Author;
            public string Path;
            public string[] Lines;
            public int Pieces;
        }

        private const string TemplateFolder = "astvard-templates";
        private const string DefaultCategory = "Разное";

        internal static readonly List<BlueprintTemplate> Templates = new List<BlueprintTemplate>();

        private static string TemplatesDir
        {
            get { return System.IO.Path.Combine(Paths.ConfigPath, TemplateFolder); }
        }

        // ---------------- what the menu is currently showing ----------------

        private static string _templateCategory = "";
        private static BlueprintTemplate _editingTemplate;
        private static SharedTemplate _selectedShared;
        private static int _shownCategories;
        private static int _shownTemplates;

        private static string FieldText(GameObject inputGo, string fallback)
        {
            var field = inputGo != null
                ? inputGo.GetComponentInChildren<UnityEngine.UI.InputField>()
                : null;
            return field == null || string.IsNullOrEmpty(field.text) ? fallback : field.text;
        }

        /// <summary>
        /// Relabels the fixed button pools from the current folder contents. The pools
        /// never grow, so the counts are what the visibility checks go by.
        /// </summary>
        private static void RebuildTemplateViews()
        {
            var categories = TemplateCategories();
            _shownCategories = Mathf.Min(categories.Count, MaxTemplateButtons);

            for (var i = 0; i < MaxTemplateButtons; i++)
            {
                var label = CategoryButtons[i] != null
                    ? CategoryButtons[i].GetComponentInChildren<UnityEngine.UI.Text>()
                    : null;
                if (label == null) continue;

                label.text = i < categories.Count
                    ? $"{categories[i]} ({TemplatesIn(categories[i]).Count})"
                    : "";
            }

            var shown = TemplatesIn(_templateCategory);
            _shownTemplates = Mathf.Min(shown.Count, MaxTemplateButtons);

            for (var i = 0; i < MaxTemplateButtons; i++)
            {
                var label = TemplateButtons[i] != null
                    ? TemplateButtons[i].GetComponentInChildren<UnityEngine.UI.Text>()
                    : null;
                if (label == null) continue;

                label.text = i < shown.Count ? $"{shown[i].Name} ({shown[i].Pieces})" : "";
            }

            UpdateTemplateHints(categories.Count, shown.Count);
            RebuildSharedViews();
        }

        /// <summary>Relabels the server-side list from whatever the server last sent.</summary>
        private static void RebuildSharedViews()
        {
            for (var i = 0; i < MaxTemplateButtons; i++)
            {
                var label = SharedButtons[i] != null
                    ? SharedButtons[i].GetComponentInChildren<UnityEngine.UI.Text>()
                    : null;
                if (label == null) continue;

                label.text = i < SharedTemplates.Count
                    ? $"{SharedTemplates[i].Name} ({SharedTemplates[i].Pieces})"
                    : "";
            }

            var hint = SharedHint != null
                ? SharedHint.GetComponentInChildren<UnityEngine.UI.Text>()
                : null;
            if (hint == null) return;

            if (MenuState == StateSharedItem && _selectedShared != null)
            {
                hint.text = $"{_selectedShared.Name}{NEWLINE}{_selectedShared.Pieces} деталей, "
                            + $"категория «{_selectedShared.Category}»."
                            + (string.IsNullOrEmpty(_selectedShared.Author)
                                ? ""
                                : $"{NEWLINE}Выложил: {_selectedShared.Author}");
                return;
            }

            hint.text = SharedTemplates.Count == 0
                ? $"На сервере пусто.{NEWLINE}Открой свой шаблон и нажми{NEWLINE}«Выложить на сервер»."
                : $"На сервере: {SharedTemplates.Count}."
                  + (SharedTemplates.Count > MaxTemplateButtons
                      ? $"{NEWLINE}Показаны первые {MaxTemplateButtons}."
                      : "");
        }

        private static void UpdateTemplateHints(int categoryCount, int shownCount)
        {
            var hint = TemplateHint != null
                ? TemplateHint.GetComponentInChildren<UnityEngine.UI.Text>()
                : null;
            if (hint != null)
            {
                if (Templates.Count == 0)
                    hint.text = $"Шаблонов нет.{NEWLINE}Скопируй постройку и сохрани{NEWLINE}её из «Скопировать».";
                else if (MenuState == StateTemplateList)
                    hint.text = $"{_templateCategory}: {shownCount}"
                                + (shownCount > MaxTemplateButtons
                                    ? $".{NEWLINE}Показаны первые {MaxTemplateButtons}."
                                    : ".");
                else
                    hint.text = $"Шаблонов: {Templates.Count}, категорий: {categoryCount}."
                                + (categoryCount > MaxTemplateButtons
                                    ? $"{NEWLINE}Показаны первые {MaxTemplateButtons}."
                                    : "");
            }

            var editHint = TemplateEditHint != null
                ? TemplateEditHint.GetComponentInChildren<UnityEngine.UI.Text>()
                : null;
            if (editHint == null) return;

            editHint.text = _editingTemplate == null
                ? "Шаблон не выбран."
                : $"{_editingTemplate.Name}{NEWLINE}{_editingTemplate.Pieces} деталей, "
                  + $"категория «{_editingTemplate.Category}»."
                  + (string.IsNullOrEmpty(_editingTemplate.Author)
                      ? ""
                      : $"{NEWLINE}Автор: {_editingTemplate.Author}");
        }

        // ---------------- reading ----------------

        internal static void ReloadTemplates()
        {
            Templates.Clear();

            try
            {
                System.IO.Directory.CreateDirectory(TemplatesDir);
                SeedBuiltInTemplates();

                foreach (var path in System.IO.Directory.GetFiles(TemplatesDir, "*.txt"))
                {
                    var template = ReadTemplate(path);
                    if (template != null) Templates.Add(template);
                }
            }
            catch (System.Exception ex)
            {
                Log.LogError($"[AstvardServerMod] Could not read templates: {ex.Message}");
            }

            Templates.Sort((a, b) =>
            {
                var byCategory = string.Compare(a.Category, b.Category,
                    System.StringComparison.CurrentCultureIgnoreCase);
                return byCategory != 0
                    ? byCategory
                    : string.Compare(a.Name, b.Name, System.StringComparison.CurrentCultureIgnoreCase);
            });

            Log.LogInfo($"[AstvardServerMod] Templates: {Templates.Count} in "
                        + $"{TemplateCategories().Count} categories.");
        }

        private static BlueprintTemplate ReadTemplate(string path)
        {
            try
            {
                var all = System.IO.File.ReadAllLines(path);

                var template = new BlueprintTemplate
                {
                    Path = path,
                    Name = System.IO.Path.GetFileNameWithoutExtension(path),
                    Category = DefaultCategory,
                    Author = "",
                };

                var body = new List<string>();
                foreach (var line in all)
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;

                    if (trimmed[0] == '#')
                    {
                        ReadHeader(trimmed, template);
                        continue;
                    }

                    // Anything that is not eight fields is a comment as far as the
                    // loader is concerned, which is what lets headers cost nothing.
                    if (trimmed.Split(';').Length == 8) body.Add(trimmed);
                }

                if (body.Count == 0) return null;

                template.Lines = body.ToArray();
                template.Pieces = body.Count;
                return template;
            }
            catch (System.Exception ex)
            {
                Log.LogWarning($"[AstvardServerMod] Skipped {System.IO.Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }

        private static void ReadHeader(string line, BlueprintTemplate template)
        {
            var space = line.IndexOf(' ');
            if (space < 0) return;

            var key = line.Substring(1, space - 1).Trim().ToLowerInvariant();
            var value = line.Substring(space + 1).Trim();
            if (value.Length == 0) return;

            if (key == "name") template.Name = value;
            else if (key == "category") template.Category = value;
            else if (key == "author") template.Author = value;
        }

        internal static List<string> TemplateCategories()
        {
            var categories = new List<string>();
            foreach (var template in Templates)
                if (!categories.Contains(template.Category)) categories.Add(template.Category);
            return categories;
        }

        internal static List<BlueprintTemplate> TemplatesIn(string category)
        {
            return Templates.Where(t => t.Category == category).ToList();
        }

        // ---------------- writing ----------------

        private static string CleanForHeader(string value, string fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            var cleaned = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return cleaned.Length == 0 ? fallback : cleaned;
        }

        private static string SafeFileName(string name)
        {
            var safe = name;
            foreach (var bad in System.IO.Path.GetInvalidFileNameChars())
                safe = safe.Replace(bad, '_');
            return safe.Trim();
        }

        internal static bool WriteTemplate(BlueprintTemplate template)
        {
            try
            {
                System.IO.Directory.CreateDirectory(TemplatesDir);

                var lines = new List<string>
                {
                    "# astvard template",
                    "#name " + template.Name,
                    "#category " + template.Category,
                };
                if (!string.IsNullOrEmpty(template.Author)) lines.Add("#author " + template.Author);
                lines.Add("# prefab;posX;posY;posZ;rotX;rotY;rotZ;rotW");
                lines.AddRange(template.Lines);

                System.IO.File.WriteAllLines(template.Path, lines);
                return true;
            }
            catch (System.Exception ex)
            {
                Log.LogError($"[AstvardServerMod] Could not write template: {ex.Message}");
                return false;
            }
        }

        internal static bool SaveClipboardAsTemplate(string name, string category, string author)
        {
            if (Clipboard.Count == 0) return false;

            name = CleanForHeader(name, "Без имени");
            category = CleanForHeader(category, DefaultCategory);

            var lines = AlignClipboard();

            var path = System.IO.Path.Combine(TemplatesDir, SafeFileName(name) + ".txt");
            // Never overwrite silently: two builds under one name would be one lost build.
            var attempt = 2;
            while (System.IO.File.Exists(path))
            {
                path = System.IO.Path.Combine(TemplatesDir, SafeFileName(name) + " " + attempt + ".txt");
                attempt++;
            }

            var template = new BlueprintTemplate
            {
                Name = name,
                Category = category,
                Author = CleanForHeader(author, ""),
                Path = path,
                Lines = lines,
                Pieces = lines.Length,
            };

            if (!WriteTemplate(template)) return false;

            ReloadTemplates();
            Log.LogInfo($"[AstvardServerMod] Saved template '{name}' ({category}), {lines.Length} pieces.");
            return true;
        }

        internal static bool RenameTemplate(BlueprintTemplate template, string name, string category)
        {
            template.Name = CleanForHeader(name, template.Name);
            template.Category = CleanForHeader(category, template.Category);

            if (!WriteTemplate(template)) return false;
            ReloadTemplates();
            return true;
        }

        /// <summary>
        /// Moved aside rather than removed. A template is a build someone copied, and
        /// the build itself may be long gone from the world by now.
        /// </summary>
        internal static bool DeleteTemplate(BlueprintTemplate template)
        {
            try
            {
                var bin = System.IO.Path.Combine(TemplatesDir, "deleted");
                System.IO.Directory.CreateDirectory(bin);

                var target = System.IO.Path.Combine(bin, System.IO.Path.GetFileName(template.Path));
                if (System.IO.File.Exists(target)) System.IO.File.Delete(target);
                System.IO.File.Move(template.Path, target);

                ReloadTemplates();
                Log.LogInfo($"[AstvardServerMod] Template '{template.Name}' moved to deleted/.");
                return true;
            }
            catch (System.Exception ex)
            {
                Log.LogError($"[AstvardServerMod] Could not delete template: {ex.Message}");
                return false;
            }
        }

        // ---------------- alignment ----------------

        /// <summary>
        /// Squares a copied build to its own axes before it is stored. A build is never
        /// copied straight: the player was facing some arbitrary direction, and often
        /// standing on a step, so without this every template is a degree or two out and
        /// a few centimetres off the floor grid.
        /// </summary>
        private static string[] AlignClipboard()
        {
            const float yawGrid = 22.5f;
            const float grid = 0.5f;
            const float snapTolerance = 0.02f;

            var yaws = Clipboard.Select(p => Geometry.YawOf(p.LocalRot.y, p.LocalRot.w)).ToList();
            var baseYaw = Geometry.BestBaseYaw(yaws, yawGrid, 11.25f);
            var worst = Geometry.WorstYawError(yaws, baseYaw, yawGrid);

            var placed = new List<KeyValuePair<string, Vector4>>();
            var xs = new List<float>();
            var ys = new List<float>();
            var zs = new List<float>();

            for (var i = 0; i < Clipboard.Count; i++)
            {
                var piece = Clipboard[i];
                var flat = Geometry.RotateXZ(new Vec2(piece.LocalPos.x, piece.LocalPos.z), -baseYaw);
                var yaw = Geometry.SnapYaw(yaws[i], baseYaw, yawGrid);

                placed.Add(new KeyValuePair<string, Vector4>(piece.Prefab,
                    new Vector4(flat.X, piece.LocalPos.y, flat.Z, yaw)));

                xs.Add(flat.X);
                ys.Add(piece.LocalPos.y);
                zs.Add(flat.Z);
            }

            // The copy origin sits at the player's feet, so a build copied from a step
            // is off the grid vertically as well as horizontally.
            var dx = Geometry.BestGridShift(xs, grid);
            var dy = Geometry.BestGridShift(ys, grid);
            var dz = Geometry.BestGridShift(zs, grid);

            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var lines = new List<string>();
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;

            var shifted = new List<Vector4>();
            foreach (var entry in placed)
            {
                var x = SnapToGrid(entry.Value.x + dx, grid, snapTolerance);
                var y = SnapToGrid(entry.Value.y + dy, grid, snapTolerance);
                var z = SnapToGrid(entry.Value.z + dz, grid, snapTolerance);
                shifted.Add(new Vector4(x, y, z, entry.Value.w));

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (z < minZ) minZ = z;
                if (z > maxZ) maxZ = z;
            }

            // Re-centre so a template sits on the player rather than beside them.
            var cx = Mathf.Round((minX + maxX) * 0.5f / grid) * grid;
            var cz = Mathf.Round((minZ + maxZ) * 0.5f / grid) * grid;

            for (var i = 0; i < shifted.Count; i++)
            {
                var v = shifted[i];
                var half = v.w * Mathf.Deg2Rad * 0.5f;

                lines.Add(string.Format(culture, "{0};{1};{2};{3};{4};{5};{6};{7}",
                    placed[i].Key,
                    v.x - cx, v.y, v.z - cz,
                    0f, Mathf.Sin(half), 0f, Mathf.Cos(half)));
            }

            Log.LogInfo($"[AstvardServerMod] Aligned: base {baseYaw:F3} deg, worst piece "
                        + $"{worst:F4} deg, shift {dx:F3}/{dy:F3}/{dz:F3}.");
            return lines.ToArray();
        }

        private static float SnapToGrid(float value, float grid, float tolerance)
        {
            var snapped = Mathf.Round(value / grid) * grid;
            return Mathf.Abs(snapped - value) < tolerance ? snapped : value;
        }

        // ---------------- first run ----------------

        /// <summary>
        /// The templates that used to be compiled in are written out once, so an
        /// existing setup keeps everything it had and the arrays can eventually go.
        /// </summary>
        private static void SeedBuiltInTemplates()
        {
            SeedOne("Платформа 4х4", "Площадки", BuiltInTemplates.Platform4x4);
            SeedOne("Стартовый дом №1", "Дома", BuiltInTemplates.StarterHouse1);
            SeedOne("Полная кухня", "Кухни", BuiltInTemplates.KitchenFull);
            SeedOne("Плавильня", "Переработка", BuiltInTemplates.SmelterHall);
            SeedOne("Угольные печи", "Переработка", BuiltInTemplates.CharcoalKilns);
        }

        private static void SeedOne(string name, string category, string[] lines)
        {
            var path = System.IO.Path.Combine(TemplatesDir, SafeFileName(name) + ".txt");
            var bin = System.IO.Path.Combine(TemplatesDir, "deleted", SafeFileName(name) + ".txt");

            // Seeding is once and for all: a template the admin deleted must not come
            // back on the next start.
            if (System.IO.File.Exists(path) || System.IO.File.Exists(bin)) return;

            WriteTemplate(new BlueprintTemplate
            {
                Name = name,
                Category = category,
                Author = "",
                Path = path,
                Lines = lines,
                Pieces = lines.Length,
            });
        }
    }
}
