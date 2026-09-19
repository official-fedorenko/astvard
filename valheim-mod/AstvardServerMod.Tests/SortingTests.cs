using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// The sorter's decisions and the zone it works inside. Both are quiet when wrong: an
/// unmarked chest read as a bin turns the whole base into «Разное», a square read as a
/// circle leaves the corners of the hall unsorted, and two zones sharing ground make one
/// chest empty into two places.
/// </summary>
public class SortingTests
{
    private const int Materials = 1;

    private const int Food = 2;

    private static Sorting.Bin Bin(int category, bool same = false, bool room = true) =>
        new Sorting.Bin { Category = category, HasSame = same, HasRoom = room };

    private static Sorting.Zone Zone(float x, float z, float radius, bool square = false) =>
        new Sorting.Zone { X = x, Z = z, Radius = radius, Square = square };

    // ---------------- the mark on a chest ----------------

    [Fact]
    public void AnUnmarkedChestIsNotABin()
    {
        // The whole sorter hangs on this. Zero is «карандашом не тронут», and if it read as
        // category zero every chest on the base would become a «Разное» bin and the sorter
        // would shuffle the base into itself.
        Assert.False(Sorting.IsBin(0));
        Assert.Equal(-1, Sorting.FromStored(0));
        Assert.True(Sorting.IsBin(Sorting.ToStored(Sorting.Misc)));
        Assert.Equal(Sorting.Misc, Sorting.FromStored(Sorting.ToStored(Sorting.Misc)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    public void EveryCategorySurvivesTheChest(int category)
    {
        Assert.Equal(category, Sorting.FromStored(Sorting.ToStored(category)));
    }

    [Fact]
    public void AnUnknownNumberInTheChestIsStillAMark()
    {
        // This used to read an unknown number as «not marked», which was safe only while
        // the list of categories could not change. Now that the site owns it, an unknown
        // number is the everyday case - a category taken away, or a list that has not
        // arrived yet - and reading it as «not marked» would turn a full chest into a
        // source and carry it off. So a mark is a mark, and only its name is missing.
        Assert.Equal(-1, Sorting.FromStored(-5));
        Assert.Equal(-1, Sorting.FromStored(0));

        Assert.Equal(998, Sorting.FromStored(999));
        Assert.True(Sorting.IsBin(999));
        Assert.Equal("Категория 998", Sorting.Title(998));

        // Пометить сундук несуществующей категорией по-прежнему нечем: это спрашивают
        // там, где выбор делает человек, и выбирать ему не из чего.
        Assert.Equal(0, Sorting.ToStored(999));
        Assert.False(Sorting.IsCategory(998));
    }

    // ---------------- where an item goes ----------------

    [Fact]
    public void AChestAlreadyHoldingItWins()
    {
        // This is «этот под дерево» without anyone naming a single item: put a stack in a
        // chest once, and the rest follows it there.
        var bins = new[] { Bin(Sorting.Misc), Bin(Food, same: true), Bin(Materials) };
        Assert.Equal(1, Sorting.Choose(Materials, bins));
    }

    [Fact]
    public void ThenTheCategory()
    {
        var bins = new[] { Bin(Sorting.Misc), Bin(Food), Bin(Materials) };
        Assert.Equal(2, Sorting.Choose(Materials, bins));
    }

    [Fact]
    public void ThenWhateverTakesTheRest()
    {
        var bins = new[] { Bin(Food), Bin(Sorting.Misc) };
        Assert.Equal(1, Sorting.Choose(Materials, bins));
    }

    [Fact]
    public void AFullChestIsNotAnAnswer()
    {
        // Full at every step, not only the first: an item whose own chest is full still
        // finds the category chest. Choosing a full one would mean an item that is picked
        // up and put back once a second, for ever.
        var bins = new[] { Bin(Materials, same: true, room: false), Bin(Materials) };
        Assert.Equal(1, Sorting.Choose(Materials, bins));

        var noRoom = new[] { Bin(Materials, room: false), Bin(Sorting.Misc, room: false) };
        Assert.Equal(-1, Sorting.Choose(Materials, noRoom));
    }

    [Fact]
    public void NoBinsMeansLeaveItAlone()
    {
        Assert.Equal(-1, Sorting.Choose(Materials, new Sorting.Bin[0]));
        Assert.Equal(-1, Sorting.Choose(Materials, null));
    }

    // ---------------- the zone ----------------

    [Fact]
    public void TheCornerIsWhereTheShapesDiffer()
    {
        // A point out on the diagonal is in the square and out of the circle. If it were not,
        // there would be no reason to offer both.
        Assert.True(Sorting.Inside(Zone(0f, 0f, 10f, square: true), 10f, 10f));
        Assert.False(Sorting.Inside(Zone(0f, 0f, 10f), 10f, 10f));

        // Straight out, both agree.
        Assert.True(Sorting.Inside(Zone(0f, 0f, 10f, square: true), 10f, 0f));
        Assert.True(Sorting.Inside(Zone(0f, 0f, 10f), 10f, 0f));
        Assert.False(Sorting.Inside(Zone(0f, 0f, 10f), 10.5f, 0f));
    }

    [Fact]
    public void HeightIsNotAsked()
    {
        // The cellar is the base too, and a zone that stopped at the floor would sort the
        // hall and not the store under it.
        Assert.True(Sorting.Inside(Zone(100f, -50f, 20f), 105f, -45f));
    }

    [Fact]
    public void StandingJustOutsideStillCounts()
    {
        // The apron is why a cart parked at the gate is emptied: its owner stops beside it,
        // which is a step outside the chests' zone, and «ты вне своих зон» is a true answer
        // that helps nobody.
        var zones = new[] { Zone(0f, 0f, 10f) };

        Assert.Equal(-1, Sorting.ZoneAt(zones, 20f, 0f));
        Assert.Equal(0, Sorting.ZoneAt(zones, 20f, 0f, 16f));
        Assert.Equal(-1, Sorting.ZoneAt(zones, 40f, 0f, 16f));

        // A square grows the way it lies, corners and all.
        var square = new[] { Zone(0f, 0f, 10f, square: true) };
        Assert.Equal(0, Sorting.ZoneAt(square, 25f, 25f, 16f));
        Assert.Equal(-1, Sorting.ZoneAt(square, 27f, 27f, 16f));
    }

    [Fact]
    public void ZonesMayNotShareGround()
    {
        Assert.True(Sorting.Overlap(Zone(0f, 0f, 10f), Zone(15f, 0f, 10f)));
        Assert.False(Sorting.Overlap(Zone(0f, 0f, 10f), Zone(25f, 0f, 10f)));

        Assert.True(Sorting.Overlap(Zone(0f, 0f, 10f, true), Zone(15f, 15f, 10f, true)));
        Assert.False(Sorting.Overlap(Zone(0f, 0f, 10f, true), Zone(25f, 0f, 10f, true)));
    }

    [Fact]
    public void ASquareAndACircleMeetAtTheNearestCorner()
    {
        // Diagonally apart: the boxes would say «apart», the circles «together», and only
        // the corner of the square against the middle of the circle answers it properly.
        var square = Zone(0f, 0f, 10f, square: true);

        Assert.True(Sorting.Overlap(square, Zone(14f, 14f, 6f)));    // reaches the corner
        Assert.False(Sorting.Overlap(square, Zone(20f, 20f, 6f)));   // does not
        Assert.True(Sorting.Overlap(Zone(14f, 14f, 6f), square));    // and says the same either way round
        Assert.False(Sorting.Overlap(Zone(20f, 20f, 6f), square));
    }

    [Fact]
    public void TheZoneYouStandIn()
    {
        var zones = new[] { Zone(0f, 0f, 10f), Zone(100f, 0f, 10f, square: true) };

        Assert.Equal(0, Sorting.ZoneAt(zones, 3f, 3f));
        Assert.Equal(1, Sorting.ZoneAt(zones, 108f, 8f));
        Assert.Equal(-1, Sorting.ZoneAt(zones, 50f, 50f));
    }

    [Fact]
    public void ZonesSurviveTheConfigLine()
    {
        var zones = new[] { Zone(-1234.5f, 678.25f, 12f), Zone(10f, 20f, 30f, square: true) };
        var back = Sorting.Parse(Sorting.Pack(zones));

        Assert.Equal(2, back.Count);
        Assert.Equal(-1234.5f, back[0].X, 1);
        Assert.Equal(678.2f, back[0].Z, 1);
        Assert.False(back[0].Square);
        Assert.True(back[1].Square);
        Assert.Equal(30f, back[1].Radius, 1);

        // A decimal point, not a comma: a config written under one locale is read under
        // another, and a comma here is a record split in half.
        Assert.Contains(".", Sorting.Pack(zones));
    }

    [Fact]
    public void AZoneKeepsTheNameItWasGiven()
    {
        var zones = new[] { new Sorting.Zone { X = 1f, Z = 2f, Radius = 20f, Name = "Двор" } };
        var back = Sorting.Parse(Sorting.Pack(zones));

        Assert.Equal("Двор", Assert.Single(back).Name);
    }

    [Fact]
    public void ANameCannotTearTheConfigLineApart()
    {
        // A comma splits fields and a semicolon splits records, so a name carrying either
        // would not merely be wrong - it would take every zone after it down with it.
        var zones = new[]
        {
            new Sorting.Zone { X = 1f, Z = 2f, Radius = 20f, Name = "Двор, склад; сарай" },
            new Sorting.Zone { X = 100f, Z = 0f, Radius = 20f, Name = "Кузница" },
        };

        var back = Sorting.Parse(Sorting.Pack(zones));

        Assert.Equal(2, back.Count);
        Assert.Equal("Двор  склад  сарай", back[0].Name);
        Assert.Equal("Кузница", back[1].Name);
    }

    [Fact]
    public void AZoneWrittenBeforeThereWereNamesHasNone()
    {
        var zone = Assert.Single(Sorting.Parse("10.0,20.0,15.0,1,45.0"));

        Assert.True(zone.Square);
        Assert.Equal("", zone.Name);
    }

    [Fact]
    public void ANameLongerThanTheButtonIsCutToIt()
    {
        var long_ = new string('я', 60);
        Assert.Equal(Sorting.MaxZoneName, Sorting.CleanName(long_).Length);
    }

    [Fact]
    public void ABrokenRecordIsDroppedNotGuessedAt()
    {
        var zones = Sorting.Parse("10.0,20.0,15.0,0;совсем не зона;;30.0,40.0,8.0,1");

        Assert.Equal(2, zones.Count);
        Assert.Equal(10f, zones[0].X, 1);
        Assert.True(zones[1].Square);
        Assert.Empty(Sorting.Parse(""));
        Assert.Empty(Sorting.Parse(null));
    }

    [Fact]
    public void AZoneIsHeldToASizeSomebodyCanWalk()
    {
        var zones = Sorting.Parse("0.0,0.0,9999.0,0;0.0,0.0,0.1,0");

        Assert.Equal(Sorting.MaxZoneRadius, zones[0].Radius);
        Assert.Equal(Sorting.MinZoneRadius, zones[1].Radius);
    }
}
