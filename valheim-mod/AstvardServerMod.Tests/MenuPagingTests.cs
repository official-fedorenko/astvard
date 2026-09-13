using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// The window a page of eight buttons shows over a longer list. Off by one here is a
/// template nobody can reach, or a button that opens the template beside the one it says.
/// </summary>
public class MenuPagingTests
{
    private const int Page = 8;

    [Theory]
    [InlineData(0, 5, 0)]    // everything fits: no window at all
    [InlineData(3, 8, 0)]    // exactly a page
    [InlineData(-4, 20, 0)]
    [InlineData(5, 20, 5)]
    [InlineData(12, 20, 12)] // the last full page starts here
    [InlineData(13, 20, 12)] // never past it
    [InlineData(99, 20, 12)]
    public void TheWindowStaysInsideTheList(int offset, int total, int expected)
    {
        Assert.Equal(expected, MenuPaging.Clamp(offset, total, Page));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 3, 3)]
    [InlineData(0, 20, 8)]
    [InlineData(12, 20, 8)]
    [InlineData(40, 20, 8)]  // clamped back to a full page, not a lone last item
    public void TheLastPageIsAlwaysFull(int offset, int total, int expected)
    {
        Assert.Equal(expected, MenuPaging.Shown(offset, total, Page));
    }

    [Fact]
    public void ButtonsMoveAPageAndTheWheelARow()
    {
        Assert.Equal(8, MenuPaging.Step(0, 23, Page, Page));
        Assert.Equal(15, MenuPaging.Step(8, 23, Page, Page));   // not 16: that would run past the end
        Assert.Equal(0, MenuPaging.Step(3, 23, Page, -Page));
        Assert.Equal(1, MenuPaging.Step(0, 23, Page, 1));
        Assert.Equal(15, MenuPaging.Step(15, 23, Page, 1));
        Assert.Equal(0, MenuPaging.Step(0, 5, Page, 1));       // a short list does not scroll
    }

    [Fact]
    public void UpAndDownSayWhetherThereIsAnythingThere()
    {
        Assert.False(MenuPaging.CanGoUp(0, 23, Page));
        Assert.True(MenuPaging.CanGoDown(0, 23, Page));
        Assert.True(MenuPaging.CanGoUp(15, 23, Page));
        Assert.False(MenuPaging.CanGoDown(15, 23, Page));
        Assert.False(MenuPaging.CanGoDown(0, 8, Page));
    }

    [Fact]
    public void TheHintNamesTheRowsOnScreen()
    {
        Assert.Equal("", MenuPaging.Window(0, 8, Page));
        Assert.Equal("Показаны с 1 по 8 из 23.", MenuPaging.Window(0, 23, Page));
        Assert.Equal("Показаны с 16 по 23 из 23.", MenuPaging.Window(99, 23, Page));
    }
}
