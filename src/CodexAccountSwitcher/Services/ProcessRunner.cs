using System.Diagnostics;
using CodexAccountSwitcher.Security;

namespace CodexAccountSwitcher.Services;

public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    bool Visible = false,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null);

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    public static CommandResult Failed(string error) => new(1, string.Empty, error);
}

public interface IProcessRunner
{
    Task<CommandResult> RunCapturedAsync(ProcessRequest request, CancellationToken cancellationToken);

    Task<CommandResult> RunVisibleAsync(ProcessRequest request, CancellationToken cancellationToken);
}

internal interface IProcessFactory
{
    IStartedProcess Create(ProcessStartInfo startInfo);
}

internal interface IStartedProcess : IDisposable
{
    bool HasExited { get; }

    int ExitCode { get; }

    bool Start();

    Task<string> ReadStandardOutputAsync(CancellationToken cancellationToken);

    Task<string> ReadStandardErrorAsync(CancellationToken cancellationToken);

    Task WaitForExitAsync(CancellationToken cancellationToken);

    void Kill(bool entireProcessTree);
}

public sealed class ProcessRunner : IProcessRunner
{
    private readonly IProcessFactory _processFactory;

    public ProcessRunner() : this(new SystemProcessFactory())
    {
    }

    internal ProcessRunner(IProcessFactory processFactory) =>
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));

    public async Task<CommandResult> RunCapturedAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startInfo = CreateStartInfo(request, useShellExecute: false);
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = _processFactory.Create(startInfo);
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start())
        {
            throw new InvalidOperationException("The process did not start.");
        }

        var outputTask = process.ReadStandardOutputAsync(cancellationToken);
        var errorTask = process.ReadStandardErrorAsync(cancellationToken);
        await WaitForExitAfterStartAsync(process, cancellationToken);
        await Task.WhenAll(outputTask, errorTask);

        return new CommandResult(
            process.ExitCode,
            SensitiveTextRedactor.Redact(await outputTask, Array.Empty<string>()),
            SensitiveTextRedactor.Redact(await errorTask, Array.Empty<string>()));
    }

    public async Task<CommandResult> RunVisibleAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Environment is { Count: > 0 })
        {
            throw new ArgumentException("Visible processes cannot override their environment.", nameof(request));
        }

        var startInfo = CreateStartInfo(request, useShellExecute: true);
        using var process = _processFactory.Create(startInfo);
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start())
        {
            throw new InvalidOperationException("The process did not start.");
        }

        await WaitForExitAfterStartAsync(process, cancellationToken);
        return new CommandResult(process.ExitCode, string.Empty, string.Empty);
    }

    private static async Task WaitForExitAfterStartAsync(IStartedProcess process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                }
            }

            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }

    private static ProcessStartInfo CreateStartInfo(ProcessRequest request, bool useShellExecute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        ArgumentNullException.ThrowIfNull(request.Arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = useShellExecute,
        };

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!useShellExecute && request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        return startInfo;
    }

    private sealed class SystemProcessFactory : IProcessFactory
    {
        public IStartedProcess Create(ProcessStartInfo startInfo) => new SystemStartedProcess(startInfo);
    }

    private sealed class SystemStartedProcess : IStartedProcess
    {
        private readonly Process _process;

        public SystemStartedProcess(ProcessStartInfo startInfo) => _process = new Process { StartInfo = startInfo };

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public bool Start() => _process.Start();

        public Task<string> ReadStandardOutputAsync(CancellationToken cancellationToken) =>
            _process.StandardOutput.ReadToEndAsync(cancellationToken);

        public Task<string> ReadStandardErrorAsync(CancellationToken cancellationToken) =>
            _process.StandardError.ReadToEndAsync(cancellationToken);

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _process.WaitForExitAsync(cancellationToken);

        public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);

        public void Dispose() => _process.Dispose();
    }
}
