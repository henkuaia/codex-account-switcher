using System.Globalization;
using System.Windows;
using System.Windows.Input;
using CodexAccountSwitcher.ViewModels;

namespace CodexAccountSwitcher.Views;

public partial class SwitchConfirmationWindow : Window
{
    public SwitchConfirmationWindow(AccountRowViewModel target)
    {
        ArgumentNullException.ThrowIfNull(target);
        InitializeComponent();

        TargetIdentityText.Text = target.DisplayIdentity;
        TargetEmailText.Text = target.Account.Email;
        QuotaText.Text = FormatQuota(target);
    }

    private static string FormatQuota(AccountRowViewModel target)
    {
        if (target.QuotaDisplay is not { } quota)
        {
            return target.QuotaLabel;
        }

        var reset = quota.ResetsAt is null
            ? string.Empty
            : string.Create(
                CultureInfo.CurrentCulture,
                $" - resets {quota.ResetsAt.Value.LocalDateTime:g}");
        return $"{target.QuotaLabel}: {quota.RemainingPercent}% remaining{reset}";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
