using System.Globalization;
using System.Windows;
using System.Windows.Input;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Views;

public partial class EditAccountMetadataWindow : Window
{
    public EditAccountMetadataWindow(AccountMetadata current)
    {
        ArgumentNullException.ThrowIfNull(current);
        InitializeComponent();
        PeriodQuotaTextBox.Text = current.PeriodQuotaUsd?.ToString(
            "0.##",
            CultureInfo.InvariantCulture) ?? string.Empty;
        UsedResetCountTextBox.Text = current.UsedResetCount.ToString(
            CultureInfo.InvariantCulture);
    }

    public AccountMetadata? Result { get; private set; }

    internal static bool TryParseMetadata(
        string quotaText,
        string resetText,
        out AccountMetadata? metadata,
        out string error)
    {
        quotaText = (quotaText ?? string.Empty).Trim();
        if (quotaText.StartsWith("US$", StringComparison.OrdinalIgnoreCase))
        {
            quotaText = quotaText[3..].Trim();
        }
        else if (quotaText.StartsWith('$'))
        {
            quotaText = quotaText[1..].Trim();
        }

        decimal? quota = null;
        if (quotaText.Length > 0)
        {
            if (!decimal.TryParse(
                    quotaText,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var parsedQuota) ||
                parsedQuota < 0 ||
                decimal.Round(parsedQuota, 2) != parsedQuota)
            {
                metadata = null;
                error = "额度必须是非负美元金额，最多保留两位小数。";
                return false;
            }

            quota = parsedQuota;
        }

        if (!int.TryParse(
                (resetText ?? string.Empty).Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var usedResets) ||
            usedResets < 0)
        {
            metadata = null;
            error = "累计已用重置次数必须是非负整数。";
            return false;
        }

        metadata = new AccountMetadata(quota, usedResets);
        error = string.Empty;
        return true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseMetadata(
                PeriodQuotaTextBox.Text,
                UsedResetCountTextBox.Text,
                out var metadata,
                out var error))
        {
            ValidationText.Text = error;
            return;
        }

        Result = metadata;
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
