using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// The road tool draws a curve and then paints everything within a distance of it.
/// The distance is what makes the strip continuous — the first attempt stamped
/// circles along the line instead, and they overwrote one another into blotches.
/// </summary>
public class RoadTests
{
    private const float Tolerance = 0.01f;

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1f, 0.05f)]
    [InlineData(5f, 0.25f)]
    [InlineData(10f, 0.5f)]
    [InlineData(-10f, -0.5f)]
    public void CurveTenPutsTheBulgeAtHalfTheChord(float curve, float fractionOfChord)
    {
        // The scale the tool promises: 1 is a gentle bend, 10 is a semicircle.
        const float length = 100f;
        Assert.Equal(length * fractionOfChord, Geometry.Sagitta(curve, length), 3);
    }

    [Fact]
    public void CurveKeepsItsShapeWhateverTheLength()
    {
        // Scaling by length is what makes one number mean the same bend on a short
        // link and a long haul.
        foreach (var length in new[] { 10f, 50f, 200f })
            Assert.Equal(0.25f, Geometry.Sagitta(5f, length) / length, 4);
    }

    [Fact]
    public void TheCurveStartsAndEndsExactlyWhereAsked()
    {
        var from = new Vec2(10f, -20f);
        var to = new Vec2(60f, 15f);
        var path = Geometry.Bezier(from, to, 8f, 1f);

        Assert.Equal(from.X, path[0].X, 3);
        Assert.Equal(from.Z, path[0].Z, 3);
        Assert.Equal(to.X, path[^1].X, 3);
        Assert.Equal(to.Z, path[^1].Z, 3);
    }

    /// <summary>
    /// A quadratic Bezier only reaches half its control offset, so the control point
    /// is pushed out twice the bulge. Get that wrong and every curve is half as deep
    /// as the number says.
    /// </summary>
    [Fact]
    public void TheMiddleOfTheCurveBulgesByExactlyTheSagitta()
    {
        var from = new Vec2(0f, 0f);
        var to = new Vec2(100f, 0f);

        foreach (var sagitta in new[] { 5f, 25f, 50f })
        {
            var path = Geometry.Bezier(from, to, sagitta, 1f);
            var middle = path[path.Count / 2];

            Assert.Equal(50f, middle.X, 1);
            Assert.Equal(sagitta, Math.Abs(middle.Z), 1);
        }
    }

    [Fact]
    public void SamplesNeverStepFurtherThanAsked()
    {
        // Sampling coarser than the step is how a strong curve came out dotted.
        var path = Geometry.Bezier(new Vec2(0f, 0f), new Vec2(80f, 0f), 40f, 1f);

        for (var i = 1; i < path.Count; i++)
        {
            var dx = path[i].X - path[i - 1].X;
            var dz = path[i].Z - path[i - 1].Z;
            Assert.True(Math.Sqrt(dx * dx + dz * dz) <= 1.5f,
                $"gap of {Math.Sqrt(dx * dx + dz * dz):F2} m between samples {i - 1} and {i}");
        }
    }

    [Fact]
    public void DistanceToAStraightRunIsThePerpendicular()
    {
        var path = Geometry.Bezier(new Vec2(0f, 0f), new Vec2(100f, 0f), 0f, 1f);
        var last = path.Count - 1;

        Assert.Equal(0f, Geometry.DistanceToPath(path, 0, last, 50f, 0f), 2);
        Assert.Equal(3f, Geometry.DistanceToPath(path, 0, last, 50f, 3f), 2);
        Assert.Equal(3f, Geometry.DistanceToPath(path, 0, last, 50f, -3f), 2);
    }

    [Fact]
    public void PastTheEndTheDistanceIsToTheEndpoint()
    {
        var path = Geometry.Bezier(new Vec2(0f, 0f), new Vec2(100f, 0f), 0f, 1f);
        var last = path.Count - 1;

        // Clamping to the segment is what stops a road painting off past its end.
        Assert.Equal(10f, Geometry.DistanceToPath(path, 0, last, 110f, 0f), 1);
        Assert.Equal(10f, Geometry.DistanceToPath(path, 0, last, -10f, 0f), 1);
    }

    /// <summary>
    /// The property that decides whether a road has holes: everything within the
    /// half-width of the centreline is covered, at any sampling density, whatever
    /// the curve. Stamping circles could not promise this; measuring can.
    /// </summary>
    [Fact]
    public void EveryPointWithinTheHalfWidthIsCovered()
    {
        var path = Geometry.Bezier(new Vec2(0f, 0f), new Vec2(60f, 40f), 15f, 1f);
        var last = path.Count - 1;
        const float halfWidth = 1.5f;

        var checkedPoints = 0;
        for (var i = 0; i < path.Count; i++)
        {
            // Walk across the strip at each sample: on the line, and out to the rim.
            foreach (var offset in new[] { -1.4f, -0.7f, 0f, 0.7f, 1.4f })
            {
                var dirX = i == last ? path[i].X - path[i - 1].X : path[i + 1].X - path[i].X;
                var dirZ = i == last ? path[i].Z - path[i - 1].Z : path[i + 1].Z - path[i].Z;
                var len = (float)Math.Sqrt(dirX * dirX + dirZ * dirZ);
                if (len < 1e-4f) continue;

                var x = path[i].X + (-dirZ / len) * offset;
                var z = path[i].Z + (dirX / len) * offset;

                Assert.True(Geometry.DistanceToPath(path, 0, last, x, z) <= halfWidth + Tolerance,
                    $"a point {offset:F1} m off the centreline fell outside the strip");
                checkedPoints++;
            }
        }

        Assert.True(checkedPoints > 200, "the sweep should have covered the whole road");
    }
}
