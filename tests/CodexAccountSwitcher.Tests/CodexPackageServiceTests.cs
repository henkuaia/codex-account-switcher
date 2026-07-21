using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class CodexPackageServiceTests
{
    private const string InstallLocation =
        @"C:\Program Files\WindowsApps\OpenAI.Codex_26.715.7063.0_x64__2p2nqsd0c76g0";

    [Fact]
    public async Task Discover_maps_single_manifest_application_and_uses_fixed_powershell_command()
    {
        var runner = new FakeProcessRunner
        {
            Result = new CommandResult(0, ValidPackageJson(), string.Empty),
        };
        var service = new CodexPackageService(runner);

        var package = await service.DiscoverAsync(default);

        Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0", package.PackageFamilyName);
        Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0!App", package.AppUserModelId);
        Assert.Equal(InstallLocation, package.InstallLocation);
        Assert.Equal(Path.Combine(InstallLocation, "app", "ChatGPT.exe"), package.MainExecutablePath);
        Assert.Equal(Path.Combine(InstallLocation, "app", "resources"), package.CliDirectory);

        Assert.Equal("powershell.exe", runner.LastRequest!.FileName);
        Assert.False(runner.LastRequest.Visible);
        Assert.Equal("-NoProfile", runner.LastRequest.Arguments[0]);
        Assert.Equal("-NonInteractive", runner.LastRequest.Arguments[1]);
        Assert.Equal("-Command", runner.LastRequest.Arguments[2]);
        Assert.Contains("Get-AppxPackage -Name OpenAI.Codex", runner.LastRequest.Arguments[3], StringComparison.Ordinal);
        Assert.Contains("ConvertTo-Json -Compress", runner.LastRequest.Arguments[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_rejects_missing_package_without_exposing_output()
    {
        const string rawOutput = "missing-package-secret";
        var service = new CodexPackageService(new FakeProcessRunner
        {
            Result = new CommandResult(0, "null", rawOutput),
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DiscoverAsync(default));

        Assert.Equal("The Codex package is not installed.", exception.Message);
        Assert.DoesNotContain(rawOutput, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_rejects_multiple_manifest_applications_without_exposing_output()
    {
        const string secondApplicationId = "PrivateAdminApp";
        var output = $$"""
            {
              "PackageFamilyName": "OpenAI.Codex_2p2nqsd0c76g0",
              "InstallLocation": "{{JsonInstallLocation()}}",
              "Applications": [
                {"Id":"App","Executable":"app/ChatGPT.exe"},
                {"Id":"{{secondApplicationId}}","Executable":"admin/Admin.exe"}
              ]
            }
            """;
        var service = CreateService(output);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DiscoverAsync(default));

        Assert.Equal("The Codex package manifest must contain exactly one application.", exception.Message);
        Assert.DoesNotContain(secondApplicationId, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_rejects_malformed_output_without_exposing_output()
    {
        const string rawOutput = "{malformed-package-secret";
        var service = CreateService(rawOutput);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => service.DiscoverAsync(default));

        Assert.Equal("The Codex package discovery output is invalid.", exception.Message);
        Assert.DoesNotContain(rawOutput, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_rejects_executable_path_outside_install_location()
    {
        var output = $$"""
            {
              "PackageFamilyName": "OpenAI.Codex_2p2nqsd0c76g0",
              "InstallLocation": "{{JsonInstallLocation()}}",
              "Applications": [{"Id":"App","Executable":"../OtherApp/ChatGPT.exe"}]
            }
            """;
        var service = CreateService(output);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => service.DiscoverAsync(default));

        Assert.Equal("The Codex package discovery output is invalid.", exception.Message);
    }

    [Fact]
    public async Task Discover_rejects_relative_install_location()
    {
        var output = """
            {
              "PackageFamilyName": "OpenAI.Codex_2p2nqsd0c76g0",
              "InstallLocation": "relative/package",
              "Applications": [{"Id":"App","Executable":"app/ChatGPT.exe"}]
            }
            """;
        var service = CreateService(output);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => service.DiscoverAsync(default));

        Assert.Equal("The Codex package discovery output is invalid.", exception.Message);
    }

    [Fact]
    public async Task Discover_rejects_manifest_executable_other_than_chatgpt()
    {
        var output = $$"""
            {
              "PackageFamilyName": "OpenAI.Codex_2p2nqsd0c76g0",
              "InstallLocation": "{{JsonInstallLocation()}}",
              "Applications": [{"Id":"App","Executable":"app/Other.exe"}]
            }
            """;
        var service = CreateService(output);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => service.DiscoverAsync(default));

        Assert.Equal("The Codex package discovery output is invalid.", exception.Message);
    }

    private static CodexPackageService CreateService(string output) =>
        new(new FakeProcessRunner { Result = new CommandResult(0, output, string.Empty) });

    private static string ValidPackageJson() => $$"""
        {
          "PackageFamilyName": "OpenAI.Codex_2p2nqsd0c76g0",
          "InstallLocation": "{{JsonInstallLocation()}}",
          "Applications": [{"Id":"App","Executable":"app/ChatGPT.exe"}]
        }
        """;

    private static string JsonInstallLocation() => InstallLocation.Replace("\\", "\\\\", StringComparison.Ordinal);

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public ProcessRequest? LastRequest { get; private set; }

        public CommandResult Result { get; init; } = new(0, string.Empty, string.Empty);

        public Task<CommandResult> RunCapturedAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Result);
        }

        public Task<CommandResult> RunVisibleAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

public sealed class CodexProcessControllerTests
{
    private const string InstallLocation =
        @"C:\Program Files\WindowsApps\OpenAI.Codex_26.715.7063.0_x64__2p2nqsd0c76g0";

    [Fact]
    public async Task Close_targets_only_installed_chatgpt_and_its_installed_descendants()
    {
        var accessor = new FakeCodexProcessAccessor(
        [
            Process(100, 1, "app", "ChatGPT.exe"),
            Process(101, 100, "app", "resources", "codex.exe"),
            Process(102, 101, "app", "resources", "helper.exe"),
            new CodexProcessEntry(200, 100, @"C:\Other\escaped-child.exe"),
            Process(300, 1, "app", "unrelated.exe"),
            new CodexProcessEntry(400, 1, @"C:\Other\ChatGPT.exe"),
            new CodexProcessEntry(500, 1, InstallLocation + @".attacker\ChatGPT.exe"),
            new CodexProcessEntry(600, 1, null),
        ]);
        accessor.ExitResults[101] = false;
        var controller = new CodexProcessController(accessor, new FakeProcessRunner());

        var result = await controller.CloseAsync(Package(), TimeSpan.FromSeconds(30), default);

        Assert.False(result.AllExited);
        Assert.Equal([101], result.RemainingProcessIds);
        Assert.Equal([100, 101, 102], accessor.ClosedProcessIds);
        Assert.Equal([100, 101, 102], accessor.WaitedProcessIds);
        Assert.All(accessor.WaitTimeouts, timeout => Assert.Equal(TimeSpan.FromSeconds(8), timeout));
        Assert.True(accessor.Events.IndexOf("close:102") < accessor.Events.IndexOf("wait:100"));
    }

    [Fact]
    public async Task Close_propagates_programming_failures()
    {
        var accessor = new FakeCodexProcessAccessor([Process(100, 1, "app", "ChatGPT.exe")])
        {
            CloseException = new InvalidOperationException("programming failure"),
        };
        var controller = new CodexProcessController(accessor, new FakeProcessRunner());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.CloseAsync(Package(), TimeSpan.FromSeconds(8), default));

        Assert.Equal("programming failure", exception.Message);
    }

    [Fact]
    public async Task Pre_cancelled_close_does_not_enumerate_processes()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var accessor = new FakeCodexProcessAccessor([]);
        var controller = new CodexProcessController(accessor, new FakeProcessRunner());

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.CloseAsync(Package(), TimeSpan.FromSeconds(8), cancellationSource.Token));

        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
        Assert.Equal(0, accessor.EnumerationCount);
    }

    [Fact]
    public async Task Force_terminate_kills_each_supplied_process_tree()
    {
        var accessor = new FakeCodexProcessAccessor([]);
        var controller = new CodexProcessController(accessor, new FakeProcessRunner());

        await controller.ForceTerminateAsync([101, 102], default);

        Assert.Equal([(101, true), (102, true)], accessor.KillCalls);
    }

    [Fact]
    public async Task Launch_uses_exact_apps_folder_target_through_explorer()
    {
        var runner = new FakeProcessRunner();
        var controller = new CodexProcessController(new FakeCodexProcessAccessor([]), runner);

        await controller.LaunchAsync(Package(), default);

        Assert.Equal("explorer.exe", runner.LastRequest!.FileName);
        Assert.Equal(["shell:AppsFolder\\OpenAI.Codex_2p2nqsd0c76g0!App"], runner.LastRequest.Arguments);
        Assert.True(runner.LastRequest.Visible);
        Assert.Equal(1, runner.VisibleCallCount);
    }

    [Fact]
    public async Task Launch_reports_nonzero_explorer_exit_without_exposing_output()
    {
        const string rawError = "launch-secret";
        var runner = new FakeProcessRunner
        {
            VisibleResult = new CommandResult(1, string.Empty, rawError),
        };
        var controller = new CodexProcessController(new FakeCodexProcessAccessor([]), runner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.LaunchAsync(Package(), default));

        Assert.Equal("Codex launch failed.", exception.Message);
        Assert.DoesNotContain(rawError, exception.ToString(), StringComparison.Ordinal);
    }

    private static CodexPackageInfo Package() => new(
        "OpenAI.Codex_2p2nqsd0c76g0",
        "OpenAI.Codex_2p2nqsd0c76g0!App",
        InstallLocation,
        Path.Combine(InstallLocation, "app", "ChatGPT.exe"),
        Path.Combine(InstallLocation, "app", "resources"));

    private static CodexProcessEntry Process(int id, int parentId, params string[] pathParts) =>
        new(id, parentId, Path.Combine([InstallLocation, .. pathParts]));

    private sealed class FakeCodexProcessAccessor(IReadOnlyList<CodexProcessEntry> processes)
        : ICodexProcessAccessor
    {
        public List<int> ClosedProcessIds { get; } = [];

        public InvalidOperationException? CloseException { get; init; }

        public int EnumerationCount { get; private set; }

        public Dictionary<int, bool> ExitResults { get; } = [];

        public List<string> Events { get; } = [];

        public List<(int ProcessId, bool EntireProcessTree)> KillCalls { get; } = [];

        public List<int> WaitedProcessIds { get; } = [];

        public List<TimeSpan> WaitTimeouts { get; } = [];

        public IReadOnlyList<CodexProcessEntry> GetProcesses()
        {
            EnumerationCount++;
            return processes;
        }

        public bool CloseMainWindow(int processId)
        {
            if (CloseException is not null)
            {
                throw CloseException;
            }

            ClosedProcessIds.Add(processId);
            Events.Add($"close:{processId}");
            return true;
        }

        public Task<bool> WaitForExitAsync(
            int processId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitedProcessIds.Add(processId);
            WaitTimeouts.Add(timeout);
            Events.Add($"wait:{processId}");
            return Task.FromResult(!ExitResults.TryGetValue(processId, out var exited) || exited);
        }

        public void Kill(int processId, bool entireProcessTree)
        {
            KillCalls.Add((processId, entireProcessTree));
        }
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public ProcessRequest? LastRequest { get; private set; }

        public int VisibleCallCount { get; private set; }

        public CommandResult VisibleResult { get; init; } = new(0, string.Empty, string.Empty);

        public Task<CommandResult> RunCapturedAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CommandResult> RunVisibleAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            VisibleCallCount++;
            return Task.FromResult(VisibleResult);
        }
    }
}
