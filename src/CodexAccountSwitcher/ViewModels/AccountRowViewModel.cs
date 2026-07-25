using System.Globalization;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.ViewModels;

public sealed class AccountRowViewModel : ObservableObject
{
    private AccountRecord _account;
    private bool _isActive;
    private bool _canSwitch;
    private bool _isRefreshing;
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
    private string _estimatedPeriodQuotaSummaryText = string.Empty;
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

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetProperty(ref _isRefreshing, value);
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

    public string EstimatedPeriodQuotaSummaryText
    {
        get => _estimatedPeriodQuotaSummaryText;
        private set => SetProperty(ref _estimatedPeriodQuotaSummaryText, value);
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

    internal void SetRefreshing(bool value) => IsRefreshing = value;

    internal void ApplyQuota(QuotaUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!string.Equals(update.AccountKey, Account.AccountKey, StringComparison.Ordinal))
        {
            throw new ArgumentException("The quota update belongs to another account.", nameof(update));
        }

        if (update.Error is not null &&
            update.Display is null &&
            QuotaDisplay is not null)
        {
            QuotaError = update.Error;
            QuotaStatusText = PrefixOnce(QuotaStatusText, update.Error, " · ");
            QuotaToolTip = PrefixOnce(QuotaToolTip, update.Error, "; ");
            HasQuotaStatus = true;
            return;
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
        QuotaToolTip = update.Error ??
            AppendTooltip(update.Display?.Tooltip, update.Display?.EstimateStatus);
        HasQuotaStatus = !string.IsNullOrEmpty(QuotaStatusText);
        UpdateMetadataDisplay();
    }

    internal void ApplyCachedQuota(QuotaCacheEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ApplyQuota(new QuotaUpdate(Account.AccountKey, entry.Display, null));

        var refreshed = $"上次刷新 {entry.RefreshedAt.UtcDateTime:yyyy-MM-dd HH:mm 'UTC'}";
        var expired = entry.Display.ResetsAt is { } resetsAt && resetsAt <= now;
        QuotaStatusText = expired
            ? $"缓存已过期，需要刷新 · {refreshed}"
            : string.IsNullOrEmpty(QuotaStatusText)
                ? refreshed
                : $"{QuotaStatusText} · {refreshed}";
        QuotaToolTip = string.IsNullOrEmpty(QuotaToolTip)
            ? QuotaStatusText
            : $"{QuotaToolTip}; {QuotaStatusText}";
        HasQuotaStatus = true;
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
        EstimatedPeriodQuotaText = FormatEstimatedPeriodQuota(QuotaDisplay);
        EstimatedPeriodQuotaSummaryText =
            EstimatedPeriodQuotaText.Split(Environment.NewLine)[0];
    }

    private static string FormatEstimatedPeriodQuota(QuotaDisplay? display)
    {
        if (display?.Period is not (QuotaPeriod.Weekly or QuotaPeriod.Monthly))
        {
            return string.Empty;
        }

        var period = display.Period == QuotaPeriod.Weekly ? "周" : "月";
        string text;
        if (display.EstimatedPeriodQuotaLowerUsd is { } lower &&
            display.EstimatedPeriodQuotaUpperUsd is { } upper)
        {
            var quality = display.EstimateQuality switch
            {
                QuotaEstimateQuality.Initial => "初步",
                QuotaEstimateQuality.MultiPoint => "多点",
                _ => string.Empty,
            };
            var source = display.EstimateSource switch
            {
                QuotaEstimateSource.Analytics => "服务器 Analytics",
                QuotaEstimateSource.Local => "本机用量",
                _ => string.Empty,
            };
            var context = string.Join(
                " · ",
                new[] { quality, source }.Where(value => !string.IsNullOrEmpty(value)));
            var contextSuffix = string.IsNullOrEmpty(context)
                ? string.Empty
                : $"（{context}）";
            var range = lower == upper
                ? FormatUsd(lower)
                : $"{FormatUsd(lower)}–{FormatUsd(upper)}";
            text =
                $"单次{period}额度（估算）：US${range}{contextSuffix}{Environment.NewLine}" +
                "按 Credits 购买价格换算，非官方套餐额度";
        }
        else if (display.UsedPercent <= 0)
        {
            text = $"单次{period}额度（估算）：产生用量后可计算";
        }
        else
        {
            text = "额度估算：采集中，还需使用后刷新";
        }

        return AppendDetailLine(text, display.EstimateStatus);
    }

    private static string AppendDetailLine(string text, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return text;
        }

        return string.IsNullOrEmpty(text)
            ? detail
            : $"{text}{Environment.NewLine}{detail}";
    }

    private static string AppendTooltip(string? tooltip, string? estimateStatus)
    {
        var text = tooltip ?? string.Empty;
        if (string.IsNullOrWhiteSpace(estimateStatus))
        {
            return text;
        }

        return string.IsNullOrEmpty(text)
            ? estimateStatus
            : $"{text}; {estimateStatus}";
    }

    private static string PrefixOnce(
        string? existing,
        string prefix,
        string separator) =>
        string.IsNullOrWhiteSpace(existing)
            ? prefix
            : string.Equals(existing, prefix, StringComparison.Ordinal) ||
              existing.StartsWith($"{prefix}{separator}", StringComparison.Ordinal)
                ? existing
                : $"{prefix}{separator}{existing}";

    private static string FormatUsd(decimal value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
