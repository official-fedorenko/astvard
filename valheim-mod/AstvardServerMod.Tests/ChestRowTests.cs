using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// Ряд одинаковых деталей поперёк постройки — то, из чего «Поставить сундуки» кладёт
/// пару. Считать тут нечего ровно до того места, где ошибка становится видна: ряд,
/// сдвинутый на полшага, выглядит как ровный, пока не встанет рядом второй такой же.
/// </summary>
public class ChestRowTests
{
    [Fact]
    public void TwoChestsStandHalfTheirWidthFromTheMiddle()
    {
        var offsets = Geometry.RowOffsets(1f, 0f, 2);

        Assert.Equal(2, offsets.Length);
        Assert.Equal(-0.5f, offsets[0], 4);
        Assert.Equal(0.5f, offsets[1], 4);
    }

    [Fact]
    public void OneChestStandsInTheMiddle()
    {
        var offsets = Geometry.RowOffsets(1.2f, 0.3f, 1);

        Assert.Single(offsets);
        Assert.Equal(0f, offsets[0], 4);
    }

    [Fact]
    public void TheRowIsSymmetric()
    {
        // Иначе пара, привязанная к середине плиты, стояла бы на ней не по середине.
        foreach (var count in new[] { 2, 3, 4, 7 })
        {
            var offsets = Geometry.RowOffsets(0.9f, 0.1f, count);

            var sum = 0f;
            foreach (var offset in offsets) sum += offset;

            Assert.Equal(0f, sum, 3);
        }
    }

    [Fact]
    public void NeighboursStandOneWidthAndOneGapApart()
    {
        var offsets = Geometry.RowOffsets(1.5f, 0.25f, 4);

        for (var i = 1; i < offsets.Length; i++)
            Assert.Equal(1.75f, offsets[i] - offsets[i - 1], 4);
    }

    [Fact]
    public void TheGapsAreOneFewerThanThePieces()
    {
        // Три сундука по метру с щелью в четверть занимают 3.5 м, а не 3.75.
        Assert.Equal(3.5f, Geometry.RowSpan(1f, 0.25f, 3), 4);
        Assert.Equal(1f, Geometry.RowSpan(1f, 0.25f, 1), 4);
    }

    [Fact]
    public void ARowOfNothingTakesNoRoom()
    {
        Assert.Empty(Geometry.RowOffsets(1f, 0f, 0));
        Assert.Equal(0f, Geometry.RowSpan(1f, 0f, 0), 4);
        Assert.Empty(Geometry.RowOffsets(1f, 0f, -3));
    }

    [Fact]
    public void ARowOfTwoIsAsWideAsThePairItPlaces()
    {
        // Ширина ряда и края крайних деталей — одно и то же число, посчитанное дважды.
        var offsets = Geometry.RowOffsets(1.1f, 0.2f, 2);
        var edges = offsets[1] - offsets[0] + 1.1f;

        Assert.Equal(Geometry.RowSpan(1.1f, 0.2f, 2), edges, 4);
    }
}
