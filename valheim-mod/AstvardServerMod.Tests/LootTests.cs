using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// «Лут» и чужое слово о категориях.
///
/// The game has no idea that a deer hide is loot: to it a hide is a material, the same
/// word it uses for stone. So the mod knows the drops by name, and whatever is said from
/// outside beats that list - the list knows vanilla, and the base belongs to people.
/// </summary>
public class LootTests
{
    [Fact]
    public void WhatFallsOffSomethingIsLoot()
    {
        Assert.True(Sorting.IsLoot("$item_greydwarfeye"));
        Assert.True(Sorting.IsLoot("$item_tar"));
        Assert.False(Sorting.IsLoot("$item_wood"));
        Assert.False(Sorting.IsLoot(null));

        // Шкуры уехали на свою полку, к тем, кто шьёт: «Добыча» осталась тем, что
        // действительно некуда деть.
        Assert.False(Sorting.IsLoot("$item_deerhide"));
    }

    [Fact]
    public void TheModKnowsItsOwnShelvesByName()
    {
        // Ключи сверены по каталогу, который прислала сама игра, а не по памяти.
        Assert.Equal(Sorting.Ore, Sorting.KnownFor("$item_copperore"));
        Assert.Equal(Sorting.Ore, Sorting.KnownFor("$item_blackmetalscrap"));
        Assert.Equal(Sorting.Wood, Sorting.KnownFor("$item_finewood"));
        Assert.Equal(Sorting.Seeds, Sorting.KnownFor("$item_carrotseeds"));
        Assert.Equal(Sorting.Seeds, Sorting.KnownFor("$item_pinecone"));
        Assert.Equal(Sorting.Hides, Sorting.KnownFor("$item_deerhide"));
        Assert.Equal(Sorting.Valuables, Sorting.KnownFor("$item_coins"));
        Assert.Equal(Sorting.Valuables, Sorting.KnownFor("$item_ancientgemstone_black"));

        // Медовухи и их основы - по началу ключа, а не списком: их сорок и прибавляется.
        // Полка у них своя и так и называется: искать «Медовухи» среди «Зелий» никто не
        // догадается, а других зелий в игре нет.
        Assert.Equal(Sorting.Meads, Sorting.KnownFor("$item_mead_hp_minor"));
        Assert.Equal(Sorting.Meads, Sorting.KnownFor("$item_meadbasehealth"));
        Assert.Equal(Sorting.Meads, Sorting.KnownFor("$item_barleywinebase"));
        Assert.Equal("Медовухи", Sorting.Title(Sorting.Meads));

        // Слиток - не руда: его возят из плавильни, а не в неё.
        Assert.Equal(-1, Sorting.KnownFor("$item_bronze"));
        Assert.Equal(-1, Sorting.KnownFor("$item_stone"));
    }

    [Fact]
    public void MeatIsFoodAndNotLoot()
    {
        // It falls off a creature too, and it still belongs with the food: the sorter is
        // for finding things again, and nobody looks for a neck tail among the pelts.
        Assert.False(Sorting.IsLoot("$item_necktail"));
        Assert.False(Sorting.IsLoot("$item_hare_meat"));
    }

    [Fact]
    public void WhatWasSaidFromOutsideIsKept()
    {
        Sorting.ReadChosen($"$item_tar={Sorting.Materials};$item_wood={Sorting.Misc}");

        Assert.Equal(Sorting.Materials, Sorting.ChosenFor("$item_tar"));
        Assert.Equal(Sorting.Misc, Sorting.ChosenFor("$item_wood"));
        Assert.Equal(-1, Sorting.ChosenFor("$item_stone"));
    }

    [Fact]
    public void ANonsenseLineChangesNothing()
    {
        // A category out of range, a record with no number, a record with no name: each is
        // dropped on its own rather than taking the rest of the line down with it.
        Sorting.ReadChosen($"$item_tar=99;=3;$item_guck;$item_stone={Sorting.Materials}");

        Assert.Equal(-1, Sorting.ChosenFor("$item_tar"));
        Assert.Equal(-1, Sorting.ChosenFor("$item_guck"));
        Assert.Equal(Sorting.Materials, Sorting.ChosenFor("$item_stone"));
    }

    [Fact]
    public void SayingNothingTakesBackWhatWasSaid()
    {
        Sorting.ReadChosen($"$item_tar={Sorting.Materials}");
        Sorting.ReadChosen("");

        Assert.Equal(-1, Sorting.ChosenFor("$item_tar"));
        Assert.Equal("", Sorting.PackChosen());
    }

    [Fact]
    public void ItSurvivesTheRoundTrip()
    {
        Sorting.ReadChosen($"$item_tar={Sorting.Loot}");
        var packed = Sorting.PackChosen();

        Sorting.ReadChosen("");
        Sorting.ReadChosen(packed);

        Assert.Equal(Sorting.Loot, Sorting.ChosenFor("$item_tar"));
        Sorting.ReadChosen("");
    }

    [Fact]
    public void ThereIsATitleForEveryCategory()
    {
        // The mark page draws one button per category from this array: a category with no
        // title would be a button with no name, and «Лут» was added to both or neither.
        Assert.Equal(Sorting.CategoryTitles.Length, Sorting.Count);
        Assert.Equal("Добыча", Sorting.Title(Sorting.Loot));
        Assert.Equal("Руда", Sorting.Title(Sorting.Ore));
        Assert.Equal("Ценное", Sorting.Title(Sorting.Valuables));

        for (var i = 0; i < Sorting.Count; i++) Assert.True(Sorting.IsCategory(i));
    }
}
