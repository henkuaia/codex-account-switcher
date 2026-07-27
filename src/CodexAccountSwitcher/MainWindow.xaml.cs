using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using CodexAccountSwitcher.Services;
using CodexAccountSwitcher.ViewModels;
using CodexAccountSwitcher.Views;

namespace CodexAccountSwitcher;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly ICodexConversationService _conversationService;
    private bool _allowClose;

    public MainWindow(
        MainWindowViewModel viewModel,
        ICodexConversationService conversationService)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _conversationService = conversationService
            ?? throw new ArgumentNullException(nameof(conversationService));
        InitializeComponent();
        DataContext = _viewModel;
    }

    public async Task ShowAndReloadAsync(CancellationToken cancellationToken = default)
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        await _viewModel.LoadAsync(cancellationToken);
    }

    public void AllowClose() => _allowClose = true;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void ConversationHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        new ConversationHistoryWindow(_conversationService)
        {
            Owner = this,
        }.ShowDialog();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
