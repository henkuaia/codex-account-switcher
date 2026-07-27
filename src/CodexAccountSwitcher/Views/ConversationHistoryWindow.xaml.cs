using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Views;

public partial class ConversationHistoryWindow : Window
{
    private readonly ICodexConversationService _service;
    private IReadOnlyList<CodexConversation> _conversations = [];

    public ConversationHistoryWindow(ICodexConversationService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) =>
        await ReloadAsync();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await ReloadAsync();

    private async Task ReloadAsync()
    {
        LoadingPanel.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        HistoryItems.Visibility = Visibility.Collapsed;
        StatusText.Text = "正在读取 Codex 本机历史…";
        try
        {
            _conversations = await _service.LoadAsync(CancellationToken.None);
            ApplyFilter();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"读取失败：{exception.Message}";
            EmptyText.Text = "无法读取历史对话，请确认 Codex 已正确安装";
            EmptyText.Visibility = Visibility.Visible;
        }
        finally
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var matches = string.IsNullOrEmpty(query)
            ? _conversations
            : _conversations
                .Where(conversation =>
                    conversation.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    conversation.Preview.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    conversation.WorkingDirectory.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    conversation.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        HistoryItems.ItemsSource = matches;
        HistoryItems.Visibility = matches.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        EmptyText.Text = "没有找到匹配的历史对话";
        EmptyText.Visibility = matches.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        StatusText.Text = $"共读取 {_conversations.Count} 个历史对话，当前显示 {matches.Count} 个";
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var conversation =
            (CodexConversation)((System.Windows.Controls.Button)sender).Tag;
        _service.Open(conversation);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
