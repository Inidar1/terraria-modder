using System.Globalization;
using Avalonia.Data.Converters;

namespace TerrariaModManager.ViewModels;

public class BoolInverterConverter : IValueConverter
{
    public static readonly BoolInverterConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;
}

public class BoolToExpandIconConverter : IValueConverter
{
    public static readonly BoolToExpandIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? "▼" : "▶";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class CountToBoolConverter : IValueConverter
{
    public static readonly CountToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int count = value is int c ? c : 0;
        bool invert = parameter is string s && s == "invert";
        return invert ? count == 0 : count > 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
