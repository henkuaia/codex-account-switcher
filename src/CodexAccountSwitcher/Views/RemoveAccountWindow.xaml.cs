using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CodexAccountSwitcher.ViewModels;

namespace CodexAccountSwitcher.Views;

public partial class RemoveAccountWindow : Window
{
    public RemoveAccountWindow(IReadOnlyList<AccountRowViewModel> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        InitializeComponent();
        AccountList.ItemsSource = accounts;
    }

    public AccountRowViewModel? SelectedTarget { get; private set; }

    private void AccountList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ConfirmButton.IsEnabled = AccountList.SelectedItem is AccountRowViewModel { IsActive: false };

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (AccountList.SelectedItem is not AccountRowViewModel { IsActive: false } target)
        {
            return;
        }

        SelectedTarget = target;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
