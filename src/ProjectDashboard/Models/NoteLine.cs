using System.Windows.Media;
using Wpf.Ui.Controls;

namespace ProjectDashboard.Models;

public class NoteLine
{
    public string Prefix { get; set; } = "";
    public string Text { get; set; } = "";
    public SymbolRegular Icon { get; set; } = SymbolRegular.Info24;
    public SolidColorBrush IconBrush { get; set; } = new(Color.FromRgb(0x88, 0x88, 0x88));

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    public static NoteLine Parse(string line)
    {
        var trimmed = line.TrimStart();

        if (trimmed.StartsWith("TASK:", StringComparison.OrdinalIgnoreCase))
            return new NoteLine { Prefix = "TASK", Text = trimmed[5..].TrimStart(), Icon = SymbolRegular.CheckboxUnchecked24, IconBrush = Brush(0x5B, 0x9B, 0xD5) };

        if (trimmed.StartsWith("BUG:", StringComparison.OrdinalIgnoreCase))
            return new NoteLine { Prefix = "BUG", Text = trimmed[4..].TrimStart(), Icon = SymbolRegular.Bug24, IconBrush = Brush(0xE0, 0x52, 0x52) };

        if (trimmed.StartsWith("WAIT:", StringComparison.OrdinalIgnoreCase))
            return new NoteLine { Prefix = "WAIT", Text = trimmed[5..].TrimStart(), Icon = SymbolRegular.Clock24, IconBrush = Brush(0xE8, 0xA3, 0x17) };

        if (trimmed.StartsWith("PLAN:", StringComparison.OrdinalIgnoreCase))
            return new NoteLine { Prefix = "PLAN", Text = trimmed[5..].TrimStart(), Icon = SymbolRegular.LightbulbCircle24, IconBrush = Brush(0x9B, 0x59, 0xB6) };

        if (trimmed.StartsWith("INFO:", StringComparison.OrdinalIgnoreCase))
            return new NoteLine { Prefix = "INFO", Text = trimmed[5..].TrimStart(), Icon = SymbolRegular.Info24, IconBrush = Brush(0x88, 0x88, 0x88) };

        // Unprefixed lines render as INFO
        return new NoteLine { Prefix = "", Text = trimmed, Icon = SymbolRegular.Info24, IconBrush = Brush(0x88, 0x88, 0x88) };
    }
}
