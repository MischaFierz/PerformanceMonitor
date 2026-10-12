using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PerformanceMonitor.Core;

namespace PerformanceMonitor.Converters;

public static class Palette
{
    public static readonly SolidColorBrush Good = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)));
    public static readonly SolidColorBrush Warn = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
    public static readonly SolidColorBrush Crit = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));
    public static readonly SolidColorBrush Info = Freeze(new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)));
    public static readonly SolidColorBrush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public static SolidColorBrush ForSeverity(Severity s) => s switch
    {
        Severity.Critical => Crit,
        Severity.Warning => Warn,
        Severity.Info => Info,
        _ => Good,
    };

    public static SolidColorBrush ForScore(int score) =>
        score >= 85 ? Good : score >= 65 ? Accent : score >= 40 ? Warn : Crit;

    public static SolidColorBrush Soft(SolidColorBrush b, byte alpha = 0x26)
    {
        var s = new SolidColorBrush(Color.FromArgb(alpha, b.Color.R, b.Color.G, b.Color.B));
        s.Freeze();
        return s;
    }
}

public sealed class SeverityBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var brush = Palette.ForSeverity(value is Severity s ? s : Severity.Ok);
        return parameter as string == "soft" ? Palette.Soft(brush) : brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class SeverityGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Severity.Critical => "",
        Severity.Warning => "",
        Severity.Info => "",
        _ => "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class VerdictBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var brush = value switch
        {
            BgVerdict.Unnecessary => Palette.Warn,
            BgVerdict.Optional => Palette.Info,
            _ => Palette.Good,
        };
        return parameter as string == "soft" ? Palette.Soft(brush) : brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ScoreBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Palette.ForScore(value is int i ? i : 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Hintergrundfarbe wie im Task-Manager: je höher der Wert, desto kräftiger.</summary>
public sealed class HeatConverter : IValueConverter
{
    private static readonly SolidColorBrush[] Steps = Enumerable.Range(0, 11).Select(i =>
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(i * 14), 0xF5, 0x9E, 0x0B));
        b.Freeze();
        return b;
    }).ToArray();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double v = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        double max = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 100;
        int step = (int)Math.Round(Math.Clamp(v / max, 0, 1) * 10);
        return Steps[step];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
