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

internal sealed record CodexProcessEntry(int Id, int ParentProcessId, string? ExecutablePath);

internal interface ICodexProcessAccessor
{
    IReadOnlyList<CodexProcessEntry> GetProcesses();

    bool CloseMainWindow(int processId);

    Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken);

    void Kill(int processId, bool entireProcessTree);
}

public sealed class CodexProcessController : ICodexProcessController
{
    private static readonly TimeSpan MaximumCloseTimeout = TimeSpan.FromSeconds(8);
    private readonly ICodexProcessAccessor _processAccessor;
    private readonly IProcessRunner _processRunner;

    public CodexProcessController() : this(new SystemCodexProcessAccessor(), new ProcessRunner())
    {
    }

    internal CodexProcessController(
        ICodexProcessAccessor processAccessor,
        IProcessRunner processRunner)
    {
        _processAccessor = processAccessor ?? throw new ArgumentNullException(nameof(processAccessor));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
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
        var targets = SelectTargetProcesses(package, _processAccessor.GetProcesses());
        foreach (var process in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _processAccessor.CloseMainWindow(process.Id);
        }

        var effectiveTimeout = timeout <= MaximumCloseTimeout ? timeout : MaximumCloseTimeout;
        var exitTasks = targets
            .Select(process => _processAccessor.WaitForExitAsync(
                process.Id,
                effectiveTimeout,
                cancellationToken))
            .ToArray();
        var exitResults = await Task.WhenAll(exitTasks);
        var remainingProcessIds = targets
            .Where((_, index) => !exitResults[index])
            .Select(process => process.Id)
            .ToArray();

        return new CloseResult(remainingProcessIds.Length == 0, remainingProcessIds);
    }

    public Task ForceTerminateAsync(
        IReadOnlyList<int> processIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        foreach (var processId in processIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _processAccessor.Kill(processId, entireProcessTree: true);
        }

        return Task.CompletedTask;
    }

    public async Task LaunchAsync(CodexPackageInfo package, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        var result = await _processRunner.RunVisibleAsync(
            new ProcessRequest(
                "explorer.exe",
                [$"shell:AppsFolder\\{package.AppUserModelId}"],
                Visible: true),
            cancellationToken);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException("Codex launch failed.");
        }
    }

    private static IReadOnlyList<CodexProcessEntry> SelectTargetProcesses(
        CodexPackageInfo package,
        IReadOnlyList<CodexProcessEntry> processes)
    {
        if (!Path.IsPathFullyQualified(package.InstallLocation))
        {
            throw new ArgumentException("The install location must be fully qualified.", nameof(package));
        }

        var selectedProcessIds = processes
            .Where(process => IsInsideInstallLocation(process.ExecutablePath, package.InstallLocation))
            .Where(process => string.Equals(
                Path.GetFileName(process.ExecutablePath),
                "ChatGPT.exe",
                StringComparison.OrdinalIgnoreCase))
            .Select(process => process.Id)
            .ToHashSet();

        var addedProcess = true;
        while (addedProcess)
        {
            addedProcess = false;
            foreach (var process in processes)
            {
                if (selectedProcessIds.Contains(process.Id) ||
                    !selectedProcessIds.Contains(process.ParentProcessId) ||
                    !IsInsideInstallLocation(process.ExecutablePath, package.InstallLocation))
                {
                    continue;
                }

                selectedProcessIds.Add(process.Id);
                addedProcess = true;
            }
        }

        return processes.Where(process => selectedProcessIds.Contains(process.Id)).ToArray();
    }

    private static bool IsInsideInstallLocation(string? executablePath, string installLocation)
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

        var nativeEntry = new NativeProcessEntry
        {
            Size = (uint)Marshal.SizeOf<NativeProcessEntry>(),
        };
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
            processes.Add(new CodexProcessEntry(
                processId,
                unchecked((int)nativeEntry.ParentProcessId),
                TryGetExecutablePath(processId)));
            nativeEntry.Size = (uint)Marshal.SizeOf<NativeProcessEntry>();
        }
        while (Process32Next(snapshot, ref nativeEntry));

        var lastError = Marshal.GetLastWin32Error();
        if (lastError != ErrorNoMoreFiles)
        {
            throw new Win32Exception(lastError);
        }

        return processes;
    }

    public bool CloseMainWindow(int processId)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return false;
        }

        using (process)
        {
            if (process.HasExited)
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
    }

    public async Task<bool> WaitForExitAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            if (process.HasExited)
            {
                return true;
            }

            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return process.HasExited;
            }
        }
    }

    public void Kill(int processId, bool entireProcessTree)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            if (process.HasExited)
            {
                return;
            }

            try
            {
                process.Kill(entireProcessTree);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
            }
        }
    }

    private static string? TryGetExecutablePath(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName;
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
