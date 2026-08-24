using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AudioStudio.Core.Analysis;
using AudioStudio.Core.Graph;

namespace AudioStudio.App;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool hasValue = value is not null && value is not string { Length: 0 };
        if (Invert) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && !b;
}

/// <summary>
/// Colour for a severity. Severity is also always shown as a word, never colour
/// alone, so the ranking survives for anyone who cannot separate the hues.
/// </summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    private static readonly Dictionary<FindingSeverity, Brush> Brushes = new()
    {
        [FindingSeverity.Critical] = Frozen(Color.FromRgb(0xFF, 0x55, 0x52)),
        [FindingSeverity.Severe] = Frozen(Color.FromRgb(0xFF, 0x8A, 0x3D)),
        [FindingSeverity.Moderate] = Frozen(Color.FromRgb(0xFF, 0xC8, 0x4A)),
        [FindingSeverity.Minor] = Frozen(Color.FromRgb(0x63, 0xB3, 0xFF)),
        [FindingSeverity.Informational] = Frozen(Color.FromRgb(0x9A, 0xA3, 0xB2)),
    };

    private static Brush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is FindingSeverity s && Brushes.TryGetValue(s, out var brush)
            ? brush
            : Brushes[FindingSeverity.Informational];

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Green through red, driven by the overall health score.</summary>
public sealed class HealthToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int score = value is int i ? i : 0;
        Color color = score switch
        {
            >= 90 => Color.FromRgb(0x4C, 0xD9, 0x86),
            >= 75 => Color.FromRgb(0x8F, 0xD1, 0x4F),
            >= 55 => Color.FromRgb(0xFF, 0xC8, 0x4A),
            >= 35 => Color.FromRgb(0xFF, 0x8A, 0x3D),
            _ => Color.FromRgb(0xFF, 0x55, 0x52)
        };
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Severity as a coloured dot, so rank is visible before any text is read.</summary>
public sealed class SeverityToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is FindingSeverity s && s >= FindingSeverity.Minor ? "●" : "○";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Colour for a treatment step's state — grey proposed, green verified, amber rolled back.</summary>
public sealed class StepStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        Color color = value is TreatmentStepState state
            ? state switch
            {
                TreatmentStepState.Verified => Color.FromRgb(0x4C, 0xD9, 0x86),
                TreatmentStepState.Applied => Color.FromRgb(0x63, 0xB3, 0xFF),
                TreatmentStepState.Rejected => Color.FromRgb(0xFF, 0xC8, 0x4A),
                TreatmentStepState.Skipped => Color.FromRgb(0x6E, 0x76, 0x84),
                _ => Color.FromRgb(0x8A, 0x93, 0xA3)
            }
            : Color.FromRgb(0x8A, 0x93, 0xA3);

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Tick, dot or cross for a treatment step.</summary>
public sealed class StepStateToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is TreatmentStepState state
            ? state switch
            {
                TreatmentStepState.Verified => "✓",
                TreatmentStepState.Applied => "●",
                TreatmentStepState.Rejected => "↺",
                TreatmentStepState.Skipped => "—",
                _ => "○"
            }
            : "○";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Green when a measurement moved the right way, amber when it did not.</summary>
public sealed class MetricToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        Color color = value is VerificationMetric m
            ? m.Improved ? Color.FromRgb(0x4C, 0xD9, 0x86)
            : m.Worsened ? Color.FromRgb(0xFF, 0xC8, 0x4A)
            : Color.FromRgb(0x9A, 0xA3, 0xB2)
            : Color.FromRgb(0x9A, 0xA3, 0xB2);

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Dims a bypassed node in the changes list.</summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && b ? 1.0 : 0.45;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
