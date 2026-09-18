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

            /// <summary>Server copies only: an admin has opened it to every player.</summary>
            public bool ForPlayers;

            /// <summary>Server copies only: the players it is open to by name («#allow»), besides everyone.</summary>
            public List<string> AllowedPlayers = new List<string>();

            /// <summary>Server copies only: sent in by a player, whose platform id this is.</summary>
            public string From;
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
        private static int _shownShared;
        private static string _sharedCategory = "";

        // The first shown row of the list on screen. Categories keep theirs while one of them
        // is open, so «Назад» comes back to the same place; a category's list starts at the top.
        private static int _categoryOffset;
        private static int _itemOffset;

        private static void SetFieldText(GameObject inputGo, string text)
        {
            var field = inputGo != null
                ? inputGo.GetComponentInChildren<UnityEngine.UI.InputField>(true)
                : null;
            if (field != null) field.text = text ?? "";
        }

        private static string FieldText(GameObject inputGo, string fallback)
        {
            var field = inputGo != null
                ? inputGo.GetComponentInChildren<UnityEngine.UI.InputField>(true)
                : null;
            return field == null || string.IsNullOrEmpty(field.text) ? fallback : field.text;
        }

        /// <summary>
        /// Relabels the fixed button pools from the current folder contents. The pools
        /// never grow, so the counts are what the visibility checks go by.
        /// </summary>
        private static void RebuildTemplateViews()
        {
            var sharedPage = MenuState == StateSharedCategories;
            var shared = SharedShown();
            var categories = sharedPage ? SharedCategories(shared) : TemplateCategories();

            // Clamped only on its own page: every refresh passes through here, and a window
            // kept for a page not on screen must not be cut to the length of another list.
            if (IsCategoryPage(MenuState))
                _categoryOffset = MenuPaging.Clamp(_categoryOffset, categories.Count, MaxTemplateButtons);
            _shownCategories = IsCategoryPage(MenuState)
                ? MenuPaging.Shown(_categoryOffset, categories.Count, MaxTemplateButtons)
                : 0;

            for (var i = 0; i < MaxTemplateButtons; i++)
            {
                var text = "";
                if (i < _shownCategories)
                {
                    var category = categories[_categoryOffset + i];
                    var count = sharedPage ? SharedIn(shared, category).Count : TemplatesIn(category).Count;
                    text = $"{category} ({count})";
                }

                SetLabel(CategoryButtons[i], text);
            }

            var shown = TemplatesIn(_templateCategory);
            if (MenuState == StateTemplateList)
                _itemOffset = MenuPaging.Clamp(_itemOffset, shown.Count, MaxTemplateButtons);
            _shownTemplates = MenuState == StateTemplateList
                ? MenuPaging.Shown(_itemOffset, shown.Count, MaxTemplateButtons)
                : 0;

            for (var i = 0; i < MaxTemplateButtons; i++)
                SetLabel(TemplateButtons[i], i < _shownTemplates
                    ? $"{shown[_itemOffset + i].Name} ({shown[_itemOffset + i].Pieces})"
                    : "");

            SetLabel(TemplateMineButton, $"Мои шаблоны ({Templates.Count})");
            SetLabel(TemplateSharedButton, $"Шаблоны сервера ({shared.Count})");

            UpdateTemplateHints();
            RebuildSharedViews();
        }

        /// <summary>Relabels the admin's list of one shared category from whatever the server last sent.</summary>
        private static void RebuildSharedViews()
        {
            var shown = SharedIn(SharedTemplates, _sharedCategory);
            if (MenuState == StateSharedList)
                _itemOffset = MenuPaging.Clamp(_itemOffset, shown.Count, MaxTemplateButtons);
            _shownShared = MenuState == StateSharedList
                ? MenuPaging.Shown(_itemOffset, shown.Count, MaxTemplateButtons)
                : 0;

            for (var i = 0; i < MaxTemplateButtons; i++)
                SetLabel(SharedButtons[i], i < _shownShared
                    ? $"{shown[_itemOffset + i].Name} ({shown[_itemOffset + i].Pieces})"
                    : "");

            var hint = SharedHint != null
                ? SharedHint.GetComponentInChildren<UnityEngine.UI.Text>(true)
                : null;
            if (hint == null) return;

            if (MenuState == StateSharedItem && _selectedShared != null)
            {
                hint.text = $"{_selectedShared.Name}{NEWLINE}{_selectedShared.Pieces} деталей, "
                            + $"категория «{_selectedShared.Category}»."
                            + (string.IsNullOrEmpty(_selectedShared.Author)
                                ? ""
                                : $"{NEWLINE}Выложил: {_selectedShared.Author}")
                            + SharedAccessNote(_selectedShared);
                return;
            }

            hint.text = $"{_sharedCategory}: {shown.Count}." + WindowNote(_itemOffset, shown.Count);
        }

        /// <summary>«Шаблоны» from either «Постройки»: first which library, one's own or the server's.</summary>
        private static void OpenTemplateSources()
        {
            MenuState = StateTemplateSource;
            // The folder read on the way in, so a file dropped there while the game ran shows up
            // without a restart; and the server asked for its list, which the other half counts.
            ReloadTemplates();
            AskSharedList();
            RefreshMenu();
        }

        /// <summary>The server's templates this client browses: all of them for an admin, the ones opened to this player otherwise.</summary>
        private static List<SharedTemplate> SharedShown()
        {
            return IsAdminUnlocked ? SharedTemplates : PlayerTemplates;
        }

        private static string CategoryOf(SharedTemplate template)
        {
            return string.IsNullOrEmpty(template.Category) ? DefaultCategory : template.Category;
        }

        private static List<string> SharedCategories(List<SharedTemplate> list)
        {
            return list.Select(CategoryOf)
                .Distinct()
                .OrderBy(category => category, System.StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static List<SharedTemplate> SharedIn(List<SharedTemplate> list, string category)
        {
            return list.Where(template => CategoryOf(template) == category)
                .OrderBy(template => template.Name, System.StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// What the admin's «Шаблоны игрокам» lists: every server template open to someone, to
        /// all or to players named on the site. Not this client's own list, which is only what
        /// is open to the admin himself.
        /// </summary>
        private static List<SharedTemplate> AllowedTemplates()
        {
            return SharedTemplates.Where(template => template.ForAll || template.Listed > 0)
                .OrderBy(CategoryOf, System.StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(template => template.Name, System.StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static bool IsListPage(int state)
        {
            return state == StateTemplates || state == StateTemplateList || state == StateSharedCategories
                   || state == StateSharedList || state == StatePlayerTemplates || state == StateAllowedList
                   || state == StateRunes || state == StateSortZones;
        }

        private static bool IsCategoryPage(int state)
        {
            return state == StateTemplates || state == StateSharedCategories;
        }

        /// <summary>Whether the list page on screen is one this client is shown at all.</summary>
        private static bool ListPageOpen(bool admin, bool copying, bool sharedOpen)
        {
            switch (MenuState)
            {
                case StateTemplates:
                case StateTemplateList:
                    return copying;
                case StateSharedCategories:
                    return sharedOpen;
                case StateSharedList:
                case StateAllowedList:
                case StateRunes:
                    return admin;
                case StateSortZones:
                    return RuleAllows("sort");
                case StatePlayerTemplates:
                    return !admin;
                default:
                    return false;
            }
        }

        /// <summary>How long the list on the current page is.</summary>
        private static int CurrentListTotal()
        {
            switch (MenuState)
            {
                case StateTemplates: return TemplateCategories().Count;
                case StateTemplateList: return TemplatesIn(_templateCategory).Count;
                case StateSharedCategories: return SharedCategories(SharedShown()).Count;
                case StateSharedList: return SharedIn(SharedTemplates, _sharedCategory).Count;
                case StatePlayerTemplates: return SharedIn(PlayerTemplates, _sharedCategory).Count;
                case StateAllowedList: return AllowedTemplates().Count;
                case StateRunes: return RosterShown().Count;
                case StateSortZones: return SortingZones().Count;
                default: return 0;
            }
        }

        private static int ListOffset()
        {
            return IsCategoryPage(MenuState) ? _categoryOffset : _itemOffset;
        }

        private static void ScrollList(int delta)
        {
            if (!IsListPage(MenuState)) return;

            var total = CurrentListTotal();
            if (IsCategoryPage(MenuState))
                _categoryOffset = MenuPaging.Step(_categoryOffset, total, MaxTemplateButtons, delta);
            else
                _itemOffset = MenuPaging.Step(_itemOffset, total, MaxTemplateButtons, delta);

            RefreshMenu();
        }

        /// <summary>
        /// From Update: the mouse wheel over the panel scrolls the list on it a row at a time.
        /// Only over the panel - the wheel elsewhere belongs to the game - and the camera does
        /// not read it while the inventory is open, which is the only time the panel is.
        /// </summary>
        internal static void TickListScroll()
        {
            if (Panel == null || !Panel.activeInHierarchy || !IsListPage(MenuState)) return;

            var wheel = ZInput.GetMouseScrollWheel();
            if (wheel == 0f) return;

            var rect = Panel.GetComponent<RectTransform>();
            var canvas = Panel.GetComponentInParent<Canvas>();
            var eye = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            if (rect == null || !RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition, eye)) return;

            if (CurrentListTotal() <= MaxTemplateButtons) return;
            ScrollList(wheel > 0f ? -1 : 1);
        }

        private static string WindowNote(int offset, int total)
        {
            var window = MenuPaging.Window(offset, total, MaxTemplateButtons);
            return window.Length == 0 ? "" : NEWLINE + window;
        }

        private static void UpdateTemplateHints()
        {
            var hint = TemplateHint != null
                ? TemplateHint.GetComponentInChildren<UnityEngine.UI.Text>(true)
                : null;
            if (hint != null)
            {
                var admin = IsAdminUnlocked;
                switch (MenuState)
                {
                    case StateTemplateSource:
                        hint.text = admin
                            ? $"Мои — на этом компьютере.{NEWLINE}Шаблоны сервера — всё, что{NEWLINE}лежит на сервере; открытые{NEWLINE}игрокам они видят у себя."
                            : $"Мои — постройки, которые{NEWLINE}ты сохранил сам. Шаблоны{NEWLINE}сервера — открытые тебе{NEWLINE}админом, их не переименовать.";
                        break;

                    case StateTemplates:
                        var myCategories = TemplateCategories();
                        hint.text = Templates.Count == 0
                            ? (admin
                                ? $"Шаблонов нет.{NEWLINE}Скопируй постройку и сохрани{NEWLINE}её из «Скопировать»."
                                : $"Шаблонов нет.{NEWLINE}Сохрани свою постройку —{NEWLINE}«Сохранить постройку».")
                            : $"Мои шаблоны: {Templates.Count},{NEWLINE}категорий: {myCategories.Count}."
                              + WindowNote(_categoryOffset, myCategories.Count);
                        break;

                    case StateTemplateList:
                        var inCategory = TemplatesIn(_templateCategory);
                        hint.text = $"{_templateCategory}: {inCategory.Count}." + WindowNote(_itemOffset, inCategory.Count);
                        break;

                    case StateSharedCategories:
                        var library = SharedShown();
                        var libraryCategories = SharedCategories(library);
                        hint.text = library.Count == 0
                            ? (admin
                                ? $"На сервере пусто.{NEWLINE}Открой свой шаблон и нажми{NEWLINE}«Выложить на сервер»."
                                : "Админ пока ничего не открыл.")
                            : (admin ? $"На сервере: {library.Count}," : $"Открыто тебе: {library.Count},")
                              + $"{NEWLINE}категорий: {libraryCategories.Count}."
                              + WindowNote(_categoryOffset, libraryCategories.Count);
                        break;
                }
            }

            var editHint = TemplateEditHint != null
                ? TemplateEditHint.GetComponentInChildren<UnityEngine.UI.Text>(true)
                : null;
            if (editHint == null) return;

            editHint.text = _editingTemplate == null
                ? "Шаблон не выбран."
                : $"{_editingTemplate.Name}{NEWLINE}{_editingTemplate.Pieces} деталей, "
                  + $"категория «{_editingTemplate.Category}»."
                  + (string.IsNullOrEmpty(_editingTemplate.Author)
                      ? ""
                      : $"{NEWLINE}Автор: {_editingTemplate.Author}")
                  + SharedAccessNote(FindShared(_editingTemplate.Name));
        }

        // ---------------- reading ----------------

        internal static void ReloadTemplates()
        {
            Templates.Clear();

            try
            {
                System.IO.Directory.CreateDirectory(TemplatesDir);

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
            else if (key == "players") template.ForPlayers = value == "yes";
            else if (key == "allow") template.AllowedPlayers = SiteSync.ParseIds(value);
            else if (key == "from") template.From = value;
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
            // Содержимое сундуков едет рядом, тем же порядком: выравнивание двигает и
            // поворачивает детали, но не меняет их число и очерёдность.
            var items = new List<string>();
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
                items.Add(piece.Items);

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

                var line = string.Format(culture, "{0};{1};{2};{3};{4};{5};{6};{7}",
                    placed[i].Key,
                    v.x - cx, v.y, v.z - cz,
                    0f, Mathf.Sin(half), 0f, Mathf.Cos(half));

                // Девятым полем — то, что лежало в сундуке. Его нет у всего остального,
                // и старые шаблоны без него читаются как читались.
                if (!string.IsNullOrEmpty(items[i])) line = line + ";" + items[i];
                lines.Add(line);
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

    }
}
