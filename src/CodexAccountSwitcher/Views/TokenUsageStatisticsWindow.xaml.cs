using System.Globalization;
using System.Windows;
using System.Windows.Input;
using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Views;

public partial class TokenUsageStatisticsWindow : Window
{
    private readonly TokenUsageStatisticsService _service;
    private readonly TimeZoneInfo _timeZone;
    private TokenUsageSnapshot? _snapshot;

    public TokenUsageStatisticsWindow(
        TokenUsageStatisticsService service,
        TimeZoneInfo? timeZone = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _timeZone = timeZone ?? TimeZoneInfo.Local;
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var today = LocalToday();
        SetDateRange(today.AddDays(-6), today);
        await RefreshAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync();

    private async Task RefreshAsync()
    {
        LoadingPanel.Visibility = Visibility.Visible;
        IsEnabled = false;
        try
        {
            _snapshot = await _service.RefreshAsync(CancellationToken.None);
            ApplySelectedRange();
            StatusText.Text = _snapshot.IsComplete
                ? $"更新于 {FormatLocalTime(_snapshot.RefreshedAt)}，已统计本机历史记录"
                : $"更新于 {FormatLocalTime(_snapshot.RefreshedAt)}，部分会话暂时无法读取";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"读取失败：{exception.Message}";
        }
        finally
        {
            IsEnabled = true;
            LoadingPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void TodayButton_Click(object sender, RoutedEventArgs e)
    {
        var today = LocalToday();
        SetDateRange(today, today);
        ApplySelectedRange();
    }

    private void LastSevenDaysButton_Click(object sender, RoutedEventArgs e)
    {
        var today = LocalToday();
        SetDateRange(today.AddDays(-6), today);
        ApplySelectedRange();
    }

    private void ThisMonthButton_Click(object sender, RoutedEventArgs e)
    {
        var today = LocalToday();
        SetDateRange(new DateOnly(today.Year, today.Month, 1), today);
        ApplySelectedRange();
    }

    private void QueryButton_Click(object sender, RoutedEventArgs e) =>
        ApplySelectedRange();

    private void ApplySelectedRange()
    {
        if (_snapshot is null ||
            StartDatePicker.SelectedDate is not { } start ||
            EndDatePicker.SelectedDate is not { } end)
        {
            return;
        }

        var startDate = DateOnly.FromDateTime(start);
        var endDate = DateOnly.FromDateTime(end);
        if (startDate > endDate)
        {
            StatusText.Text = "开始日期不能晚于结束日期。";
            return;
        }

        var summary = TokenUsageAggregator.Aggregate(
            _snapshot.Buckets,
            startDate,
            endDate,
            _timeZone);
        RangeText.Text = $"{summary.RangeText} 每日明细";
        InputTokenText.Text = summary.InputTokens.ToString("N0", CultureInfo.CurrentCulture);
        CachedInputTokenText.Text = summary.CachedInputTokens.ToString("N0", CultureInfo.CurrentCulture);
        OutputTokenText.Text = summary.OutputTokens.ToString("N0", CultureInfo.CurrentCulture);
        TotalTokenText.Text = summary.TotalTokens.ToString("N0", CultureInfo.CurrentCulture);
        DailyItems.ItemsSource = summary.Days.Reverse().ToArray();
    }

    private void SetDateRange(DateOnly startDate, DateOnly endDate)
    {
        StartDatePicker.SelectedDate = startDate.ToDateTime(TimeOnly.MinValue);
        EndDatePicker.SelectedDate = endDate.ToDateTime(TimeOnly.MinValue);
    }

    private DateOnly LocalToday() => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _timeZone).Date);

    private string FormatLocalTime(DateTimeOffset timestamp) =>
        TimeZoneInfo.ConvertTime(timestamp, _timeZone).ToString("M.d HH:mm");

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
