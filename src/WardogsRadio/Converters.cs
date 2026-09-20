using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WardogsRadio;

/// <summary>0..1 level to a star GridLength, so a two-column grid becomes a level meter.</summary>
public sealed class LevelToStarConverter : IValueConverter
{
    public bool Inverse { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double v = value is float f ? f : value is double d ? d : 0;
        v = Math.Clamp(v, 0, 1);
        if (Inverse) v = 1 - v;
        return new GridLength(Math.Max(v, 0.0001), GridUnitType.Star);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Inverse { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool x && x;
        if (Inverse) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Inverse { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool has = value is string s ? !string.IsNullOrEmpty(s) : value != null;
        if (Inverse) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class EnumEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null && parameter != null && value.ToString() == parameter.ToString() ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d ? Math.Round(d) + "%" : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
