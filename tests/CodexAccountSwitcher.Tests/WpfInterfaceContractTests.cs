namespace CodexAccountSwitcher.Tests;

public sealed class WpfInterfaceContractTests
{
    [Fact]
    public void Active_operation_exit_message_covers_every_account_operation()
    {
        Assert.Equal(
            "An account operation is still running. Wait for it to finish before exiting.",
            App.ActiveOperationExitMessage);
    }

    [Theory]
    [InlineData("5H")]
    [InlineData("five-hour")]
    [InlineData("Settings")]
    [InlineData("tray behavior")]
    [InlineData("LinearGradientBrush")]
    [InlineData("RadialGradientBrush")]
    public void Production_xaml_excludes_forbidden_content(string forbidden)
    {
        var xaml = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(
                    FindDirectory("src", "CodexAccountSwitcher"),
                    "*.xaml",
                    SearchOption.AllDirectories)
                .Select(File.ReadAllText));

        Assert.DoesNotContain(forbidden, xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Main_window_binds_quota_status_retry_launch_and_unofficial_endpoint_disclosure()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindDirectory("src", "CodexAccountSwitcher"),
            "MainWindow.xaml"));

        Assert.Contains("Text=\"{Binding QuotaStatusText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding QuotaToolTip}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding RetryLaunchCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding CanRetryLaunch}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("unofficial endpoint", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AutomationProperties.HelpText", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Main_window_exposes_compact_reset_quota_metadata_and_edit_command()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindDirectory("src", "CodexAccountSwitcher"),
            "MainWindow.xaml"));

        Assert.Contains("Text=\"{Binding AvailableResetText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding UsedResetText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding PeriodQuotaText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding EstimatedPeriodQuotaText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding OfficialMonthlyLimitText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding DataContext.EditMetadataCommand", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Main_window_uses_value_colored_cards_with_collapsed_details()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindDirectory("src", "CodexAccountSwitcher"),
            "MainWindow.xaml"));

        Assert.Contains("x:Name=\"QuotaDetailsExpander\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"详情\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsExpanded=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Converter={StaticResource QuotaRemainingBrushConverter}",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Add_flow_has_no_pre_confirmation_message_box()
    {
        var source = File.ReadAllText(Path.Combine(
            FindDirectory("src", "CodexAccountSwitcher"),
            "App.xaml.cs"));

        Assert.DoesNotContain("ConfirmAddAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBoxButton.OKCancel", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_startup_wires_local_quota_cache_without_automatic_refresh()
    {
        var source = File.ReadAllText(Path.Combine(
            FindDirectory("src", "CodexAccountSwitcher"),
            "App.xaml.cs"));

        Assert.Contains(
            "var quotaCacheService = QuotaCacheService.CreateDefault();",
            source,
            StringComparison.Ordinal);
        Assert.Contains("quotaCacheService);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshCommand.Execute", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Removal_dialog_is_compact_app_owned_single_selection_with_active_account_disabled()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindDirectory("src", "CodexAccountSwitcher"),
            "Views",
            "RemoveAccountWindow.xaml"));

        Assert.Contains("Width=\"400\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionMode=\"Single\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding IsActive}\" Value=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsEnabled\" Value=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Active - switch first", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Remove account\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("5H", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quota", xaml, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindDirectory(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(string.Join(Path.DirectorySeparatorChar, relativePath));
    }
}
