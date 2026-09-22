using System.Collections.Generic;
using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// Заказ на варку принадлежит персонажу, а не игре: файл на всех, по строке на
/// каждого, ключ — номер персонажа. Имя рядом только для чтения глазами, и на него
/// ничего не опирается: двух «Бьёрнов» завести никто не мешает.
/// </summary>
public class BrewBookTests
{
    private static Brewing.Book Book(long id, string name, params (string, int)[] wishes)
    {
        var book = new Brewing.Book { Id = id, Name = name };
        foreach (var (what, keep) in wishes) book.Wishes[what] = keep;
        return book;
    }

    [Fact]
    public void AnOrderSurvivesTheRoundTrip()
    {
        var lines = Brewing.PackBooks(new[]
        {
            Book(12345L, "Бьёрн", ("MeadBaseTasty", 10), ("MeadBaseHealthMinor", 4)),
            Book(999L, "Сигрид", ("MeadBaseStaminaMinor", 2)),
        });

        var back = Brewing.ReadBooks(lines);

        Assert.Equal(2, back.Count);
        Assert.Equal("Бьёрн", back[12345L].Name);
        Assert.Equal(10, back[12345L].Wishes["MeadBaseTasty"]);
        Assert.Equal(4, back[12345L].Wishes["MeadBaseHealthMinor"]);
        Assert.Equal(2, back[999L].Wishes["MeadBaseStaminaMinor"]);
    }

    [Fact]
    public void TwoCharactersOfOneNameKeepTheirOwnOrders()
    {
        // Это и есть причина, по которой ключ — номер, а не имя.
        var lines = Brewing.PackBooks(new[]
        {
            Book(1L, "Бьёрн", ("MeadBaseTasty", 10)),
            Book(2L, "Бьёрн", ("MeadBaseTasty", 3)),
        });

        var back = Brewing.ReadBooks(lines);

        Assert.Equal(2, back.Count);
        Assert.Equal(10, back[1L].Wishes["MeadBaseTasty"]);
        Assert.Equal(3, back[2L].Wishes["MeadBaseTasty"]);
    }

    [Fact]
    public void AnOrderOfNothingIsNotTheSameAsNoOrderAtAll()
    {
        // «Я ничего не варю» и «я ещё ничего не решал» — разные ответы, и от второго
        // зависит, достанется ли персонажу общая строка.
        var lines = Brewing.PackBooks(new[] { Book(7L, "Тихий") });
        var back = Brewing.ReadBooks(lines);

        Assert.True(back.ContainsKey(7L));
        Assert.Empty(back[7L].Wishes);
        Assert.Empty(Brewing.WishesFor(back, 7L));
    }

    [Fact]
    public void ACharacterWithNoLineHasNoOrders()
    {
        var books = Brewing.ReadBooks(Brewing.PackBooks(new[] { Book(1L, "Бьёрн", ("MeadBaseTasty", 5)) }));

        Assert.Empty(Brewing.WishesFor(books, 2L));
        Assert.Empty(Brewing.WishesFor(books, 0L));
        Assert.Single(Brewing.WishesFor(books, 1L));
    }

    [Theory]
    [InlineData("Бьёрн\tСигрид")]
    [InlineData("Бьёрн\nСигрид")]
    [InlineData("Бьёрн\r\nСигрид")]
    public void ANameCannotTearTheFileApart(string name)
    {
        // Имя персонажа — чужой ввод, а поля здесь разделены табуляцией.
        var lines = Brewing.PackBooks(new[] { Book(5L, name, ("MeadBaseTasty", 1)) });

        Assert.Single(lines);
        var back = Brewing.ReadBooks(lines);
        Assert.Single(back);
        Assert.Equal(1, back[5L].Wishes["MeadBaseTasty"]);
    }

    [Fact]
    public void ANamelessCharacterGetsSomethingReadable()
    {
        Assert.Equal("…", Brewing.CleanLabel(""));
        Assert.Equal("…", Brewing.CleanLabel("   "));
        Assert.Equal("Бьёрн", Brewing.CleanLabel("  Бьёрн  "));
    }

    [Fact]
    public void RubbishInTheFileIsSkippedAndTheRestIsRead()
    {
        var back = Brewing.ReadBooks(new[]
        {
            "# astvard brewing: character id, name, what to keep brewing",
            "",
            "   ",
            "не число\tКто-то\tMeadBaseTasty=5",
            "0\tНоль\tMeadBaseTasty=5",
            "42\tБьёрн\tMeadBaseTasty=5",
        });

        Assert.Single(back);
        Assert.Equal(5, back[42L].Wishes["MeadBaseTasty"]);
    }

    [Fact]
    public void TheLastLineForACharacterWins()
    {
        var back = Brewing.ReadBooks(new[]
        {
            "42\tБьёрн\tMeadBaseTasty=5",
            "42\tБьёрн\tMeadBaseTasty=9",
        });

        Assert.Equal(9, back[42L].Wishes["MeadBaseTasty"]);
    }

    // ---------------- переезд общей строки ----------------

    [Fact]
    public void TheSharedOrderGoesToWhoeverCanBrewAllOfIt()
    {
        var shared = Brewing.ReadWishes("MeadBaseTasty=10;MeadBaseHealthMinor=4");
        var eightRecipes = new HashSet<string>
            { "MeadBaseTasty", "MeadBaseHealthMinor", "MeadBaseStaminaMinor" };

        Assert.True(Brewing.MayAdopt(shared, eightRecipes));
    }

    [Fact]
    public void ANewCharacterCannotTakeAnOrderItCouldNotHaveMade()
    {
        // Ровно тот случай, с которого всё началось: восемь заказов у персонажа с
        // четырьмя рецептами.
        var shared = Brewing.ReadWishes("MeadBaseTasty=10;MeadBaseHealthMinor=4");
        var fourRecipes = new HashSet<string> { "MeadBaseTasty" };

        Assert.False(Brewing.MayAdopt(shared, fourRecipes));
    }

    [Fact]
    public void ThereIsNothingToAdoptFromAnEmptyOrder()
    {
        var known = new HashSet<string> { "MeadBaseTasty" };

        Assert.False(Brewing.MayAdopt(Brewing.ReadWishes(""), known));
        Assert.False(Brewing.MayAdopt(new Dictionary<string, int>(), known));
        Assert.False(Brewing.MayAdopt(Brewing.ReadWishes("MeadBaseTasty=1"), new HashSet<string>()));
    }
}
