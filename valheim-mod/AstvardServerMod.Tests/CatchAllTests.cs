using System.Collections.Generic;
using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// «Разное» — запасной сундук для всего, и порядок, в котором он предлагается, важнее,
/// чем кажется.
///
/// The catch-all exists so that nothing is ever stuck in a cart: a kind whose own category
/// has no chest still has somewhere to go. But it suits everything, so «does this chest
/// suit» is not the question the tidying pass may ask - a mushroom that once landed in the
/// catch-all suits it for ever, and «Еда» stands empty beside it. The pass reads the order
/// instead, and these tests pin that order.
/// </summary>
public class CatchAllTests
{
    private static Sorting.BinState Bin(int category, int slots)
    {
        return new Sorting.BinState { Category = category, Slots = slots };
    }

    private static Sorting.Load Load(int category, string kind, int units, int stack)
    {
        return new Sorting.Load { Kind = kind, Category = category, Units = units, StackSize = stack };
    }

    [Fact]
    public void FoodComesBeforeTheCatchAll()
    {
        // Сундук «Еда» и сундук «Разное»: гриб годится в оба, но первым — свой.
        var bins = new List<Sorting.BinState> { Bin(Sorting.Food, 10), Bin(Sorting.Misc, 10) };
        var loads = new List<Sorting.Load> { Load(Sorting.Food, "Гриб", 10, 50) };

        var plan = Sorting.MakePlan(bins, loads, 4);
        var where = plan.Where("Гриб", Sorting.Food);

        Assert.Equal(new[] { 0, 1 }, where.ToArray());
    }

    [Fact]
    public void TheCatchAllIsStillOfferedWhenTheCategoryHasNoChest()
    {
        // Одно «Разное» и ничего больше: иначе добру некуда ехать вовсе, и повозка
        // стоит в зоне полной - так это уже ломалось однажды.
        var bins = new List<Sorting.BinState> { Bin(Sorting.Misc, 10) };
        var loads = new List<Sorting.Load> { Load(Sorting.Food, "Гриб", 10, 50) };

        var plan = Sorting.MakePlan(bins, loads, 4);

        Assert.Equal(new[] { 0 }, plan.Where("Гриб", Sorting.Food).ToArray());
    }

    [Fact]
    public void AKindWithItsOwnChestIsCarriedHomeFromTheCatchAll()
    {
        // Куча, заработавшая свой сундук, стоит впереди и «Еды», и «Разного».
        var bins = new List<Sorting.BinState>
        {
            Bin(Sorting.Food, 10), Bin(Sorting.Food, 10), Bin(Sorting.Misc, 10)
        };
        var loads = new List<Sorting.Load>
        {
            Load(Sorting.Food, "Гриб", 500, 50),
            Load(Sorting.Food, "Малина", 5, 50),
        };

        var plan = Sorting.MakePlan(bins, loads, 4);
        var home = Assert.Single(plan.Homes["Гриб"]);

        Assert.Equal(home, plan.Where("Гриб", Sorting.Food)[0]);
        Assert.Equal(2, plan.Where("Гриб", Sorting.Food).IndexOf(2));
    }

    [Fact]
    public void TheListGivenBackIsTheSameAsTheOneFilledInPlace()
    {
        var bins = new List<Sorting.BinState>
        {
            Bin(Sorting.Food, 10), Bin(Sorting.Materials, 10), Bin(Sorting.Misc, 10)
        };
        var loads = new List<Sorting.Load> { Load(Sorting.Food, "Гриб", 10, 50) };

        var plan = Sorting.MakePlan(bins, loads, 4);

        var into = new List<int> { 42, 42, 42 };
        plan.WhereInto("Гриб", Sorting.Food, into);

        // И список чистится перед заполнением: иначе проход за проходом он бы только рос.
        Assert.Equal(plan.Where("Гриб", Sorting.Food).ToArray(), into.ToArray());
        Assert.DoesNotContain(42, into);
    }

    [Fact]
    public void AChestOfAnotherCategoryIsNoPlaceAtAll()
    {
        // «Материалы» грибу не годятся ни первым, ни последним: у «Разного» это особое
        // право, у остальных категорий его нет.
        var bins = new List<Sorting.BinState> { Bin(Sorting.Materials, 10), Bin(Sorting.Misc, 10) };
        var loads = new List<Sorting.Load> { Load(Sorting.Food, "Гриб", 10, 50) };

        var plan = Sorting.MakePlan(bins, loads, 4);

        Assert.DoesNotContain(0, plan.Where("Гриб", Sorting.Food));
        Assert.Contains(1, plan.Where("Гриб", Sorting.Food));
    }
}
