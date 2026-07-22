using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class CodexCliStagerTests
{
    [Fact]
    public async Task Stages_codex_cli_in_per_user_cache_and_returns_cache_directory()
    {
        using var source = new TemporaryDirectory();
        using var localAppData = new TemporaryDirectory();
        var cliDirectory = CreateSourceCli(source, [1, 2, 3, 4]);
        var expectedDirectory = Path.Combine(
            localAppData.Path,
            "CodexAccountSwitcher",
            "codex-cli");

        var actualDirectory = await new CodexCliStager(localAppData.Path)
            .StageAsync(cliDirectory, default);

        Assert.Equal(expectedDirectory, actualDirectory);
        Assert.Equal(
            [1, 2, 3, 4],
            File.ReadAllBytes(Path.Combine(actualDirectory, "codex.exe")));
        Assert.Empty(Directory.EnumerateFiles(actualDirectory, "*.tmp"));
    }

    [Fact]
    public async Task Reuses_hash_matching_cached_cli_without_replacing_it()
    {
        using var source = new TemporaryDirectory();
        using var localAppData = new TemporaryDirectory();
        var bytes = new byte[] { 4, 3, 2, 1 };
        var cliDirectory = CreateSourceCli(source, bytes);
        var cacheDirectory = CreateCache(localAppData, bytes);
        var cachedExecutable = Path.Combine(cacheDirectory, "codex.exe");
        using var retainedReadHandle = new FileStream(
            cachedExecutable,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var actualDirectory = await new CodexCliStager(localAppData.Path)
            .StageAsync(cliDirectory, default);

        Assert.Equal(cacheDirectory, actualDirectory);
        Assert.Equal(bytes, File.ReadAllBytes(cachedExecutable));
    }

    [Fact]
    public async Task Atomically_replaces_hash_mismatched_cached_cli()
    {
        using var source = new TemporaryDirectory();
        using var localAppData = new TemporaryDirectory();
        var sourceBytes = new byte[] { 9, 8, 7, 6 };
        var cliDirectory = CreateSourceCli(source, sourceBytes);
        var cacheDirectory = CreateCache(localAppData, [1, 1, 1]);

        var actualDirectory = await new CodexCliStager(localAppData.Path)
            .StageAsync(cliDirectory, default);

        Assert.Equal(cacheDirectory, actualDirectory);
        Assert.Equal(sourceBytes, File.ReadAllBytes(Path.Combine(cacheDirectory, "codex.exe")));
        Assert.Empty(Directory.EnumerateFiles(cacheDirectory, "*.tmp"));
    }

    [Fact]
    public async Task Failed_atomic_replacement_removes_temporary_file()
    {
        using var source = new TemporaryDirectory();
        using var localAppData = new TemporaryDirectory();
        var cliDirectory = CreateSourceCli(source, [7, 7, 7]);
        var cacheDirectory = Path.Combine(
            localAppData.Path,
            "CodexAccountSwitcher",
            "codex-cli");
        Directory.CreateDirectory(Path.Combine(cacheDirectory, "codex.exe"));

        var exception = await Record.ExceptionAsync(() => new CodexCliStager(localAppData.Path)
            .StageAsync(cliDirectory, default));

        Assert.True(exception is IOException or UnauthorizedAccessException);
        Assert.Empty(Directory.EnumerateFiles(cacheDirectory, "*.tmp"));
    }

    [Fact]
    public async Task Missing_source_cli_does_not_create_cache()
    {
        using var source = new TemporaryDirectory();
        using var localAppData = new TemporaryDirectory();

        await Assert.ThrowsAsync<FileNotFoundException>(() => new CodexCliStager(localAppData.Path)
            .StageAsync(source.Path, default));

        Assert.False(Directory.Exists(Path.Combine(localAppData.Path, "CodexAccountSwitcher")));
    }

    [Fact]
    public async Task Pre_cancelled_staging_does_not_create_cache()
    {
        using var source = new TemporaryDirectory();
        using var localAppData = new TemporaryDirectory();
        var cliDirectory = CreateSourceCli(source, [5, 5, 5]);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CodexCliStager(localAppData.Path)
                .StageAsync(cliDirectory, cancellationSource.Token));

        Assert.False(Directory.Exists(Path.Combine(localAppData.Path, "CodexAccountSwitcher")));
    }

    private static string CreateCache(TemporaryDirectory localAppData, byte[] bytes)
    {
        var cacheDirectory = Path.Combine(
            localAppData.Path,
            "CodexAccountSwitcher",
            "codex-cli");
        Directory.CreateDirectory(cacheDirectory);
        File.WriteAllBytes(Path.Combine(cacheDirectory, "codex.exe"), bytes);
        return cacheDirectory;
    }

    private static string CreateSourceCli(TemporaryDirectory source, byte[] bytes)
    {
        var cliDirectory = Path.Combine(source.Path, "resources");
        Directory.CreateDirectory(cliDirectory);
        File.WriteAllBytes(Path.Combine(cliDirectory, "codex.exe"), bytes);
        return cliDirectory;
    }
}
