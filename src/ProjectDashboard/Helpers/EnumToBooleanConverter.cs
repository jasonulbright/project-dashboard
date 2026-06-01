using System.Globalization;
using System.Windows.Data;

namespace ProjectDashboard.Helpers;

public class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
            return false;

        var enumValue = value.ToString();
        var targetValue = parameter.ToString();

        return string.Equals(enumValue, targetValue, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null)
            return Binding.DoNothing;

        var targetValue = parameter.ToString();

        if (targetValue is not null && Enum.TryParse(targetType, targetValue, true, out var result))
            return result;

        return Binding.DoNothing;
    }
}
