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
            "#315C48", "#CF9D39", "#202629", "#667178", "#E9EEF0", "#E3E8EA",
            "#DCEAF7");

    private static readonly IReadOnlyDictionary<string, MediaColor> DarkColors =
        CreateColors(
            "#292A2D", "#202124", "#3C4043", "#5F8FCF", "#24352F", "#416352",
            "#81C995", "#FDD663", "#E8EAED", "#9AA0A6", "#303134", "#3C4043",
            "#394B63");

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
        string progressTrack,
        string selectionBackground) =>
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
            ["SelectionBackgroundBrush"] = Parse(selectionBackground),
        };

    private static MediaColor Parse(string value) =>
        (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(value);
}
