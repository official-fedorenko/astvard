using System.Collections.Generic;
using System.Text;

namespace AstvardServerMod
{
    /// <summary>
    /// Сколько медовухи варить и когда остановиться.
    ///
    /// Вся цепочка медовухи в игре — три шага: сварить основу в «Котле для медовух»,
    /// поставить её в бочку, снять через двое суток шесть бутылок. Два последних шага мод
    /// делал и раньше, первого не было вовсе, и из-за этого автоматика упиралась в руки:
    /// пока человек не наварит основ, бочки стоят пустые.
    ///
    /// Здесь — только арифметика решения «варить или хватит», без Unity и без игры, чтобы
    /// её можно было прогнать тестами. Что именно варится, сколько бутылок выходит и из
    /// чего они делаются, спрашивается у самой игры (таблица превращений бочки и рецепт из
    /// ObjectDB) — списка напитков здесь нет и быть не должно: в новой версии игры их
    /// станет больше, и он бы устарел молча.
    /// </summary>
    public static class Brewing
    {
        /// <summary>Больше этого за раз не заказать: опечатка в поле не должна варить вечно.</summary>
        public const int MaxKeep = 9999;

        /// <summary>
        /// Что держать на складе: «основа=сколько бутылок», через точку с запятой.
        ///
        /// Ключ — имя префаба основы, а не готового напитка: основу мы кладём в бочку, и
        /// она же связывает рецепт с превращением. Имя напитка выводится из неё игрой.
        /// </summary>
        public static Dictionary<string, int> ReadWishes(string packed)
        {
            var wishes = new Dictionary<string, int>();
            if (string.IsNullOrEmpty(packed)) return wishes;

            foreach (var piece in packed.Split(';'))
            {
                var line = piece.Trim();
                if (line.Length == 0) continue;

                var at = line.IndexOf('=');
                if (at <= 0 || at == line.Length - 1) continue;

                var name = line.Substring(0, at).Trim();
                if (name.Length == 0) continue;

                int keep;
                if (!int.TryParse(line.Substring(at + 1).Trim(), out keep)) continue;
                if (keep <= 0) continue;

                // Последнее слово за последней записью: строку правит и человек в конфиге.
                wishes[name] = keep > MaxKeep ? MaxKeep : keep;
            }

            return wishes;
        }

        /// <summary>Обратно в строку конфига. Нули не пишутся — «не варить» это отсутствие записи.</summary>
        public static string PackWishes(Dictionary<string, int> wishes)
        {
            if (wishes == null || wishes.Count == 0) return "";

            var names = new List<string>(wishes.Keys);
            names.Sort(System.StringComparer.Ordinal);

            var packed = new StringBuilder();
            foreach (var name in names)
            {
                var keep = wishes[name];
                if (keep <= 0 || string.IsNullOrEmpty(name)) continue;
                if (name.IndexOf('=') >= 0 || name.IndexOf(';') >= 0) continue;

                if (packed.Length > 0) packed.Append(';');
                packed.Append(name).Append('=').Append(keep > MaxKeep ? MaxKeep : keep);
            }

            return packed.ToString();
        }

        /// <summary>
        /// Сколько основ ещё нужно поставить, чтобы на складе оказалось <paramref name="keep"/>
        /// бутылок.
        ///
        /// Считается и то, что уже бродит: бочка, поставленная минуту назад, — это шесть
        /// будущих бутылок, и не учесть их значит наварить вшестеро больше заказанного.
        /// Остаток округляется вверх: заказ в одну бутылку — это всё равно одна бочка.
        /// </summary>
        public static int StillToBrew(int keep, int inStock, int brewing, int perBrew)
        {
            if (keep <= 0 || perBrew <= 0) return 0;

            var have = inStock + brewing * perBrew;
            var short_ = keep - have;
            if (short_ <= 0) return 0;

            return (short_ + perBrew - 1) / perBrew;
        }

        /// <summary>
        /// Хватает ли на одну основу. Что и сколько нужно, приходит из рецепта игры —
        /// своей таблицы составляющих тут нет, иначе первая же правка рецепта в игре
        /// превратила бы варку в печатный станок.
        /// </summary>
        public static bool CanAfford(IList<KeyValuePair<string, int>> need, System.Func<string, int> inStock)
        {
            if (need == null || need.Count == 0 || inStock == null) return false;

            foreach (var item in need)
            {
                if (string.IsNullOrEmpty(item.Key) || item.Value <= 0) return false;
                if (inStock(item.Key) < item.Value) return false;
            }

            return true;
        }

        /// <summary>
        /// Чего варить первым, когда свободна одна бочка, а заказов несколько.
        ///
        /// Берём тот, которого не хватает сильнее — в долях от заказа, а не в бутылках:
        /// иначе заказ на сто бутылок всегда перебивал бы заказ на десять, и второй не
        /// сварился бы никогда. Равные разрешаются именем, чтобы выбор не скакал от
        /// прохода к проходу.
        /// </summary>
        public static string Next(IList<string> bases, System.Func<string, int> stillToBrew,
                                  System.Func<string, int> keep)
        {
            if (bases == null || stillToBrew == null || keep == null) return null;

            string best = null;
            var bestShare = 0f;

            foreach (var name in bases)
            {
                if (string.IsNullOrEmpty(name)) continue;

                var left = stillToBrew(name);
                if (left <= 0) continue;

                var wanted = keep(name);
                if (wanted <= 0) continue;

                var share = (float)left / wanted;
                if (best == null || share > bestShare
                    || (share == bestShare && string.CompareOrdinal(name, best) < 0))
                {
                    best = name;
                    bestShare = share;
                }
            }

            return best;
        }

        // ---------------- заказы по персонажам ----------------

        /// <summary>
        /// Заказы одного персонажа: его номер, имя для чтения и что он заказал.
        ///
        /// Ключ — номер персонажа (`Player.GetPlayerID()`), а не его имя: имя не уникально,
        /// двух «Бьёрнов» завести никто не мешает, и заказы у них слились бы в один. Имя
        /// лежит рядом только затем, чтобы файл можно было прочесть глазами.
        /// </summary>
        public sealed class Book
        {
            public long Id;

            public string Name = "";

            public Dictionary<string, int> Wishes = new Dictionary<string, int>();
        }

        /// <summary>
        /// Читает файл заказов: по строке на персонажа, поля через табуляцию.
        ///
        /// Табуляция, а не «=», потому что сами заказы внутри строки разделены как раз
        /// «=» и «;», а имя персонажа — чужой ввод, в котором может оказаться что угодно.
        /// Имя при записи чистится (<see cref="CleanLabel"/>), но читать надо и то, что
        /// туда мог вписать человек руками.
        ///
        /// Строка с пустыми заказами — **не** то же самое, что отсутствие строки. «Я
        /// ничего не варю» и «я ещё ничего не решал» — разные ответы, и от второго
        /// зависит, достанутся ли персонажу общие заказы.
        /// </summary>
        public static Dictionary<long, Book> ReadBooks(IEnumerable<string> lines)
        {
            var books = new Dictionary<long, Book>();
            if (lines == null) return books;

            foreach (var raw in lines)
            {
                if (raw == null) continue;

                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                var parts = line.Split('\t');
                if (parts.Length < 2) continue;

                long id;
                if (!long.TryParse(parts[0].Trim(), out id) || id == 0L) continue;

                var book = new Book();
                book.Id = id;
                book.Name = parts[1].Trim();
                book.Wishes = ReadWishes(parts.Length > 2 ? parts[2] : "");

                // Последнее слово за последней строкой, как и внутри строки заказов.
                books[id] = book;
            }

            return books;
        }

        /// <summary>Обратно в строки файла, без заголовка — его пишет тот, кто сохраняет.</summary>
        public static List<string> PackBooks(IEnumerable<Book> books)
        {
            var lines = new List<string>();
            if (books == null) return lines;

            foreach (var book in books)
            {
                if (book == null || book.Id == 0L) continue;
                lines.Add(book.Id + "\t" + CleanLabel(book.Name) + "\t" + PackWishes(book.Wishes));
            }

            return lines;
        }

        /// <summary>
        /// Имя, которым не разорвать строку файла: без табуляций, переводов строки и
        /// прочего невидимого. Пустое имя — это «…»: строка без имени читается хуже, чем
        /// строка с заглушкой.
        /// </summary>
        public static string CleanLabel(string name)
        {
            if (string.IsNullOrEmpty(name)) return "…";

            var clean = new StringBuilder(name.Length);
            foreach (var c in name)
                if (!char.IsControl(c) && c != '\t') clean.Append(c);

            var done = clean.ToString().Trim();
            return done.Length == 0 ? "…" : done;
        }

        /// <summary>
        /// Чьи заказы в силе: свои, если у персонажа есть своя строка, иначе ничьи.
        ///
        /// Именно ничьи, а не общие. Общая строка (та, что жила в конфиге до 22.09.2026)
        /// достаётся ровно одному персонажу и один раз — см. <see cref="MayAdopt"/>, —
        /// а всем остальным доставаться не должна: тем, что новый персонаж наследовал
        /// чужие заказы, всё это и началось.
        /// </summary>
        public static Dictionary<string, int> WishesFor(Dictionary<long, Book> books, long id)
        {
            Book book;
            if (books != null && id != 0L && books.TryGetValue(id, out book) && book != null)
                return new Dictionary<string, int>(book.Wishes);

            return new Dictionary<string, int>();
        }

        /// <summary>
        /// Может ли этот персонаж забрать себе общую строку заказов.
        ///
        /// Может тот, кто умеет сварить **всё**, что в ней заказано. Это не догадка о том,
        /// кто её писал, а единственное, что про неё вообще известно: заказ на восемь
        /// медовух мог сделать только персонаж с восемью рецептами, и новый, у которого их
        /// четыре, её не заберёт. Забрал — строка стирается, и второй раз её уже никто не
        /// унаследует.
        ///
        /// Пустую строку забирать не у кого и незачем.
        /// </summary>
        public static bool MayAdopt(Dictionary<string, int> shared, ICollection<string> knownBases)
        {
            if (shared == null || shared.Count == 0 || knownBases == null) return false;

            foreach (var name in shared.Keys)
                if (!knownBases.Contains(name)) return false;

            return true;
        }
    }
}
