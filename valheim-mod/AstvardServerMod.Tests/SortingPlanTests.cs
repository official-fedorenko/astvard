using System.Collections.Generic;
using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// Splitting one category between its chests: a chest for each pile worth the room, one
/// chest for the odds and ends. Worked out afresh every sweep and kept nowhere, so the
/// tests are the only place its habits can be pinned down.
/// </summary>
public class SortingPlanTests
{
    private const int Materials = Sorting.Materials;

    private const int OwnChest = 4;   // slots a pile needs before it earns a chest

    private static Sorting.BinState Bin(int slots, params (string kind, int units)[] holds)
    {
        var bin = new Sorting.BinState { Category = Materials, Slots = slots };
        foreach (var (kind, units) in holds) bin.Holds[kind] = units;
        return bin;
    }

    private static Sorting.Load Load(string kind, int units, int stack) =>
        new Sorting.Load { Kind = kind, Category = Materials, Units = units, StackSize = stack };

    [Theory]
    [InlineData(500, 50, 10)]
    [InlineData(200, 50, 4)]
    [InlineData(30, 30, 1)]
    [InlineData(2, 30, 1)]
    [InlineData(0, 50, 0)]
    [InlineData(5, 0, 5)]     // a stack size of nothing is one to a slot, not a division by zero
    public void APileIsMeasuredInSlots(int units, int stack, int expected)
    {
        Assert.Equal(expected, Sorting.SlotsFor(units, stack));
    }

    [Fact]
    public void TheOwnersExample()
    {
        // Four chests of twelve slots marked «Материалы», and what the owner said he had:
        // five hundred wood, two hundred stone, three hundred ingots, thirty copper ore and
        // two tin. The three piles take a chest each; the ore, a slot apiece, shares the last.
        var bins = new List<Sorting.BinState> { Bin(12), Bin(12), Bin(12), Bin(12) };
        var loads = new List<Sorting.Load>
        {
            Load("Дерево", 500, 50),
            Load("Камень", 200, 50),
            Load("Слиток", 300, 30),
            Load("РудаМеди", 30, 30),
            Load("РудаОлова", 2, 30),
        };

        var plan = Sorting.MakePlan(bins, loads, OwnChest);

        var wood = Assert.Single(plan.Homes["Дерево"]);
        var stone = Assert.Single(plan.Homes["Камень"]);
        var ingots = Assert.Single(plan.Homes["Слиток"]);

        // Three different chests, one pile each.
        Assert.Equal(3, new HashSet<int> { wood, stone, ingots }.Count);

        // And the odd ore goes together, into the one nobody claimed.
        var mixed = Assert.Single(plan.Mixed[Materials]);
        Assert.Equal(new[] { mixed }, plan.Where("РудаМеди", Materials).ToArray());
        Assert.Equal(new[] { mixed }, plan.Where("РудаОлова", Materials).ToArray());
        Assert.NotEqual(wood, mixed);

        // And nothing else is ever carried into the wood chest: the empty slots in it are
        // not waste, they are where the next load goes.
        Assert.DoesNotContain(wood, plan.Where("РудаМеди", Materials));
        Assert.False(plan.Belongs("РудаМеди", Materials, wood));
    }

    [Fact]
    public void APileTooBigForOneChestTakesTwo()
    {
        // Ten slots of wood into chests of four: three chests, and the fourth still free for
        // everything else. A pile that did not ask for enough room would be sorted into a
        // chest, fill it, and spend every sweep afterwards failing to fit.
        var bins = new List<Sorting.BinState> { Bin(4), Bin(4), Bin(4), Bin(4) };
        var loads = new List<Sorting.Load> { Load("Дерево", 500, 50), Load("РудаОлова", 2, 30) };

        var plan = Sorting.MakePlan(bins, loads, OwnChest);

        Assert.Equal(3, plan.Homes["Дерево"].Count);
        Assert.Single(plan.Mixed[Materials]);

        // Overflow has somewhere to go - the shared chest, last in the list - so a pile
        // whose own chests are full does not sit in the cart for ever.
        Assert.Equal(4, plan.Where("Дерево", Materials).Count);
    }

    [Fact]
    public void ThePileGoesWhereMostOfItAlreadyIs()
    {
        // Otherwise the plan would move five hundred wood across the base to satisfy an
        // arbitrary choice, and might make the other choice next sweep.
        var bins = new List<Sorting.BinState>
        {
            Bin(12, ("Дерево", 5)),
            Bin(12, ("Дерево", 400)),
            Bin(12),
        };

        var plan = Sorting.MakePlan(bins, new List<Sorting.Load> { Load("Дерево", 500, 50) }, OwnChest);

        Assert.Equal(1, plan.Where("Дерево", Materials)[0]);
    }

    [Fact]
    public void GrowingOutOfTheMixedChestIsEnoughToBeMovedOut()
    {
        // Nobody promotes anything: the wood is simply big enough this sweep, so it is given
        // the chest it is already in, and the stone that was keeping it company is not.
        var bins = new List<Sorting.BinState> { Bin(12, ("Дерево", 500), ("Камень", 10)), Bin(12) };
        var loads = new List<Sorting.Load> { Load("Дерево", 500, 50), Load("Камень", 10, 50) };

        var plan = Sorting.MakePlan(bins, loads, OwnChest);

        Assert.Equal(0, plan.Where("Дерево", Materials)[0]);
        Assert.False(plan.Belongs("Камень", Materials, 0));
        Assert.Equal(1, plan.Where("Камень", Materials)[0]);
    }

    [Fact]
    public void ASpentPileGoesBackInWithTheRest()
    {
        // The other way round, and just as free: ten wood is not worth a chest any more, so
        // it shares one again. Nothing had to be un-promoted, because nothing was promoted.
        var bins = new List<Sorting.BinState> { Bin(12, ("Дерево", 10)), Bin(12, ("Камень", 300)) };
        var loads = new List<Sorting.Load> { Load("Дерево", 10, 50), Load("Камень", 300, 50) };

        var plan = Sorting.MakePlan(bins, loads, OwnChest);

        Assert.Equal(1, plan.Where("Камень", Materials)[0]);
        Assert.Equal(new[] { 0 }, plan.Mixed[Materials].ToArray());
    }

    [Fact]
    public void OneChestForTheCategoryIsNotSplitAtAll()
    {
        var bins = new List<Sorting.BinState> { Bin(12) };
        var loads = new List<Sorting.Load> { Load("Дерево", 500, 50), Load("РудаОлова", 2, 30) };

        var plan = Sorting.MakePlan(bins, loads, OwnChest);

        Assert.Equal(0, plan.Where("Дерево", Materials)[0]);
        Assert.Equal(0, plan.Where("РудаОлова", Materials)[0]);
    }

    [Fact]
    public void OneChestOfACategoryIsNeverGivenAway()
    {
        // Two chests and two piles: the bigger gets its own, the other shares with the ore.
        // If both were dedicated there would be nowhere at all for two tin, and the answer
        // to that is not to pour the tin in with the wood.
        var bins = new List<Sorting.BinState> { Bin(12), Bin(12) };
        var loads = new List<Sorting.Load>
        {
            Load("Дерево", 500, 50),
            Load("Камень", 300, 50),
            Load("РудаОлова", 2, 30),
        };

        var plan = Sorting.MakePlan(bins, loads, OwnChest);

        var wood = Assert.Single(plan.Homes["Дерево"]);
        var shared = Assert.Single(plan.Mixed[Materials]);
        Assert.NotEqual(wood, shared);

        // The stone did not get a chest of its own, so it shares - and so does the tin.
        Assert.False(plan.Homes.ContainsKey("Камень"));
        Assert.Equal(new[] { shared }, plan.Where("Камень", Materials).ToArray());
        Assert.Equal(new[] { shared }, plan.Where("РудаОлова", Materials).ToArray());
    }

    [Fact]
    public void SwitchedOffItIsTheOldRuleAgain()
    {
        // Nought splits nothing: every chest of the category takes everything, and a kind
        // still prefers the chest it is already in - which is what the sorter did before
        // there was a plan at all.
        var bins = new List<Sorting.BinState> { Bin(12), Bin(12, ("Дерево", 5)) };
        var loads = new List<Sorting.Load> { Load("Дерево", 500, 50) };

        var plan = Sorting.MakePlan(bins, loads, 0);

        Assert.Equal(2, plan.Mixed[Materials].Count);
        Assert.Equal(1, plan.Where("Дерево", Materials)[0]);
    }

    [Fact]
    public void AKindNobodyPlannedForStillHasSomewhereToGo()
    {
        var plan = Sorting.MakePlan(new List<Sorting.BinState> { Bin(12) }, new List<Sorting.Load>(), OwnChest);

        Assert.Equal(new[] { 0 }, plan.Where("ЧтоТоНовое", Materials).ToArray());
        Assert.Empty(plan.Where("ЧтоТоНовое", Sorting.Food));
    }

    [Fact]
    public void WithNoChestOfItsOwnKindItGoesToTheOddsAndEnds()
    {
        // One chest marked «Разное» and nothing else: the wood in the cart still has
        // somewhere to go. Without this a cart can stand in the zone, be counted, and never
        // be emptied - which is exactly what happened.
        var bins = new List<Sorting.BinState>
        {
            new Sorting.BinState { Category = Sorting.Misc, Slots = 12 },
        };
        var loads = new List<Sorting.Load> { Load("Дерево", 500, 50) };

        var plan = Sorting.MakePlan(bins, loads, OwnChest);

        Assert.Equal(new[] { 0 }, plan.Where("Дерево", Materials).ToArray());
        Assert.True(plan.Belongs("Дерево", Materials, 0));
    }

    [Fact]
    public void ItsOwnCategoryStillComesFirst()
    {
        var bins = new List<Sorting.BinState>
        {
            new Sorting.BinState { Category = Sorting.Misc, Slots = 12 },
            Bin(12),
            Bin(12),
        };
        var loads = new List<Sorting.Load> { Load("Дерево", 500, 50), Load("РудаОлова", 2, 30) };

        var plan = Sorting.MakePlan(bins, loads, OwnChest);

        // The wood chest, then the shared «Материалы» one, and «Разное» only after both.
        var where = plan.Where("Дерево", Materials);
        Assert.Equal(1, where[0]);
        Assert.Equal(0, where[where.Count - 1]);
    }

    [Fact]
    public void CategoriesDoNotBorrowEachOthersChests()
    {
        var bins = new List<Sorting.BinState>
        {
            Bin(12),
            new Sorting.BinState { Category = Sorting.Food, Slots = 12 },
        };
        var loads = new List<Sorting.Load>
        {
            Load("Дерево", 500, 50),
            new Sorting.Load { Kind = "Мясо", Category = Sorting.Food, Units = 400, StackSize = 20 },
        };

        var plan = Sorting.MakePlan(bins, loads, OwnChest);

        Assert.Equal(0, plan.Where("Дерево", Materials)[0]);
        Assert.Equal(1, plan.Where("Мясо", Sorting.Food)[0]);
    }
}
