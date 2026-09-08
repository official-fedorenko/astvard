using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// Where a wooden bridge puts its legs. The limits these lean on came out of
/// WearNTear's own support decay, so the numbers are not adjustable taste: a leg
/// one step too far apart is a bridge that falls down some seconds after it is
/// finished, with nothing to show why.
/// </summary>
public class BridgeTests
{
    /// <summary>
    /// A profile of the given depths below a deck at zero.
    /// </summary>
    private static float[] Under(params float[] depths)
    {
        var ground = new float[depths.Length];
        for (var i = 0; i < depths.Length; i++) ground[i] = -depths[i];
        return ground;
    }

    [Theory]
    [InlineData(0f, 16f)]      // on the bank: full support, the free span
    [InlineData(2f, 16f)]
    [InlineData(4f, 14f)]
    [InlineData(6f, 10f)]
    [InlineData(8f, 10f)]
    [InlineData(10f, 6f)]
    [InlineData(12f, 4f)]
    [InlineData(14f, 2f)]
    public void SpacingMatchesWhatTheSupportDecayAllows(float height, float span)
    {
        Assert.Equal(span, Geometry.MaxPierSpacing(height));
    }

    [Fact]
    public void HeightsRoundUpToTheNextMeasuredStep()
    {
        // Between two measured heights the shorter span is the honest answer.
        // Interpolating would promise a distance nothing was ever tested at.
        Assert.Equal(14f, Geometry.MaxPierSpacing(2.1f));
        Assert.Equal(10f, Geometry.MaxPierSpacing(5.9f));
        Assert.Equal(2f, Geometry.MaxPierSpacing(13.5f));
    }

    [Fact]
    public void NothingHangsOffALegTallerThanTheLimit()
    {
        Assert.Equal(0f, Geometry.MaxPierSpacing(Geometry.MaxPierHeight + 0.1f));
        Assert.Equal(0f, Geometry.MaxPierSpacing(20f));
    }

    [Fact]
    public void AGapInsideTheFreeSpanNeedsNoLegs()
    {
        // 8 samples of 2 m: 16 m of deck between two banks, which is exactly what
        // the two-sided rule carries.
        var ground = Under(0f, 9f, 9f, 9f, 9f, 9f, 9f, 9f, 0f);
        var plan = Geometry.PlanPiers(ground, 2f, 0f);

        Assert.True(plan.Stands);
        Assert.Empty(plan.Piers);
    }

    [Fact]
    public void AGapPastTheFreeSpanWithNowhereToStandFails()
    {
        // Same ravine, one sample wider, and too deep for a leg anywhere in it.
        var ground = Under(0f, 20f, 20f, 20f, 20f, 20f, 20f, 20f, 20f, 20f, 0f);
        var plan = Geometry.PlanPiers(ground, 2f, 0f);

        Assert.False(plan.Stands);
        Assert.Equal(0, plan.GapFrom);
        Assert.Equal(ground.Length - 1, plan.GapTo);
    }

    /// <summary>
    /// What the refusal points at. The far bank is where the bridge ends, not where
    /// the trouble is, and naming it makes one hole in an otherwise fine crossing read
    /// as the whole crossing being impossible.
    /// </summary>
    [Fact]
    public void TheRefusalNamesTheStretchThatCannotBeCrossed()
    {
        // Twenty metres of water too deep for any leg, with ordinary ground on both
        // sides of it. The walk gets as far as sample 1 and stops; sample 11 is the
        // first place past the hole where a leg could stand again.
        var ground = Under(0f, 2f, 20f, 20f, 20f, 20f, 20f, 20f, 20f, 20f, 20f, 2f, 2f);
        var plan = Geometry.PlanPiers(ground, 2f, 0f);

        Assert.False(plan.Stands);
        Assert.Equal(1, plan.GapFrom);
        Assert.Equal(11, plan.GapTo);
    }

    [Fact]
    public void ShallowWaterGetsLegsAtTheFullAllowedSpacing()
    {
        // Two metres deep the whole way: legs may stand 16 m apart, which is
        // every eighth sample.
        var ground = Under(new float[25].Select(_ => 2f).ToArray());
        var plan = Geometry.PlanPiers(ground, 2f, 0f);

        Assert.True(plan.Stands);
        foreach (var pier in plan.Piers) Assert.Equal(0, pier % 8);
        Assert.DoesNotContain(0, plan.Piers);
        Assert.DoesNotContain(ground.Length - 1, plan.Piers);
    }

    [Fact]
    public void DeeperWaterGetsMoreLegs()
    {
        var shallow = Geometry.PlanPiers(Under(new float[41].Select(_ => 3f).ToArray()), 2f, 0f);
        var deep = Geometry.PlanPiers(Under(new float[41].Select(_ => 11f).ToArray()), 2f, 0f);

        Assert.True(shallow.Stands);
        Assert.True(deep.Stands);
        Assert.True(deep.Piers.Count > shallow.Piers.Count,
            $"deep {deep.Piers.Count} should need more legs than shallow {shallow.Piers.Count}");
    }

    [Fact]
    public void NoLegIsEverPlantedDeeperThanItCanStand()
    {
        // A channel dropping past the limit in the middle and coming back.
        var ground = Under(0f, 4f, 9f, 13f, 18f, 22f, 18f, 13f, 9f, 4f, 0f);
        var plan = Geometry.PlanPiers(ground, 2f, 0f);

        foreach (var pier in plan.Piers)
            Assert.True(-ground[pier] <= Geometry.MaxPierHeight,
                $"leg at sample {pier} would be {-ground[pier]} m tall");
    }

    /// <summary>
    /// The walk has to try every candidate rather than stopping at the first one it
    /// cannot reach. A shallow spot further out carries a longer stretch than a deep
    /// one nearer, so a walk that gave up at the first refusal would plant legs it did
    /// not need — or call an ordinary crossing impossible.
    /// </summary>
    [Fact]
    public void ADistantShallowSpotIsReachedPastNearerDeepOnes()
    {
        // 30 m across, so it cannot be spanned in one go. Twelve metres down a leg
        // only carries 4 m of deck; two metres down it carries 16. Samples 5 and 10
        // are the shallow ones, and both sit exactly 10 m from the previous support.
        // Getting to them means scanning past samples 3 and 4, which are near enough
        // to look promising and too deep to reach.
        var ground = Under(0f, 12f, 12f, 12f, 12f, 2f,
                           12f, 12f, 12f, 12f, 2f,
                           12f, 12f, 12f, 12f, 0f);
        var plan = Geometry.PlanPiers(ground, 2f, 0f);

        Assert.True(plan.Stands);
        Assert.Equal(new[] { 5, 10 }, plan.Piers);
    }

    [Fact]
    public void GroundStandingAboveTheDeckIsNotANegativeLeg()
    {
        // The deck cuts into a rise: that sample is resting on the ground, which is
        // the strongest support there is, not a leg of negative height.
        Assert.Equal(Geometry.MaxFreeSpan, Geometry.MaxPierSpacing(0f));

        var ground = Under(0f, 2f, -5f, 2f, 0f);
        var plan = Geometry.PlanPiers(ground, 2f, 0f);
        Assert.True(plan.Stands);
    }

    [Fact]
    public void ATrivialProfileIsNotAFailure()
    {
        Assert.True(Geometry.PlanPiers(new[] { 0f, 0f }, 2f, 0f).Stands);
        Assert.False(Geometry.PlanPiers(null, 2f, 0f).Stands);
    }
}
