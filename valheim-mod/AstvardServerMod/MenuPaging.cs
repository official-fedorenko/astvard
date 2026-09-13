namespace AstvardServerMod
{
    /// <summary>
    /// Which part of a long list a fixed pool of buttons shows.
    ///
    /// Every list in the panel has eight buttons and the lists have no such limit: past
    /// eight, the rest used to be simply out of reach, with «Показаны первые 8» under the
    /// list. This is the arithmetic of the window that moves over the list instead, kept
    /// apart from Unity so the tests can hold it to its edges.
    ///
    /// The window never runs past the end: the last page is always a full one, reaching back
    /// as far as it needs to, so the buttons do not empty out halfway down the panel.
    /// </summary>
    public static class MenuPaging
    {
        /// <summary>The first shown item, kept inside the list.</summary>
        public static int Clamp(int offset, int total, int page)
        {
            if (page <= 0 || total <= page || offset <= 0) return 0;
            return offset > total - page ? total - page : offset;
        }

        /// <summary>How many buttons the window fills.</summary>
        public static int Shown(int offset, int total, int page)
        {
            if (page <= 0 || total <= 0) return 0;
            var left = total - Clamp(offset, total, page);
            return left < page ? left : page;
        }

        /// <summary>The window moved by delta rows: a page for the buttons, a row for the wheel.</summary>
        public static int Step(int offset, int total, int page, int delta)
        {
            return Clamp(Clamp(offset, total, page) + delta, total, page);
        }

        public static bool CanGoUp(int offset, int total, int page)
        {
            return Clamp(offset, total, page) > 0;
        }

        public static bool CanGoDown(int offset, int total, int page)
        {
            return page > 0 && Clamp(offset, total, page) + page < total;
        }

        /// <summary>«Показаны с 9 по 16 из 23.», or nothing when the whole list fits.</summary>
        public static string Window(int offset, int total, int page)
        {
            if (page <= 0 || total <= page) return "";
            var start = Clamp(offset, total, page);
            return "Показаны с " + (start + 1) + " по " + (start + Shown(start, total, page)) + " из " + total + ".";
        }
    }
}
