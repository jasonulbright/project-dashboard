using System.Text.RegularExpressions;
using System.Windows.Input;
using ProjectDashboard.Models;
using ProjectDashboard.Views.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The cheat sheet renders this table and nothing else, so a gesture the app registers
/// but the table omits is invisible. The gestures live in code-behind key handlers and
/// per-control InputBindings rather than one registry, so there is nothing to enumerate
/// at runtime: the coverage list below is asserted explicitly, and the Ctrl+digit range
/// is cross-checked against the routing function the detail page actually calls.
/// </summary>
public class ShortcutTableTests
{
    [Fact]
    public void TableIsNotEmpty()
        => Assert.NotEmpty(ShortcutTable.All);

    [Theory]
    [InlineData("Ctrl+K")]
    [InlineData("?")]
    [InlineData("Esc")]
    [InlineData("Alt+Left")]
    [InlineData("Backspace")]
    [InlineData("Ctrl+Enter")]
    [InlineData("Ctrl+0")]
    public void EveryRegisteredGesture_HasARow(string gesture)
        => Assert.Contains(ShortcutTable.All, e => e.Gesture == gesture);

    [Fact]
    public void CtrlDigitRange_CoversEveryTabTheRouterAccepts()
    {
        var row = ShortcutTable.All.Single(e => e.Gesture.StartsWith("Ctrl+1", StringComparison.Ordinal));
        Assert.Contains("Ctrl+9", row.Gesture);

        // The router maps D1..D9 then D0; the table must not advertise a tenth digit
        // the routing function would reject.
        for (var key = Key.D1; key <= Key.D9; key++)
            Assert.NotNull(ProjectDetailTabs.TabIndexForDigit(key, 10));
        Assert.Equal(9, ProjectDetailTabs.TabIndexForDigit(Key.D0, 10));
    }

    [Fact]
    public void ArrowAndDirectionalGestures_AreDocumented()
    {
        Assert.Contains(ShortcutTable.All, e => e.Gesture.Contains("Up") && e.Group == ShortcutTable.GlobalGroup);
        Assert.Contains(ShortcutTable.All, e => e.Gesture.Contains("Arrow") && e.Group == ShortcutTable.DashboardGroup);
        Assert.Contains(ShortcutTable.All, e => e.Gesture.Contains("Up") && e.Group == ShortcutTable.PaletteGroup);
    }

    [Fact]
    public void CardQuickActions_AreReachableFromTheKeyboard_AndSaidSo()
        => Assert.Contains(ShortcutTable.All,
            e => e.Gesture == "Tab" && Regex.IsMatch(e.Description, "Fetch|Pull|Push"));

    [Fact]
    public void NoGestureIsListedTwiceInOneGroup()
    {
        var duplicates = ShortcutTable.All
            .GroupBy(e => (e.Group, e.Gesture))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Group}/{g.Key.Gesture}")
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void EveryRowIsRenderable()
    {
        foreach (var entry in ShortcutTable.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Group));
            Assert.False(string.IsNullOrWhiteSpace(entry.Gesture));
            Assert.False(string.IsNullOrWhiteSpace(entry.Description));
        }
    }

    [Fact]
    public void GroupsCoverEveryRow_InDeclarationOrder()
    {
        Assert.Equal(ShortcutTable.All.Count, ShortcutTable.Groups.Sum(g => g.Count()));
        Assert.Equal(ShortcutTable.GlobalGroup, ShortcutTable.Groups[0].Key);
    }
}
