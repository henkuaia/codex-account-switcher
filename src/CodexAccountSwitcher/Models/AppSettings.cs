namespace CodexAccountSwitcher.Models;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed record AppSettings(
    AppTheme Theme,
    bool StartMinimizedToTray)
{
    public static AppSettings Default { get; } = new(AppTheme.System, true);
}
