using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// Весть в середине экрана игра рисует одной строкой и за край не переносит - перенос
/// наш. Хозяин прислал снимок 23.09.2026: отчёт об уложенной дорожке уехал за оба края.
/// </summary>
public class SayTests
{
    /// <summary>Та самая весть со снимка, 84 знака.</summary>
    private const string TooLong =
        "Дорожка 258 м готова, ширина 3,0 м, сглажена, снесено: 74, факелов: 38, обойдено: 2";

    [Fact]
    public void AShortMessageComesBackUntouched()
    {
        // Вести самой игры короткие, и перенос их касаться не должен вовсе.
        Assert.Equal("Вы замёрзли", Say.Wrap("Вы замёрзли"));
        Assert.Equal("", Say.Wrap(""));
        Assert.Null(Say.Wrap(null));
    }

    [Fact]
    public void TheMessageFromTheScreenshotFitsInTwoLines()
    {
        var wrapped = Say.Wrap(TooLong);
        var lines = wrapped.Split('\n');

        Assert.Equal(2, lines.Length);
        foreach (var line in lines)
            Assert.True(line.Length <= Say.Line, $"строка в {line.Length} знаков: «{line}»");

        // Ни одного знака не потеряно и не добавлено, кроме самих переносов.
        Assert.Equal(TooLong, wrapped.Replace("\n", " "));
    }

    [Fact]
    public void ItBreaksBetweenWordsAndNotInsideThem()
    {
        var lines = Say.Wrap(TooLong).Split('\n');

        foreach (var line in lines)
        {
            Assert.False(line.StartsWith(" "), $"строка начинается с пробела: «{line}»");
            Assert.False(line.EndsWith(" "), $"строка кончается пробелом: «{line}»");
        }
    }

    [Fact]
    public void AWordLongerThanTheLineIsLeftWhole()
    {
        // Перенос посреди числа или имени хуже строки, вылезшей за край.
        var long_ = new string('ё', 80);
        var wrapped = Say.Wrap(long_ + " хвост", 20);

        Assert.Equal(long_ + "\nхвост", wrapped);
    }

    [Fact]
    public void BreaksPutThereOnPurposeSurvive()
    {
        var wrapped = Say.Wrap("первая\nвторая", 20);

        Assert.Equal("первая\nвторая", wrapped);
    }

    [Fact]
    public void AVeryLongMessageTakesAsManyLinesAsItNeeds()
    {
        var text = "слово слово слово слово слово слово слово слово слово слово слово слово";

        var lines = Say.Wrap(text, 20).Split('\n');

        Assert.True(lines.Length >= 4, $"вышло строк {lines.Length}");
        foreach (var line in lines) Assert.True(line.Length <= 20, line);
    }
}
