using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CloudOrc.WorkspaceUI.Ui;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>"ok" | "warn" | "bad" | other -&gt; strong brush.</summary>
public sealed class ToneToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Application.Current.TryFindResource((value as string) switch
        {
            "ok" => "Ok",
            "warn" => "Warn",
            "bad" => "Bad",
            _ => "Muted"
        }) as Brush ?? Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ToneToSoftBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Application.Current.TryFindResource((value as string) switch
        {
            "ok" => "OkSoft",
            "warn" => "WarnSoft",
            "bad" => "BadSoft",
            _ => "SurfaceBg"
        }) as Brush ?? Brushes.WhiteSmoke;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Capability state (ON / ALLOWED / READY / TEXT_ONLY / BLOCKED) -&gt; tone.</summary>
public sealed class CapabilityToneConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value as string) switch
        {
            "ON" or "ALLOWED" or "READY" => "ok",
            "TEXT_ONLY" => "warn",
            "BLOCKED" => "bad",
            _ => "neutral"
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
