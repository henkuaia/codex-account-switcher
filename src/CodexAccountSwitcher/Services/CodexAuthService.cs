using CodexAccountSwitcher.Security;
using System.IO;

namespace CodexAccountSwitcher.Services;

public sealed class CodexAuthService
{
    private const string HelperFileName = "codex-auth.exe";
    private readonly string _helperPath;
    private readonly string _codexCliDirectory;
    private readonly IProcessRunner _processRunner;

    public CodexAuthService(string helperPath, string codexCliDirectory, IProcessRunner? processRunner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexCliDirectory);

        _helperPath = helperPath;
        _codexCliDirectory = codexCliDirectory;
        _processRunner = processRunner ?? new ProcessRunner();
    }

    public Task<CommandResult> SwitchAsync(string selector, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        return RunCapturedAsync(["switch", selector], null, cancellationToken);
    }

    public Task<CommandResult> LoginAsync(CancellationToken cancellationToken)
    {
        return LoginAsyncCore(progress: null, cancellationToken);
    }

    public Task<CommandResult> LoginAsync(
        IProgress<ProcessOutputLine> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return LoginAsyncCore(progress, cancellationToken);
    }

    private Task<CommandResult> LoginAsyncCore(
        IProgress<ProcessOutputLine>? progress,
        CancellationToken cancellationToken)
    {
        if (!TryResolveCliDirectory(out var cliDirectory))
        {
            return Task.FromResult(CommandResult.Failed("The Codex CLI directory is unavailable."));
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        var childPath = string.IsNullOrEmpty(path)
            ? cliDirectory
            : string.Concat(cliDirectory, Path.PathSeparator, path);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = childPath,
        };

        return progress is null
            ? RunCapturedAsync(["login", "--device-auth"], environment, cancellationToken)
            : RunCapturedAsync(["login", "--device-auth"], environment, progress, cancellationToken);
    }

    public async Task<CommandResult> RemoveAsync(CancellationToken cancellationToken)
    {
        if (!TryResolveHelperPath(out var helperPath))
        {
            return CommandResult.Failed("The codex-auth helper is unavailable.");
        }

        return await _processRunner.RunVisibleAsync(
            new ProcessRequest(helperPath, ["remove"], Visible: true),
            cancellationToken);
    }

    private async Task<CommandResult> RunCapturedAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        if (!TryResolveHelperPath(out var helperPath))
        {
            return CommandResult.Failed("The codex-auth helper is unavailable.");
        }

        var result = await _processRunner.RunCapturedAsync(
            new ProcessRequest(helperPath, arguments, Environment: environment),
            cancellationToken);
        return result with
        {
            StandardOutput = SensitiveTextRedactor.Redact(result.StandardOutput, Array.Empty<string>()),
            StandardError = SensitiveTextRedactor.Redact(result.StandardError, Array.Empty<string>()),
        };
    }

    private async Task<CommandResult> RunCapturedAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        IProgress<ProcessOutputLine> progress,
        CancellationToken cancellationToken)
    {
        if (!TryResolveHelperPath(out var helperPath))
        {
            return CommandResult.Failed("The codex-auth helper is unavailable.");
        }

        var result = await _processRunner.RunCapturedAsync(
            new ProcessRequest(helperPath, arguments, Environment: environment),
            progress,
            cancellationToken);
        return result with
        {
            StandardOutput = SensitiveTextRedactor.Redact(result.StandardOutput, Array.Empty<string>()),
            StandardError = SensitiveTextRedactor.Redact(result.StandardError, Array.Empty<string>()),
        };
    }

    private bool TryResolveHelperPath(out string helperPath)
    {
        helperPath = string.Empty;
        try
        {
            helperPath = Path.GetFullPath(_helperPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return string.Equals(Path.GetFileName(helperPath), HelperFileName, StringComparison.OrdinalIgnoreCase)
            && File.Exists(helperPath);
    }

    private bool TryResolveCliDirectory(out string cliDirectory)
    {
        cliDirectory = string.Empty;
        try
        {
            cliDirectory = Path.GetFullPath(_codexCliDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return Directory.Exists(cliDirectory);
    }
}
