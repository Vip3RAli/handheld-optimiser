using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;
using HandheldOptimiser.ViewModels;

namespace HandheldOptimiser.Views;

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && !b;
}

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && v == Visibility.Visible;
}

/// <summary>A section's heading is written in capitals, which a TextBlock cannot do by itself.</summary>
public sealed class UpperCaseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value as string)?.ToUpper(culture) ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        !string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class RiskToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is RiskLevel risk
            ? new SolidColorBrush(risk switch
            {
                RiskLevel.Safe => Color.FromRgb(0x6C, 0xC0, 0x7A),
                RiskLevel.Moderate => Color.FromRgb(0xE8, 0xC4, 0x5F),
                RiskLevel.SecurityTradeoff => Color.FromRgb(0xE8, 0x8E, 0x4D),
                RiskLevel.Breaking => Color.FromRgb(0xE0, 0x6C, 0x6C),
                _ => Colors.Gray
            })
            : Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class HealthStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is HealthStatus status
            ? new SolidColorBrush(status switch
            {
                HealthStatus.Good => Color.FromRgb(0x6C, 0xC0, 0x7A),
                HealthStatus.Warning => Color.FromRgb(0xE8, 0xC4, 0x5F),
                HealthStatus.Bad => Color.FromRgb(0xE0, 0x6C, 0x6C),
                HealthStatus.Info => Color.FromRgb(0x7F, 0xB0, 0xE0),
                _ => Color.FromRgb(0x90, 0x90, 0x90)
            })
            : Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class HealthStatusToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is HealthStatus status
            ? status switch
            {
                HealthStatus.Good => "",
                HealthStatus.Warning => "",
                HealthStatus.Bad => "",
                HealthStatus.Info => "",
                _ => ""
            }
            : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class LogSeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is LogSeverity severity
            ? new SolidColorBrush(severity switch
            {
                LogSeverity.Command => Color.FromRgb(0x7C, 0xB8, 0xE8),
                LogSeverity.Success => Color.FromRgb(0x6C, 0xC0, 0x7A),
                LogSeverity.Warning => Color.FromRgb(0xE8, 0xC4, 0x5F),
                LogSeverity.Error => Color.FromRgb(0xE0, 0x6C, 0x6C),
                LogSeverity.Trace => Color.FromRgb(0x88, 0x88, 0x88),
                _ => Color.FromRgb(0xD0, 0xD0, 0xD0)
            })
            : Brushes.LightGray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class RuntimeStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is RuntimeStatus status
            ? new SolidColorBrush(status switch
            {
                RuntimeStatus.UpToDate => Color.FromRgb(0x6C, 0xC0, 0x7A),
                RuntimeStatus.UpdateAvailable => Color.FromRgb(0xE8, 0xC4, 0x5F),
                RuntimeStatus.Error => Color.FromRgb(0xE0, 0x6C, 0x6C),
                _ => Color.FromRgb(0x90, 0x90, 0x90)
            })
            : Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class TweakStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is TweakState state
            ? new SolidColorBrush(state switch
            {
                TweakState.Applied => Color.FromRgb(0x6C, 0xC0, 0x7A),
                TweakState.Partial => Color.FromRgb(0xE8, 0xC4, 0x5F),
                TweakState.Error => Color.FromRgb(0xE0, 0x6C, 0x6C),
                _ => Color.FromRgb(0x90, 0x90, 0x90)
            })
            : Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class UpdateRowStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ViewModels.UpdateRowState state
            ? new SolidColorBrush(state switch
            {
                ViewModels.UpdateRowState.Done => Color.FromRgb(0x6C, 0xC0, 0x7A),
                ViewModels.UpdateRowState.Available => Color.FromRgb(0xE8, 0xC4, 0x5F),
                ViewModels.UpdateRowState.Working => Color.FromRgb(0x22, 0xD3, 0xEE),
                ViewModels.UpdateRowState.Failed => Color.FromRgb(0xE0, 0x6C, 0x6C),
                _ => Color.FromRgb(0x90, 0x90, 0x90)
            })
            : Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>The dot on a Dashboard card: green, yellow or red for how the goal stands, cyan for a plain fact.</summary>
public sealed class CardLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is CardLevel level
            ? new SolidColorBrush(level switch
            {
                CardLevel.Good => Color.FromRgb(0x6C, 0xC0, 0x7A),
                CardLevel.Attention => Color.FromRgb(0xE8, 0xC4, 0x5F),
                CardLevel.Bad => Color.FromRgb(0xE0, 0x6C, 0x6C),
                CardLevel.Neutral => Color.FromRgb(0x22, 0xD3, 0xEE),
                _ => Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)
            })
            : Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
