using System.Windows;
using System.Windows.Media;
using CodexAccountSwitcher.Models;
using Microsoft.Win32;
using MediaColor = System.Windows.Media.Color;

namespace CodexAccountSwitcher.Services;

public sealed class ThemeService
{
    private const string PersonalizeKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public void Apply(AppTheme theme)
    {
        var dark = theme == AppTheme.Dark ||
            (theme == AppTheme.System && IsSystemDark());
        var colors = dark ? DarkColors : LightColors;
        var resources = System.Windows.Application.Current.Resources;

        foreach (var (key, color) in colors)
        {
            resources[key] = new SolidColorBrush(color);
        }
    }

    private static bool IsSystemDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    private static readonly IReadOnlyDictionary<string, MediaColor> LightColors =
        CreateColors(
            "#F4F6F7", "#FBFCFC", "#D9DEE2", "#2D6678", "#EDF5F1", "#C9DED3",
            "#315C48", "#CF9D39", "#202629", "#667178", "#E9EEF0", "#E3E8EA");

    private static readonly IReadOnlyDictionary<string, MediaColor> DarkColors =
        CreateColors(
            "#181B1D", "#202427", "#384045", "#5A9CB2", "#21342C", "#3D6652",
            "#8FD3AF", "#E0B45B", "#F2F5F6", "#A9B3B8", "#30363A", "#3A4145");

    private static IReadOnlyDictionary<string, MediaColor> CreateColors(
        string window,
        string surface,
        string border,
        string primary,
        string activeBackground,
        string activeBorder,
        string activeText,
        string warning,
        string textPrimary,
        string textSecondary,
        string hover,
        string progressTrack) =>
        new Dictionary<string, MediaColor>(StringComparer.Ordinal)
        {
            ["WindowBackgroundBrush"] = Parse(window),
            ["SurfaceBrush"] = Parse(surface),
            ["BorderBrush"] = Parse(border),
            ["PrimaryBrush"] = Parse(primary),
            ["ActiveBackgroundBrush"] = Parse(activeBackground),
            ["ActiveBorderBrush"] = Parse(activeBorder),
            ["ActiveTextBrush"] = Parse(activeText),
            ["WarningBrush"] = Parse(warning),
            ["TextPrimaryBrush"] = Parse(textPrimary),
            ["TextSecondaryBrush"] = Parse(textSecondary),
            ["HoverBrush"] = Parse(hover),
            ["ProgressTrackBrush"] = Parse(progressTrack),
        };

    private static MediaColor Parse(string value) =>
        (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(value);
}
