using System.Globalization;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.ViewModels;

public sealed class AccountRowViewModel : ObservableObject
{
    private AccountRecord _account;
    private bool _isActive;
    private bool _canSwitch;
    private string _displayIdentity;
    private bool _hasQuotaStatus;
    private bool _hasOfficialMonthlyLimit;
    private bool _hasEstimatedPeriodQuotaText;
    private string? _switchUnavailableReason;
    private QuotaDisplay? _quotaDisplay;
    private AccountMetadata _metadata = new(null, 0);
    private string _availableResetText = "可用重置 —";
    private string _usedResetText = "已用重置 0（本机）";
    private string _periodQuotaText = "单次额度 —";
    private string _officialMonthlyLimitText = string.Empty;
    private string _estimatedPeriodQuotaText = string.Empty;
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

    public AccountMetadata Metadata => _metadata;

    public string AvailableResetText
    {
        get => _availableResetText;
        private set => SetProperty(ref _availableResetText, value);
    }

    public string UsedResetText
    {
        get => _usedResetText;
        private set => SetProperty(ref _usedResetText, value);
    }

    public string PeriodQuotaText
    {
        get => _periodQuotaText;
        private set => SetProperty(ref _periodQuotaText, value);
    }

    public string OfficialMonthlyLimitText
    {
        get => _officialMonthlyLimitText;
        private set => SetProperty(ref _officialMonthlyLimitText, value);
    }

    public bool HasOfficialMonthlyLimit
    {
        get => _hasOfficialMonthlyLimit;
        private set => SetProperty(ref _hasOfficialMonthlyLimit, value);
    }

    public string EstimatedPeriodQuotaText
    {
        get => _estimatedPeriodQuotaText;
        private set => SetProperty(ref _estimatedPeriodQuotaText, value);
    }

    public bool HasEstimatedPeriodQuotaText
    {
        get => _hasEstimatedPeriodQuotaText;
        private set => SetProperty(ref _hasEstimatedPeriodQuotaText, value);
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
        UpdateMetadataDisplay();
    }

    internal void ApplyMetadata(AccountMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.PeriodQuotaUsd is < 0 || metadata.UsedResetCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(metadata));
        }

        SetProperty(ref _metadata, metadata, nameof(Metadata));
        UpdateMetadataDisplay();
    }

    private static string ResolveDisplayIdentity(AccountRecord account) =>
        !string.IsNullOrWhiteSpace(account.Alias)
            ? account.Alias
            : account.Email;

    private static string FormatReset(DateTimeOffset? resetsAt) => resetsAt is { } value
        ? $"Resets {value.UtcDateTime:yyyy-MM-dd HH:mm 'UTC'}"
        : string.Empty;

    private void UpdateMetadataDisplay()
    {
        AvailableResetText = QuotaDisplay?.AvailableResetCount is { } available
            ? $"可用重置 {available}"
            : "可用重置 —";
        UsedResetText = $"已用重置 {Metadata.UsedResetCount}（本机）";

        var period = QuotaDisplay?.Period switch
        {
            QuotaPeriod.Weekly => "周",
            QuotaPeriod.Monthly => "月",
            _ => string.Empty,
        };
        PeriodQuotaText = Metadata.PeriodQuotaUsd is { } quota
            ? $"单次{period}额度 US${FormatUsd(quota)}"
            : $"单次{period}额度 —";

        HasOfficialMonthlyLimit = QuotaDisplay?.IndividualLimitUsd is not null;
        OfficialMonthlyLimitText = QuotaDisplay?.IndividualLimitUsd is { } limit
            ? $"官方月度上限 US${FormatUsd(limit)}"
            : string.Empty;

        HasEstimatedPeriodQuotaText =
            QuotaDisplay?.Period is QuotaPeriod.Weekly or QuotaPeriod.Monthly;
        EstimatedPeriodQuotaText = QuotaDisplay switch
        {
            { Period: QuotaPeriod.Weekly, EstimatedPeriodQuotaLowerUsd: { } lower,
                EstimatedPeriodQuotaUpperUsd: { } upper } when lower == upper =>
                $"估算单次周额度 US${FormatUsd(lower)}",
            { Period: QuotaPeriod.Weekly, EstimatedPeriodQuotaLowerUsd: { } lower,
                EstimatedPeriodQuotaUpperUsd: { } upper } =>
                $"估算单次周额度 US${FormatUsd(lower)}–{FormatUsd(upper)}",
            { Period: QuotaPeriod.Weekly, UsedPercent: <= 0 } =>
                "估算单次周额度：产生用量后可计算",
            { Period: QuotaPeriod.Weekly } =>
                "估算单次周额度：暂不可用",
            { Period: QuotaPeriod.Monthly, EstimatedPeriodQuotaLowerUsd: { } lower,
                EstimatedPeriodQuotaUpperUsd: { } upper } when lower == upper =>
                $"估算单次月额度 US${FormatUsd(lower)}",
            { Period: QuotaPeriod.Monthly, EstimatedPeriodQuotaLowerUsd: { } lower,
                EstimatedPeriodQuotaUpperUsd: { } upper } =>
                $"估算单次月额度 US${FormatUsd(lower)}–{FormatUsd(upper)}",
            { Period: QuotaPeriod.Monthly, UsedPercent: <= 0 } =>
                "估算单次月额度：产生用量后可计算",
            { Period: QuotaPeriod.Monthly } =>
                "估算单次月额度：暂不可用",
            _ => string.Empty,
        };
    }

    private static string FormatUsd(decimal value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
