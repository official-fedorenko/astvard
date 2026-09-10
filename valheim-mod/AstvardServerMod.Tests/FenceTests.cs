using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// The ring of palisade the fence tool puts round a player. What matters is that it is
/// closed - no gap on a side and none at a corner - that it stands exactly as far out
/// as asked, and that it is built the way the player built «30м.» by hand.
/// </summary>
public class FenceTests
{
    private const float StakeWidth = 2f;

    [Fact]
    public void ThirtyMetresComesOutAsThePlayersOwnFence()
    {
        // «30м.»: sixteen sides of six stakes, the northern one at z = 30.16 with its
        // stakes at x = -5, -3 ... 5 and no turn, each next side 22.5 degrees on.
        var plan = Geometry.FenceRing(30.16f, 6, StakeWidth);

        Assert.Equal(16, plan.Sides);
        Assert.Equal(6, plan.PerSide);
        Assert.Equal(96, plan.Stakes.Count);

        var xs = new[] { -5f, -3f, -1f, 1f, 3f, 5f };
        for (var j = 0; j < 6; j++)
        {
            Assert.Equal(xs[j], plan.Stakes[j].At.X, 1);
            Assert.Equal(30.16f, plan.Stakes[j].At.Z, 2);
            Assert.Equal(0f, plan.Stakes[j].Yaw, 3);
        }

        for (var side = 0; side < 16; side++)
            Assert.Equal(22.5f * side, plan.Stakes[side * 6].Yaw, 3);
    }

    [Fact]
    public void NoSideIsLongerThanSixStakes()
    {
        for (var radius = 4f; radius <= 64f; radius += 0.5f)
        {
            var plan = Geometry.FenceRing(radius, 6, StakeWidth);
            Assert.InRange(plan.PerSide, 1, 6);
            Assert.True(plan.Sides >= 16, $"{radius} m: only {plan.Sides} sides");
            Assert.Equal(plan.Sides * plan.PerSide, plan.Stakes.Count);
        }
    }

    [Fact]
    public void EveryStakeStandsExactlyAsFarOutAsAsked()
    {
        foreach (var radius in new[] { 4f, 12.5f, 17f, 30f, 41.3f, 64f })
        {
            var plan = Geometry.FenceRing(radius, 6, StakeWidth);
            foreach (var stake in plan.Stakes)
            {
                // Distance to the side's line, not to the centre: the side's outward
                // direction is the stake's yaw.
                var yaw = stake.Yaw * Math.PI / 180.0;
                var outward = stake.At.X * Math.Sin(yaw) + stake.At.Z * Math.Cos(yaw);
                Assert.Equal(radius, (float)outward, 3);
            }
        }
    }

    [Fact]
    public void ASideHasNoGapsAndReachesBothCorners()
    {
        foreach (var radius in new[] { 4f, 9f, 17f, 30f, 45f, 64f })
        {
            var plan = Geometry.FenceRing(radius, 6, StakeWidth);

            // Stakes no further apart than they are wide...
            Assert.True(plan.Spacing <= StakeWidth + 1e-4f, $"{radius} m: spacing {plan.Spacing}");

            // ...and the last on each end reaching past its corner.
            for (var side = 0; side < plan.Sides; side++)
            {
                var first = plan.Stakes[side * plan.PerSide];
                var last = plan.Stakes[side * plan.PerSide + plan.PerSide - 1];
                var before = plan.Corners[(side + plan.Sides - 1) % plan.Sides];
                var after = plan.Corners[side];

                Assert.True(Distance(first.At, before) <= StakeWidth * 0.5f + 1e-3f,
                    $"{radius} m, side {side}: {Distance(first.At, before):F3} m short of its first corner");
                Assert.True(Distance(last.At, after) <= StakeWidth * 0.5f + 1e-3f,
                    $"{radius} m, side {side}: {Distance(last.At, after):F3} m short of its last corner");
            }
        }
    }

    [Fact]
    public void ALargeRingGetsMoreSidesRatherThanLongerOnes()
    {
        var plan = Geometry.FenceRing(40f, 6, StakeWidth);

        Assert.True(plan.Sides > 16);
        Assert.Equal(6, plan.PerSide);
    }

    [Fact]
    public void TheCornersAreEvenlyRoundTheRing()
    {
        var plan = Geometry.FenceRing(20f, 6, StakeWidth);
        var far = 20f / (float)Math.Cos(Math.PI / plan.Sides);

        Assert.Equal(plan.Sides, plan.Corners.Count);
        Assert.All(plan.Corners, corner => Assert.Equal(far, Distance(corner, new Vec2(0f, 0f)), 3));
    }

    [Fact]
    public void NothingForANonsenseRing()
    {
        Assert.Empty(Geometry.FenceRing(0f, 6, StakeWidth).Stakes);
        Assert.Empty(Geometry.FenceRing(10f, 0, StakeWidth).Stakes);
    }

    [Fact]
    public void ASixStakeSideIsThePlayersSectionExactly()
    {
        // «Секция 6шт.»: floors and roof panels at x = -5, -3 ... 5, posts at x = -4 and 4.
        Assert.Equal(new[] { -5f, -3f, -1f, 1f, 3f, 5f }, Geometry.SectionPanels(6));
        Assert.Equal(new[] { -4f, 4f }, Geometry.SectionPosts(6));
    }

    [Fact]
    public void PanelsKeepTheirOwnTwoMetreStepOnAShorterSide()
    {
        Assert.Equal(new[] { -3f, -1f, 1f, 3f }, Geometry.SectionPanels(4));
        Assert.Equal(new[] { -1f, 1f }, Geometry.SectionPanels(2));
        Assert.Equal(new[] { 0f }, Geometry.SectionPanels(1));
    }

    [Fact]
    public void PostsStayAPanelInFromTheEnds()
    {
        Assert.Equal(new[] { -2f, 2f }, Geometry.SectionPosts(4));
        Assert.Equal(new[] { -1f, 1f }, Geometry.SectionPosts(3));
        Assert.Equal(new[] { 0f }, Geometry.SectionPosts(2));
        Assert.Equal(new[] { 0f }, Geometry.SectionPosts(1));
    }

    [Fact]
    public void PanelsNeverRunMoreThanAMetrePastACorner()
    {
        // The stakes fill a side exactly; the panels keep a two metre step, so on a
        // side that is not a whole number of panels long they run over its ends.
        for (var radius = 4f; radius <= 64f; radius += 0.5f)
        {
            var plan = Geometry.FenceRing(radius, 6, StakeWidth);
            var sideLength = plan.PerSide * plan.Spacing;
            var panels = Geometry.SectionPanels(plan.PerSide);
            var over = panels[^1] + 1f - sideLength * 0.5f;
            Assert.InRange(over, -1e-3f, 1f + 1e-3f);
        }
    }

    private static float Distance(Vec2 a, Vec2 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }
}
