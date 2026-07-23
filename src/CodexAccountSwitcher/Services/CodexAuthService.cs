using CodexAccountSwitcher.Security;
using System.IO;

namespace CodexAccountSwitcher.Services;

public sealed record HelperAvailability(bool IsAvailable, string ExpectedPath, string Error);

public sealed class CodexAuthService
{
    private const string HelperFileName = "codex-auth.exe";
    private const string SkipServiceReconcileVariable = "CODEX_AUTH_SKIP_SERVICE_RECONCILE";
    private const string CliStagingFailureMessage = "The Codex CLI could not be prepared.";
    private readonly string _helperPath;
    private readonly string _codexCliDirectory;
    private readonly ICodexCliStager _codexCliStager;
    private readonly IProcessRunner _processRunner;

    public CodexAuthService(string helperPath, string codexCliDirectory, IProcessRunner? processRunner = null)
        : this(
            helperPath,
            codexCliDirectory,
            processRunner ?? new ProcessRunner(),
            new CodexCliStager())
    {
    }

    internal CodexAuthService(
        string helperPath,
        string codexCliDirectory,
        IProcessRunner processRunner,
        ICodexCliStager codexCliStager)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexCliDirectory);

        _helperPath = helperPath;
        _codexCliDirectory = codexCliDirectory;
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _codexCliStager = codexCliStager ?? throw new ArgumentNullException(nameof(codexCliStager));
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

    private async Task<CommandResult> LoginAsyncCore(
        ProcessOutputHandler? outputHandler,
        CancellationToken cancellationToken)
    {
        string stagedCliDirectory;
        try
        {
            stagedCliDirectory = await _codexCliStager.StageAsync(
                _codexCliDirectory,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException or NotSupportedException)
        {
            return CommandResult.Failed(CliStagingFailureMessage);
        }

        if (!TryResolveCliDirectory(stagedCliDirectory, out var cliDirectory))
        {
            return CommandResult.Failed("The Codex CLI directory is unavailable.");
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        var childPath = string.IsNullOrEmpty(path)
            ? cliDirectory
            : string.Concat(cliDirectory, Path.PathSeparator, path);
        var environment = CreateMutationEnvironment(childPath);

        return outputHandler is null
            ? await RunCapturedAsync(["login"], environment, cancellationToken)
            : await RunCapturedAsync(
                ["login"],
                environment,
                outputHandler,
                cancellationToken);
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

    private static bool TryResolveCliDirectory(string requestedDirectory, out string cliDirectory)
    {
        cliDirectory = string.Empty;
        try
        {
            cliDirectory = Path.GetFullPath(requestedDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return Directory.Exists(cliDirectory);
    }
}
