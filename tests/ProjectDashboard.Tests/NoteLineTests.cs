using System.Windows.Media;
using ProjectDashboard.Models;
using Wpf.Ui.Controls;

namespace ProjectDashboard.Tests;

public class NoteLineTests
{
    [Theory]
    [InlineData("TASK: ship the build", "TASK", "ship the build")]
    [InlineData("BUG: crash on empty repo", "BUG", "crash on empty repo")]
    [InlineData("WAIT: upstream release", "WAIT", "upstream release")]
    [InlineData("PLAN: split the service", "PLAN", "split the service")]
    [InlineData("INFO: docs moved", "INFO", "docs moved")]
    public void KnownPrefixes_SplitPrefixAndText(string line, string prefix, string text)
    {
        var note = NoteLine.Parse(line);

        Assert.Equal(prefix, note.Prefix);
        Assert.Equal(text, note.Text);
    }

    [Theory]
    [InlineData("task: lower case")]
    [InlineData("Task: mixed case")]
    [InlineData("TASK:no space after colon")]
    [InlineData("   TASK: leading whitespace")]
    public void PrefixMatching_IsCaseInsensitiveAndWhitespaceTolerant(string line)
    {
        Assert.Equal("TASK", NoteLine.Parse(line).Prefix);
    }

    [Fact]
    public void UnprefixedLine_RendersAsInfoWithEmptyPrefix()
    {
        var note = NoteLine.Parse("  just a plain note");

        Assert.Equal("", note.Prefix);
        Assert.Equal("just a plain note", note.Text);
        Assert.Equal(SymbolRegular.Info24, note.Icon);
        Assert.Equal(Color.FromRgb(0x88, 0x88, 0x88), note.IconBrush.Color);
    }

    [Fact]
    public void EachPrefix_GetsItsOwnIconAndColor()
    {
        Assert.Equal(SymbolRegular.CheckboxUnchecked24, NoteLine.Parse("TASK: x").Icon);
        Assert.Equal(Color.FromRgb(0x5B, 0x9B, 0xD5), NoteLine.Parse("TASK: x").IconBrush.Color);

        Assert.Equal(SymbolRegular.Bug24, NoteLine.Parse("BUG: x").Icon);
        Assert.Equal(Color.FromRgb(0xE0, 0x52, 0x52), NoteLine.Parse("BUG: x").IconBrush.Color);

        Assert.Equal(SymbolRegular.Clock24, NoteLine.Parse("WAIT: x").Icon);
        Assert.Equal(Color.FromRgb(0xE8, 0xA3, 0x17), NoteLine.Parse("WAIT: x").IconBrush.Color);

        Assert.Equal(SymbolRegular.LightbulbCircle24, NoteLine.Parse("PLAN: x").Icon);
        Assert.Equal(Color.FromRgb(0x9B, 0x59, 0xB6), NoteLine.Parse("PLAN: x").IconBrush.Color);

        Assert.Equal(SymbolRegular.Info24, NoteLine.Parse("INFO: x").Icon);
        Assert.Equal(Color.FromRgb(0x88, 0x88, 0x88), NoteLine.Parse("INFO: x").IconBrush.Color);
    }

    [Fact]
    public void PrefixWithoutColon_IsNotAPrefix()
    {
        var note = NoteLine.Parse("TASK without a colon");

        Assert.Equal("", note.Prefix);
        Assert.Equal("TASK without a colon", note.Text);
    }
}
