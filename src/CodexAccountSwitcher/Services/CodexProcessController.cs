using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.Services;

public sealed record CloseResult(bool AllExited, IReadOnlyList<int> RemainingProcessIds)
{
    public bool SideEffectsStarted { get; init; }
}

public sealed class CodexCloseCanceledException : OperationCanceledException
{
    public CodexCloseCanceledException(bool sideEffectsStarted, CancellationToken cancellationToken)
        : this(sideEffectsStarted, cancellationToken, null)
    {
    }

    internal CodexCloseCanceledException(
        bool sideEffectsStarted,
        CancellationToken cancellationToken,
        OperationCanceledException? innerException)
        : base("Codex close was canceled.", innerException, cancellationToken) =>
        SideEffectsStarted = sideEffectsStarted;

    public bool SideEffectsStarted { get; }
}

public sealed class CodexForceTerminateCanceledException : OperationCanceledException
{
    public CodexForceTerminateCanceledException(
        bool sideEffectsStarted,
        CancellationToken cancellationToken)
        : this(sideEffectsStarted, cancellationToken, null)
    {
    }

    internal CodexForceTerminateCanceledException(
        bool sideEffectsStarted,
        CancellationToken cancellationToken,
        OperationCanceledException? innerException)
        : base("Codex force termination was canceled.", innerException, cancellationToken) =>
        SideEffectsStarted = sideEffectsStarted;

    public bool SideEffectsStarted { get; }
}

public sealed class CodexLaunchException()
    : InvalidOperationException("Codex launch failed.");

public sealed class CodexProcessDiscoveryException()
    : IOException("Codex process state could not be verified.");

public interface ICodexProcessController
{
    Task<CloseResult> CloseAsync(
        CodexPackageInfo package,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task ForceTerminateAsync(IReadOnlyList<int> processIds, CancellationToken cancellationToken);

    Task LaunchAsync(CodexPackageInfo package, CancellationToken cancellationToken);
}

internal sealed record CodexProcessIdentity(
    int Id,
    string ExecutablePath,
    long CreationTimeUtcTicks);

internal sealed record CodexProcessEntry(CodexProcessIdentity Identity, int ParentProcessId);

internal sealed record CodexProcessSnapshot(
    IReadOnlyList<CodexProcessEntry> Processes,
    IReadOnlyList<int> UnresolvedChatGptProcessIds);

internal sealed record RawProcessEntry(int Id, int ParentProcessId, string ExecutableFile);

internal interface ICodexProcessAccessor
{
    IReadOnlyList<CodexProcessEntry> GetProcesses();

    CodexProcessSnapshot GetProcessSnapshot() => new(GetProcesses(), []);

    bool CloseMainWindow(CodexProcessIdentity expectedIdentity);

    Task<bool> WaitForExitAsync(
        CodexProcessIdentity expectedIdentity,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    bool Kill(CodexProcessIdentity expectedIdentity, bool entireProcessTree);
}

internal sealed record AppsFolderLaunchRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    bool UseShellExecute);

internal interface IAppsFolderLauncher
{
    bool Start(AppsFolderLaunchRequest request, CancellationToken cancellationToken);
}

internal interface IWindowsProcessApi
{
    SafeProcessHandle? TryOpenProcessForQuery(int processId);

    SafeProcessHandle? TryOpenProcessForTermination(int processId);

    bool TryGetIdentity(
        SafeProcessHandle processHandle,
        int processId,
        out CodexProcessIdentity processIdentity);

    bool CloseMainWindows(SafeProcessHandle processHandle, int processId);

    Task<bool> WaitForExitAsync(
        SafeProcessHandle processHandle,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    bool TerminateProcess(SafeProcessHandle processHandle);
}

public sealed class CodexProcessController : ICodexProcessController
{
    private const int MaximumForceTerminationWaves = 16;
    private static readonly TimeSpan StableZeroObservationInterval = TimeSpan.FromMilliseconds(25);
    private const string ForceTerminationTimeoutMessage =
        "Codex processes did not exit after force termination.";
    private const string UntrustedForceTargetMessage =
        "Force termination target was not issued by the latest close operation.";
    private static readonly TimeSpan MaximumCloseTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MaximumForceTerminationTimeout = TimeSpan.FromSeconds(8);
    private readonly IAppsFolderLauncher _appsFolderLauncher;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Dictionary<int, CodexProcessIdentity> _issuedRemainingTargets = [];
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ICodexProcessAccessor _processAccessor;
    private string? _issuedInstallLocation;

    public CodexProcessController()
        : this(new SystemCodexProcessAccessor(), new SystemAppsFolderLauncher(), Task.Delay)
    {
    }

    internal CodexProcessController(
        ICodexProcessAccessor processAccessor,
        IAppsFolderLauncher appsFolderLauncher,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _processAccessor = processAccessor ?? throw new ArgumentNullException(nameof(processAccessor));
        _appsFolderLauncher = appsFolderLauncher ?? throw new ArgumentNullException(nameof(appsFolderLauncher));
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public async Task<CloseResult> CloseAsync(
        CodexPackageInfo package,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var gateEntered = false;
        var sideEffectsStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _lifecycleGate.WaitAsync(cancellationToken);
            gateEntered = true;
            _issuedRemainingTargets.Clear();
            _issuedInstallLocation = null;
            var targets = SelectTargetProcesses(package, GetVerifiedProcesses());
            foreach (var process in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sideEffectsStarted |= _processAccessor.CloseMainWindow(process.Identity);
            }

            var effectiveTimeout = timeout <= MaximumCloseTimeout ? timeout : MaximumCloseTimeout;
            var exitTasks = targets
                .Select(process => _processAccessor.WaitForExitAsync(
                    process.Identity,
                    effectiveTimeout,
                    cancellationToken))
                .ToArray();
            var exitResults = await Task.WhenAll(exitTasks);
            var currentProcesses = GetVerifiedProcesses();
            cancellationToken.ThrowIfCancellationRequested();
            var remainingTargets = SelectTargetProcesses(package, currentProcesses)
                .Where(process => !WasObservedExited(process.Identity, targets, exitResults))
                .Select(process => process.Identity)
                .ToArray();

            if (remainingTargets.Length == 0)
            {
                await _delayAsync(StableZeroObservationInterval, cancellationToken);
                currentProcesses = GetVerifiedProcesses();
                cancellationToken.ThrowIfCancellationRequested();
                remainingTargets = SelectTargetProcesses(package, currentProcesses)
                    .Where(process => !WasObservedExited(process.Identity, targets, exitResults))
                    .Select(process => process.Identity)
                    .ToArray();
            }

            foreach (var identity in remainingTargets)
            {
                _issuedRemainingTargets.Add(identity.Id, identity);
            }

            if (remainingTargets.Length > 0)
            {
                _issuedInstallLocation = Path.GetFullPath(package.InstallLocation);
            }

            return new CloseResult(
                remainingTargets.Length == 0,
                remainingTargets.Select(identity => identity.Id).ToArray())
            {
                SideEffectsStarted = sideEffectsStarted,
            };
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new CodexCloseCanceledException(
                sideEffectsStarted,
                cancellationToken,
                exception);
        }
        finally
        {
            if (gateEntered)
            {
                _lifecycleGate.Release();
            }
        }
    }

    public async Task ForceTerminateAsync(
        IReadOnlyList<int> processIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        var gateEntered = false;
        var sideEffectsStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _lifecycleGate.WaitAsync(cancellationToken);
            gateEntered = true;
            var requestedTargets = new List<CodexProcessIdentity>(processIds.Count);
            var requestedProcessIds = new HashSet<int>();
            foreach (var processId in processIds)
            {
                if (!requestedProcessIds.Add(processId) ||
                    !_issuedRemainingTargets.TryGetValue(processId, out var identity) ||
                    _issuedInstallLocation is null)
                {
                    throw new InvalidOperationException(UntrustedForceTargetMessage);
                }

                requestedTargets.Add(identity);
            }

            if (requestedTargets.Count == 0)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var installLocation = _issuedInstallLocation!;
            var currentProcesses = GetVerifiedProcesses();
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var identity in requestedTargets)
            {
                _issuedRemainingTargets.Remove(identity.Id);
            }

            if (_issuedRemainingTargets.Count == 0)
            {
                _issuedInstallLocation = null;
            }

            var authorizedTargets = requestedTargets.ToDictionary(identity => identity.Id);
            for (var wave = 0; wave < MaximumForceTerminationWaves; wave++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfBlockedPackageCandidatesRemain(
                    authorizedTargets.Values.ToArray(),
                    currentProcesses,
                    installLocation);
                var terminationTargets = SelectPackageWideTerminationTargets(
                    authorizedTargets.Values.ToArray(),
                    currentProcesses,
                    installLocation);
                foreach (var identity in terminationTargets)
                {
                    authorizedTargets[identity.Id] = identity;
                }

                if (terminationTargets.Count == 0)
                {
                    await _delayAsync(StableZeroObservationInterval, cancellationToken);
                    currentProcesses = GetVerifiedProcesses();
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfBlockedPackageCandidatesRemain(
                        authorizedTargets.Values.ToArray(),
                        currentProcesses,
                        installLocation);
                    terminationTargets = SelectPackageWideTerminationTargets(
                        authorizedTargets.Values.ToArray(),
                        currentProcesses,
                        installLocation);
                    if (terminationTargets.Count == 0)
                    {
                        return;
                    }
                }

                foreach (var identity in terminationTargets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sideEffectsStarted |= _processAccessor.Kill(identity, entireProcessTree: true);
                }

                var exitResults = await Task.WhenAll(terminationTargets.Select(identity =>
                    _processAccessor.WaitForExitAsync(
                        identity,
                        MaximumForceTerminationTimeout,
                        cancellationToken)));
                cancellationToken.ThrowIfCancellationRequested();

                currentProcesses = GetVerifiedProcesses();
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfBlockedPackageCandidatesRemain(
                    authorizedTargets.Values.ToArray(),
                    currentProcesses,
                    installLocation);
                var remainingTargets = SelectPackageWideTerminationTargets(
                    authorizedTargets.Values.ToArray(),
                    currentProcesses,
                    installLocation);
                foreach (var identity in remainingTargets)
                {
                    authorizedTargets[identity.Id] = identity;
                }

                if (remainingTargets.Count == 0)
                {
                    await _delayAsync(StableZeroObservationInterval, cancellationToken);
                    currentProcesses = GetVerifiedProcesses();
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfBlockedPackageCandidatesRemain(
                        authorizedTargets.Values.ToArray(),
                        currentProcesses,
                        installLocation);
                    remainingTargets = SelectPackageWideTerminationTargets(
                        authorizedTargets.Values.ToArray(),
                        currentProcesses,
                        installLocation);
                    foreach (var identity in remainingTargets)
                    {
                        authorizedTargets[identity.Id] = identity;
                    }

                    if (remainingTargets.Count == 0)
                    {
                        return;
                    }
                }

                var timedOutIdentities = terminationTargets
                    .Where((_, index) => !exitResults[index])
                    .ToArray();
                if (remainingTargets.Any(remaining =>
                        timedOutIdentities.Any(timedOut => IdentitiesMatch(remaining, timedOut))))
                {
                    throw new IOException(ForceTerminationTimeoutMessage);
                }
            }

            throw new IOException(ForceTerminationTimeoutMessage);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new CodexForceTerminateCanceledException(
                sideEffectsStarted,
                cancellationToken,
                exception);
        }
        finally
        {
            if (gateEntered)
            {
                _lifecycleGate.Release();
            }
        }
    }

    public Task LaunchAsync(CodexPackageInfo package, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();
        var started = _appsFolderLauncher.Start(
            new AppsFolderLaunchRequest(
                "explorer.exe",
                [$"shell:AppsFolder\\{package.AppUserModelId}"],
                UseShellExecute: true),
            cancellationToken);

        if (!started)
        {
            throw new CodexLaunchException();
        }

        return Task.CompletedTask;
    }

    private IReadOnlyList<CodexProcessEntry> GetVerifiedProcesses()
    {
        var snapshot = _processAccessor.GetProcessSnapshot();
        if (snapshot.UnresolvedChatGptProcessIds.Count > 0)
        {
            throw new CodexProcessDiscoveryException();
        }

        return snapshot.Processes;
    }

    private static IReadOnlyList<CodexProcessEntry> SelectTargetProcesses(
        CodexPackageInfo package,
        IReadOnlyList<CodexProcessEntry> processes)
    {
        if (!Path.IsPathFullyQualified(package.InstallLocation))
        {
            throw new ArgumentException("The install location must be fully qualified.", nameof(package));
        }

        return SelectTargetProcesses(package.InstallLocation, processes);
    }

    private static IReadOnlyList<CodexProcessEntry> SelectTargetProcesses(
        string installLocation,
        IReadOnlyList<CodexProcessEntry> processes)
    {
        if (!Path.IsPathFullyQualified(installLocation))
        {
            throw new ArgumentException("The install location must be fully qualified.", nameof(installLocation));
        }

        var selectedIdentities = new Dictionary<int, CodexProcessIdentity>();
        foreach (var process in processes)
        {
            if (IsInsideInstallLocation(process.Identity.ExecutablePath, installLocation) &&
                string.Equals(
                    Path.GetFileName(process.Identity.ExecutablePath),
                    "ChatGPT.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                selectedIdentities.TryAdd(process.Identity.Id, process.Identity);
            }
        }

        var addedProcess = true;
        while (addedProcess)
        {
            addedProcess = false;
            foreach (var process in processes)
            {
                if (selectedIdentities.ContainsKey(process.Identity.Id) ||
                    !selectedIdentities.TryGetValue(process.ParentProcessId, out var parentIdentity) ||
                    parentIdentity.CreationTimeUtcTicks > process.Identity.CreationTimeUtcTicks ||
                    !IsInsideInstallLocation(process.Identity.ExecutablePath, installLocation))
                {
                    continue;
                }

                selectedIdentities.Add(process.Identity.Id, process.Identity);
                addedProcess = true;
            }
        }

        return processes
            .Where(process =>
                selectedIdentities.TryGetValue(process.Identity.Id, out var selectedIdentity) &&
                selectedIdentity == process.Identity)
            .ToArray();
    }

    private static IReadOnlyList<CodexProcessIdentity> SelectForceTerminationTargets(
        IReadOnlyList<CodexProcessIdentity> trustedRoots,
        IReadOnlyList<CodexProcessEntry> processes,
        string installLocation)
    {
        var unambiguousEntries = new Dictionary<int, CodexProcessEntry>();
        var ambiguousProcessIds = new HashSet<int>();
        foreach (var process in processes)
        {
            if (ambiguousProcessIds.Contains(process.Identity.Id))
            {
                continue;
            }

            if (!unambiguousEntries.TryAdd(process.Identity.Id, process))
            {
                unambiguousEntries.Remove(process.Identity.Id);
                ambiguousProcessIds.Add(process.Identity.Id);
            }
        }

        var selectedIdentities = new Dictionary<int, CodexProcessIdentity>();
        var selectedTargets = new List<CodexProcessIdentity>();
        foreach (var trustedRoot in trustedRoots)
        {
            if (ambiguousProcessIds.Contains(trustedRoot.Id) ||
                !IsInsideInstallLocation(trustedRoot.ExecutablePath, installLocation))
            {
                continue;
            }

            if (!unambiguousEntries.TryGetValue(trustedRoot.Id, out var currentRoot))
            {
                selectedIdentities.Add(trustedRoot.Id, trustedRoot);
                continue;
            }

            if (IdentitiesMatch(currentRoot.Identity, trustedRoot) &&
                IsInsideInstallLocation(currentRoot.Identity.ExecutablePath, installLocation))
            {
                selectedIdentities.Add(currentRoot.Identity.Id, currentRoot.Identity);
                selectedTargets.Add(currentRoot.Identity);
            }
        }

        var currentCandidates = processes
            .Where(process =>
                unambiguousEntries.TryGetValue(process.Identity.Id, out var unambiguousEntry) &&
                unambiguousEntry == process)
            .ToArray();
        var addedProcess = true;
        while (addedProcess)
        {
            addedProcess = false;
            foreach (var process in currentCandidates)
            {
                if (selectedIdentities.ContainsKey(process.Identity.Id) ||
                    !selectedIdentities.TryGetValue(process.ParentProcessId, out var parentIdentity) ||
                    parentIdentity.CreationTimeUtcTicks > process.Identity.CreationTimeUtcTicks ||
                    !IsInsideInstallLocation(process.Identity.ExecutablePath, installLocation))
                {
                    continue;
                }

                selectedIdentities.Add(process.Identity.Id, process.Identity);
                selectedTargets.Add(process.Identity);
                addedProcess = true;
            }
        }

        var depthByProcessId = new Dictionary<int, int>();
        var resolvingProcessIds = new HashSet<int>();
        int GetDepth(CodexProcessIdentity identity)
        {
            if (depthByProcessId.TryGetValue(identity.Id, out var knownDepth))
            {
                return knownDepth;
            }

            if (!resolvingProcessIds.Add(identity.Id))
            {
                return 0;
            }

            var depth = 0;
            if (unambiguousEntries.TryGetValue(identity.Id, out var process) &&
                selectedIdentities.TryGetValue(process.ParentProcessId, out var parentIdentity) &&
                parentIdentity.CreationTimeUtcTicks <= identity.CreationTimeUtcTicks)
            {
                depth = GetDepth(parentIdentity) + 1;
            }

            resolvingProcessIds.Remove(identity.Id);
            depthByProcessId[identity.Id] = depth;
            return depth;
        }

        return selectedTargets
            .Select((identity, index) => new { Identity = identity, Depth = GetDepth(identity), Index = index })
            .OrderBy(target => target.Depth)
            .ThenBy(target => target.Index)
            .Select(target => target.Identity)
            .ToArray();
    }

    private static IReadOnlyList<CodexProcessIdentity> SelectPackageWideTerminationTargets(
        IReadOnlyList<CodexProcessIdentity> trustedRoots,
        IReadOnlyList<CodexProcessEntry> processes,
        string installLocation)
    {
        var targets = new Dictionary<int, CodexProcessIdentity>();
        foreach (var identity in SelectForceTerminationTargets(trustedRoots, processes, installLocation))
        {
            targets.TryAdd(identity.Id, identity);
        }

        var entriesById = processes
            .GroupBy(process => process.Identity.Id)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        var blockedRootIds = GetBlockedPackageProcessIds(
            trustedRoots,
            processes,
            installLocation);

        foreach (var process in SelectTargetProcesses(installLocation, processes))
        {
            if (!HasBlockedAncestor(process, entriesById, blockedRootIds))
            {
                targets.TryAdd(process.Identity.Id, process.Identity);
            }
        }

        return targets.Values.ToArray();
    }

    private static void ThrowIfBlockedPackageCandidatesRemain(
        IReadOnlyList<CodexProcessIdentity> trustedRoots,
        IReadOnlyList<CodexProcessEntry> processes,
        string installLocation)
    {
        var blockedRootIds = GetBlockedPackageProcessIds(
            trustedRoots,
            processes,
            installLocation);
        if (blockedRootIds.Count == 0)
        {
            return;
        }

        throw new CodexProcessDiscoveryException();
    }

    private static HashSet<int> GetBlockedPackageProcessIds(
        IReadOnlyList<CodexProcessIdentity> trustedRoots,
        IReadOnlyList<CodexProcessEntry> processes,
        string installLocation)
    {
        var unambiguousEntries = processes
            .GroupBy(process => process.Identity.Id)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        return SelectTargetProcesses(installLocation, processes)
            .Where(process =>
                unambiguousEntries.TryGetValue(process.Identity.Id, out var entry) &&
                entry == process &&
                trustedRoots.Any(trusted =>
                    trusted.Id == process.Identity.Id &&
                    !IdentitiesMatch(trusted, process.Identity)))
            .Select(process => process.Identity.Id)
            .ToHashSet();
    }

    private static bool WasObservedExited(
        CodexProcessIdentity identity,
        IReadOnlyList<CodexProcessEntry> initialTargets,
        IReadOnlyList<bool> exitResults)
    {
        for (var index = 0; index < initialTargets.Count; index++)
        {
            if (exitResults[index] && IdentitiesMatch(identity, initialTargets[index].Identity))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasBlockedAncestor(
        CodexProcessEntry process,
        IReadOnlyDictionary<int, CodexProcessEntry> entriesById,
        IReadOnlySet<int> blockedRootIds)
    {
        var current = process;
        var visited = new HashSet<int>();
        while (visited.Add(current.Identity.Id))
        {
            if (blockedRootIds.Contains(current.Identity.Id))
            {
                return true;
            }

            if (!entriesById.TryGetValue(current.ParentProcessId, out current!))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IdentitiesMatch(
        CodexProcessIdentity currentIdentity,
        CodexProcessIdentity expectedIdentity) =>
        currentIdentity.Id == expectedIdentity.Id &&
        currentIdentity.CreationTimeUtcTicks == expectedIdentity.CreationTimeUtcTicks &&
        string.Equals(
            currentIdentity.ExecutablePath,
            expectedIdentity.ExecutablePath,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsInsideInstallLocation(string executablePath, string installLocation)
    {
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !Path.IsPathFullyQualified(executablePath))
        {
            return false;
        }

        try
        {
            var fullInstallLocation = Path.GetFullPath(installLocation);
            var fullExecutablePath = Path.GetFullPath(executablePath);
            var relativePath = Path.GetRelativePath(fullInstallLocation, fullExecutablePath);
            return !Path.IsPathFullyQualified(relativePath) &&
                   !string.Equals(relativePath, "..", StringComparison.Ordinal) &&
                   !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                   !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

internal sealed class SystemAppsFolderLauncher : IAppsFolderLauncher
{
    public bool Start(AppsFolderLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = request.UseShellExecute,
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}

internal sealed class WindowsProcessApi : IWindowsProcessApi
{
    private const uint ProcessTerminate = 0x00000001;
    private const uint QueryLimitedInformation = 0x00001000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xffffffff;
    private const uint CloseMessage = 0x0010;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorInvalidWindowHandle = 1400;
    private const int MaximumPathCharacters = 32768;

    public SafeProcessHandle? TryOpenProcessForQuery(int processId) =>
        TryOpenProcess(processId, QueryLimitedInformation | Synchronize);

    public SafeProcessHandle? TryOpenProcessForTermination(int processId) =>
        TryOpenProcess(processId, ProcessTerminate | QueryLimitedInformation | Synchronize);

    private static SafeProcessHandle? TryOpenProcess(int processId, uint desiredAccess)
    {
        var processHandle = OpenProcess(
            desiredAccess,
            inheritHandle: false,
            processId);
        if (!processHandle.IsInvalid)
        {
            return processHandle;
        }

        var error = Marshal.GetLastWin32Error();
        processHandle.Dispose();
        if (error is ErrorAccessDenied or ErrorInvalidParameter)
        {
            return null;
        }

        throw new Win32Exception(error);
    }

    public bool TryGetIdentity(
        SafeProcessHandle processHandle,
        int processId,
        out CodexProcessIdentity processIdentity)
    {
        var retainedProcessId = GetRetainedProcessId(processHandle);
        if (retainedProcessId != unchecked((uint)processId))
        {
            processIdentity = null!;
            return false;
        }

        var pathBuffer = new StringBuilder(MaximumPathCharacters);
        var characterCount = (uint)pathBuffer.Capacity;
        if (!QueryFullProcessImageName(
                processHandle,
                flags: 0,
                pathBuffer,
                ref characterCount))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (!GetProcessTimes(
                processHandle,
                out var creationTime,
                out _,
                out _,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var rawExecutablePath = pathBuffer.ToString();
        if (string.IsNullOrWhiteSpace(rawExecutablePath) ||
            !Path.IsPathFullyQualified(rawExecutablePath))
        {
            processIdentity = null!;
            return false;
        }

        processIdentity = new CodexProcessIdentity(
            unchecked((int)retainedProcessId),
            Path.GetFullPath(rawExecutablePath),
            DateTime.FromFileTimeUtc(creationTime.ToInt64()).Ticks);
        return true;
    }

    public bool CloseMainWindows(SafeProcessHandle processHandle, int processId)
    {
        var retainedProcessId = GetRetainedProcessId(processHandle);
        if (retainedProcessId != unchecked((uint)processId))
        {
            return false;
        }

        if (!IsProcessRunning(processHandle))
        {
            return false;
        }

        var closePosted = false;
        Exception? callbackException = null;
        var enumerated = EnumWindows((windowHandle, _) =>
        {
            try
            {
                GetWindowThreadProcessId(windowHandle, out var windowProcessId);
                if (windowProcessId != retainedProcessId ||
                    !IsProcessRunning(processHandle))
                {
                    return true;
                }

                if (PostMessage(windowHandle, CloseMessage, UIntPtr.Zero, IntPtr.Zero))
                {
                    closePosted = true;
                    return true;
                }

                var error = Marshal.GetLastWin32Error();
                if (error != ErrorInvalidWindowHandle)
                {
                    throw new Win32Exception(error);
                }

                return true;
            }
            catch (Exception exception)
            {
                callbackException = exception;
                return false;
            }
        }, IntPtr.Zero);

        if (callbackException is not null)
        {
            ExceptionDispatchInfo.Capture(callbackException).Throw();
        }

        if (!enumerated)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return closePosted;
    }

    public async Task<bool> WaitForExitAsync(
        SafeProcessHandle processHandle,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var waitHandle = new NonOwningProcessWaitHandle(processHandle);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        var registeredWait = ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            static (state, timedOut) => ((TaskCompletionSource<bool>)state!).TrySetResult(!timedOut),
            completion,
            timeout,
            executeOnlyOnce: true);

        try
        {
            return await completion.Task;
        }
        finally
        {
            registeredWait.Unregister(null);
        }
    }

    public bool TerminateProcess(SafeProcessHandle processHandle)
    {
        if (!IsProcessRunning(processHandle))
        {
            return false;
        }

        if (TerminateProcessNative(processHandle, exitCode: 1))
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (!IsProcessRunning(processHandle))
        {
            return false;
        }

        throw new Win32Exception(error);
    }

    private static bool IsProcessRunning(SafeProcessHandle processHandle)
    {
        var waitResult = WaitForSingleObject(processHandle, milliseconds: 0);
        return waitResult switch
        {
            WaitTimeout => true,
            WaitObject0 => false,
            WaitFailed => throw new Win32Exception(Marshal.GetLastWin32Error()),
            _ => throw new InvalidOperationException("Unexpected process wait result."),
        };
    }

    private static uint GetRetainedProcessId(SafeProcessHandle processHandle)
    {
        var processId = GetProcessId(processHandle);
        return processId != 0
            ? processId
            : throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        StringBuilder executableName,
        ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(
        SafeProcessHandle process,
        uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeProcessHandle process);

    [DllImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcessNative(
        SafeProcessHandle process,
        uint exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr windowHandle,
        out uint processId);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        IntPtr windowHandle,
        uint message,
        UIntPtr wordParameter,
        IntPtr longParameter);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public readonly long ToInt64() =>
            unchecked((long)(((ulong)HighDateTime << 32) | LowDateTime));
    }

    private sealed class NonOwningProcessWaitHandle : WaitHandle
    {
        private readonly SafeProcessHandle _processHandle;
        private bool _referenceAdded;

        public NonOwningProcessWaitHandle(SafeProcessHandle processHandle)
        {
            _processHandle = processHandle;
            processHandle.DangerousAddRef(ref _referenceAdded);
            try
            {
                SafeWaitHandle = new SafeWaitHandle(
                    processHandle.DangerousGetHandle(),
                    ownsHandle: false);
            }
            catch
            {
                ReleaseReference();
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            ReleaseReference();
        }

        private void ReleaseReference()
        {
            if (!_referenceAdded)
            {
                return;
            }

            _processHandle.DangerousRelease();
            _referenceAdded = false;
        }
    }
}

internal sealed class SystemProcessHandle : IDisposable
{
    private readonly IWindowsProcessApi _nativeApi;
    private readonly int _processId;
    private readonly SafeProcessHandle _safeHandle;

    private SystemProcessHandle(
        int processId,
        SafeProcessHandle safeHandle,
        IWindowsProcessApi nativeApi)
    {
        _processId = processId;
        _safeHandle = safeHandle;
        _nativeApi = nativeApi;
    }

    public static SystemProcessHandle? TryOpenForQuery(
        int processId,
        IWindowsProcessApi nativeApi) =>
        TryOpen(processId, nativeApi, requireTermination: false);

    public static SystemProcessHandle? TryOpenForTermination(
        int processId,
        IWindowsProcessApi nativeApi) =>
        TryOpen(processId, nativeApi, requireTermination: true);

    private static SystemProcessHandle? TryOpen(
        int processId,
        IWindowsProcessApi nativeApi,
        bool requireTermination)
    {
        var safeHandle = requireTermination
            ? nativeApi.TryOpenProcessForTermination(processId)
            : nativeApi.TryOpenProcessForQuery(processId);
        return safeHandle is null
            ? null
            : new SystemProcessHandle(processId, safeHandle, nativeApi);
    }

    public bool TryGetIdentity(out CodexProcessIdentity processIdentity) =>
        _nativeApi.TryGetIdentity(_safeHandle, _processId, out processIdentity);

    public bool CloseMainWindow() =>
        _nativeApi.CloseMainWindows(_safeHandle, _processId);

    public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _nativeApi.WaitForExitAsync(_safeHandle, timeout, cancellationToken);

    public bool Kill(bool entireProcessTree)
    {
        if (!entireProcessTree)
        {
            throw new ArgumentException("Every authorized tree process must be terminated explicitly.", nameof(entireProcessTree));
        }

        return _nativeApi.TerminateProcess(_safeHandle);
    }

    public void Dispose() => _safeHandle.Dispose();
}

internal sealed class SystemCodexProcessAccessor : ICodexProcessAccessor
{
    private const uint SnapshotProcesses = 0x00000002;
    private const int ErrorNoMoreFiles = 18;
    private readonly Func<IReadOnlyList<RawProcessEntry>> _enumerateRawProcesses;
    private readonly IWindowsProcessApi _nativeApi;

    public SystemCodexProcessAccessor()
        : this(new WindowsProcessApi(), EnumerateRawProcesses)
    {
    }

    internal SystemCodexProcessAccessor(IWindowsProcessApi nativeApi)
        : this(nativeApi, EnumerateRawProcesses)
    {
    }

    internal SystemCodexProcessAccessor(
        IWindowsProcessApi nativeApi,
        Func<IReadOnlyList<RawProcessEntry>> enumerateRawProcesses)
    {
        _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
        _enumerateRawProcesses = enumerateRawProcesses
            ?? throw new ArgumentNullException(nameof(enumerateRawProcesses));
    }

    public IReadOnlyList<CodexProcessEntry> GetProcesses() => GetProcessSnapshot().Processes;

    public CodexProcessSnapshot GetProcessSnapshot()
    {
        var processes = new List<CodexProcessEntry>();
        var unresolvedChatGptProcessIds = new List<int>();
        foreach (var rawProcess in _enumerateRawProcesses())
        {
            var isRawChatGpt = string.Equals(
                Path.GetFileName(rawProcess.ExecutableFile),
                "ChatGPT.exe",
                StringComparison.OrdinalIgnoreCase);
            try
            {
                using var processHandle = SystemProcessHandle.TryOpenForQuery(
                    rawProcess.Id,
                    _nativeApi);
                if (processHandle is not null &&
                    processHandle.TryGetIdentity(out var identity))
                {
                    processes.Add(new CodexProcessEntry(identity, rawProcess.ParentProcessId));
                }
                else if (isRawChatGpt)
                {
                    unresolvedChatGptProcessIds.Add(rawProcess.Id);
                }
            }
            catch (Win32Exception) when (isRawChatGpt)
            {
                unresolvedChatGptProcessIds.Add(rawProcess.Id);
            }
        }

        return new CodexProcessSnapshot(processes, unresolvedChatGptProcessIds);
    }

    private static IReadOnlyList<RawProcessEntry> EnumerateRawProcesses()
    {
        using var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var nativeEntry = NewNativeEntry();
        if (!Process32First(snapshot, ref nativeEntry))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNoMoreFiles)
            {
                return [];
            }

            throw new Win32Exception(error);
        }

        var processes = new List<RawProcessEntry>();
        do
        {
            processes.Add(new RawProcessEntry(
                unchecked((int)nativeEntry.ProcessId),
                unchecked((int)nativeEntry.ParentProcessId),
                nativeEntry.ExecutableFile));

            nativeEntry = NewNativeEntry();
        }
        while (Process32Next(snapshot, ref nativeEntry));

        var lastError = Marshal.GetLastWin32Error();
        if (lastError != ErrorNoMoreFiles)
        {
            throw new Win32Exception(lastError);
        }

        return processes;
    }

    public bool CloseMainWindow(CodexProcessIdentity expectedIdentity)
    {
        using var processHandle = SystemProcessHandle.TryOpenForQuery(expectedIdentity.Id, _nativeApi);
        if (processHandle is null ||
            !processHandle.TryGetIdentity(out var currentIdentity) ||
            !IdentityMatches(currentIdentity, expectedIdentity))
        {
            return false;
        }

        return processHandle.CloseMainWindow();
    }

    public async Task<bool> WaitForExitAsync(
        CodexProcessIdentity expectedIdentity,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var processHandle = SystemProcessHandle.TryOpenForQuery(expectedIdentity.Id, _nativeApi);
        if (processHandle is null ||
            !processHandle.TryGetIdentity(out var currentIdentity) ||
            !IdentityMatches(currentIdentity, expectedIdentity))
        {
            return true;
        }

        var exited = await processHandle.WaitForExitAsync(timeout, cancellationToken);
        if (exited)
        {
            return true;
        }

        return !processHandle.TryGetIdentity(out currentIdentity) ||
               !IdentityMatches(currentIdentity, expectedIdentity);
    }

    public bool Kill(CodexProcessIdentity expectedIdentity, bool entireProcessTree)
    {
        using var processHandle = SystemProcessHandle.TryOpenForTermination(expectedIdentity.Id, _nativeApi);
        if (processHandle is null ||
            !processHandle.TryGetIdentity(out var currentIdentity) ||
            !IdentityMatches(currentIdentity, expectedIdentity))
        {
            return false;
        }

        return processHandle.Kill(entireProcessTree);
    }

    private static bool IdentityMatches(
        CodexProcessIdentity currentIdentity,
        CodexProcessIdentity expectedIdentity) =>
        currentIdentity.Id == expectedIdentity.Id &&
        currentIdentity.CreationTimeUtcTicks == expectedIdentity.CreationTimeUtcTicks &&
        string.Equals(
            currentIdentity.ExecutablePath,
            expectedIdentity.ExecutablePath,
            StringComparison.OrdinalIgnoreCase);

    private static NativeProcessEntry NewNativeEntry() => new()
    {
        Size = (uint)Marshal.SizeOf<NativeProcessEntry>(),
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(
        SafeFileHandle snapshot,
        ref NativeProcessEntry entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(
        SafeFileHandle snapshot,
        ref NativeProcessEntry entry);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }
}
