using System.Globalization;
using System.Windows.Data;

namespace ProjectDashboard.Helpers;

public class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        DateTimeOffset date;

        if (value is DateTimeOffset dto)
            date = dto;
        else
            return "";

        if (date == default)
            return "";

        var elapsed = DateTimeOffset.Now - date;

        if (elapsed.TotalMinutes < 1)
            return "just now";
        if (elapsed.TotalHours < 1)
            return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalHours < 24)
            return $"{(int)elapsed.TotalHours}h ago";
        if (elapsed.TotalDays < 7)
            return $"{(int)elapsed.TotalDays}d ago";
        if (elapsed.TotalDays < 30)
            return $"{(int)(elapsed.TotalDays / 7)}w ago";

        return date.ToString("MMM d", CultureInfo.InvariantCulture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
