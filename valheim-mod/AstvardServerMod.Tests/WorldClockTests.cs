using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// Часы мира: перевод прожитой доли суток в час циферблата и обратно.
///
/// Valheim hours are ours, not the game's: it keeps one fraction of the day and nothing
/// else. What is the game's - and what makes this arithmetic worth testing - is that the
/// fraction is bent. Seventy per cent of a day is spent on the lit half of the dial and
/// thirty on the dark one, so a daytime hour lasts two and a third night hours. Every
/// «через сколько» in the panel goes through the inverse of that bend, and an inverse
/// that looks right on its own is the easiest thing in this file to get wrong.
/// </summary>
public class WorldClockTests
{
    [Theory]
    [InlineData(0f, 0f)]          // полночь
    [InlineData(0.15f, 0.25f)]    // подъём — 06:00
    [InlineData(0.5f, 0.5f)]      // полдень
    [InlineData(0.85f, 0.75f)]    // закат — 18:00
    public void TheClockBendsWhereTheGameBendsIt(float elapsed, float clock)
    {
        Assert.Equal(clock, Geometry.ClockFromElapsed(elapsed), 4);
        Assert.Equal(elapsed, Geometry.ElapsedFromClock(clock), 4);
    }

    [Fact]
    public void TheClockAndTheWorldAgreeBothWays()
    {
        // Ровно до единицы, не включая её: целые сутки — это те же нулевые, и обратный
        // перевод честно отвечает полночью, а не концом вчерашнего дня.
        for (var i = 0; i < 100; i++)
        {
            var elapsed = i / 100f;
            Assert.Equal(elapsed, Geometry.ElapsedFromClock(Geometry.ClockFromElapsed(elapsed)), 4);
        }

        Assert.Equal(0f, Geometry.ClockFromElapsed(1f), 4);
        Assert.Equal(0f, Geometry.ClockFromElapsed(-1f), 4);
    }

    /// <summary>
    /// Светлая половина циферблата занимает 70% суток, тёмная — 30%. Это и есть вся
    /// причина, по которой «через три часа» нельзя считать в часах.
    /// </summary>
    [Fact]
    public void DaylightTakesSevenTenthsOfTheDay()
    {
        var dawn = Geometry.ElapsedFromClock(0.25f);
        var dusk = Geometry.ElapsedFromClock(0.75f);

        Assert.Equal(0.7f, dusk - dawn, 4);

        var dayHour = Geometry.ElapsedFromClock(11f / 24f) - Geometry.ElapsedFromClock(10f / 24f);
        var nightHour = Geometry.ElapsedFromClock(23f / 24f) - Geometry.ElapsedFromClock(22f / 24f);

        Assert.Equal(7f / 3f, dayHour / nightHour, 3);
    }

    [Fact]
    public void WaitingForAnHourAlreadyGoneMeansWaitingForTomorrow()
    {
        // До полуночи из полудня — полсуток, а не минус полсуток.
        Assert.Equal(0.5f, Geometry.ElapsedUntil(0.5f, 0f), 4);
        Assert.Equal(0.25f, Geometry.ElapsedUntil(0.75f, 0f), 4);
        Assert.Equal(0f, Geometry.ElapsedUntil(0.3f, 0.3f), 4);
    }
}
