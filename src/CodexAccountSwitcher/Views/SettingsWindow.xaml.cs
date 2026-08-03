using System.IO;
using System.Windows;
using System.Windows.Input;
using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsService _settingsService;
    private readonly StartupRegistrationService _startupRegistrationService;
    private readonly ThemeService _themeService;

    public SettingsWindow(
        AppSettingsService settingsService,
        StartupRegistrationService startupRegistrationService,
        ThemeService themeService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _startupRegistrationService = startupRegistrationService
            ?? throw new ArgumentNullException(nameof(startupRegistrationService));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var settings = await _settingsService.LoadAsync(CancellationToken.None);
        AutoStartCheckBox.IsChecked = _startupRegistrationService.IsEnabled();
        StartMinimizedCheckBox.IsChecked = settings.StartMinimizedToTray;
        ThemeComboBox.SelectedIndex = (int)settings.Theme;
        UpdateStartMinimizedAvailability();
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        IsEnabled = false;
        try
        {
            var settings = new AppSettings(
                (AppTheme)ThemeComboBox.SelectedIndex,
                StartMinimizedCheckBox.IsChecked == true);
            await _settingsService.SaveAsync(settings, CancellationToken.None);
            _startupRegistrationService.SetEnabled(
                AutoStartCheckBox.IsChecked == true,
                settings.StartMinimizedToTray);
            _themeService.Apply(settings.Theme);
            DialogResult = true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = $"保存失败：{exception.Message}";
            IsEnabled = true;
        }
    }

    private void AutoStartCheckBox_Changed(object sender, RoutedEventArgs e) =>
        UpdateStartMinimizedAvailability();

    private void UpdateStartMinimizedAvailability() =>
        StartMinimizedCheckBox.IsEnabled = AutoStartCheckBox.IsChecked == true;

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
