using CodexAccountSwitcher.Security;
using System.IO;

namespace CodexAccountSwitcher.Services;

public sealed record HelperAvailability(bool IsAvailable, string ExpectedPath, string Error);

public sealed class CodexAuthService
{
    private const string HelperFileName = "codex-auth.exe";
    private const string SkipServiceReconcileVariable = "CODEX_AUTH_SKIP_SERVICE_RECONCILE";
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
        return RunCapturedAsync(["switch", selector], CreateMutationEnvironment(), cancellationToken);
    }

    public HelperAvailability CheckAvailability()
    {
        string expectedPath;
        try
        {
            expectedPath = Path.GetFullPath(_helperPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            expectedPath = _helperPath;
        }

        var isAvailable = string.Equals(
                Path.GetFileName(expectedPath),
                HelperFileName,
                StringComparison.OrdinalIgnoreCase) &&
            File.Exists(expectedPath);
        var error = isAvailable
            ? string.Empty
            : $"The codex-auth helper is unavailable at the expected path: {expectedPath}";
        return new HelperAvailability(isAvailable, expectedPath, error);
    }

    public Task<CommandResult> LoginAsync(CancellationToken cancellationToken)
    {
        return LoginAsyncCore(outputHandler: null, cancellationToken);
    }

    public Task<CommandResult> LoginAsync(
        ProcessOutputHandler outputHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outputHandler);
        return LoginAsyncCore(outputHandler, cancellationToken);
    }

    private Task<CommandResult> LoginAsyncCore(
        ProcessOutputHandler? outputHandler,
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
        var environment = CreateMutationEnvironment(childPath);

        return outputHandler is null
            ? RunCapturedAsync(["login", "--device-auth"], environment, cancellationToken)
            : RunCapturedAsync(["login", "--device-auth"], environment, outputHandler, cancellationToken);
    }

    public async Task<CommandResult> RemoveAsync(CancellationToken cancellationToken)
    {
        if (!TryResolveHelperPath(out var helperPath))
        {
            return CommandResult.Failed(CheckAvailability().Error);
        }

        return await _processRunner.RunVisibleAsync(
            new ProcessRequest(
                helperPath,
                ["remove"],
                Visible: true,
                Environment: CreateMutationEnvironment()),
            cancellationToken);
    }

    public Task<CommandResult> RemoveAsync(string selector, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        return RunCapturedAsync(
            ["remove", selector],
            CreateMutationEnvironment(),
            cancellationToken);
    }

    private static IReadOnlyDictionary<string, string> CreateMutationEnvironment(string? childPath = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SkipServiceReconcileVariable] = "1",
        };
        if (childPath is not null)
        {
            environment["PATH"] = childPath;
        }

        return environment;
    }

    private async Task<CommandResult> RunCapturedAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        if (!TryResolveHelperPath(out var helperPath))
        {
            return CommandResult.Failed(CheckAvailability().Error);
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
        ProcessOutputHandler outputHandler,
        CancellationToken cancellationToken)
    {
        if (!TryResolveHelperPath(out var helperPath))
        {
            return CommandResult.Failed(CheckAvailability().Error);
        }

        var result = await _processRunner.RunCapturedAsync(
            new ProcessRequest(helperPath, arguments, Environment: environment),
            outputHandler,
            cancellationToken);
        return result with
        {
            StandardOutput = SensitiveTextRedactor.Redact(result.StandardOutput, Array.Empty<string>()),
            StandardError = SensitiveTextRedactor.Redact(result.StandardError, Array.Empty<string>()),
        };
    }

    private bool TryResolveHelperPath(out string helperPath)
    {
        var availability = CheckAvailability();
        helperPath = availability.ExpectedPath;
        return availability.IsAvailable;
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
