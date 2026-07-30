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
    public void Main_window_exposes_standard_minimize_button()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindDirectory("src", "CodexAccountSwitcher"),
            "MainWindow.xaml"));

        Assert.Contains("x:Name=\"WindowMinimizeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"MinimizeButton_Click\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Main_window_exposes_read_only_conversation_recovery()
    {
        var sourceDirectory = FindDirectory("src", "CodexAccountSwitcher");
        var mainWindow = File.ReadAllText(Path.Combine(sourceDirectory, "MainWindow.xaml"));
        var historyWindow = File.ReadAllText(Path.Combine(
            sourceDirectory,
            "Views",
            "ConversationHistoryWindow.xaml"));

        Assert.Contains("x:Name=\"ConversationHistoryButton\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Click=\"ConversationHistoryButton_Click\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Text=\"历史对话恢复\"", historyWindow, StringComparison.Ordinal);
        Assert.Contains("Content=\"在 Codex 打开\"", historyWindow, StringComparison.Ordinal);
        Assert.Contains("只读查看本机 Codex 记录", historyWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void Main_window_exposes_token_usage_statistics_with_date_ranges()
    {
        var sourceDirectory = FindDirectory("src", "CodexAccountSwitcher");
        var mainWindow = File.ReadAllText(Path.Combine(sourceDirectory, "MainWindow.xaml"));
        var usageWindow = File.ReadAllText(Path.Combine(
            sourceDirectory,
            "Views",
            "TokenUsageStatisticsWindow.xaml"));

        Assert.Contains("x:Name=\"TokenUsageButton\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Click=\"TokenUsageButton_Click\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Content=\"今日\"", usageWindow, StringComparison.Ordinal);
        Assert.Contains("Content=\"最近7天\"", usageWindow, StringComparison.Ordinal);
        Assert.Contains("Content=\"本月\"", usageWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StartDatePicker\"", usageWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EndDatePicker\"", usageWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DailyItems\"", usageWindow, StringComparison.Ordinal);
        Assert.Contains("输入（百万 Token）", usageWindow, StringComparison.Ordinal);
        Assert.Contains("Binding InputMillions, StringFormat=N3", usageWindow, StringComparison.Ordinal);
        Assert.Contains("Binding TotalMillions, StringFormat=N3", usageWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void Main_window_exposes_compact_reset_quota_metadata_and_edit_command()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindDirectory("src", "CodexAccountSwitcher"),
            "MainWindow.xaml"));

        Assert.Contains("Text=\"{Binding AvailableResetText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding UsedResetText}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Text=\"{Binding PeriodQuotaText}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Text=\"{Binding EstimatedPeriodQuotaText}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding PeriodQuotaSummaryText}\"",
            xaml,
            StringComparison.Ordinal);
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
    public void Main_window_uses_fixed_two_column_account_cards()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindDirectory("src", "CodexAccountSwitcher"),
            "MainWindow.xaml"));

        Assert.Contains("Width=\"780\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"780\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"780\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"620\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"620\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=\"620\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<WrapPanel", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemWidth=\"355\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "HorizontalScrollBarVisibility=\"Disabled\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Main_window_binds_bulk_and_account_refresh_animations()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindDirectory("src", "CodexAccountSwitcher"),
            "MainWindow.xaml"));

        Assert.Contains("Binding=\"{Binding IsBulkRefreshing}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding IsRefreshing}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"正在刷新额度…\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Command=\"{Binding DataContext.RefreshAccountCommand",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("RepeatBehavior=\"Forever\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Storyboard.TargetProperty=\"(UIElement.RenderTransform).(RotateTransform.Angle)\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"BulkRefreshStoryboard\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"AccountRefreshStoryboard\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"QuotaSweepStoryboard\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"LoadingRefreshRing\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"LoadingRingStoryboard\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("<DataTrigger.ExitActions>", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "<RemoveStoryboard BeginStoryboardName=\"BulkRefreshStoryboard\" />",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<RemoveStoryboard BeginStoryboardName=\"AccountRefreshStoryboard\" />",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<RemoveStoryboard BeginStoryboardName=\"QuotaSweepStoryboard\" />",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<RemoveStoryboard BeginStoryboardName=\"LoadingRingStoryboard\" />",
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
    public void Add_account_operation_window_uses_compact_animated_state_layout()
    {
        var viewDirectory = Path.Combine(
            FindDirectory("src", "CodexAccountSwitcher"),
            "Views");
        var xaml = File.ReadAllText(Path.Combine(viewDirectory, "OperationWindow.xaml"));
        var source = File.ReadAllText(Path.Combine(viewDirectory, "OperationWindow.xaml.cs"));

        Assert.Contains("Width=\"420\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"230\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LoadingSpinner\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StateIcon\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StateTitleText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StateSubtitleText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailsExpander\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"OutputTextBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CloseButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HeaderCloseButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<DoubleAnimation", xaml, StringComparison.Ordinal);
        Assert.Contains("RepeatBehavior=\"Forever\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(1000)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_startup_wires_hybrid_estimator_and_local_usage_roots()
    {
        var source = File.ReadAllText(Path.Combine(
            FindDirectory("src", "CodexAccountSwitcher"),
            "App.xaml.cs"));

        Assert.Contains(
            "var ledgerService = QuotaEstimateLedgerService.CreateDefault();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Path.Combine(codexHome, \"sessions\")",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "archivedSessionRoot: Path.Combine(codexHome, \"archived_sessions\")",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "var hybridEstimator = new HybridQuotaEstimateService(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "var individualLimitReader =",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "individualLimitReader: individualLimitReader);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "var quotaCacheService = QuotaCacheService.CreateDefault();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "hybridEstimator.ObserveRegistryAsync);",
            source,
            StringComparison.Ordinal);
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
