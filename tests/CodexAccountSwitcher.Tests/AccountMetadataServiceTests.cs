using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class AccountMetadataServiceTests
{
    [Fact]
    public async Task Missing_file_loads_empty_metadata()
    {
        using var directory = new TemporaryDirectory();
        var service = new AccountMetadataService(System.IO.Path.Combine(directory.Path, "metadata.json"));

        var result = await service.LoadAsync(default);

        Assert.Empty(result.Accounts);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Metadata_round_trips_by_account_key_without_temp_residue()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "metadata.json");
        var service = new AccountMetadataService(path);
        var expected = new Dictionary<string, AccountMetadata>(StringComparer.Ordinal)
        {
            ["account-a"] = new(40m, 3),
            ["account-b"] = new(null, 0),
        };

        await service.SaveAsync(expected, default);
        var result = await service.LoadAsync(default);

        Assert.Null(result.Error);
        Assert.Equal(expected, result.Accounts);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, -1)]
    public async Task Save_rejects_negative_values(decimal periodQuotaUsd, int usedResetCount)
    {
        using var directory = new TemporaryDirectory();
        var service = new AccountMetadataService(System.IO.Path.Combine(directory.Path, "metadata.json"));
        var metadata = new Dictionary<string, AccountMetadata>
        {
            ["account-a"] = new(periodQuotaUsd, usedResetCount),
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.SaveAsync(metadata, default));
    }

    [Fact]
    public async Task Malformed_file_is_reported_and_cannot_be_overwritten()
    {
        using var directory = new TemporaryDirectory();
        const string malformed = """{"schemaVersion":1,"accounts":""";
        directory.Write("metadata.json", malformed);
        var path = System.IO.Path.Combine(directory.Path, "metadata.json");
        var service = new AccountMetadataService(path);

        var result = await service.LoadAsync(default);

        Assert.Empty(result.Accounts);
        Assert.Equal("账号额度记录文件无效，原文件已保留。", result.Error);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveAsync(
                new Dictionary<string, AccountMetadata>
                {
                    ["account-a"] = new(40m, 3),
                },
                default));
        Assert.Equal(malformed, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Unsupported_schema_is_reported_without_overwriting()
    {
        using var directory = new TemporaryDirectory();
        const string unsupported = """{"schemaVersion":2,"accounts":{}}""";
        directory.Write("metadata.json", unsupported);
        var path = System.IO.Path.Combine(directory.Path, "metadata.json");
        var service = new AccountMetadataService(path);

        var result = await service.LoadAsync(default);

        Assert.Equal("账号额度记录版本不受支持，原文件已保留。", result.Error);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveAsync(new Dictionary<string, AccountMetadata>(), default));
        Assert.Equal(unsupported, await File.ReadAllTextAsync(path));
    }
}
