using CodexAccountSwitcher.Services;
using Microsoft.Win32.SafeHandles;

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
        Assert.Contains("$packages.Count -ne 1", runner.LastRequest.Arguments[3], StringComparison.Ordinal);
        Assert.Contains("ConvertTo-Json -Compress", runner.LastRequest.Arguments[3], StringComparison.Ordinal);
        Assert.DoesNotContain("Select-Object -First 1", runner.LastRequest.Arguments[3], StringComparison.Ordinal);
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
    public async Task Discover_rejects_zero_manifest_applications()
    {
        var output = $$"""
            {
              "PackageFamilyName": "OpenAI.Codex_2p2nqsd0c76g0",
              "InstallLocation": "{{JsonInstallLocation()}}",
              "Applications": []
            }
            """;
        var service = CreateService(output);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DiscoverAsync(default));

        Assert.Equal("The Codex package manifest must contain exactly one application.", exception.Message);
    }

    [Fact]
    public async Task Discover_rejects_multiple_installed_packages_without_exposing_output()
    {
        const string rawError = "MultiplePackages";
        var service = CreateService($$"""{"DiscoveryError":"{{rawError}}"}""");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DiscoverAsync(default));

        Assert.Equal("Multiple Codex packages are installed.", exception.Message);
        Assert.DoesNotContain(rawError, exception.ToString(), StringComparison.Ordinal);
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
    public async Task Close_targets_only_installed_chatgpt_and_valid_installed_descendants()
    {
        var accessor = new FakeCodexProcessAccessor(
        [
            Process(100, 1, 100, "app", "ChatGPT.exe"),
            Process(101, 100, 101, "app", "resources", "codex.exe"),
            Process(102, 101, 102, "app", "resources", "helper.exe"),
            Process(200, 100, 103, @"C:\Other\escaped-child.exe"),
            Process(300, 1, 104, "app", "unrelated.exe"),
            Process(400, 1, 105, @"C:\Other\ChatGPT.exe"),
            Process(500, 1, 106, InstallLocation + @".attacker\ChatGPT.exe"),
        ]);
        accessor.ExitResults[101] = false;
        var controller = Controller(accessor);

        var result = await controller.CloseAsync(Package(), TimeSpan.FromSeconds(30), default);

        Assert.False(result.AllExited);
        Assert.Equal([101], result.RemainingProcessIds);
        Assert.Equal([100, 101, 102], accessor.ClosedProcessIds);
        Assert.Equal([100, 101, 102], accessor.WaitedProcessIds);
        Assert.All(accessor.WaitTimeouts, timeout => Assert.Equal(TimeSpan.FromSeconds(8), timeout));
        Assert.True(accessor.Events.IndexOf("close:102") < accessor.Events.IndexOf("wait:100"));
    }

    [Fact]
    public async Task Close_rejects_child_when_reused_parent_is_newer_than_child()
    {
        var accessor = new FakeCodexProcessAccessor(
        [
            Process(100, 1, 200, "app", "ChatGPT.exe"),
            Process(101, 100, 100, "app", "resources", "codex.exe"),
        ]);
        var controller = Controller(accessor);

        await controller.CloseAsync(Package(), TimeSpan.FromSeconds(8), default);

        Assert.Equal([100], accessor.ClosedProcessIds);
        Assert.Equal([100], accessor.WaitedProcessIds);
    }

    [Fact]
    public async Task Close_does_not_touch_reused_pid()
    {
        var original = Process(100, 1, 100, "app", "ChatGPT.exe");
        var accessor = new FakeCodexProcessAccessor([original]);
        accessor.CurrentIdentities[100] = original.Identity with { ExecutablePath = @"C:\Other\ChatGPT.exe" };
        var controller = Controller(accessor);

        var result = await controller.CloseAsync(Package(), TimeSpan.FromSeconds(8), default);

        Assert.True(result.AllExited);
        Assert.Empty(accessor.ClosedProcessIds);
        Assert.Empty(result.RemainingProcessIds);
    }

    [Fact]
    public async Task Close_does_not_issue_target_when_pid_is_reused_before_wait()
    {
        var original = Process(100, 1, 100, "app", "ChatGPT.exe");
        var accessor = new FakeCodexProcessAccessor([original]);
        accessor.OnClose = identity =>
            accessor.CurrentIdentities[identity.Id] = identity with { CreationTimeUtcTicks = 200 };
        var controller = Controller(accessor);

        var result = await controller.CloseAsync(Package(), TimeSpan.FromSeconds(8), default);

        Assert.True(result.AllExited);
        Assert.Equal([100], accessor.ClosedProcessIds);
        Assert.Empty(result.RemainingProcessIds);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.ForceTerminateAsync([100], default));
    }

    [Fact]
    public async Task Close_propagates_programming_failures()
    {
        var accessor = new FakeCodexProcessAccessor([Process(100, 1, 100, "app", "ChatGPT.exe")])
        {
            CloseException = new InvalidOperationException("programming failure"),
        };
        var controller = Controller(accessor);

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
        var controller = Controller(accessor);

        var exception = await Assert.ThrowsAsync<CodexCloseCanceledException>(() =>
            controller.CloseAsync(Package(), TimeSpan.FromSeconds(8), cancellationSource.Token));

        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
        Assert.False(exception.SideEffectsStarted);
        Assert.Equal(0, accessor.EnumerationCount);
    }

    [Fact]
    public async Task Close_cancellation_before_first_close_action_reports_no_side_effect()
    {
        using var cancellationSource = new CancellationTokenSource();
        var accessor = new FakeCodexProcessAccessor(
            [Process(100, 1, 100, "app", "ChatGPT.exe")])
        {
            OnGetProcesses = cancellationSource.Cancel,
        };
        var controller = Controller(accessor);

        var exception = await Assert.ThrowsAsync<CodexCloseCanceledException>(() =>
            controller.CloseAsync(Package(), TimeSpan.FromSeconds(8), cancellationSource.Token));

        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
        Assert.False(exception.SideEffectsStarted);
        Assert.Empty(accessor.ClosedProcessIds);
    }

    [Fact]
    public async Task Close_cancellation_after_close_action_reports_side_effect_started()
    {
        using var cancellationSource = new CancellationTokenSource();
        var accessor = new FakeCodexProcessAccessor(
            [Process(100, 1, 100, "app", "ChatGPT.exe")])
        {
            OnClose = _ => cancellationSource.Cancel(),
        };
        var controller = Controller(accessor);

        var exception = await Assert.ThrowsAsync<CodexCloseCanceledException>(() =>
            controller.CloseAsync(Package(), TimeSpan.FromSeconds(8), cancellationSource.Token));

        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
        Assert.True(exception.SideEffectsStarted);
        Assert.Equal([100], accessor.ClosedProcessIds);
    }

    [Fact]
    public async Task Force_terminate_rejects_unissued_process_id()
    {
        var accessor = new FakeCodexProcessAccessor([]);
        var controller = Controller(accessor);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.ForceTerminateAsync([999], default));

        Assert.Equal("Force termination target was not issued by the latest close operation.", exception.Message);
        Assert.Empty(accessor.KilledProcessIds);
    }

    [Fact]
    public async Task Force_terminate_validates_all_ids_before_killing_any_target()
    {
        var accessor = new FakeCodexProcessAccessor(
        [
            Process(100, 1, 100, "app", "ChatGPT.exe"),
            Process(101, 100, 101, "app", "resources", "codex.exe"),
        ]);
        accessor.ExitResults[100] = false;
        accessor.ExitResults[101] = false;
        var controller = Controller(accessor);
        await controller.CloseAsync(Package(), TimeSpan.FromSeconds(8), default);
        var enumerationCountAfterClose = accessor.EnumerationCount;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.ForceTerminateAsync([100, 999], default));

        Assert.Empty(accessor.KilledProcessIds);
        Assert.Equal(enumerationCountAfterClose, accessor.EnumerationCount);
        await controller.ForceTerminateAsync([100, 101], default);
        Assert.Equal([100, 101], accessor.KilledProcessIds);
    }

    [Fact]
    public async Task Force_terminate_includes_new_in_package_descendants()
    {
        var root = Process(100, 1, 100, "app", "ChatGPT.exe");
        var accessor = new FakeCodexProcessAccessor([root]);
        accessor.ExitResults[100] = false;
        var controller = Controller(accessor);
        await controller.CloseAsync(Package(), TimeSpan.FromSeconds(8), default);
        accessor.SetProcesses(
        [
            root,
            Process(101, 100, 101, "app", "resources", "new-child.exe"),
        ]);

        await controller.ForceTerminateAsync([100], default);

        Assert.Equal([100, 101], accessor.KilledProcessIds);
        Assert.Equal(2, accessor.EnumerationCount);
    }

    [Fact]
    public async Task Force_terminate_excludes_outside_stale_and_ambiguous_descendants()
    {
        var root = Process(100, 1, 100, "app", "ChatGPT.exe");
        var accessor = new FakeCodexProcessAccessor([root]);
        accessor.ExitResults[100] = false;
        var controller = Controller(accessor);
        await controller.CloseAsync(Package(), TimeSpan.FromSeconds(8), default);
        accessor.SetProcesses(
        [
            root,
            Process(101, 100, 101, "app", "resources", "valid-child.exe"),
            Process(102, 100, 102, @"C:\Other\outside-child.exe"),
            Process(103, 100, 99, "app", "resources", "stale-parent-child.exe"),
            Process(104, 100, 104, "app", "resources", "ambiguous-a.exe"),
            Process(104, 100, 105, "app", "resources", "ambiguous-b.exe"),
            Process(105, 999, 105, "app", "resources", "unchained-child.exe"),
        ]);

        await controller.ForceTerminateAsync([100], default);

        Assert.Equal([100, 101], accessor.KilledProcessIds);
    }

    [Fact]
    public async Task Force_terminate_revalidates_identity_and_consumes_reused_target()
    {
        var original = Process(100, 1, 100, "app", "ChatGPT.exe");
        var accessor = new FakeCodexProcessAccessor([original]);
        accessor.ExitResults[100] = false;
        var controller = Controller(accessor);
        await controller.CloseAsync(Package(), TimeSpan.FromSeconds(8), default);
        accessor.SetProcesses(
        [
            new CodexProcessEntry(
                original.Identity with { CreationTimeUtcTicks = 200 },
                original.ParentProcessId),
            Process(101, 100, 201, "app", "resources", "reused-root-child.exe"),
        ]);

        await controller.ForceTerminateAsync([100], default);

        Assert.Empty(accessor.KilledProcessIds);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.ForceTerminateAsync([100], default));
    }

    [Fact]
    public async Task Later_close_replaces_force_authority_from_previous_close()
    {
        var accessor = new FakeCodexProcessAccessor([Process(100, 1, 100, "app", "ChatGPT.exe")]);
        accessor.ExitResults[100] = false;
        var controller = Controller(accessor);
        await controller.CloseAsync(Package(), TimeSpan.FromSeconds(8), default);
        accessor.ExitResults[100] = true;

        await controller.CloseAsync(Package(), TimeSpan.FromSeconds(8), default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.ForceTerminateAsync([100], default));
        Assert.Empty(accessor.KilledProcessIds);
    }

    [Fact]
    public async Task Force_terminate_kills_each_issued_process_tree()
    {
        var accessor = new FakeCodexProcessAccessor(
        [
            Process(100, 1, 100, "app", "ChatGPT.exe"),
            Process(101, 100, 101, "app", "resources", "codex.exe"),
        ]);
        accessor.ExitResults[100] = false;
        accessor.ExitResults[101] = false;
        var controller = Controller(accessor);
        await controller.CloseAsync(Package(), TimeSpan.FromSeconds(8), default);

        await controller.ForceTerminateAsync([101, 100], default);

        Assert.Equal([100, 101], accessor.KilledProcessIds);
        Assert.All(accessor.KillEntireTreeValues, Assert.True);
    }

    [Fact]
    public async Task Launch_uses_exact_apps_folder_request()
    {
        var launcher = new FakeAppsFolderLauncher();
        var controller = Controller(new FakeCodexProcessAccessor([]), launcher);

        await controller.LaunchAsync(Package(), default);

        Assert.Equal("explorer.exe", launcher.LastRequest!.FileName);
        Assert.Equal(["shell:AppsFolder\\OpenAI.Codex_2p2nqsd0c76g0!App"], launcher.LastRequest.Arguments);
        Assert.True(launcher.LastRequest.UseShellExecute);
        Assert.Equal(1, launcher.StartCallCount);
    }

    [Fact]
    public async Task Pre_cancelled_launch_does_not_start_explorer()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var launcher = new FakeAppsFolderLauncher();
        var controller = Controller(new FakeCodexProcessAccessor([]), launcher);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.LaunchAsync(Package(), cancellationSource.Token));

        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
        Assert.Equal(0, launcher.StartCallCount);
    }

    [Fact]
    public async Task Cancellation_after_activation_start_is_non_destructive()
    {
        using var cancellationSource = new CancellationTokenSource();
        var launcher = new FakeAppsFolderLauncher
        {
            OnStart = cancellationSource.Cancel,
        };
        var controller = Controller(new FakeCodexProcessAccessor([]), launcher);

        await controller.LaunchAsync(Package(), cancellationSource.Token);

        Assert.Equal(1, launcher.StartCallCount);
        Assert.True(cancellationSource.IsCancellationRequested);
    }

    [Fact]
    public async Task Launch_reports_expected_start_failure_with_fixed_message()
    {
        var launcher = new FakeAppsFolderLauncher { StartResult = false };
        var controller = Controller(new FakeCodexProcessAccessor([]), launcher);

        var exception = await Assert.ThrowsAsync<CodexLaunchException>(() =>
            controller.LaunchAsync(Package(), default));

        Assert.Equal("Codex launch failed.", exception.Message);
    }

    private static CodexProcessController Controller(
        FakeCodexProcessAccessor accessor,
        FakeAppsFolderLauncher? launcher = null) =>
        new(accessor, launcher ?? new FakeAppsFolderLauncher());

    private static CodexPackageInfo Package() => new(
        "OpenAI.Codex_2p2nqsd0c76g0",
        "OpenAI.Codex_2p2nqsd0c76g0!App",
        InstallLocation,
        Path.Combine(InstallLocation, "app", "ChatGPT.exe"),
        Path.Combine(InstallLocation, "app", "resources"));

    private static CodexProcessEntry Process(
        int id,
        int parentId,
        long creationTimeUtcTicks,
        params string[] pathParts)
    {
        var executablePath = pathParts.Length == 1 && Path.IsPathFullyQualified(pathParts[0])
            ? pathParts[0]
            : Path.Combine([InstallLocation, .. pathParts]);
        return new CodexProcessEntry(
            new CodexProcessIdentity(id, executablePath, creationTimeUtcTicks),
            parentId);
    }

    private sealed class FakeCodexProcessAccessor : ICodexProcessAccessor
    {
        private IReadOnlyList<CodexProcessEntry> _processes;

        public FakeCodexProcessAccessor(IReadOnlyList<CodexProcessEntry> processes)
        {
            _processes = processes;
            CurrentIdentities = processes.ToDictionary(process => process.Identity.Id, process => process.Identity);
        }

        public List<int> ClosedProcessIds { get; } = [];

        public InvalidOperationException? CloseException { get; init; }

        public Dictionary<int, CodexProcessIdentity> CurrentIdentities { get; }

        public int EnumerationCount { get; private set; }

        public Dictionary<int, bool> ExitResults { get; } = [];

        public List<string> Events { get; } = [];

        public List<bool> KillEntireTreeValues { get; } = [];

        public List<int> KilledProcessIds { get; } = [];

        public Action<CodexProcessIdentity>? OnClose { get; set; }

        public Action? OnGetProcesses { get; init; }

        public List<int> WaitedProcessIds { get; } = [];

        public List<TimeSpan> WaitTimeouts { get; } = [];

        public IReadOnlyList<CodexProcessEntry> GetProcesses()
        {
            EnumerationCount++;
            OnGetProcesses?.Invoke();
            return _processes;
        }

        public void SetProcesses(IReadOnlyList<CodexProcessEntry> processes)
        {
            _processes = processes;
            CurrentIdentities.Clear();
            foreach (var process in processes)
            {
                CurrentIdentities[process.Identity.Id] = process.Identity;
            }
        }

        public bool CloseMainWindow(CodexProcessIdentity expectedIdentity)
        {
            if (CloseException is not null)
            {
                throw CloseException;
            }

            if (!Matches(expectedIdentity))
            {
                return false;
            }

            ClosedProcessIds.Add(expectedIdentity.Id);
            Events.Add($"close:{expectedIdentity.Id}");
            OnClose?.Invoke(expectedIdentity);
            return true;
        }

        public Task<bool> WaitForExitAsync(
            CodexProcessIdentity expectedIdentity,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitedProcessIds.Add(expectedIdentity.Id);
            WaitTimeouts.Add(timeout);
            Events.Add($"wait:{expectedIdentity.Id}");
            if (!Matches(expectedIdentity))
            {
                return Task.FromResult(true);
            }

            return Task.FromResult(
                !ExitResults.TryGetValue(expectedIdentity.Id, out var exited) || exited);
        }

        public bool Kill(CodexProcessIdentity expectedIdentity, bool entireProcessTree)
        {
            if (!Matches(expectedIdentity))
            {
                return false;
            }

            KilledProcessIds.Add(expectedIdentity.Id);
            KillEntireTreeValues.Add(entireProcessTree);
            return true;
        }

        private bool Matches(CodexProcessIdentity expectedIdentity) =>
            CurrentIdentities.TryGetValue(expectedIdentity.Id, out var currentIdentity) &&
            currentIdentity == expectedIdentity;
    }

    private sealed class FakeAppsFolderLauncher : IAppsFolderLauncher
    {
        public AppsFolderLaunchRequest? LastRequest { get; private set; }

        public Action? OnStart { get; init; }

        public int StartCallCount { get; private set; }

        public bool StartResult { get; init; } = true;

        public bool Start(AppsFolderLaunchRequest request, CancellationToken cancellationToken)
        {
            StartCallCount++;
            LastRequest = request;
            OnStart?.Invoke();
            return StartResult;
        }
    }
}

public sealed class SystemCodexProcessAccessorTests
{
    private static readonly CodexProcessIdentity ExpectedIdentity = new(
        100,
        @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0.0.0_x64__family\app\ChatGPT.exe",
        1234);

    [Fact]
    public void Close_uses_one_retained_handle_for_identity_action_and_disposal()
    {
        var nativeApi = new FakeWindowsProcessApi(ExpectedIdentity);
        var accessor = new SystemCodexProcessAccessor(nativeApi);

        var closed = accessor.CloseMainWindow(ExpectedIdentity);

        Assert.True(closed);
        Assert.Equal(1, nativeApi.OpenCount);
        Assert.Equal(["open", "identity", "close"], nativeApi.Events);
        Assert.True(nativeApi.OpenedHandle!.IsClosed);
    }

    [Fact]
    public async Task Wait_uses_one_retained_handle_for_identity_action_and_disposal()
    {
        var nativeApi = new FakeWindowsProcessApi(ExpectedIdentity) { WaitResult = true };
        var accessor = new SystemCodexProcessAccessor(nativeApi);

        var exited = await accessor.WaitForExitAsync(ExpectedIdentity, TimeSpan.FromSeconds(1), default);

        Assert.True(exited);
        Assert.Equal(1, nativeApi.OpenCount);
        Assert.Equal(["open", "identity", "wait"], nativeApi.Events);
        Assert.True(nativeApi.OpenedHandle!.IsClosed);
    }

    [Fact]
    public async Task Wait_timeout_revalidates_identity_through_the_same_retained_handle()
    {
        var nativeApi = new FakeWindowsProcessApi(ExpectedIdentity) { WaitResult = false };
        var accessor = new SystemCodexProcessAccessor(nativeApi);

        var exited = await accessor.WaitForExitAsync(ExpectedIdentity, TimeSpan.FromSeconds(1), default);

        Assert.False(exited);
        Assert.Equal(1, nativeApi.OpenCount);
        Assert.Equal(["open", "identity", "wait", "identity"], nativeApi.Events);
        Assert.True(nativeApi.OpenedHandle!.IsClosed);
    }

    [Fact]
    public void Kill_uses_one_retained_handle_for_identity_action_and_disposal()
    {
        var nativeApi = new FakeWindowsProcessApi(ExpectedIdentity);
        var accessor = new SystemCodexProcessAccessor(nativeApi);

        var killed = accessor.Kill(ExpectedIdentity, entireProcessTree: true);

        Assert.True(killed);
        Assert.Equal(1, nativeApi.OpenCount);
        Assert.Equal(["open", "identity", "terminate"], nativeApi.Events);
        Assert.True(nativeApi.OpenedHandle!.IsClosed);
    }

    [Fact]
    public void Identity_mismatch_disposes_retained_handle_without_action()
    {
        var reusedIdentity = ExpectedIdentity with { CreationTimeUtcTicks = 5678 };
        var nativeApi = new FakeWindowsProcessApi(reusedIdentity);
        var accessor = new SystemCodexProcessAccessor(nativeApi);

        var closed = accessor.CloseMainWindow(ExpectedIdentity);

        Assert.False(closed);
        Assert.Equal(1, nativeApi.OpenCount);
        Assert.Equal(["open", "identity"], nativeApi.Events);
        Assert.True(nativeApi.OpenedHandle!.IsClosed);
    }

    [Fact]
    public void Action_failure_still_disposes_retained_handle()
    {
        var nativeApi = new FakeWindowsProcessApi(ExpectedIdentity) { CloseResult = false };
        var accessor = new SystemCodexProcessAccessor(nativeApi);

        var closed = accessor.CloseMainWindow(ExpectedIdentity);

        Assert.False(closed);
        Assert.Equal(["open", "identity", "close"], nativeApi.Events);
        Assert.True(nativeApi.OpenedHandle!.IsClosed);
    }

    private sealed class FakeWindowsProcessApi(CodexProcessIdentity identity) : IWindowsProcessApi
    {
        public bool CloseResult { get; init; } = true;

        public List<string> Events { get; } = [];

        public int OpenCount { get; private set; }

        public SafeProcessHandle? OpenedHandle { get; private set; }

        public bool TerminateResult { get; init; } = true;

        public bool WaitResult { get; init; }

        public SafeProcessHandle? TryOpenProcess(int processId)
        {
            Assert.Equal(ExpectedIdentity.Id, processId);
            OpenCount++;
            Events.Add("open");
            OpenedHandle = new SafeProcessHandle(new IntPtr(1234), ownsHandle: false);
            return OpenedHandle;
        }

        public bool TryGetIdentity(
            SafeProcessHandle processHandle,
            int processId,
            out CodexProcessIdentity processIdentity)
        {
            AssertOpenHandle(processHandle);
            Assert.Equal(ExpectedIdentity.Id, processId);
            Events.Add("identity");
            processIdentity = identity;
            return true;
        }

        public bool CloseMainWindows(SafeProcessHandle processHandle, int processId)
        {
            AssertOpenHandle(processHandle);
            Assert.Equal(ExpectedIdentity.Id, processId);
            Events.Add("close");
            return CloseResult;
        }

        public Task<bool> WaitForExitAsync(
            SafeProcessHandle processHandle,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            AssertOpenHandle(processHandle);
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("wait");
            return Task.FromResult(WaitResult);
        }

        public bool TerminateProcess(SafeProcessHandle processHandle)
        {
            AssertOpenHandle(processHandle);
            Events.Add("terminate");
            return TerminateResult;
        }

        private void AssertOpenHandle(SafeProcessHandle processHandle)
        {
            Assert.Same(OpenedHandle, processHandle);
            Assert.False(processHandle.IsClosed);
        }
    }
}
