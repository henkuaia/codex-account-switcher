using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class SingleInstanceOwnershipTests
{
    [Fact]
    public void App_acquires_ownership_before_constructing_account_services_and_releases_on_exit()
    {
        Assert.Equal(
            "Codex Account Switcher is already running for this Windows user.",
            App.SecondInstanceMessage);

        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "CodexAccountSwitcher",
            "App.xaml.cs"));
        var startup = source[
            source.IndexOf("protected override async void OnStartup", StringComparison.Ordinal)..
            source.IndexOf("protected override void OnExit", StringComparison.Ordinal)];

        AssertInOrder(
            startup,
            "SingleInstanceOwnership.TryAcquire",
            "SecondInstanceMessage",
            "Shutdown();",
            "return;",
            "var processRunner = new ProcessRunner();");
        Assert.Contains("_singleInstanceOwnership?.Dispose();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void First_owner_blocks_second_until_deterministic_release()
    {
        var ownershipType = typeof(SafeSwitchCoordinator).Assembly.GetType(
            "CodexAccountSwitcher.Services.SingleInstanceOwnership");
        Assert.NotNull(ownershipType);
        var tryAcquire = ownershipType.GetMethod(
            "TryAcquire",
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static,
            [typeof(string)]);
        Assert.NotNull(tryAcquire);
        var name = $"CodexAccountSwitcher.Tests-{Guid.NewGuid():N}";

        var first = Assert.IsAssignableFrom<IDisposable>(tryAcquire.Invoke(null, [name]));
        try
        {
            Assert.Null(tryAcquire.Invoke(null, [name]));
        }
        finally
        {
            first.Dispose();
        }

        using var afterRelease = Assert.IsAssignableFrom<IDisposable>(tryAcquire.Invoke(null, [name]));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodexAccountSwitcher.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static void AssertInOrder(string content, params string[] snippets)
    {
        var searchStart = 0;
        foreach (var snippet in snippets)
        {
            var index = content.IndexOf(snippet, searchStart, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Could not find '{snippet}' after index {searchStart}.");
            searchStart = index + snippet.Length;
        }
    }
}
