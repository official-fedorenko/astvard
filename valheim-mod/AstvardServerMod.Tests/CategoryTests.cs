using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// Категории, которыми теперь распоряжается сайт.
///
/// The number of a category is what lies inside the chest, so these tests are mostly
/// about one thing: that a number never comes to mean something else. Renaming is free,
/// a new category takes the next number, and one taken away leaves a hole rather than
/// pulling its neighbours down a place - because a wall of thirty marked chests has no
/// way of telling anybody that it started keeping the wrong things.
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
        Assert.Equal("Лут", Sorting.Title(Sorting.Loot));
        Assert.True(Sorting.IsCategory(Sorting.Loot));
    }

    [Fact]
    public void RenamingKeepsTheNumber()
    {
        Sorting.ReadCategories("1=Сырьё;2=Харчи");

        Assert.Equal("Сырьё", Sorting.Title(Sorting.Materials));
        Assert.Equal("Харчи", Sorting.Title(Sorting.Food));

        // И то, что лежит в сундуке, продолжает значить ровно то же.
        Assert.Equal(Sorting.Materials, Sorting.FromStored(Sorting.ToStored(Sorting.Materials)));

        Builtin();
    }

    [Fact]
    public void ANewCategoryTakesTheNextNumber()
    {
        Sorting.ReadCategories("0=Разное;1=Материалы;2=Еда;3=Оружие;4=Броня;5=Инструменты;"
                               + "6=Трофеи;7=Лут;8=Слитки");

        Assert.True(Sorting.IsCategory(8));
        Assert.Equal("Слитки", Sorting.Title(8));
        Assert.Equal(9, Sorting.Count);

        Builtin();
    }

    [Fact]
    public void ACategoryTakenAwayLeavesItsPlaceEmpty()
    {
        Sorting.ReadCategories("0=Разное;1=Материалы;2=Еда;3=Оружие;4=Броня;5=Инструменты;"
                               + "6=Трофеи;7=Лут;8=Слитки;9=Уголь");
        Assert.True(Sorting.IsCategory(9));

        // Восьмую убрали: девятая обязана остаться девятой.
        Sorting.ReadCategories("0=Разное;1=Материалы;2=Еда;3=Оружие;4=Броня;5=Инструменты;"
                               + "6=Трофеи;7=Лут;9=Уголь");

        Assert.False(Sorting.IsCategory(8));
        Assert.True(Sorting.IsCategory(9));
        Assert.Equal("Уголь", Sorting.Title(9));

        Builtin();
    }

    [Fact]
    public void AChestMarkedWithAnUnknownNumberIsStillABin()
    {
        // Самое дорогое место: сундук, помеченный категорией, которой у нас сейчас нет.
        // Прочитать это как «не помечен» значит сделать из полного сундука источник и
        // вынести его по всей базе.
        Builtin();

        Assert.Equal(12, Sorting.FromStored(13));
        Assert.True(Sorting.IsBin(13));
        Assert.False(Sorting.IsCategory(12));
        Assert.Equal("Категория 12", Sorting.Title(12));
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
        // Сайт назвал только одну: остальные встроенные обязаны остаться при своих именах,
        // иначе одна опечатка в ответе оставила бы базу без «Разного».
        Sorting.ReadCategories("3=Железо");

        Assert.Equal("Железо", Sorting.Title(Sorting.Weapons));
        Assert.Equal("Разное", Sorting.Title(Sorting.Misc));
        Assert.Equal("Лут", Sorting.Title(Sorting.Loot));

        Builtin();
    }

    [Fact]
    public void TheListSurvivesBeingPackedAndReadBack()
    {
        Sorting.ReadCategories("0=Разное;1=Материалы;2=Еда;3=Оружие;4=Броня;5=Инструменты;"
                               + "6=Трофеи;7=Лут;9=Уголь");
        var packed = Sorting.PackCategories();

        Sorting.ReadCategories(packed);

        Assert.Equal(packed, Sorting.PackCategories());
        Assert.Equal("Уголь", Sorting.Title(9));
        Assert.False(Sorting.IsCategory(8));

        Builtin();
    }

    [Fact]
    public void RubbishIsIgnoredRatherThanBelieved()
    {
        Sorting.ReadCategories("=безномера;abc=Что-то;-1=Минус;999=Далеко;2=Харчи");

        Assert.Equal("Харчи", Sorting.Title(Sorting.Food));
        Assert.False(Sorting.IsCategory(999));
        Assert.Equal(Sorting.CategoryTitles.Length, Sorting.Count);

        Builtin();
    }
}
