using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.ViewModels;

public sealed class AccountRowViewModel : ObservableObject
{
    private AccountRecord _account;
    private bool _isActive;
    private bool _canSwitch;
    private string _displayIdentity;
    private bool _hasQuotaStatus;
    private string? _switchUnavailableReason;
    private QuotaDisplay? _quotaDisplay;
    private string _quotaLabel = "Not queried";
    private string? _quotaError;
    private string _quotaStatusText = string.Empty;
    private string _quotaToolTip = string.Empty;

    internal AccountRowViewModel(
        AccountRecord account,
        bool isActive,
        bool canSwitch,
        string? switchUnavailableReason)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _displayIdentity = ResolveDisplayIdentity(account);
        _isActive = isActive;
        _canSwitch = canSwitch;
        _switchUnavailableReason = switchUnavailableReason;
    }

    public AccountRecord Account => _account;

    public string DisplayIdentity => _displayIdentity;

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

    public string QuotaStatusText
    {
        get => _quotaStatusText;
        private set => SetProperty(ref _quotaStatusText, value);
    }

    public string QuotaToolTip
    {
        get => _quotaToolTip;
        private set => SetProperty(ref _quotaToolTip, value);
    }

    public bool HasQuotaStatus
    {
        get => _hasQuotaStatus;
        private set => SetProperty(ref _hasQuotaStatus, value);
    }

    internal void ApplyAccountState(
        AccountRecord account,
        bool isActive,
        bool canSwitch,
        string? switchUnavailableReason)
    {
        ArgumentNullException.ThrowIfNull(account);
        SetProperty(ref _account, account, nameof(Account));
        SetProperty(ref _displayIdentity, ResolveDisplayIdentity(account), nameof(DisplayIdentity));
        IsActive = isActive;
        CanSwitch = canSwitch;
        SwitchUnavailableReason = switchUnavailableReason;
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
        QuotaStatusText = update.Error ?? FormatReset(update.Display?.ResetsAt);
        QuotaToolTip = update.Error ?? update.Display?.Tooltip ?? string.Empty;
        HasQuotaStatus = !string.IsNullOrEmpty(QuotaStatusText);
    }

    private static string ResolveDisplayIdentity(AccountRecord account) =>
        !string.IsNullOrWhiteSpace(account.Alias)
            ? account.Alias
            : account.Email;

    private static string FormatReset(DateTimeOffset? resetsAt) => resetsAt is { } value
        ? $"Resets {value.UtcDateTime:yyyy-MM-dd HH:mm 'UTC'}"
        : string.Empty;
}
