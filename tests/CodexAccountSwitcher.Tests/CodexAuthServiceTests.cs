using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class CodexAuthServiceTests
{
    [Fact]
    public async Task Switch_uses_exact_stable_command_arguments()
    {
        using var directory = new TemporaryDirectory();
        var helperPath = CreateHelper(directory);
        var runner = new FakeProcessRunner();
        var service = new CodexAuthService(helperPath, directory.Path, runner);

        await service.SwitchAsync("main", default);

        Assert.Equal(["switch", "main"], runner.LastRequest!.Arguments);
        Assert.False(runner.LastRequest.Visible);
        Assert.Equal(1, runner.CapturedCallCount);
        Assert.Equal(0, runner.StreamingCallCount);
    }

    [Fact]
    public async Task Login_uses_exact_stable_command_arguments()
    {
        using var directory = new TemporaryDirectory();
        var helperPath = CreateHelper(directory);
        var runner = new FakeProcessRunner();
        var service = new CodexAuthService(helperPath, directory.Path, runner);

        await service.LoginAsync(default);

        Assert.Equal(["login", "--device-auth"], runner.LastRequest!.Arguments);
        Assert.False(runner.LastRequest.Visible);
        Assert.Equal(1, runner.CapturedCallCount);
        Assert.Equal(0, runner.StreamingCallCount);
    }

    [Fact]
    public async Task Streaming_login_uses_streaming_overload_and_exact_stable_command_arguments()
    {
        using var directory = new TemporaryDirectory();
        var helperPath = CreateHelper(directory);
        var runner = new FakeProcessRunner();
        var service = new CodexAuthService(helperPath, directory.Path, runner);
        ProcessOutputHandler outputHandler = (_, _) => ValueTask.CompletedTask;

        await service.LoginAsync(outputHandler, default);

        Assert.Equal(["login", "--device-auth"], runner.LastRequest!.Arguments);
        Assert.False(runner.LastRequest.Visible);
        Assert.Equal(0, runner.CapturedCallCount);
        Assert.Equal(1, runner.StreamingCallCount);
    }

    [Fact]
    public async Task Remove_uses_visible_stable_command()
    {
        using var directory = new TemporaryDirectory();
        var helperPath = CreateHelper(directory);
        var runner = new FakeProcessRunner();
        var service = new CodexAuthService(helperPath, directory.Path, runner);

        await service.RemoveAsync(default);

        Assert.Equal(["remove"], runner.LastRequest!.Arguments);
        Assert.True(runner.LastRequest.Visible);
        Assert.Equal(0, runner.CapturedCallCount);
        Assert.Equal(0, runner.StreamingCallCount);
        Assert.Equal(1, runner.VisibleCallCount);
    }

    [Theory]
    [InlineData("switch")]
    [InlineData("login")]
    public async Task Captured_nonzero_exit_returns_redacted_output(string command)
    {
        using var directory = new TemporaryDirectory();
        var helperPath = CreateHelper(directory);
        var runner = new FakeProcessRunner
        {
            CapturedResult = new CommandResult(
                1,
                "Authorization: Bearer output-token-secret",
                "{\"access_token\":\"error-token-secret\"}"),
        };
        var service = new CodexAuthService(helperPath, directory.Path, runner);

        var result = command == "switch"
            ? await service.SwitchAsync("main", default)
            : await service.LoginAsync(default);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain("output-token-secret", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("error-token-secret", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_helper_returns_failure_before_process_start()
    {
        using var directory = new TemporaryDirectory();
        var runner = new FakeProcessRunner();
        var service = new CodexAuthService(Path.Combine(directory.Path, "missing.exe"), directory.Path, runner);

        var result = await service.SwitchAsync("main", default);

        Assert.False(result.Succeeded);
        Assert.Equal(0, runner.CapturedCallCount);
        Assert.Equal(0, runner.VisibleCallCount);
        Assert.DoesNotContain("missing.exe", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_adds_resolved_cli_directory_to_child_path_without_mutating_parent()
    {
        using var directory = new TemporaryDirectory();
        var helperPath = CreateHelper(directory);
        var cliDirectory = Path.Combine(directory.Path, "app", "resources");
        Directory.CreateDirectory(cliDirectory);
        var parentPath = Environment.GetEnvironmentVariable("PATH");
        var runner = new FakeProcessRunner();
        var service = new CodexAuthService(helperPath, cliDirectory, runner);

        await service.LoginAsync(default);

        Assert.NotNull(runner.LastRequest!.Environment);
        Assert.True(runner.LastRequest.Environment!.TryGetValue("PATH", out var childPath));
        Assert.Contains(Path.GetFullPath(cliDirectory), childPath!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(parentPath, Environment.GetEnvironmentVariable("PATH"));
    }

    private static string CreateHelper(TemporaryDirectory directory)
    {
        const string helperFileName = "codex-auth.exe";
        directory.Write(helperFileName, string.Empty);
        return Path.Combine(directory.Path, helperFileName);
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public CommandResult CapturedResult { get; set; } = new(0, string.Empty, string.Empty);

        public ProcessRequest? LastRequest { get; private set; }

        public int CapturedCallCount { get; private set; }

        public int VisibleCallCount { get; private set; }

        public int StreamingCallCount { get; private set; }

        public Task<CommandResult> RunCapturedAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            CapturedCallCount++;
            return Task.FromResult(CapturedResult);
        }

        public Task<CommandResult> RunVisibleAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            VisibleCallCount++;
            return Task.FromResult(new CommandResult(0, string.Empty, string.Empty));
        }

        public Task<CommandResult> RunCapturedAsync(
            ProcessRequest request,
            ProcessOutputHandler outputHandler,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            StreamingCallCount++;
            return Task.FromResult(CapturedResult);
        }
    }
}
