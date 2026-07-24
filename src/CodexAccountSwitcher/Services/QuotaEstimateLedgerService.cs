using System.IO;
using System.Text.Json;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public sealed record QuotaEstimateLedgerLoadResult(
    QuotaEstimateLedgerState State,
    string? Error);

public sealed class QuotaEstimateLedgerService
{
    private const int CurrentSchemaVersion = 1;
    private const string InvalidFileError = "本地额度估算账本无效，原文件已保留。";
    private const string UnsupportedVersionError = "本地额度估算账本版本不受支持，原文件已保留。";
    private const string ReadError = "本地额度估算账本暂时无法读取，原文件已保留。";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private bool _saveBlocked;

    public QuotaEstimateLedgerService(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public static QuotaEstimateLedgerService CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new QuotaEstimateLedgerService(Path.Combine(
            localAppData,
            "CodexAccountSwitcher",
            "quota-estimate-ledger.json"));
    }

    public async Task<QuotaEstimateLedgerLoadResult> LoadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            _saveBlocked = false;
            return Empty();
        }

        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<LedgerDocument>(
                stream,
                JsonOptions,
                cancellationToken);
            if (document?.Accounts is null)
            {
                return Blocked(InvalidFileError);
            }

            if (document.SchemaVersion != CurrentSchemaVersion)
            {
                return Blocked(UnsupportedVersionError);
            }

            var accounts = new Dictionary<string, AccountQuotaEstimateLedger>(
                StringComparer.Ordinal);
            foreach (var (accountKey, ledger) in document.Accounts)
            {
                if (ledger is null || !accounts.TryAdd(accountKey, Copy(ledger)))
                {
                    return Blocked(InvalidFileError);
                }
            }

            var state = new QuotaEstimateLedgerState(accounts);
            if (!IsValid(state))
            {
                return Blocked(InvalidFileError);
            }

            _saveBlocked = false;
            return new QuotaEstimateLedgerLoadResult(state, null);
        }
        catch (JsonException)
        {
            return Blocked(InvalidFileError);
        }
        catch (IOException)
        {
            return Blocked(ReadError);
        }
        catch (UnauthorizedAccessException)
        {
            return Blocked(ReadError);
        }
    }

    public async Task SaveAsync(
        QuotaEstimateLedgerState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_saveBlocked)
        {
            throw new InvalidOperationException(
                "The existing quota estimate ledger cannot be overwritten.");
        }

        if (state.Accounts is null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        if (state.Accounts.Keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Account keys cannot be empty.", nameof(state));
        }

        if (!IsValid(state))
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                "Quota estimate ledger values are invalid.");
        }

        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $"{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var document = new LedgerDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                Accounts = state.Accounts.ToDictionary(
                    pair => pair.Key,
                    pair => (AccountQuotaEstimateLedger?)pair.Value,
                    StringComparer.Ordinal),
            };
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static QuotaEstimateLedgerState ObserveRegistry(
        QuotaEstimateLedgerState state,
        AccountRegistry registry,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(registry);
        if (!IsUtcTimestamp(observedAt))
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedAt),
                "Observation time must be a valid UTC timestamp.");
        }

        if (!IsValid(state))
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                "Quota estimate ledger values are invalid.");
        }

        if (registry.ActiveAccountKey is null)
        {
            return state;
        }

        if (string.IsNullOrWhiteSpace(registry.ActiveAccountKey) ||
            registry.Accounts.Count(account => string.Equals(
                account.AccountKey,
                registry.ActiveAccountKey,
                StringComparison.Ordinal)) != 1)
        {
            throw new ArgumentException(
                "The registry active account key is invalid.",
                nameof(registry));
        }

        var openActivation = state.Accounts
            .SelectMany(pair => pair.Value.Activations.Select(
                (activation, index) => new ActivationLocation(pair.Key, index, activation)))
            .SingleOrDefault(item => item.Activation.EndedAt is null);
        if (openActivation is not null &&
            string.Equals(
                openActivation.AccountKey,
                registry.ActiveAccountKey,
                StringComparison.Ordinal))
        {
            return state;
        }

        var activatedAt = registry.ActiveAccountActivatedAt is { } registryActivatedAt &&
            IsUtcTimestamp(registryActivatedAt) &&
            registryActivatedAt <= observedAt
                ? registryActivatedAt
                : observedAt;
        var earliestAllowed = openActivation?.Activation.StartedAt ??
            state.Accounts
                .SelectMany(pair => pair.Value.Activations)
                .Where(activation => activation.EndedAt.HasValue)
                .Select(activation => activation.EndedAt!.Value)
                .DefaultIfEmpty(DateTimeOffset.MinValue)
                .Max();
        if (activatedAt < earliestAllowed ||
            (openActivation is not null && activatedAt == earliestAllowed))
        {
            activatedAt = observedAt;
        }

        if (activatedAt < earliestAllowed ||
            (openActivation is not null && activatedAt == earliestAllowed))
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedAt),
                "Registry observations must advance activation history.");
        }

        var accounts = state.Accounts.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        if (openActivation is not null)
        {
            var previousLedger = accounts[openActivation.AccountKey];
            var activations = previousLedger.Activations.ToArray();
            activations[openActivation.Index] =
                openActivation.Activation with { EndedAt = activatedAt };
            accounts[openActivation.AccountKey] =
                previousLedger with { Activations = activations };
        }

        accounts.TryGetValue(registry.ActiveAccountKey, out var activeLedger);
        activeLedger ??= new AccountQuotaEstimateLedger([], []);
        accounts[registry.ActiveAccountKey] = activeLedger with
        {
            Activations = activeLedger.Activations
                .Append(new AccountActivationInterval(activatedAt, null))
                .ToArray(),
        };
        return new QuotaEstimateLedgerState(accounts);
    }

    private static QuotaEstimateLedgerLoadResult Empty() =>
        new(
            new QuotaEstimateLedgerState(
                new Dictionary<string, AccountQuotaEstimateLedger>(StringComparer.Ordinal)),
            null);

    private QuotaEstimateLedgerLoadResult Blocked(string error)
    {
        _saveBlocked = true;
        return new QuotaEstimateLedgerLoadResult(
            new QuotaEstimateLedgerState(
                new Dictionary<string, AccountQuotaEstimateLedger>(StringComparer.Ordinal)),
            error);
    }

    private static AccountQuotaEstimateLedger Copy(AccountQuotaEstimateLedger ledger) =>
        new(
            ledger.Activations?.ToArray()!,
            ledger.Observations?.ToArray()!);

    private static bool IsValid(QuotaEstimateLedgerState state)
    {
        if (state.Accounts is null)
        {
            return false;
        }

        var allActivations = new List<AccountActivationInterval>();
        foreach (var (accountKey, ledger) in state.Accounts)
        {
            if (string.IsNullOrWhiteSpace(accountKey) ||
                ledger?.Activations is null ||
                ledger.Observations is null ||
                !AreValidActivations(ledger.Activations) ||
                !AreValidObservations(ledger.Observations))
            {
                return false;
            }

            allActivations.AddRange(ledger.Activations);
        }

        var orderedActivations = allActivations
            .OrderBy(activation => activation.StartedAt)
            .ToArray();
        for (var index = 1; index < orderedActivations.Length; index++)
        {
            var previous = orderedActivations[index - 1];
            if (previous.EndedAt is null ||
                previous.EndedAt.Value > orderedActivations[index].StartedAt)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreValidActivations(
        IReadOnlyList<AccountActivationInterval> activations)
    {
        DateTimeOffset? previousEnd = null;
        for (var index = 0; index < activations.Count; index++)
        {
            var activation = activations[index];
            if (activation is null ||
                !IsUtcTimestamp(activation.StartedAt) ||
                activation.EndedAt is { } endedAt &&
                (!IsUtcTimestamp(endedAt) || endedAt <= activation.StartedAt) ||
                index > 0 &&
                (previousEnd is null || previousEnd.Value > activation.StartedAt))
            {
                return false;
            }

            previousEnd = activation.EndedAt;
        }

        return true;
    }

    private static bool AreValidObservations(
        IReadOnlyList<QuotaUsageObservation> observations)
    {
        DateTimeOffset? previousObservedAt = null;
        foreach (var observation in observations)
        {
            if (observation?.Segment is not { } segment ||
                segment.Period is not QuotaPeriod.Weekly and not QuotaPeriod.Monthly ||
                !IsUtcTimestamp(segment.SegmentStart) ||
                !IsUtcTimestamp(segment.ResetsAt) ||
                segment.SegmentStart >= segment.ResetsAt ||
                !IsUtcTimestamp(observation.ObservedAt) ||
                previousObservedAt > observation.ObservedAt ||
                !double.IsFinite(observation.UsedPercent) ||
                observation.UsedPercent is < 0 or > 100 ||
                !double.IsFinite(observation.PercentResolution) ||
                observation.PercentResolution <= 0 ||
                observation.AttributedCredits < 0 ||
                !HasValidBounds(observation.LowerUsd, observation.UpperUsd) ||
                !Enum.IsDefined(observation.Source) ||
                !Enum.IsDefined(observation.Kind))
            {
                return false;
            }

            previousObservedAt = observation.ObservedAt;
        }

        return true;
    }

    private static bool HasValidBounds(decimal? lower, decimal? upper) =>
        lower.HasValue == upper.HasValue &&
        lower is null or >= 0 &&
        upper is null or >= 0 &&
        (lower is null || lower <= upper);

    private static bool IsUtcTimestamp(DateTimeOffset value) =>
        value != default && value.Offset == TimeSpan.Zero;

    private sealed class LedgerDocument
    {
        public int SchemaVersion { get; set; }

        public Dictionary<string, AccountQuotaEstimateLedger?>? Accounts { get; set; }
    }

    private sealed record ActivationLocation(
        string AccountKey,
        int Index,
        AccountActivationInterval Activation);
}
