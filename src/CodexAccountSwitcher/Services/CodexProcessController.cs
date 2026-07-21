using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.Services;

public sealed record CloseResult(bool AllExited, IReadOnlyList<int> RemainingProcessIds);

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

internal interface ICodexProcessAccessor
{
    IReadOnlyList<CodexProcessEntry> GetProcesses();

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

public sealed class CodexProcessController : ICodexProcessController
{
    private const string UntrustedForceTargetMessage =
        "Force termination target was not issued by the latest close operation.";
    private static readonly TimeSpan MaximumCloseTimeout = TimeSpan.FromSeconds(8);
    private readonly IAppsFolderLauncher _appsFolderLauncher;
    private readonly Dictionary<int, CodexProcessIdentity> _issuedRemainingTargets = [];
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ICodexProcessAccessor _processAccessor;

    public CodexProcessController()
        : this(new SystemCodexProcessAccessor(), new SystemAppsFolderLauncher())
    {
    }

    internal CodexProcessController(
        ICodexProcessAccessor processAccessor,
        IAppsFolderLauncher appsFolderLauncher)
    {
        _processAccessor = processAccessor ?? throw new ArgumentNullException(nameof(processAccessor));
        _appsFolderLauncher = appsFolderLauncher ?? throw new ArgumentNullException(nameof(appsFolderLauncher));
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

        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            _issuedRemainingTargets.Clear();
            var targets = SelectTargetProcesses(package, _processAccessor.GetProcesses());
            foreach (var process in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _processAccessor.CloseMainWindow(process.Identity);
            }

            var effectiveTimeout = timeout <= MaximumCloseTimeout ? timeout : MaximumCloseTimeout;
            var exitTasks = targets
                .Select(process => _processAccessor.WaitForExitAsync(
                    process.Identity,
                    effectiveTimeout,
                    cancellationToken))
                .ToArray();
            var exitResults = await Task.WhenAll(exitTasks);
            var remainingTargets = targets
                .Where((_, index) => !exitResults[index])
                .Select(process => process.Identity)
                .ToArray();

            foreach (var identity in remainingTargets)
            {
                _issuedRemainingTargets.Add(identity.Id, identity);
            }

            return new CloseResult(
                remainingTargets.Length == 0,
                remainingTargets.Select(identity => identity.Id).ToArray());
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task ForceTerminateAsync(
        IReadOnlyList<int> processIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var requestedTargets = new List<CodexProcessIdentity>(processIds.Count);
            var requestedProcessIds = new HashSet<int>();
            foreach (var processId in processIds)
            {
                if (!requestedProcessIds.Add(processId) ||
                    !_issuedRemainingTargets.TryGetValue(processId, out var identity))
                {
                    throw new InvalidOperationException(UntrustedForceTargetMessage);
                }

                requestedTargets.Add(identity);
            }

            foreach (var identity in requestedTargets)
            {
                _issuedRemainingTargets.Remove(identity.Id);
            }

            foreach (var identity in requestedTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _processAccessor.Kill(identity, entireProcessTree: true);
            }
        }
        finally
        {
            _lifecycleGate.Release();
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
            throw new InvalidOperationException("Codex launch failed.");
        }

        return Task.CompletedTask;
    }

    private static IReadOnlyList<CodexProcessEntry> SelectTargetProcesses(
        CodexPackageInfo package,
        IReadOnlyList<CodexProcessEntry> processes)
    {
        if (!Path.IsPathFullyQualified(package.InstallLocation))
        {
            throw new ArgumentException("The install location must be fully qualified.", nameof(package));
        }

        var selectedIdentities = new Dictionary<int, CodexProcessIdentity>();
        foreach (var process in processes)
        {
            if (IsInsideInstallLocation(process.Identity.ExecutablePath, package.InstallLocation) &&
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
                    !IsInsideInstallLocation(process.Identity.ExecutablePath, package.InstallLocation))
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

internal sealed class SystemCodexProcessAccessor : ICodexProcessAccessor
{
    private const uint SnapshotProcesses = 0x00000002;
    private const int ErrorNoMoreFiles = 18;

    public IReadOnlyList<CodexProcessEntry> GetProcesses()
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

        var processes = new List<CodexProcessEntry>();
        do
        {
            var processId = unchecked((int)nativeEntry.ProcessId);
            var identity = TryReadIdentity(processId);
            if (identity is not null)
            {
                processes.Add(new CodexProcessEntry(
                    identity,
                    unchecked((int)nativeEntry.ParentProcessId)));
            }

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
        using var process = TryOpenMatchingProcess(expectedIdentity);
        if (process is null)
        {
            return false;
        }

        try
        {
            return process.CloseMainWindow();
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            return false;
        }
    }

    public async Task<bool> WaitForExitAsync(
        CodexProcessIdentity expectedIdentity,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = TryOpenMatchingProcess(expectedIdentity);
        if (process is null)
        {
            return true;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var currentIdentity = TryReadIdentity(process);
            return currentIdentity is null || !IdentityMatches(currentIdentity, expectedIdentity);
        }
    }

    public bool Kill(CodexProcessIdentity expectedIdentity, bool entireProcessTree)
    {
        using var process = TryOpenMatchingProcess(expectedIdentity);
        if (process is null)
        {
            return false;
        }

        try
        {
            process.Kill(entireProcessTree);
            return true;
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            return false;
        }
    }

    private static Process? TryOpenMatchingProcess(CodexProcessIdentity expectedIdentity)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(expectedIdentity.Id);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var currentIdentity = TryReadIdentity(process);
        if (currentIdentity is not null && IdentityMatches(currentIdentity, expectedIdentity))
        {
            return process;
        }

        process.Dispose();
        return null;
    }

    private static CodexProcessIdentity? TryReadIdentity(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return TryReadIdentity(process);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static CodexProcessIdentity? TryReadIdentity(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return null;
            }

            var executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath) ||
                !Path.IsPathFullyQualified(executablePath))
            {
                return null;
            }

            return new CodexProcessIdentity(
                process.Id,
                Path.GetFullPath(executablePath),
                process.StartTime.ToUniversalTime().Ticks);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
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
