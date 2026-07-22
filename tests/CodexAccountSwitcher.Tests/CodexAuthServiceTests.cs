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
        var service = CreateService(helperPath, directory.Path, runner);

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
        var service = CreateService(helperPath, directory.Path, runner);

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
        var service = CreateService(helperPath, directory.Path, runner);
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
        var service = CreateService(helperPath, directory.Path, runner);

        await service.RemoveAsync(default);

        Assert.Equal(["remove"], runner.LastRequest!.Arguments);
        Assert.True(runner.LastRequest.Visible);
        Assert.Equal(0, runner.CapturedCallCount);
        Assert.Equal(0, runner.StreamingCallCount);
        Assert.Equal(1, runner.VisibleCallCount);
    }

    [Fact]
    public async Task Targeted_remove_uses_captured_direct_selector_with_reconcile_suppression()
    {
        using var directory = new TemporaryDirectory();
        var helperPath = CreateHelper(directory);
        var runner = new FakeProcessRunner();
        var service = CreateService(helperPath, directory.Path, runner);
        var method = typeof(CodexAuthService).GetMethod(
            "RemoveAsync",
            [typeof(string), typeof(CancellationToken)]);

        Assert.NotNull(method);
        var invocation = method.Invoke(service, ["unique@example.com", CancellationToken.None]);
        var result = await Assert.IsAssignableFrom<Task<CommandResult>>(invocation);

        Assert.True(result.Succeeded);
        Assert.Equal(["remove", "unique@example.com"], runner.LastRequest!.Arguments);
        Assert.False(runner.LastRequest.Visible);
        Assert.Equal(1, runner.CapturedCallCount);
        Assert.Equal(0, runner.VisibleCallCount);
        Assert.Equal("1", runner.LastRequest.Environment!["CODEX_AUTH_SKIP_SERVICE_RECONCILE"]);
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
        var service = CreateService(helperPath, directory.Path, runner);

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
        var service = CreateService(
            Path.Combine(directory.Path, "missing.exe"),
            directory.Path,
            runner);

        var result = await service.SwitchAsync("main", default);

        Assert.False(result.Succeeded);
        Assert.Equal(0, runner.CapturedCallCount);
        Assert.Equal(0, runner.VisibleCallCount);
        Assert.Contains(Path.GetFullPath(Path.Combine(directory.Path, "missing.exe")), result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void Availability_reports_resolved_expected_helper_path()
    {
        using var directory = new TemporaryDirectory();
        var missingPath = Path.Combine(directory.Path, "missing", "codex-auth.exe");
        var service = CreateService(missingPath, directory.Path, new FakeProcessRunner());
        var method = typeof(CodexAuthService).GetMethod("CheckAvailability");

        Assert.NotNull(method);
        var availability = method.Invoke(service, null);

        Assert.NotNull(availability);
        Assert.False(RequiredProperty<bool>(availability, "IsAvailable"));
        Assert.Equal(Path.GetFullPath(missingPath), RequiredProperty<string>(availability, "ExpectedPath"));
        Assert.Contains(Path.GetFullPath(missingPath), RequiredProperty<string>(availability, "Error"), StringComparison.Ordinal);
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
        var service = CreateService(helperPath, cliDirectory, runner);

        await service.LoginAsync(default);

        Assert.NotNull(runner.LastRequest!.Environment);
        Assert.True(runner.LastRequest.Environment!.TryGetValue("PATH", out var childPath));
        Assert.Contains(Path.GetFullPath(cliDirectory), childPath!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(parentPath, Environment.GetEnvironmentVariable("PATH"));
    }

    [Theory]
    [InlineData("switch")]
    [InlineData("login")]
    [InlineData("streaming-login")]
    [InlineData("remove")]
    public async Task Every_mutating_command_suppresses_managed_service_reconciliation(string command)
    {
        using var directory = new TemporaryDirectory();
        var helperPath = CreateHelper(directory);
        var runner = new FakeProcessRunner();
        var service = CreateService(helperPath, directory.Path, runner);

        switch (command)
        {
            case "switch":
                await service.SwitchAsync("main", default);
                break;
            case "login":
                await service.LoginAsync(default);
                break;
            case "streaming-login":
                await service.LoginAsync((_, _) => ValueTask.CompletedTask, default);
                break;
            case "remove":
                await service.RemoveAsync(default);
                break;
            default:
                throw new InvalidOperationException($"Unknown command: {command}");
        }

        Assert.NotNull(runner.LastRequest?.Environment);
        Assert.True(runner.LastRequest.Environment.TryGetValue(
            "CODEX_AUTH_SKIP_SERVICE_RECONCILE",
            out var value));
        Assert.Equal("1", value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Login_staging_failure_returns_before_helper_start(bool streaming)
    {
        using var directory = new TemporaryDirectory();
        var helperPath = CreateHelper(directory);
        var runner = new FakeProcessRunner();
        var stager = new FakeCodexCliStager
        {
            Exception = new IOException("copy denied"),
        };
        var service = CreateInjectedService(helperPath, directory.Path, runner, stager);

        var result = streaming
            ? await service.LoginAsync((_, _) => ValueTask.CompletedTask, default)
            : await service.LoginAsync(default);

        Assert.False(result.Succeeded);
        Assert.Contains("Codex CLI", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, stager.CallCount);
        Assert.Equal(0, runner.CapturedCallCount);
        Assert.Equal(0, runner.StreamingCallCount);
    }

    [Theory]
    [InlineData(false, "invalid-data")]
    [InlineData(true, "invalid-data")]
    [InlineData(false, "argument")]
    [InlineData(true, "argument")]
    [InlineData(false, "not-supported")]
    [InlineData(true, "not-supported")]
    public async Task Expected_staging_errors_return_failure_before_helper_start(
        bool streaming,
        string exceptionKind)
    {
        using var directory = new TemporaryDirectory();
        var helperPath = CreateHelper(directory);
        var runner = new FakeProcessRunner();
        var stager = new FakeCodexCliStager
        {
            Exception = CreateStagingException(exceptionKind),
        };
        var service = CreateInjectedService(helperPath, directory.Path, runner, stager);

        var result = streaming
            ? await service.LoginAsync((_, _) => ValueTask.CompletedTask, default)
            : await service.LoginAsync(default);

        Assert.False(result.Succeeded);
        Assert.Contains("Codex CLI", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, stager.CallCount);
        Assert.Equal(0, runner.CapturedCallCount);
        Assert.Equal(0, runner.StreamingCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pre_cancelled_login_propagates_cancellation_before_helper_start(bool streaming)
    {
        using var directory = new TemporaryDirectory();
        var helperPath = CreateHelper(directory);
        var runner = new FakeProcessRunner();
        var stager = new FakeCodexCliStager { Result = directory.Path };
        var service = CreateInjectedService(helperPath, directory.Path, runner, stager);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => streaming
            ? service.LoginAsync((_, _) => ValueTask.CompletedTask, cancellationSource.Token)
            : service.LoginAsync(cancellationSource.Token));

        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
        Assert.Equal(0, runner.CapturedCallCount);
        Assert.Equal(0, runner.StreamingCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Login_adds_staged_cache_directory_to_child_path(bool streaming)
    {
        using var directory = new TemporaryDirectory();
        var helperPath = CreateHelper(directory);
        var sourceDirectory = Path.Combine(directory.Path, "package", "resources");
        var cacheDirectory = Path.Combine(directory.Path, "cache", "codex-cli");
        Directory.CreateDirectory(cacheDirectory);
        var runner = new FakeProcessRunner();
        var stager = new FakeCodexCliStager { Result = cacheDirectory };
        var service = CreateInjectedService(helperPath, sourceDirectory, runner, stager);
        var parentPath = Environment.GetEnvironmentVariable("PATH");

        if (streaming)
        {
            await service.LoginAsync((_, _) => ValueTask.CompletedTask, default);
        }
        else
        {
            await service.LoginAsync(default);
        }

        Assert.Equal(sourceDirectory, stager.LastCliDirectory);
        Assert.StartsWith(
            string.Concat(Path.GetFullPath(cacheDirectory), Path.PathSeparator),
            runner.LastRequest!.Environment!["PATH"],
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(streaming ? 1 : 0, runner.StreamingCallCount);
        Assert.Equal(streaming ? 0 : 1, runner.CapturedCallCount);
        Assert.Equal(parentPath, Environment.GetEnvironmentVariable("PATH"));
    }

    [Theory]
    [InlineData("switch")]
    [InlineData("remove")]
    [InlineData("targeted-remove")]
    public async Task Switch_and_remove_do_not_stage_codex_cli(string command)
    {
        using var directory = new TemporaryDirectory();
        var helperPath = CreateHelper(directory);
        var runner = new FakeProcessRunner();
        var stager = new FakeCodexCliStager
        {
            Exception = new IOException("must not be observed"),
        };
        var service = CreateInjectedService(helperPath, directory.Path, runner, stager);

        var result = command switch
        {
            "switch" => await service.SwitchAsync("main", default),
            "remove" => await service.RemoveAsync(default),
            "targeted-remove" => await service.RemoveAsync("main", default),
            _ => throw new InvalidOperationException(),
        };

        Assert.True(result.Succeeded);
        Assert.Equal(0, stager.CallCount);
        Assert.NotNull(runner.LastRequest);
    }

    private static CodexAuthService CreateService(
        string helperPath,
        string cliDirectory,
        IProcessRunner runner) =>
        CreateInjectedService(
            helperPath,
            cliDirectory,
            runner,
            new FakeCodexCliStager { Result = cliDirectory });

    private static Exception CreateStagingException(string exceptionKind) => exceptionKind switch
    {
        "invalid-data" => new InvalidDataException("invalid hash"),
        "argument" => new ArgumentException("invalid path"),
        "not-supported" => new NotSupportedException("unsupported path"),
        _ => throw new InvalidOperationException(),
    };

    private static CodexAuthService CreateInjectedService(
        string helperPath,
        string cliDirectory,
        IProcessRunner runner,
        ICodexCliStager stager) =>
        new(helperPath, cliDirectory, runner, stager);

    private static string CreateHelper(TemporaryDirectory directory)
    {
        const string helperFileName = "codex-auth.exe";
        directory.Write(helperFileName, string.Empty);
        return Path.Combine(directory.Path, helperFileName);
    }

    private static T RequiredProperty<T>(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<T>(property.GetValue(instance));
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

    private sealed class FakeCodexCliStager : ICodexCliStager
    {
        public int CallCount { get; private set; }

        public Exception? Exception { get; init; }

        public string? LastCliDirectory { get; private set; }

        public string Result { get; init; } = string.Empty;

        public Task<string> StageAsync(string cliDirectory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastCliDirectory = cliDirectory;
            if (Exception is not null)
            {
                return Task.FromException<string>(Exception);
            }

            return Task.FromResult(Result);
        }
    }
}
