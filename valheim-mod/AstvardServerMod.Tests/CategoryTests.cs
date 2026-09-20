using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// Категории: встроенные в мод и те, что заводит админ на сайте.
///
/// The number of a category is what lies inside the chest, so these tests are mostly
/// about one thing: that a number never comes to mean something else. A new category
/// takes the next number, one taken away leaves a hole rather than pulling its
/// neighbours down a place, and the built-in ones are the mod's own - what the site
/// says about them is not listened to at all, because the mod sorts by them when
/// nobody has said anything about an item.
/// </summary>
public class CategoryTests
{
    private static void Builtin() => Sorting.ReadCategories("");

    [Fact]
    public void NothingSaidLeavesTheBuiltInList()
    {
        Builtin();

        Assert.Equal("Разное", Sorting.Title(Sorting.Misc));
        Assert.Equal("Материалы", Sorting.Title(Sorting.Materials));
        Assert.Equal("Добыча", Sorting.Title(Sorting.Loot));
        Assert.True(Sorting.IsCategory(Sorting.Loot));
    }

    [Fact]
    public void TheBuiltInOnesAreNotTheSitesToRename()
    {
        // Решение хозяина: встроенные зашиты в мод. По ним мод раскладывает сам, когда о
        // предмете ничего не сказано, так что «Разное», переименованное снаружи в «Хлам»,
        // осталось бы ответом для всего бездомного, но перестало бы им читаться.
        Sorting.ReadCategories("1=Сырьё;2=Харчи");

        Assert.Equal("Материалы", Sorting.Title(Sorting.Materials));
        Assert.Equal("Еда", Sorting.Title(Sorting.Food));

        // И то, что лежит в сундуке, продолжает значить ровно то же.
        Assert.Equal(Sorting.Materials, Sorting.FromStored(Sorting.ToStored(Sorting.Materials)));

        Builtin();
    }

    [Fact]
    public void ACategoryOfOnesOwnIsRenamedFreely()
    {
        Sorting.ReadCategories("15=Уголь");
        Assert.Equal("Уголь", Sorting.Title(15));

        Sorting.ReadCategories("15=Топливо");
        Assert.Equal("Топливо", Sorting.Title(15));

        Builtin();
    }

    [Fact]
    public void ANewCategoryTakesTheNextNumber()
    {
        Sorting.ReadCategories("15=Уголь");

        Assert.True(Sorting.IsCategory(15));
        Assert.Equal("Уголь", Sorting.Title(15));
        Assert.Equal(16, Sorting.Count);

        Builtin();
    }

    [Fact]
    public void ACategoryTakenAwayLeavesItsPlaceEmpty()
    {
        Sorting.ReadCategories("15=Уголь;16=Смола");
        Assert.True(Sorting.IsCategory(16));

        // Пятнадцатую убрали: шестнадцатая обязана остаться шестнадцатой.
        Sorting.ReadCategories("16=Смола");

        Assert.False(Sorting.IsCategory(15));
        Assert.True(Sorting.IsCategory(16));
        Assert.Equal("Смола", Sorting.Title(16));

        Builtin();
    }

    [Fact]
    public void AChestMarkedWithAnUnknownNumberIsStillABin()
    {
        // Самое дорогое место: сундук, помеченный категорией, которой у нас сейчас нет.
        // Прочитать это как «не помечен» значит сделать из полного сундука источник и
        // вынести его по всей базе.
        Builtin();

        Assert.Equal(20, Sorting.FromStored(21));
        Assert.True(Sorting.IsBin(21));
        Assert.False(Sorting.IsCategory(20));
        Assert.Equal("Категория 20", Sorting.Title(20));
    }

    [Fact]
    public void AnUnmarkedChestIsStillASource()
    {
        Builtin();

        Assert.Equal(-1, Sorting.FromStored(0));
        Assert.False(Sorting.IsBin(0));
    }

    [Fact]
    public void TheBuiltInOnesCannotBeLost()
    {
        // Что бы сайт ни сказал про встроенные, они остаются при своих именах: и когда он
        // молчит о них, и когда пытается переименовать.
        Sorting.ReadCategories("3=Железо");

        Assert.Equal("Оружие", Sorting.Title(Sorting.Weapons));
        Assert.Equal("Разное", Sorting.Title(Sorting.Misc));
        Assert.Equal("Добыча", Sorting.Title(Sorting.Loot));

        Builtin();
    }

    [Fact]
    public void TheListSurvivesBeingPackedAndReadBack()
    {
        Sorting.ReadCategories("16=Смола");
        var packed = Sorting.PackCategories();

        Sorting.ReadCategories(packed);

        Assert.Equal(packed, Sorting.PackCategories());
        Assert.Equal("Смола", Sorting.Title(16));
        Assert.False(Sorting.IsCategory(15));

        Builtin();
    }

    [Fact]
    public void RubbishIsIgnoredRatherThanBelieved()
    {
        Sorting.ReadCategories("=безномера;abc=Что-то;-1=Минус;999=Далеко;2=Харчи");

        Assert.Equal("Еда", Sorting.Title(Sorting.Food));
        Assert.False(Sorting.IsCategory(999));
        Assert.Equal(Sorting.CategoryTitles.Length, Sorting.Count);

        Builtin();
    }
}
