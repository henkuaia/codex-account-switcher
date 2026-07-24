using System.IO;
using System.Text.Json;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public sealed class QuotaCacheService
{
    private const int CurrentSchemaVersion = 1;
    private const string InvalidFileError = "本地额度缓存文件无效，原文件已保留。";
    private const string UnsupportedVersionError = "本地额度缓存版本不受支持，原文件已保留。";
    private const string ReadError = "本地额度缓存暂时无法读取，原文件已保留。";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private bool _saveBlocked;

    public QuotaCacheService(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public static QuotaCacheService CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new QuotaCacheService(Path.Combine(
            localAppData,
            "CodexAccountSwitcher",
            "quota-cache.json"));
    }

    public async Task<QuotaCacheLoadResult> LoadAsync(CancellationToken cancellationToken)
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
            var document = await JsonSerializer.DeserializeAsync<CacheDocument>(
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

            var accounts = new Dictionary<string, QuotaCacheEntry>(StringComparer.Ordinal);
            foreach (var (accountKey, entry) in document.Accounts)
            {
                if (string.IsNullOrWhiteSpace(accountKey) ||
                    entry is null ||
                    !IsValid(entry) ||
                    !accounts.TryAdd(accountKey, entry))
                {
                    return Blocked(InvalidFileError);
                }
            }

            _saveBlocked = false;
            return new QuotaCacheLoadResult(accounts, null);
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
        IReadOnlyDictionary<string, QuotaCacheEntry> accounts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (_saveBlocked)
        {
            throw new InvalidOperationException("The existing quota cache cannot be overwritten.");
        }

        foreach (var (accountKey, entry) in accounts)
        {
            if (string.IsNullOrWhiteSpace(accountKey))
            {
                throw new ArgumentException("Account keys cannot be empty.", nameof(accounts));
            }

            Validate(entry);
        }

        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $"{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var document = new CacheDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                Accounts = accounts.ToDictionary(
                    pair => pair.Key,
                    pair => (QuotaCacheEntry?)pair.Value,
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
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
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

    private static QuotaCacheLoadResult Empty() =>
        new(new Dictionary<string, QuotaCacheEntry>(StringComparer.Ordinal), null);

    private QuotaCacheLoadResult Blocked(string error)
    {
        _saveBlocked = true;
        return new QuotaCacheLoadResult(
            new Dictionary<string, QuotaCacheEntry>(StringComparer.Ordinal),
            error);
    }

    private static bool IsValid(QuotaCacheEntry? entry)
    {
        if (entry?.Display is not { } display)
        {
            return false;
        }

        var hasBothEstimateBounds =
            display.EstimatedPeriodQuotaLowerUsd.HasValue ==
            display.EstimatedPeriodQuotaUpperUsd.HasValue;
        return entry.RefreshedAt != default &&
            display is not null &&
            Enum.IsDefined(display.Period) &&
            display.RemainingPercent is >= 0 and <= 100 &&
            double.IsFinite(display.UsedPercent) &&
            display.UsedPercent is >= 0 and <= 100 &&
            display.WindowDuration > TimeSpan.Zero &&
            display.Tooltip is not null &&
            display.AvailableResetCount is null or >= 0 &&
            display.IndividualLimitUsd is null or >= 0 &&
            display.EstimatedPeriodQuotaLowerUsd is null or >= 0 &&
            display.EstimatedPeriodQuotaUpperUsd is null or >= 0 &&
            hasBothEstimateBounds &&
            (display.EstimatedPeriodQuotaLowerUsd is null ||
             display.EstimatedPeriodQuotaLowerUsd <= display.EstimatedPeriodQuotaUpperUsd);
    }

    private static void Validate(QuotaCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(entry.Display);
        if (!IsValid(entry))
        {
            throw new ArgumentOutOfRangeException(nameof(entry), "Quota cache values are invalid.");
        }
    }

    private sealed class CacheDocument
    {
        public int SchemaVersion { get; set; }

        public Dictionary<string, QuotaCacheEntry?>? Accounts { get; set; }
    }
}
