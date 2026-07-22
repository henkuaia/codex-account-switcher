using System.Diagnostics;
using System.IO;
using System.Text;
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

public enum ProcessOutputStream
{
    StandardOutput,
    StandardError,
}

public sealed record ProcessOutputLine(ProcessOutputStream Stream, string Text);

public delegate ValueTask ProcessOutputHandler(
    ProcessOutputLine output,
    CancellationToken cancellationToken);

public interface IProcessRunner
{
    Task<CommandResult> RunCapturedAsync(ProcessRequest request, CancellationToken cancellationToken);

    async Task<CommandResult> RunCapturedAsync(
        ProcessRequest request,
        ProcessOutputHandler outputHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outputHandler);

        var result = await RunCapturedAsync(request, cancellationToken);
        await ReportLinesAsync(
            result.StandardOutput,
            ProcessOutputStream.StandardOutput,
            outputHandler,
            cancellationToken);
        await ReportLinesAsync(
            result.StandardError,
            ProcessOutputStream.StandardError,
            outputHandler,
            cancellationToken);
        return result;

        static async ValueTask ReportLinesAsync(
            string text,
            ProcessOutputStream stream,
            ProcessOutputHandler outputHandler,
            CancellationToken cancellationToken)
        {
            using var reader = new StringReader(text);
            while (reader.ReadLine() is { } line)
            {
                await outputHandler(new ProcessOutputLine(stream, line), cancellationToken);
            }
        }
    }

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

    ValueTask<string?> ReadStandardOutputLineAsync(CancellationToken cancellationToken);

    ValueTask<string?> ReadStandardErrorLineAsync(CancellationToken cancellationToken);

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

    public async Task<CommandResult> RunCapturedAsync(
        ProcessRequest request,
        ProcessOutputHandler outputHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(outputHandler);

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

        using var pumpCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var outputTask = PumpLinesAsync(
            process.ReadStandardOutputLineAsync,
            ProcessOutputStream.StandardOutput,
            standardOutput,
            outputHandler,
            pumpCancellationSource.Token);
        var errorTask = PumpLinesAsync(
            process.ReadStandardErrorLineAsync,
            ProcessOutputStream.StandardError,
            standardError,
            outputHandler,
            pumpCancellationSource.Token);

        await WaitForExitAndPumpsAsync(
            process,
            outputTask,
            errorTask,
            pumpCancellationSource,
            cancellationToken);
        return new CommandResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
    }

    public async Task<CommandResult> RunVisibleAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startInfo = CreateStartInfo(
            request,
            useShellExecute: request.Environment is not { Count: > 0 });
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
            await TerminateAndWaitForExitAsync(process);
            throw;
        }
    }

    private static async Task PumpLinesAsync(
        Func<CancellationToken, ValueTask<string?>> readLineAsync,
        ProcessOutputStream stream,
        StringBuilder destination,
        ProcessOutputHandler outputHandler,
        CancellationToken cancellationToken)
    {
        while (await readLineAsync(cancellationToken) is { } line)
        {
            var sanitizedLine = SensitiveTextRedactor.Redact(line, Array.Empty<string>());
            destination.AppendLine(sanitizedLine);
            await outputHandler(new ProcessOutputLine(stream, sanitizedLine), cancellationToken);
        }
    }

    private static async Task WaitForExitAndPumpsAsync(
        IStartedProcess process,
        Task outputTask,
        Task errorTask,
        CancellationTokenSource pumpCancellationSource,
        CancellationToken cancellationToken)
    {
        Task? waitTask = null;

        try
        {
            waitTask = process.WaitForExitAsync(cancellationToken);
            var pendingTasks = new List<Task> { waitTask, outputTask, errorTask };
            while (pendingTasks.Count > 0)
            {
                var completedTask = await Task.WhenAny(pendingTasks);
                pendingTasks.Remove(completedTask);
                await completedTask;
            }
        }
        catch
        {
            try
            {
                pumpCancellationSource.Cancel();
            }
            catch
            {
            }

            try
            {
                await TerminateAndWaitForExitAsync(process);
            }
            catch
            {
            }

            if (waitTask is not null)
            {
                await ObserveTaskAsync(waitTask);
            }

            await ObserveTaskAsync(outputTask);
            await ObserveTaskAsync(errorTask);
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static async Task ObserveTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private static async Task TerminateAndWaitForExitAsync(IStartedProcess process)
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

        public ValueTask<string?> ReadStandardOutputLineAsync(CancellationToken cancellationToken) =>
            _process.StandardOutput.ReadLineAsync(cancellationToken);

        public ValueTask<string?> ReadStandardErrorLineAsync(CancellationToken cancellationToken) =>
            _process.StandardError.ReadLineAsync(cancellationToken);

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _process.WaitForExitAsync(cancellationToken);

        public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);

        public void Dispose() => _process.Dispose();
    }
}
