using System.Globalization;
using System.Windows.Data;

namespace ProjectDashboard.Helpers;

/// <summary>
/// True when the bound text holds something other than whitespace. Gates a commit button whose
/// action refuses a whitespace-only message, so the refusal is visible before the click.
/// </summary>
public class HasNonWhitespaceTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        !string.IsNullOrWhiteSpace(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
