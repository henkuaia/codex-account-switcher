using System.IO;
using Microsoft.Win32;

namespace CodexAccountSwitcher.Services;

public sealed class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexAccountSwitcher";
    private readonly string _executablePath;

    public StartupRegistrationService(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = Path.GetFullPath(executablePath);
    }

    public static StartupRegistrationService CreateDefault() => new(
        Environment.ProcessPath
            ?? throw new InvalidOperationException("The application executable path is unavailable."));

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public void SetEnabled(bool enabled, bool startMinimized)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            key.SetValue(
                ValueName,
                BuildCommand(_executablePath, startMinimized),
                RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    internal static string BuildCommand(string executablePath, bool startMinimized) =>
        $"\"{Path.GetFullPath(executablePath)}\"" + (startMinimized ? " --minimized" : string.Empty);
}
