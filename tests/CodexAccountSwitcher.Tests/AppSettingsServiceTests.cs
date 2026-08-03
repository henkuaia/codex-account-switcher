using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class AppSettingsServiceTests
{
    [Fact]
    public async Task Missing_file_loads_default_settings()
    {
        using var directory = new TemporaryDirectory();
        var service = new AppSettingsService(Path.Combine(directory.Path, "settings.json"));

        var settings = await service.LoadAsync(default);

        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.True(settings.StartMinimizedToTray);
    }

    [Fact]
    public async Task Settings_round_trip_without_temp_residue()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var service = new AppSettingsService(path);
        var expected = new AppSettings(AppTheme.Dark, false);

        await service.SaveAsync(expected, default);
        var loaded = await service.LoadAsync(default);

        Assert.Equal(expected, loaded);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void Startup_command_includes_minimized_argument_only_when_requested()
    {
        const string executable = @"C:\Program Files\CodexAccountSwitcher\CodexAccountSwitcher.exe";

        Assert.Equal(
            $"\"{executable}\" --minimized",
            StartupRegistrationService.BuildCommand(executable, startMinimized: true));
        Assert.Equal(
            $"\"{executable}\"",
            StartupRegistrationService.BuildCommand(executable, startMinimized: false));
    }
}
