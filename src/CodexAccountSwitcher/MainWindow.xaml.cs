using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using CodexAccountSwitcher.ViewModels;

namespace CodexAccountSwitcher;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private bool _allowClose;

    public MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
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

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
