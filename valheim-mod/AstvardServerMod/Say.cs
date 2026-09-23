using System.Collections.Generic;
using System.Text;

namespace AstvardServerMod
{
    /// <summary>
    /// Разбиение длинной вести на строки.
    ///
    /// Сообщение в середине экрана игра рисует одной строкой и за край не переносит:
    /// «Дорожка 258 м готова, ширина 3,0 м, сглажена, снесено: 74, факелов: 38, обойдено:
    /// 2» уезжает за оба края и читается наполовину. Хозяин прислал снимок 23.09.2026.
    ///
    /// **Перенос ставим свой, а не просим у шрифта.** Настройки переноса у `TMP_Text` —
    /// чужой виджет, общий на все вести игры, и менять его ширину значит менять вид
    /// сообщений, к которым мы не имеем отношения. Явный `\n` работает всегда и ничего
    /// чужого не трогает.
    ///
    /// Мерка в знаках, а не в точках, и это честная оговорка: у шрифта буквы разной
    /// ширины, так что строка выходит примерно, а не ровно. Для вести на пару строк этого
    /// довольно, а точная мерка потребовала бы спрашивать сам шрифт на каждую букву.
    /// </summary>
    internal static class Say
    {
        /// <summary>
        /// Сколько знаков в строке. Подобрано под ту самую весть: 84 знака в одну строку
        /// не влезли, а пополам влезают обе половины.
        /// </summary>
        internal const int Line = 52;

        /// <summary>
        /// Переносит по словам, не трогая уже расставленных переносов.
        ///
        /// Слово длиннее строки не рвётся: перенос посреди имени игрока или числа хуже,
        /// чем строка, вылезшая за край. Такие в наших вестях не встречаются вовсе, и
        /// заводить ради них правило значило бы гадать.
        /// </summary>
        internal static string Wrap(string text, int width = Line)
        {
            if (string.IsNullOrEmpty(text) || width <= 0) return text;
            if (text.Length <= width && text.IndexOf('\n') < 0) return text;

            var lines = new List<string>();
            foreach (var had in text.Split('\n')) lines.Add(had);

            var made = new StringBuilder();
            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0) made.Append('\n');
                Fold(made, lines[i], width);
            }

            return made.ToString();
        }

        private static void Fold(StringBuilder made, string line, int width)
        {
            var at = 0;
            while (at < line.Length)
            {
                if (line.Length - at <= width)
                {
                    made.Append(line, at, line.Length - at);
                    return;
                }

                // Последний пробел в пределах строки - там и рвём. Нет ни одного, значит
                // слово длиннее строки: отдаём его целиком и идём дальше.
                var cut = line.LastIndexOf(' ', at + width, width);
                if (cut <= at)
                {
                    var word = line.IndexOf(' ', at);
                    if (word < 0)
                    {
                        made.Append(line, at, line.Length - at);
                        return;
                    }

                    cut = word;
                }

                made.Append(line, at, cut - at).Append('\n');
                at = cut + 1;
            }
        }
    }
}
