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

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<CommandResult> RunCapturedAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startInfo = CreateStartInfo(request, useShellExecute: false);
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("The process did not start.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
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
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("The process did not start.");
        }

        await process.WaitForExitAsync(cancellationToken);
        return new CommandResult(process.ExitCode, string.Empty, string.Empty);
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
}
