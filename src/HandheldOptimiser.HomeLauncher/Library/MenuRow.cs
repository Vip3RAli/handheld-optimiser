using System.Windows;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// What a menu row shows in the Old School layout's settings panel and nowhere else: an icon before its
/// name, and a line or two about it beside the list while it is in focus, as the Xbox 360's own settings
/// panel had. The grid and the row keep their one line rows with no description, which the user asked
/// for on 2026-10-12.
/// </summary>
internal static class MenuRow
{
    /// <summary>A Segoe Fluent Icons glyph drawn before the row's name.</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(string), typeof(MenuRow), new PropertyMetadata(null));

    /// <summary>What the row does, shown beside the list while the row is in focus.</summary>
    public static readonly DependencyProperty AboutProperty = DependencyProperty.RegisterAttached(
        "About", typeof(string), typeof(MenuRow), new PropertyMetadata(null));

    public static string? GetIcon(DependencyObject row) => (string?)row.GetValue(IconProperty);

    public static void SetIcon(DependencyObject row, string? value) => row.SetValue(IconProperty, value);

    public static string? GetAbout(DependencyObject row) => (string?)row.GetValue(AboutProperty);

    public static void SetAbout(DependencyObject row, string? value) => row.SetValue(AboutProperty, value);
}
