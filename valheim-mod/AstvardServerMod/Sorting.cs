using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AstvardServerMod
{
    /// <summary>
    /// Куда класть предмет: решение сортировщика, без Unity и без игры.
    ///
    /// The sorter itself is a walk over the containers around the player; this is the part
    /// of it that decides anything, and the part where being wrong is expensive. A chest
    /// that is not marked is a source - things are carried out of it - so reading «not
    /// marked» as a category would turn every chest on the base into a bin and shuffle it
    /// into itself. That is one off-by-one away, and it is why the mark is stored one
    /// higher than the category it means.
    ///
    /// Kept beside Geometry.cs and Roster.cs for the same reason: the game cannot be asked
    /// about any of this, and the tests can.
    /// </summary>
    public static class Sorting
    {
        /// <summary>
        /// Groups a person would name, not the game's own item types. «Разное» is first
        /// because it is also the answer for everything that fits nowhere else.
        /// </summary>
        public static readonly string[] CategoryTitles =
        {
            "Разное", "Материалы", "Еда", "Оружие", "Броня", "Инструменты", "Трофеи", "Лут"
        };

        /// <summary>The catch-all: what an item goes to when no bin wants it by name.</summary>
        public const int Misc = 0;

        // The rest, in the order of the titles above. Named, because the mapping from
        // the game's item types lives in another file and a number would drift from it.
        public const int Materials = 1;

        public const int Food = 2;

        public const int Weapons = 3;

        public const int Armour = 4;

        public const int Tools = 5;

        public const int Trophies = 6;

        public const int Loot = 7;

        /// <summary>
        /// Что падает с убитого. Отличить это по типу предмета нельзя: для игры шкура,
        /// потроха и глаз грейдворфа - такой же «материал», как дерево и камень, так что
        /// здесь их приходится знать по именам.
        ///
        /// Ключи перевода, а не русские слова: `m_shared.m_name` - это `$item_deerhide`
        /// на любом языке игры. Список сверен по данным игры, а не по памяти; то, чего в
        /// нём не хватает, добавляется потом - для этого есть Chosen ниже.
        /// </summary>
        private static readonly HashSet<string> LootKeys = new HashSet<string>
        {
            "$item_leatherscraps", "$item_deerhide", "$item_trollhide", "$item_wolfpelt",
            "$item_loxpelt", "$item_scalehide", "$item_chitin", "$item_carapace",
            "$item_entrails", "$item_guck", "$item_greydwarfeye", "$item_bonefragments",
            "$item_witheredbone", "$item_charredbone", "$item_freezegland", "$item_softtissue",
            "$item_serpentscale", "$item_wolffang", "$item_hardantler", "$item_queenbee",
            "$item_goblintotem", "$item_ymirremains", "$item_surtlingcore", "$item_ancientseed",
            "$item_wolfhairbundle", "$item_bloodbag", "$item_eyescream", "$item_dragontear",
            "$item_morgenheart", "$item_bonemawtooth", "$item_royaljelly", "$item_feathers",
            "$item_blackmetalscrap", "$item_tar",
        };

        /// <summary>
        /// Что об этом предмете сказали снаружи - с сайта. Перевешивает и список выше, и
        /// тип предмета: список мода знает ваниль, а базу держат люди, и спорить с ними
        /// о том, где у них лежит смола, моду нечем.
        /// </summary>
        private static readonly Dictionary<string, int> Chosen = new Dictionary<string, int>();

        /// <summary>Какую категорию выбрали для этого предмета, или -1 - не выбирали.</summary>
        public static int ChosenFor(string kind)
        {
            int category;
            if (kind == null || !Chosen.TryGetValue(kind, out category)) return -1;

            return IsCategory(category) ? category : -1;
        }

        public static bool IsLoot(string kind)
        {
            return kind != null && LootKeys.Contains(kind);
        }

        /// <summary>Имена, которые мод знает сам - сайту, чтобы было что показывать.</summary>
        public static List<string> KnownLoot()
        {
            var kinds = new List<string>(LootKeys);
            kinds.Sort(System.StringComparer.Ordinal);
            return kinds;
        }

        /// <summary>
        /// Чужое слово о категориях: «ключ=номер», через точку с запятой. Пустая строка
        /// снимает всё сказанное - это не то же самое, что «сайт молчит», и разбирать
        /// молчание должен тот, кто звал.
        /// </summary>
        public static void ReadChosen(string text)
        {
            Chosen.Clear();
            if (string.IsNullOrEmpty(text)) return;

            foreach (var record in text.Split(';'))
            {
                var at = record.IndexOf('=');
                if (at <= 0) continue;

                var kind = record.Substring(0, at).Trim();
                int category;
                if (kind.Length == 0) continue;
                if (!int.TryParse(record.Substring(at + 1).Trim(), NumberStyles.Integer,
                                  Invariant, out category)) continue;
                if (!IsCategory(category)) continue;

                Chosen[kind] = category;
            }
        }

        public static string PackChosen()
        {
            var text = new StringBuilder();
            foreach (var pair in Chosen)
            {
                if (text.Length > 0) text.Append(';');
                text.Append(pair.Key).Append('=').Append(pair.Value.ToString(Invariant));
            }

            return text.ToString();
        }

        public static int Count
        {
            get { return CategoryTitles.Length; }
        }

        public static bool IsCategory(int category)
        {
            return category >= 0 && category < CategoryTitles.Length;
        }

        public static string Title(int category)
        {
            return IsCategory(category) ? CategoryTitles[category] : "?";
        }

        /// <summary>
        /// What goes into the chest's own field. Zero has to keep meaning «not marked», so
        /// every category is written one higher than it is.
        /// </summary>
        public static int ToStored(int category)
        {
            return IsCategory(category) ? category + 1 : 0;
        }

        /// <summary>The category a chest was marked with, or -1 when it was not marked.</summary>
        public static int FromStored(int stored)
        {
            var category = stored - 1;
            return IsCategory(category) ? category : -1;
        }

        /// <summary>Is this chest a destination. Everything else in reach is a source.</summary>
        public static bool IsBin(int stored)
        {
            return FromStored(stored) >= 0;
        }

        /// <summary>
        /// Зона сортировки: где стоят сундуки, которые разбирает сортировщик.
        ///
        /// Square or round, chosen before it is placed, because bases are not round. A
        /// circle wide enough to hold a long hall reaches half of what stands beside it; a
        /// square laid along the hall does not. The shape belongs to the zone rather than to
        /// a setting, so two zones on one base may differ.
        ///
        /// Radius means the reach from the middle either way - for a square that is half its
        /// side. One number, so the ring and the box are the same thing to everything else.
        /// </summary>
        public sealed class Zone
        {
            public float X;

            public float Z;

            public float Radius;

            public bool Square;

            /// <summary>Degrees the square is turned by. A circle has no use for it.</summary>
            public float Angle;

            /// <summary>Что хозяин её назвал. Пусто — зовём по форме и размеру.</summary>
            public string Name;

            /// <summary>
            /// Which zone this is, as the server numbers them. Zero is a zone the server
            /// has never seen: one made offline, or one on its way to being made.
            /// </summary>
            public int Id;

            /// <summary>Whose it is, by platform id. Empty means nobody's but this client's.</summary>
            public string Owner = "";

            /// <summary>Who else was written into it, by platform id.</summary>
            public List<string> Members = new List<string>();

            /// <summary>
            /// Whether this client is the one to sort here. The server decides it and says
            /// so with the zone; two clients emptying one cart between them would move the
            /// same stack twice, and that is an item made or an item lost, silently.
            /// </summary>
            public bool Drive;

            public Zone Copy()
            {
                return new Zone
                {
                    X = X, Z = Z, Radius = Radius, Square = Square, Angle = Angle, Name = Name,
                    Id = Id, Owner = Owner, Members = new List<string>(Members), Drive = Drive,
                };
            }
        }

        /// <summary>
        /// A platform id as we are willing to keep it: digits and nothing else. It travels
        /// in the same line as the zone, so a stray comma or semicolon in it would tear the
        /// record in two and take every zone after it down with the parse.
        /// </summary>
        public static string CleanId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";

            var kept = new StringBuilder();
            foreach (var symbol in id)
            {
                if (symbol < '0' || symbol > '9') continue;
                if (kept.Length >= 20) break;

                kept.Append(symbol);
            }

            return kept.ToString();
        }

        /// <summary>Whether this person has the zone at all: theirs, or written into it.</summary>
        public static bool Sees(Zone zone, string id)
        {
            if (zone == null) return false;

            var who = CleanId(id);
            if (who.Length == 0) return false;
            if (zone.Owner == who) return true;

            if (zone.Members == null) return false;
            foreach (var member in zone.Members)
                if (member == who) return true;

            return false;
        }

        /// <summary>
        /// Whether this person may change the zone: the one who put it down, or an admin.
        /// Being written into a zone is leave to work in it, not leave to move it - the
        /// chests inside are somebody's, and so is the decision about where the zone ends.
        /// </summary>
        public static bool MayEdit(Zone zone, string id, bool admin)
        {
            if (zone == null) return false;
            if (admin) return true;

            var who = CleanId(id);
            return who.Length > 0 && zone.Owner == who;
        }

        public const float MinZoneRadius = 4f;

        // Past this it stops being a base and starts being a valley; the sorter walks every
        // container inside it once a second.
        public const float MaxZoneRadius = 64f;

        public const int MaxZones = 32;

        public static float ClampRadius(float radius)
        {
            if (radius < MinZoneRadius) return MinZoneRadius;
            return radius > MaxZoneRadius ? MaxZoneRadius : radius;
        }

        // A square reaches to its corner, which is further than its half-side, so a scan
        // that asks for a radius has to ask for the diagonal and let Inside throw back
        // whatever fell outside the box.
        public const float SquareDiagonal = 1.415f;

        /// <summary>
        /// Сколько метров вокруг середины зоны спрашивать у игры, чтобы не потерять края.
        ///
        /// The game hands out pieces by a radius measured in three dimensions from one
        /// point, and the point we can give it stands at the player's own height. A radius
        /// that just covers the zone on the map therefore covers almost none of it above
        /// and below that floor: at the corner of a square zone the two distances are
        /// equal, so a chest a metre higher than the player fell outside the scan - not
        /// labelled, not sorted, and nothing said about either. The zone has no height of
        /// its own (Inside asks only for x and z), so the radius is stretched to leave
        /// Headroom metres of it either way. Asking for more costs nothing: the game walks
        /// every piece in the world whatever radius it is given.
        /// </summary>
        public const float Headroom = 32f;

        public static float ScanRadius(Zone zone)
        {
            if (zone == null) return 0f;

            var reach = ClampRadius(zone.Radius) * (zone.Square ? SquareDiagonal : 1f);
            return (float)Math.Sqrt(reach * reach + Headroom * Headroom);
        }

        /// <summary>Is this spot inside the zone. Height is not asked: a cellar is the base too.</summary>
        public static bool Inside(Zone zone, float x, float z)
        {
            if (zone == null) return false;

            var dx = x - zone.X;
            var dz = z - zone.Z;
            var reach = ClampRadius(zone.Radius);

            // A circle looks the same from every side, so its angle is nothing to it.
            if (!zone.Square) return dx * dx + dz * dz <= reach * reach;

            // The square may be turned to lie along the hall, so the spot is asked in
            // the square's own frame rather than in the world's.
            ToLocal(zone.Angle, dx, dz, out var alongX, out var alongZ);
            return Abs(alongX) <= reach && Abs(alongZ) <= reach;
        }

        /// <summary>
        /// Do two zones share any ground. They are not allowed to: a chest inside both
        /// belongs to both, and «why does this chest empty into two places» is a question
        /// nobody should have to ask. Cheaper to refuse the second zone than to explain.
        /// </summary>
        public static bool Overlap(Zone a, Zone b)
        {
            if (a == null || b == null) return false;

            var ar = ClampRadius(a.Radius);
            var br = ClampRadius(b.Radius);
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;

            if (!a.Square && !b.Square) return dx * dx + dz * dz < (ar + br) * (ar + br);
            if (a.Square && b.Square) return BoxesMeet(a, ar, b, br, dx, dz);

            // One of each: the nearest point of the box to the middle of the circle
            // decides, asked in the box's own frame so that a turned box is no harder
            // than a straight one.
            var boxFirst = a.Square;
            var box = boxFirst ? a : b;
            var boxReach = boxFirst ? ar : br;
            var ringReach = boxFirst ? br : ar;
            var toRing = boxFirst ? 1f : -1f;

            ToLocal(box.Angle, dx * toRing, dz * toRing, out var localX, out var localZ);
            var offX = localX - Clamp(localX, -boxReach, boxReach);
            var offZ = localZ - Clamp(localZ, -boxReach, boxReach);
            return offX * offX + offZ * offZ < ringReach * ringReach;
        }

        /// <summary>
        /// Насколько близко надо подойти, чтобы зона считалась работающей.
        ///
        /// Полторы клетки игры (она держит вокруг игрока 3x3 клетки по 64 м): дальше
        /// деталей у клиента может уже не быть вовсе, и обещать там работу нечем. Кайма
        /// решает **только**
        /// то, работает ли зона вообще; что именно она берёт, по-прежнему решает сама зона,
        /// поэтому повозка у ворот, оставленная снаружи, снаружи и останется.
        /// </summary>
        public const float NearReach = 96f;

        /// <summary>
        /// Точка внутри зоны или в кайме вокруг неё.
        ///
        /// Grown here rather than by making a wider Zone and asking Inside: Inside clamps
        /// the radius it is given to MaxZoneRadius, so a zone already at the limit would
        /// have grown by nothing at all, and the apron would have quietly done nothing for
        /// the largest zones - the ones most likely to have a player standing just outside.
        /// </summary>
        public static bool Near(Zone zone, float x, float z, float slack)
        {
            if (zone == null) return false;
            if (slack <= 0f) return Inside(zone, x, z);

            var reach = ClampRadius(zone.Radius) + slack;
            var dx = x - zone.X;
            var dz = z - zone.Z;

            if (!zone.Square) return dx * dx + dz * dz <= reach * reach;

            ToLocal(zone.Angle, dx, dz, out var alongX, out var alongZ);
            return Abs(alongX) <= reach && Abs(alongZ) <= reach;
        }

        /// <summary>The first zone this spot falls into, or -1.</summary>
        public static int ZoneAt(IList<Zone> zones, float x, float z)
        {
            return ZoneAt(zones, x, z, 0f);
        }

        /// <summary>
        /// The first zone this spot falls into, counting an apron of `slack` metres round it.
        ///
        /// A cart is parked where it stops - at the gate, by the path, on the near side of
        /// the wall - and its owner stands beside it, which is a step outside the zone their
        /// chests are in. Read strictly, nothing happens and nothing explains why. The apron
        /// is only about whether the sorter runs at all; what it picks up is decided by the
        /// zone itself, not by this.
        /// </summary>
        public static int ZoneAt(IList<Zone> zones, float x, float z, float slack)
        {
            if (zones == null) return -1;

            for (var i = 0; i < zones.Count; i++)
            {
                var zone = zones[i];
                if (zone == null) continue;

                if (Near(zone, x, z, slack)) return i;
            }

            return -1;
        }

        /// <summary>
        /// The zones as one line of the config. Written by hand into a config file it is not,
        /// but it is read back by a build that may be older or newer, so a record that does
        /// not parse is dropped rather than guessed at.
        /// </summary>
        public static string Pack(IEnumerable<Zone> zones)
        {
            var text = new StringBuilder();
            if (zones == null) return "";

            foreach (var zone in zones)
            {
                if (zone == null) continue;
                if (text.Length > 0) text.Append(';');

                text.Append(zone.X.ToString("F1", Invariant)).Append(',')
                    .Append(zone.Z.ToString("F1", Invariant)).Append(',')
                    .Append(ClampRadius(zone.Radius).ToString("F1", Invariant)).Append(',')
                    .Append(zone.Square ? '1' : '0').Append(',')
                    .Append(NormaliseAngle(zone.Angle).ToString("F1", Invariant)).Append(',')
                    .Append(CleanName(zone.Name)).Append(',')
                    .Append(zone.Id.ToString(Invariant)).Append(',')
                    .Append(CleanId(zone.Owner)).Append(',')
                    .Append(PackMembers(zone.Members)).Append(',')
                    .Append(zone.Drive ? '1' : '0');
            }

            return text.ToString();
        }

        public static List<Zone> Parse(string text)
        {
            var zones = new List<Zone>();
            if (string.IsNullOrEmpty(text)) return zones;

            foreach (var record in text.Split(';'))
            {
                var parts = record.Split(',');
                if (parts.Length < 3) continue;

                float x, z, radius;
                if (!float.TryParse(parts[0], NumberStyles.Float, Invariant, out x)) continue;
                if (!float.TryParse(parts[1], NumberStyles.Float, Invariant, out z)) continue;
                if (!float.TryParse(parts[2], NumberStyles.Float, Invariant, out radius)) continue;

                // The angle came later than the rest: a record written before it is a
                // zone that was never turned, which is exactly what a missing field says.
                float angle;
                if (parts.Length < 5 || !float.TryParse(parts[4], NumberStyles.Float, Invariant, out angle))
                    angle = 0f;

                // Everything past the name came later still, the same way: a record
                // without it is a zone that was only ever this client's.
                int id;
                if (parts.Length < 7 || !int.TryParse(parts[6], NumberStyles.Integer, Invariant, out id))
                    id = 0;

                zones.Add(new Zone
                {
                    X = x,
                    Z = z,
                    Radius = ClampRadius(radius),
                    Square = parts.Length > 3 && parts[3] == "1",
                    Angle = NormaliseAngle(angle),
                    Name = parts.Length > 5 ? CleanName(parts[5]) : "",
                    Id = id < 0 ? 0 : id,
                    Owner = parts.Length > 7 ? CleanId(parts[7]) : "",
                    Members = ParseMembers(parts.Length > 8 ? parts[8] : ""),
                    Drive = parts.Length > 9 && parts[9] == "1",
                });

                if (zones.Count >= MaxZones) break;
            }

            return zones;
        }

        /// <summary>
        /// Имя, которое переживёт запись в строку конфига. Запятая там разделяет поля, а
        /// точка с запятой — записи, так что имя с ними разорвало бы файл на куски и
        /// потеряло бы все зоны следом.
        /// </summary>
        public static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";

            var clean = name.Replace(',', ' ').Replace(';', ' ')
                            .Replace('\n', ' ').Replace('\r', ' ')
                            .Replace('\t', ' ').Trim();

            return clean.Length > MaxZoneName ? clean.Substring(0, MaxZoneName).Trim() : clean;
        }

        /// <summary>Длиннее кнопка всё равно не покажет.</summary>
        public const int MaxZoneName = 24;

        /// <summary>The same turn said the same way, so two of them compare.</summary>
        public static float NormaliseAngle(float angle)
        {
            if (float.IsNaN(angle) || float.IsInfinity(angle)) return 0f;

            angle = angle % 360f;
            return angle < 0f ? angle + 360f : angle;
        }

        // No Unity here, and no Mathf with it.
        private const float Deg2Rad = 0.0174532924f;

        /// <summary>An offset from the middle of a square, seen the way that square lies.</summary>
        private static void ToLocal(float angle, float dx, float dz, out float alongX, out float alongZ)
        {
            var radians = NormaliseAngle(angle) * Deg2Rad;
            var cos = (float)System.Math.Cos(radians);
            var sin = (float)System.Math.Sin(radians);

            alongX = dx * cos + dz * sin;
            alongZ = -dx * sin + dz * cos;
        }

        /// <summary>
        /// Two squares, either of them turned. Four axes - the two sides of one and the
        /// two of the other - and a gap along any single one of them means they do not
        /// meet. Squares nobody turned fall out of this as the plain comparison of
        /// distances they were before there was an angle at all.
        /// </summary>
        private static bool BoxesMeet(Zone a, float ar, Zone b, float br, float dx, float dz)
        {
            for (var i = 0; i < 4; i++)
            {
                var along = (i < 2 ? a.Angle : b.Angle) + (i % 2 == 0 ? 0f : 90f);
                var radians = NormaliseAngle(along) * Deg2Rad;
                var axisX = (float)System.Math.Cos(radians);
                var axisZ = (float)System.Math.Sin(radians);

                var apart = Abs(dx * axisX + dz * axisZ);
                if (apart >= Spread(a.Angle, ar, axisX, axisZ) + Spread(b.Angle, br, axisX, axisZ))
                    return false;
            }

            return true;
        }

        /// <summary>How far a square of this reach, lying so, stretches along an axis.</summary>
        private static float Spread(float angle, float reach, float axisX, float axisZ)
        {
            var radians = NormaliseAngle(angle) * Deg2Rad;
            var cos = (float)System.Math.Cos(radians);
            var sin = (float)System.Math.Sin(radians);

            return reach * (Abs(cos * axisX + sin * axisZ) + Abs(-sin * axisX + cos * axisZ));
        }

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        private static float Abs(float value)
        {
            return value < 0f ? -value : value;
        }

        private static float Clamp(float value, float low, float high)
        {
            if (value < low) return low;
            return value > high ? high : value;
        }

        // ---------------- раскладка внутри категории ----------------

        /// <summary>
        /// How many slots of a chest this many of one thing takes. Slots, not units, because
        /// a chest fills by slots: five hundred wood is ten of them and thirty copper ore is
        /// one, and that is the whole difference between «gets its own chest» and «goes in
        /// with the odds and ends».
        /// </summary>
        public static int SlotsFor(int units, int stackSize)
        {
            if (units <= 0) return 0;
            if (stackSize < 1) stackSize = 1;
            return (units + stackSize - 1) / stackSize;
        }

        /// <summary>One chest of a category, as the plan sees it.</summary>
        public sealed class BinState
        {
            public int Category;

            /// <summary>Where it stands. Height is not asked, as nowhere else here.</summary>
            public float X;

            public float Z;

            /// <summary>How many slots it has at all.</summary>
            public int Slots;

            /// <summary>What it holds now: item name to units.</summary>
            public Dictionary<string, int> Holds = new Dictionary<string, int>();

            public int Held(string kind)
            {
                int units;
                return Holds != null && Holds.TryGetValue(kind, out units) ? units : 0;
            }

            /// <summary>Everything in it that is not this kind - how mixed it already is.</summary>
            public int HeldOther(string kind)
            {
                var other = 0;
                if (Holds == null) return 0;

                foreach (var pair in Holds)
                    if (pair.Key != kind) other += pair.Value;

                return other;
            }
        }

        /// <summary>The people written into a zone, as one field: ids and spaces, nothing else.</summary>
        public static string PackMembers(IEnumerable<string> members)
        {
            var text = new StringBuilder();
            if (members == null) return "";

            foreach (var member in members)
            {
                var who = CleanId(member);
                if (who.Length == 0) continue;

                if (text.Length > 0) text.Append(' ');
                text.Append(who);
            }

            return text.ToString();
        }

        public static List<string> ParseMembers(string text)
        {
            var members = new List<string>();
            if (string.IsNullOrEmpty(text)) return members;

            foreach (var part in text.Split(' '))
            {
                var who = CleanId(part);
                if (who.Length == 0 || members.Contains(who)) continue;

                members.Add(who);
                if (members.Count >= MaxMembers) break;
            }

            return members;
        }

        /// <summary>More people than this in one zone is not a base, it is a server.</summary>
        public const int MaxMembers = 32;

        /// <summary>Square of the gap between two chests: only the order of it is ever used.</summary>
        private static float Gap(BinState a, BinState b)
        {
            if (a == null || b == null) return 0f;

            var dx = a.X - b.X;
            var dz = a.Z - b.Z;
            return dx * dx + dz * dz;
        }

        /// <summary>
        /// Chests standing together, by single linkage: one joins a group as soon as it is
        /// within <paramref name="span"/> of any chest already in it. So a wall of them is
        /// one group however long the wall runs - the span says «сосед», not «размер
        /// группы», and a room away is a group of its own.
        /// </summary>
        public static List<List<int>> Groups(IList<BinState> bins, IList<int> of, float span)
        {
            var groups = new List<List<int>>();
            if (bins == null || of == null) return groups;

            var left = new List<int>(of);
            var reach = span * span;

            while (left.Count > 0)
            {
                var group = new List<int> { left[0] };
                left.RemoveAt(0);

                // group grows as it goes, and that is the linkage: a chest pulled in brings
                // in its own neighbours on the next turn of the outer loop.
                for (var i = 0; i < group.Count; i++)
                    for (var j = left.Count - 1; j >= 0; j--)
                    {
                        if (Gap(bins[group[i]], bins[left[j]]) > reach) continue;

                        group.Add(left[j]);
                        left.RemoveAt(j);
                    }

                group.Sort();
                groups.Add(group);
            }

            return groups;
        }

        /// <summary>
        /// Which group the pile goes to: the one that already holds most of it, and among
        /// equals the roomier one - nothing held anywhere means the biggest group, where it
        /// is least likely to run out and spill into the next room after all.
        /// </summary>
        private static List<int> PickGroup(IList<BinState> bins, List<List<int>> groups, string kind)
        {
            List<int> best = null;
            var bestHeld = -1;

            foreach (var group in groups)
            {
                var held = 0;
                foreach (var bin in group) held += bins[bin].Held(kind);

                if (best != null
                    && (held < bestHeld || (held == bestHeld && group.Count <= best.Count))) continue;

                best = group;
                bestHeld = held;
            }

            return best != null ? new List<int>(best) : new List<int>();
        }

        /// <summary>Everything of one kind the category has to hold - in its chests and on its way there.</summary>
        public sealed class Load
        {
            public string Kind;

            public int Category;

            public int Units;

            public int StackSize;
        }

        /// <summary>Where each kind belongs, and where everything else goes.</summary>
        public sealed class Plan
        {
            /// <summary>
            /// Kind to the chests kept for it alone. Nothing else is ever carried into one of
            /// these: the empty slots are not waste, they are room for the next load.
            /// </summary>
            public readonly Dictionary<string, List<int>> Homes = new Dictionary<string, List<int>>();

            /// <summary>Category to the chests that take whatever has no chest of its own.</summary>
            public readonly Dictionary<int, List<int>> Mixed = new Dictionary<int, List<int>>();

            // Which of the shared chests a kind is already in - the old rule, «туда, где уже
            // лежит такое же», kept for the things that share.
            internal readonly Dictionary<string, List<int>> Preferred = new Dictionary<string, List<int>>();

            /// <summary>
            /// Every chest this kind may be carried into, best first: its own, and then the
            /// shared one for whatever did not fit. Overflow going to the shared chest is why
            /// a full pile does not simply stay in the cart.
            /// </summary>
            public List<int> Where(string kind, int category)
            {
                var found = new List<int>();
                WhereInto(kind, category, found);
                return found;
            }

            /// <summary>
            /// То же самое, но в готовый список.
            ///
            /// The tidying pass asks this of every item in every marked chest, every second;
            /// a fresh list for each of them is a heap of garbage for a question that is
            /// usually answered «it is already in the right place».
            /// </summary>
            public void WhereInto(string kind, int category, List<int> into)
            {
                into.Clear();

                List<int> home;
                if (kind != null && Homes.TryGetValue(kind, out home)) into.AddRange(home);

                List<int> shared;
                if (kind != null && Preferred.TryGetValue(kind, out shared)) into.AddRange(shared);
                else if (Mixed.TryGetValue(category, out shared)) into.AddRange(shared);

                // «Разное» is the catch-all, and that is the whole reason it exists: a kind
                // whose own category has no chest at all - wood when nobody marked anything
                // «Материалы» - still has somewhere to go. Losing this was how a cart could
                // stand in the zone, be counted, and never be emptied. It comes last, and
                // that order is not decoration: the tidying pass reads it as «лучше или
                // хуже», and a mushroom in the catch-all is pulled back to «Еда» by it.
                List<int> spare;
                if (category != Misc && Mixed.TryGetValue(Misc, out spare))
                    foreach (var bin in spare)
                        if (!into.Contains(bin)) into.Add(bin);
            }

            /// <summary>
            /// Годится ли этот сундук для такого добра. A kind with a chest of its own
            /// belongs only there - so what is left of it in the shared chest is carried home
            /// as soon as there is room, rather than settling where it landed.
            ///
            /// «Годится», not «лучший»: the catch-all suits everything, and asking this about
            /// a mushroom lying in «Разное» answers yes while «Еда» stands empty beside it.
            /// Whoever needs «could it be better off elsewhere» reads the order of
            /// <see cref="Where"/> instead.
            /// </summary>
            public bool Belongs(string kind, int category, int bin)
            {
                List<int> home;
                if (kind != null && Homes.TryGetValue(kind, out home)) return home.Contains(bin);

                return Where(kind, category).Contains(bin);
            }
        }

        /// <summary>
        /// Splits every category between the chests marked for it.
        ///
        /// A pile big enough to matter - ownChestSlots slots or more - gets chests of its own,
        /// as many as it needs, and they hold nothing else; everything that is one or a dozen
        /// of something shares what is left. The odd thing about this is that it is worked out
        /// afresh every sweep and kept nowhere, which is what makes it self-righting: a heap of
        /// wood that grows past the threshold is given a chest on the next sweep without
        /// anybody promoting it, and one that is spent goes back to sharing.
        ///
        /// One chest of a category is never given away, whatever the piles want. Without that
        /// rule a category of one chest would dedicate it to the first big pile and then have
        /// nowhere at all for the ore; with it, there is always somewhere for the odds and ends,
        /// and the dedicated chests stay clean.
        ///
        /// Which chest a pile gets is decided by what is already in the chests, so the answer
        /// reinforces itself instead of flapping: the chest holding the most wood is the wood
        /// chest, and carrying wood into it only makes it more so. Ties fall to the order the
        /// bins were given in, which the caller is expected to keep stable - by position, not
        /// by whatever order the game handed them over in.
        ///
        /// With ownChestSlots at zero nothing is split: every chest of a category takes
        /// everything, which is what this did before there was a plan.
        /// </summary>
        public static Plan MakePlan(IList<BinState> bins, IList<Load> loads, int ownChestSlots,
                                    float groupSpan = 0f)
        {
            var plan = new Plan();
            if (bins == null) return plan;

            // Which chests each category has, in the order they were handed over.
            var byCategory = new Dictionary<int, List<int>>();
            for (var i = 0; i < bins.Count; i++)
            {
                if (bins[i] == null) continue;

                List<int> list;
                if (!byCategory.TryGetValue(bins[i].Category, out list))
                {
                    list = new List<int>();
                    byCategory[bins[i].Category] = list;
                }

                list.Add(i);
            }

            var claimed = new HashSet<int>();

            if (ownChestSlots > 0 && loads != null)
            {
                // Biggest piles first: they are the ones a chest of their own actually helps,
                // and the ones that would otherwise swamp everything else.
                var big = new List<Load>();
                foreach (var load in loads)
                {
                    if (load == null || string.IsNullOrEmpty(load.Kind)) continue;
                    if (SlotsFor(load.Units, load.StackSize) < ownChestSlots) continue;
                    big.Add(load);
                }

                big.Sort((a, b) =>
                {
                    var bySlots = SlotsFor(b.Units, b.StackSize).CompareTo(SlotsFor(a.Units, a.StackSize));
                    return bySlots != 0 ? bySlots : string.CompareOrdinal(a.Kind, b.Kind);
                });

                foreach (var load in big)
                {
                    List<int> ofCategory;
                    if (!byCategory.TryGetValue(load.Category, out ofCategory)) continue;

                    var free = new List<int>();
                    foreach (var bin in ofCategory)
                        if (!claimed.Contains(bin)) free.Add(bin);

                    // The last one stays shared, always. Counted over the category and not
                    // over the group below: a shared chest in the other room is still one.
                    var mayTake = free.Count - 1;
                    if (mayTake < 1) continue;

                    // The chest that already holds most of it, and among equals the one that
                    // holds least of anything else - a chest half full of this is a better
                    // home than an empty one somebody is about to want for something else.
                    var kind = load.Kind;
                    Comparison<int> byHolding = (a, b) =>
                    {
                        var mine = bins[b].Held(kind).CompareTo(bins[a].Held(kind));
                        if (mine != 0) return mine;

                        var others = bins[a].HeldOther(kind).CompareTo(bins[b].HeldOther(kind));
                        return others != 0 ? others : a.CompareTo(b);
                    };

                    // One pile, one place. Chests standing together count as a group, so a
                    // pile that outgrows its chest spreads along the wall it is already on
                    // instead of turning up in the cellar as well: «дрова вон там» has to
                    // stay true, and a pile split across the base is a pile nobody finds.
                    var pool = groupSpan > 0f
                        ? PickGroup(bins, Groups(bins, free, groupSpan), kind)
                        : free;

                    pool.Sort(byHolding);

                    // And past the first chest - the nearest to it, not the next in line.
                    if (pool.Count > 1)
                    {
                        var anchor = pool[0];
                        var tail = pool.GetRange(1, pool.Count - 1);

                        tail.Sort((a, b) =>
                        {
                            var byGap = Gap(bins[anchor], bins[a]).CompareTo(Gap(bins[anchor], bins[b]));
                            return byGap != 0 ? byGap : byHolding(a, b);
                        });

                        pool = new List<int> { anchor };
                        pool.AddRange(tail);
                    }

                    var slots = SlotsFor(load.Units, load.StackSize);
                    var home = new List<int>();

                    foreach (var bin in pool)
                    {
                        if (home.Count >= mayTake) break;
                        if (home.Count > 0 && slots <= 0) break;

                        home.Add(bin);
                        claimed.Add(bin);
                        slots -= bins[bin].Slots > 0 ? bins[bin].Slots : 1;
                    }

                    if (home.Count > 0) plan.Homes[load.Kind] = home;
                }
            }

            foreach (var pair in byCategory)
            {
                var rest = new List<int>();
                foreach (var bin in pair.Value)
                    if (!claimed.Contains(bin)) rest.Add(bin);

                plan.Mixed[pair.Key] = rest;
            }

            // Among the shared chests, a kind still prefers the one it is already in.
            if (loads != null)
            {
                foreach (var load in loads)
                {
                    if (load == null || string.IsNullOrEmpty(load.Kind)) continue;

                    List<int> mixed;
                    if (!plan.Mixed.TryGetValue(load.Category, out mixed) || mixed.Count < 2) continue;

                    var order = new List<int>(mixed);
                    order.Sort((a, b) =>
                    {
                        var mine = bins[b].Held(load.Kind).CompareTo(bins[a].Held(load.Kind));
                        return mine != 0 ? mine : a.CompareTo(b);
                    });

                    plan.Preferred[load.Kind] = order;
                }
            }

            return plan;
        }

        /// <summary>One marked chest, as far as this decision is concerned.</summary>
        public sealed class Bin
        {
            public int Category;

            /// <summary>Already holds this very item - by far the strongest hint there is.</summary>
            public bool HasSame;

            /// <summary>Has room for it: a full chest is not an answer, it is a delay.</summary>
            public bool HasRoom;
        }

        /// <summary>
        /// Which bin this item belongs in, or -1 to leave it where it is.
        ///
        /// Three rules, in order. A chest that already holds this very item wins over
        /// everything: that is what makes «этот под дерево» work without anyone naming a
        /// single item - you put a stack of wood in it once, and wood goes there. Then the
        /// category the chest was marked with. Then «Разное», which is where the things
        /// nobody has made a place for end up.
        ///
        /// A full bin is skipped at every step rather than chosen and failed at, so an item
        /// whose own chest is full still finds the category chest, and only then stays put.
        /// It is never dropped on the ground - that is the automation's old sin and it is
        /// not repeated here.
        /// </summary>
        public static int Choose(int itemCategory, IList<Bin> bins)
        {
            if (bins == null) return -1;

            var byCategory = -1;
            var byMisc = -1;

            for (var i = 0; i < bins.Count; i++)
            {
                var bin = bins[i];
                if (bin == null || !bin.HasRoom) continue;

                if (bin.HasSame) return i;

                if (byCategory < 0 && bin.Category == itemCategory) byCategory = i;
                if (byMisc < 0 && bin.Category == Misc) byMisc = i;
            }

            if (byCategory >= 0) return byCategory;
            return byMisc;
        }
    }
}
