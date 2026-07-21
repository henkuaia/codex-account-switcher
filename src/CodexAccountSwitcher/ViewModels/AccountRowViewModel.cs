using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.ViewModels;

public sealed class AccountRowViewModel : ObservableObject
{
    private bool _isActive;
    private bool _canSwitch;
    private string? _switchUnavailableReason;
    private QuotaDisplay? _quotaDisplay;
    private string _quotaLabel = "Not queried";
    private string? _quotaError;

    internal AccountRowViewModel(
        AccountRecord account,
        bool isActive,
        bool canSwitch,
        string? switchUnavailableReason)
    {
        Account = account ?? throw new ArgumentNullException(nameof(account));
        _isActive = isActive;
        _canSwitch = canSwitch;
        _switchUnavailableReason = switchUnavailableReason;
    }

    public AccountRecord Account { get; }

    public string DisplayIdentity => !string.IsNullOrWhiteSpace(Account.Alias)
        ? Account.Alias
        : !string.IsNullOrWhiteSpace(Account.AccountName)
            ? Account.AccountName
            : Account.Email;

    public bool IsActive
    {
        get => _isActive;
        private set => SetProperty(ref _isActive, value);
    }

    public bool CanSwitch
    {
        get => _canSwitch;
        private set => SetProperty(ref _canSwitch, value);
    }

    public string? SwitchUnavailableReason
    {
        get => _switchUnavailableReason;
        private set => SetProperty(ref _switchUnavailableReason, value);
    }

    public QuotaDisplay? QuotaDisplay
    {
        get => _quotaDisplay;
        private set => SetProperty(ref _quotaDisplay, value);
    }

    public string QuotaLabel
    {
        get => _quotaLabel;
        private set => SetProperty(ref _quotaLabel, value);
    }

    public string? QuotaError
    {
        get => _quotaError;
        private set => SetProperty(ref _quotaError, value);
    }

    internal void ApplyQuota(QuotaUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!string.Equals(update.AccountKey, Account.AccountKey, StringComparison.Ordinal))
        {
            throw new ArgumentException("The quota update belongs to another account.", nameof(update));
        }

        QuotaDisplay = update.Display;
        QuotaError = update.Error;
        QuotaLabel = update.Error is not null || update.Display is null
            ? "Unavailable"
            : update.Display.Period switch
            {
                QuotaPeriod.Weekly => "Weekly",
                QuotaPeriod.Monthly => "Monthly",
                _ => "Quota",
            };
    }

}
