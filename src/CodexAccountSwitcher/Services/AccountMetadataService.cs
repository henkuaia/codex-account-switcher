using System.IO;
using System.Text.Json;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public sealed class AccountMetadataService
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private bool _saveBlocked;

    public AccountMetadataService(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public static AccountMetadataService CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AccountMetadataService(Path.Combine(
            localAppData,
            "CodexAccountSwitcher",
            "account-metadata.json"));
    }

    public async Task<AccountMetadataLoadResult> LoadAsync(CancellationToken cancellationToken)
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
            var document = await JsonSerializer.DeserializeAsync<MetadataDocument>(
                stream,
                JsonOptions,
                cancellationToken);
            if (document is null || document.Accounts is null)
            {
                return Blocked("账号额度记录文件无效，原文件已保留。");
            }

            if (document.SchemaVersion != CurrentSchemaVersion)
            {
                return Blocked("账号额度记录版本不受支持，原文件已保留。");
            }

            var accounts = new Dictionary<string, AccountMetadata>(StringComparer.Ordinal);
            foreach (var (accountKey, metadata) in document.Accounts)
            {
                if (string.IsNullOrWhiteSpace(accountKey) ||
                    metadata is null ||
                    !IsValid(metadata))
                {
                    return Blocked("账号额度记录文件无效，原文件已保留。");
                }

                accounts.Add(accountKey, metadata);
            }

            _saveBlocked = false;
            return new AccountMetadataLoadResult(accounts, null);
        }
        catch (JsonException)
        {
            return Blocked("账号额度记录文件无效，原文件已保留。");
        }
    }

    public async Task SaveAsync(
        IReadOnlyDictionary<string, AccountMetadata> accounts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (_saveBlocked)
        {
            throw new InvalidOperationException("The existing account metadata file cannot be overwritten.");
        }

        foreach (var (accountKey, metadata) in accounts)
        {
            if (string.IsNullOrWhiteSpace(accountKey))
            {
                throw new ArgumentException("Account keys cannot be empty.", nameof(accounts));
            }

            Validate(metadata);
        }

        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $"{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var document = new MetadataDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                Accounts = accounts.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
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

    private static AccountMetadataLoadResult Empty() =>
        new(new Dictionary<string, AccountMetadata>(StringComparer.Ordinal), null);

    private AccountMetadataLoadResult Blocked(string error)
    {
        _saveBlocked = true;
        return new AccountMetadataLoadResult(
            new Dictionary<string, AccountMetadata>(StringComparer.Ordinal),
            error);
    }

    private static bool IsValid(AccountMetadata metadata) =>
        metadata.PeriodQuotaUsd is null or >= 0 &&
        metadata.UsedResetCount >= 0;

    private static void Validate(AccountMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!IsValid(metadata))
        {
            throw new ArgumentOutOfRangeException(nameof(metadata), "Metadata values cannot be negative.");
        }
    }

    private sealed class MetadataDocument
    {
        public int SchemaVersion { get; set; }

        public Dictionary<string, AccountMetadata>? Accounts { get; set; }
    }
}
