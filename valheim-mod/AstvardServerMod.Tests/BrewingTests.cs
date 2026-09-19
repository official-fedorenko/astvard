using System;
using System.Collections.Generic;
using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// Варка медовух: сколько ставить и когда остановиться.
///
/// Ошибиться тут дорого в обе стороны. Недосчитал бродящее — и заказ в двадцать бутылок
/// заставит поставить двадцать бочек вместо четырёх. Пересчитал — бочки встанут, и
/// человек будет думать, что мод сломался.
/// </summary>
public class BrewingTests
{
    [Fact]
    public void WishesSurviveTheRoundTrip()
    {
        var wishes = Brewing.ReadWishes("MeadBaseTasty=20;MeadBaseHealthMinor=6");

        Assert.Equal(2, wishes.Count);
        Assert.Equal(20, wishes["MeadBaseTasty"]);
        Assert.Equal(6, wishes["MeadBaseHealthMinor"]);
        Assert.Equal("MeadBaseHealthMinor=6;MeadBaseTasty=20", Brewing.PackWishes(wishes));
    }

    [Fact]
    public void BrokenLinesAreSkippedInsteadOfBreakingTheRest()
    {
        // Строку правит и человек в конфиге: половина записи не должна уносить остальные.
        var wishes = Brewing.ReadWishes(";;MeadBaseTasty=20;мусор;=5;MeadBaseSwimmer=;MeadBaseHasty=нет;MeadBaseStrength=3");

        Assert.Equal(2, wishes.Count);
        Assert.Equal(20, wishes["MeadBaseTasty"]);
        Assert.Equal(3, wishes["MeadBaseStrength"]);
    }

    [Fact]
    public void ZeroAndBelowMeanDoNotBrew()
    {
        var wishes = Brewing.ReadWishes("MeadBaseTasty=0;MeadBaseHasty=-4");

        Assert.Empty(wishes);
        Assert.Equal("", Brewing.PackWishes(wishes));
    }

    [Fact]
    public void AnOrderIsHeldToItsCeiling()
    {
        var wishes = Brewing.ReadWishes("MeadBaseTasty=1000000");

        Assert.Equal(Brewing.MaxKeep, wishes["MeadBaseTasty"]);
    }

    [Fact]
    public void NamesThatWouldTearTheLineApartAreNotWritten()
    {
        var wishes = new Dictionary<string, int> { { "good", 2 }, { "ba;d", 3 }, { "al=so", 4 } };

        Assert.Equal("good=2", Brewing.PackWishes(wishes));
    }

    [Fact]
    public void WhatIsBrewingCountsTowardsTheOrder()
    {
        // Заказ 20 бутылок, шесть с бочки: четыре бочки хватит, и одна уже стоит.
        Assert.Equal(4, Brewing.StillToBrew(20, 0, 0, 6));
        Assert.Equal(3, Brewing.StillToBrew(20, 0, 1, 6));
        Assert.Equal(0, Brewing.StillToBrew(20, 0, 4, 6));
    }

    [Fact]
    public void WhatIsOnTheShelfCountsToo()
    {
        Assert.Equal(1, Brewing.StillToBrew(20, 18, 0, 6));
        Assert.Equal(0, Brewing.StillToBrew(20, 20, 0, 6));
        Assert.Equal(0, Brewing.StillToBrew(20, 26, 0, 6));
    }

    [Fact]
    public void OneBottleStillCostsAWholeBarrel()
    {
        Assert.Equal(1, Brewing.StillToBrew(1, 0, 0, 6));
        // Семь бутылок с шести за бочку — это две бочки и пять лишних бутылок: меньше
        // заказанного не наливают, а дробить бочку игра не умеет.
        Assert.Equal(2, Brewing.StillToBrew(7, 0, 0, 6));
        Assert.Equal(2, Brewing.StillToBrew(7, 0, 0, 4));
    }

    [Fact]
    public void NoOrderAndNoYieldMeanNoWork()
    {
        Assert.Equal(0, Brewing.StillToBrew(0, 0, 0, 6));
        Assert.Equal(0, Brewing.StillToBrew(20, 0, 0, 0));
    }

    [Fact]
    public void AffordabilityIsAskedOfEveryIngredient()
    {
        var need = new List<KeyValuePair<string, int>>
        {
            new("Honey", 10),
            new("Raspberry", 10)
        };
        var stock = new Dictionary<string, int> { { "Honey", 10 }, { "Raspberry", 9 } };

        Assert.False(Brewing.CanAfford(need, name => stock.TryGetValue(name, out var n) ? n : 0));

        stock["Raspberry"] = 10;
        Assert.True(Brewing.CanAfford(need, name => stock.TryGetValue(name, out var n) ? n : 0));
    }

    [Fact]
    public void AnEmptyRecipeBuysNothing()
    {
        // Рецепт без составляющих — это не «бесплатно», это «мы его не поняли».
        Assert.False(Brewing.CanAfford(new List<KeyValuePair<string, int>>(), _ => 100));
        Assert.False(Brewing.CanAfford(null, _ => 100));
    }

    [Fact]
    public void TheOrderFurthestFromDoneGoesFirst()
    {
        var bases = new List<string> { "MeadBaseTasty", "MeadBaseHasty" };
        var left = new Dictionary<string, int> { { "MeadBaseTasty", 2 }, { "MeadBaseHasty", 3 } };
        var keep = new Dictionary<string, int> { { "MeadBaseTasty", 12 }, { "MeadBaseHasty", 6 } };

        // Вкусной не хватает двух бочек из двенадцати бутылок, Рататоску — трёх из шести.
        Assert.Equal("MeadBaseHasty", Brewing.Next(bases, n => left[n], n => keep[n]));
    }

    [Fact]
    public void ABigOrderDoesNotStarveASmallOne()
    {
        // Иначе заказ на сотню перебивал бы десяток всегда, и второй не сварился бы никогда.
        var bases = new List<string> { "Big", "Small" };
        var left = new Dictionary<string, int> { { "Big", 10 }, { "Small", 1 } };
        var keep = new Dictionary<string, int> { { "Big", 600 }, { "Small", 6 } };

        Assert.Equal("Small", Brewing.Next(bases, n => left[n], n => keep[n]));
    }

    [Fact]
    public void EqualNeedIsSettledByNameSoTheChoiceDoesNotJump()
    {
        var bases = new List<string> { "Beta", "Alpha" };

        Assert.Equal("Alpha", Brewing.Next(bases, _ => 1, _ => 6));
    }

    [Fact]
    public void NothingToDoAnswersWithNothing()
    {
        var bases = new List<string> { "MeadBaseTasty" };

        Assert.Null(Brewing.Next(bases, _ => 0, _ => 6));
        Assert.Null(Brewing.Next(new List<string>(), _ => 5, _ => 6));
        Assert.Null(Brewing.Next(null, _ => 5, _ => 6));
    }
}
