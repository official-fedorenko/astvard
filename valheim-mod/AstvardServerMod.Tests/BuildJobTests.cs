using System.Collections.Generic;
using AstvardServerMod;

namespace AstvardServerMod.Tests;

/// <summary>
/// Поручения с сайта: переименовать постройку, сменить категорию, убрать.
///
/// Файлы построек пишет только мод, у сайта нет прав на его папку, — поэтому просьба
/// едет поручением с номером, а номер возвращается, когда сделано. Разбор этой строки
/// и правка заголовка в файле проверяются здесь: ошибиться в заголовке значит стереть
/// координаты деталей, а это чья-то база.
/// </summary>
public class BuildJobTests
{
    private const string Tab = "\t";

    private static SiteSync.Pulled Pull(params string[] lines)
    {
        return SiteSync.ParsePull(string.Join("\n", lines) + "\n");
    }

    [Fact]
    public void AJobIsReadWithItsNumber()
    {
        var pulled = Pull("revision" + Tab + "7",
                          "job" + Tab + "3" + Tab + "rename" + Tab + "Плавильня" + Tab + "Плавильня у моря");

        var job = Assert.Single(pulled.Jobs);
        Assert.Equal(3, job.Id);
        Assert.Equal("rename", job.Kind);
        Assert.Equal("Плавильня", job.Name);
        Assert.Equal("Плавильня у моря", job.Value);
    }

    [Fact]
    public void AJobWithoutAValueIsStillAJob()
    {
        // «Убрать» ничего не несёт, и пустое поле в конце строки при разборе пропадает.
        var pulled = Pull("revision" + Tab + "1", "job" + Tab + "9" + Tab + "delete" + Tab + "Сарай");

        var job = Assert.Single(pulled.Jobs);
        Assert.Equal("delete", job.Kind);
        Assert.Equal("", job.Value);
    }

    [Theory]
    [InlineData("job\t0\tdelete\tСарай")]      // номера ноль не бывает
    [InlineData("job\tнет\tdelete\tСарай")]
    [InlineData("job\t3\t\tСарай")]
    [InlineData("job\t3\tdelete\t")]
    [InlineData("job\t3")]
    public void ALineThatDoesNotReadIsDropped(string line)
    {
        // И только она: остальные строки ответа должны дойти. Одно поручение, которого
        // эта сборка не понимает, не имеет права остановить весь обмен.
        var pulled = Pull("revision" + Tab + "1", line.Replace("\\t", Tab), "rule" + Tab + "sort" + Tab + "1");

        Assert.True(pulled.Valid);
        Assert.Empty(pulled.Jobs);
        Assert.Single(pulled.Rules);
    }

    [Fact]
    public void DoneCarriesOnlyTheNumber()
    {
        Assert.Equal("done" + Tab + "12", SiteSync.DoneLine(12));
    }

    [Fact]
    public void AHeaderIsReplacedWhereItStands()
    {
        var file = new List<string>
        {
            "# astvard shared template",
            "#name Плавильня",
            "#category Производство",
            "#players yes",
            "wood_floor,0,0,0,0,0,0,1",
        };

        var updated = SiteSync.WithHeader(file, "name", "Плавильня у моря");

        Assert.Equal("#name Плавильня у моря", updated[1]);
        Assert.Equal("#category Производство", updated[2]);
        Assert.Equal(file.Count, updated.Count);
        Assert.Equal("wood_floor,0,0,0,0,0,0,1", updated[updated.Count - 1]);
    }

    [Fact]
    public void AHeaderThatIsNotThereIsWrittenIn()
    {
        var file = new List<string> { "# astvard shared template", "#name Сарай", "wood_floor,0,0,0" };

        var updated = SiteSync.WithHeader(file, "category", "Разное");

        Assert.Equal("#category Разное", updated[1]);
        Assert.Equal(4, updated.Count);
        Assert.Equal("wood_floor,0,0,0", updated[updated.Count - 1]);
    }

    [Fact]
    public void ALongerHeaderIsNotTheSameHeader()
    {
        // «#name» и «#nameplate» отличаются одним пробелом, и спутать их значит написать
        // имя постройки в чужую строку.
        var file = new List<string> { "# astvard", "#nameplate нет", "#name Сарай", "wood_floor,0" };

        var updated = SiteSync.WithHeader(file, "name", "Овин");

        Assert.Equal("#nameplate нет", updated[1]);
        Assert.Equal("#name Овин", updated[2]);
    }

    [Fact]
    public void TheBodyIsNeverTouched()
    {
        // Заголовки кончаются на первой не-# строке. Дальше координаты деталей, и строка
        // «#name» среди них — это чьё-то имя детали, а не заголовок.
        var file = new List<string> { "# astvard", "#name Сарай", "wood_floor,0", "#name чужое" };

        var updated = SiteSync.WithHeader(file, "name", "Овин");

        Assert.Equal("#name Овин", updated[1]);
        Assert.Equal("#name чужое", updated[3]);
    }

    [Fact]
    public void AnEmptyFileHasNoHeadersToChange()
    {
        Assert.Null(SiteSync.WithHeader(new List<string>(), "name", "Овин"));
        Assert.Null(SiteSync.WithHeader(null, "name", "Овин"));
    }
}
