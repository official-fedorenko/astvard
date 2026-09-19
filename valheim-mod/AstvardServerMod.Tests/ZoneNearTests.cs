using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// Кайма вокруг зоны: она решает, работает ли зона, и ничего больше.
///
/// The owner stands at the gate and his kilns spit coal on the floor, because the line
/// runs between him and them. So a player near the zone counts as a player in it - but
/// only for that question. What the zone takes is still decided by the zone itself, or
/// the cart left outside the wall would be emptied from across it, which is exactly the
/// thing he had the old apron removed for.
/// </summary>
public class ZoneNearTests
{
    private static Sorting.Zone Round(float radius) =>
        new Sorting.Zone { X = 0f, Z = 0f, Radius = radius, Square = false };

    private static Sorting.Zone Square(float radius, float angle = 0f) =>
        new Sorting.Zone { X = 0f, Z = 0f, Radius = radius, Square = true, Angle = angle };

    [Fact]
    public void AStepOutsideIsStillNear()
    {
        var zone = Round(20f);

        Assert.False(Sorting.Inside(zone, 30f, 0f));
        Assert.True(Sorting.Near(zone, 30f, 0f, Sorting.NearReach));
    }

    [Fact]
    public void FarEnoughIsFar()
    {
        var zone = Round(20f);

        // 20 m of zone plus the apron: a step past that and nothing runs.
        Assert.True(Sorting.Near(zone, 20f + Sorting.NearReach - 0.5f, 0f, Sorting.NearReach));
        Assert.False(Sorting.Near(zone, 20f + Sorting.NearReach + 0.5f, 0f, Sorting.NearReach));
    }

    [Fact]
    public void TheBiggestZoneGrowsToo()
    {
        // The regression this was written for: the apron used to be applied by building a
        // wider zone and asking Inside, and Inside clamps the radius it is handed to
        // MaxZoneRadius. A zone already at the limit therefore grew by nothing, and the
        // largest zones - the ones a player is most likely to stand just outside of - were
        // the only ones the apron never helped.
        var zone = Round(Sorting.MaxZoneRadius);

        Assert.True(Sorting.Near(zone, Sorting.MaxZoneRadius + 10f, 0f, Sorting.NearReach));
    }

    [Fact]
    public void ATurnedSquareGrowsTheWayItLies()
    {
        // Straight out along +x, thirteen metres from the middle. For a square standing
        // square that is three metres past its side and needs an apron; for the same
        // square turned by 45° the corner has swung round to meet it, so it is inside
        // already. The apron follows the square's own axes, as everything else does.
        var straight = Square(10f);
        var turned = Square(10f, 45f);

        Assert.True(Sorting.Inside(turned, 13f, 0f));

        Assert.False(Sorting.Near(straight, 13f, 0f, 2f));
        Assert.True(Sorting.Near(straight, 13f, 0f, 4f));
    }

    [Fact]
    public void TheApronDoesNotMoveTheLineForWhatIsSorted()
    {
        // The whole point: near is near, inside is inside, and only the second decides
        // whether a chest or a cart belongs to the zone.
        var zone = Square(10f);

        Assert.True(Sorting.Near(zone, 20f, 0f, Sorting.NearReach));
        Assert.False(Sorting.Inside(zone, 20f, 0f));
    }

    [Fact]
    public void NoSlackIsTheOldStrictAnswer()
    {
        var zone = Round(20f);

        Assert.False(Sorting.Near(zone, 21f, 0f, 0f));
        Assert.True(Sorting.Near(zone, 19f, 0f, 0f));
    }
}
