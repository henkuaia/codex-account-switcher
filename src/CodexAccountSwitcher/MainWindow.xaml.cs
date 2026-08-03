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
    private readonly TokenUsageStatisticsService? _tokenUsageService;
    private readonly AppSettingsService? _settingsService;
    private readonly StartupRegistrationService? _startupRegistrationService;
    private readonly ThemeService? _themeService;
    private bool _allowClose;

    public MainWindow(
        MainWindowViewModel viewModel,
        ICodexConversationService conversationService,
        TokenUsageStatisticsService? tokenUsageService = null,
        AppSettingsService? settingsService = null,
        StartupRegistrationService? startupRegistrationService = null,
        ThemeService? themeService = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _conversationService = conversationService
            ?? throw new ArgumentNullException(nameof(conversationService));
        _tokenUsageService = tokenUsageService;
        _settingsService = settingsService;
        _startupRegistrationService = startupRegistrationService;
        _themeService = themeService;
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

    public Task ReloadAsync(CancellationToken cancellationToken = default) =>
        _viewModel.LoadAsync(cancellationToken);

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

    private void TokenUsageButton_Click(object sender, RoutedEventArgs e)
    {
        var service = _tokenUsageService
            ?? throw new InvalidOperationException("Token usage service is not configured.");
        new TokenUsageStatisticsWindow(service)
        {
            Owner = this,
        }.ShowDialog();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsService is null ||
            _startupRegistrationService is null ||
            _themeService is null)
        {
            throw new InvalidOperationException("Settings services are not configured.");
        }

        new SettingsWindow(
            _settingsService,
            _startupRegistrationService,
            _themeService)
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
