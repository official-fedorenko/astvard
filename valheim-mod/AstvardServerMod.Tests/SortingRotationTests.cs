using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// A sorting zone turned to lie along the hall. Drawing it turned is the easy half; these
/// are the other half, where a mistake is a zone that looks turned and sorts straight -
/// worse than one that never turned at all, because the picture lies about it.
/// </summary>
public class SortingRotationTests
{
    private static Sorting.Zone Square(float x, float z, float radius, float angle) =>
        new Sorting.Zone { X = x, Z = z, Radius = radius, Square = true, Angle = angle };

    private static Sorting.Zone Round(float x, float z, float radius, float angle = 0f) =>
        new Sorting.Zone { X = x, Z = z, Radius = radius, Square = false, Angle = angle };

    [Fact]
    public void TurningMovesTheCornersOntoTheSides()
    {
        // Straight out along one axis: past the side of a straight square, inside a turned
        // one, because the corner has swung round to meet it.
        Assert.False(Sorting.Inside(Square(0f, 0f, 10f, 0f), 13f, 0f));
        Assert.True(Sorting.Inside(Square(0f, 0f, 10f, 45f), 13f, 0f));

        // And the other way about: the old corner is now past the new side.
        Assert.True(Sorting.Inside(Square(0f, 0f, 10f, 0f), 10f, 10f));
        Assert.False(Sorting.Inside(Square(0f, 0f, 10f, 45f), 10f, 10f));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(90f)]
    [InlineData(180f)]
    [InlineData(-90f)]
    [InlineData(360f)]
    public void AQuarterTurnLeavesASquareWhereItWas(float angle)
    {
        // Four of the game's sixteen clicks bring a square back to itself. If any of these
        // disagreed, the turn would be doing something other than turning.
        Assert.True(Sorting.Inside(Square(0f, 0f, 10f, angle), 9f, 9f));
        Assert.False(Sorting.Inside(Square(0f, 0f, 10f, angle), 11f, 0f));
    }

    [Fact]
    public void ACircleDoesNotCareHowItIsTurned()
    {
        Assert.True(Sorting.Inside(Round(0f, 0f, 10f, 37f), 9f, 0f));
        Assert.False(Sorting.Inside(Round(0f, 0f, 10f, 37f), 9f, 9f));
    }

    [Fact]
    public void ATurnedZoneReachesWhereAStraightOneDoesNot()
    {
        // Twenty-four metres apart: two straight squares of ten miss each other, and the
        // same pair with one turned do not. This is the case that says the overlap check
        // is looking at the shape rather than at the numbers it was built from.
        var straight = Square(0f, 0f, 10f, 0f);

        Assert.False(Sorting.Overlap(straight, Square(24f, 0f, 10f, 0f)));
        Assert.True(Sorting.Overlap(straight, Square(24f, 0f, 10f, 45f)));

        // Asked either way round it answers the same.
        Assert.True(Sorting.Overlap(Square(24f, 0f, 10f, 45f), straight));
    }

    [Fact]
    public void ACircleMeetsTheTurnedSideToo()
    {
        var ring = Round(26f, 0f, 4f);

        Assert.False(Sorting.Overlap(Square(0f, 0f, 20f, 0f), ring));
        Assert.True(Sorting.Overlap(Square(0f, 0f, 20f, 45f), ring));
        Assert.True(Sorting.Overlap(ring, Square(0f, 0f, 20f, 45f)));
    }

    [Fact]
    public void TheZoneYouStandInKnowsItsTurn()
    {
        var zones = new[] { Square(0f, 0f, 10f, 45f) };

        Assert.Equal(0, Sorting.ZoneAt(zones, 13f, 0f));
        Assert.Equal(-1, Sorting.ZoneAt(zones, 10f, 10f));
    }

    [Fact]
    public void TheTurnSurvivesTheConfigLine()
    {
        var zones = new[] { Square(10f, 20f, 30f, 112.5f), Round(-5f, -5f, 12f) };
        var back = Sorting.Parse(Sorting.Pack(zones));

        Assert.Equal(112.5f, back[0].Angle, 1);
        Assert.Equal(0f, back[1].Angle, 1);
    }

    [Fact]
    public void AZoneWrittenBeforeThereWasATurnWasNeverTurned()
    {
        // Four fields is what the config held before the angle existed, and a zone nobody
        // could turn is a zone at zero. Dropping the record instead would lose somebody's
        // zones on the day they updated.
        var zones = Sorting.Parse("10.0,20.0,15.0,1");

        var zone = Assert.Single(zones);
        Assert.True(zone.Square);
        Assert.Equal(0f, zone.Angle);
    }

    [Theory]
    [InlineData(-90f, 270f)]
    [InlineData(450f, 90f)]
    [InlineData(0f, 0f)]
    [InlineData(360f, 0f)]
    public void OneTurnIsSaidOneWay(float given, float expected)
    {
        Assert.Equal(expected, Sorting.NormaliseAngle(given), 2);
    }

    [Fact]
    public void NonsenseIsNoTurnAtAll()
    {
        Assert.Equal(0f, Sorting.NormaliseAngle(float.NaN));
        Assert.Equal(0f, Sorting.NormaliseAngle(float.PositiveInfinity));
    }
}
