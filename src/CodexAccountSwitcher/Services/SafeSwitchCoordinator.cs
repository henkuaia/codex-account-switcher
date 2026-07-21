using System.IO;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public sealed record SwitchResult(bool Succeeded, string Message, bool LaunchSucceeded);

public sealed class SafeSwitchCoordinator
{
    private const string AlreadyActiveMessage = "The requested account is already active.";
    private const string SelectorUnavailableMessage = "No unique account selector is available.";
    private const string SwitchSucceededMessage = "Account switch verified.";
    private const string SwitchFailedMessage =
        "Account switch failed. The prior authentication state was restored.";
    private const string CancellationMessage =
        "Account switch was canceled after Codex closed. The prior authentication state was restored.";
    private const string RecoveryFailureMessage =
        "Authentication state recovery could not be verified. Codex was not launched.";
    private const string SuccessfulLaunchFailureMessage =
        "Account switch was verified, but Codex launch failed.";
    private const string FailedLaunchFailureMessage =
        "The prior authentication state was restored, but Codex launch failed.";
    private const string CancellationLaunchFailureMessage =
        "Account switch was canceled after Codex closed. " +
        "The prior authentication state was restored, but Codex launch failed.";
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(8);

    private readonly CodexPackageInfo _package;
    private readonly string _codexHome;
    private readonly ICodexProcessController _processController;
    private readonly Func<string, CancellationToken, Task<CommandResult>> _switchAsync;
    private readonly Func<string, CancellationToken, Task<AccountRegistry>> _loadRegistryAsync;
    private readonly Func<string, CancellationToken, Task<string>> _readAuthAccountIdAsync;
    private readonly Func<string, CancellationToken, Task<IAuthStateCheckpoint>> _captureAsync;

    public SafeSwitchCoordinator(
        CodexPackageInfo package,
        string codexHome,
        ICodexProcessController processController,
        CodexAuthService codexAuthService,
        AccountRegistryService accountRegistryService)
        : this(
            package,
            codexHome,
            processController,
            CreateSwitchDelegate(codexAuthService),
            CreateRegistryDelegate(accountRegistryService),
            ReadAuthAccountIdAsync,
            CaptureAuthStateAsync)
    {
    }

    internal SafeSwitchCoordinator(
        CodexPackageInfo package,
        string codexHome,
        ICodexProcessController processController,
        Func<string, CancellationToken, Task<CommandResult>> switchAsync,
        Func<string, CancellationToken, Task<AccountRegistry>> loadRegistryAsync,
        Func<string, CancellationToken, Task<string>> readAuthAccountIdAsync,
        Func<string, CancellationToken, Task<IAuthStateCheckpoint>> captureAsync)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        _codexHome = codexHome;
        _processController = processController ?? throw new ArgumentNullException(nameof(processController));
        _switchAsync = switchAsync ?? throw new ArgumentNullException(nameof(switchAsync));
        _loadRegistryAsync = loadRegistryAsync ?? throw new ArgumentNullException(nameof(loadRegistryAsync));
        _readAuthAccountIdAsync = readAuthAccountIdAsync
            ?? throw new ArgumentNullException(nameof(readAuthAccountIdAsync));
        _captureAsync = captureAsync ?? throw new ArgumentNullException(nameof(captureAsync));
    }

    public async Task<SwitchResult> SwitchAsync(
        AccountRecord target,
        AccountRegistry before,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(before);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.Equals(before.ActiveAccountKey, target.AccountKey, StringComparison.Ordinal))
        {
            return new SwitchResult(true, AlreadyActiveMessage, true);
        }

        var selector = AccountSelectorResolver.Resolve(target, before.Accounts);
        if (!selector.IsAvailable)
        {
            return new SwitchResult(false, SelectorUnavailableMessage, true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var closeResult = await _processController.CloseAsync(_package, CloseTimeout, cancellationToken);
        if (!closeResult.AllExited)
        {
            await _processController.ForceTerminateAsync(
                closeResult.RemainingProcessIds,
                cancellationToken);
        }

        IAuthStateCheckpoint? checkpoint = null;
        var result = new SwitchResult(false, SwitchFailedMessage, false);
        var restoreRequired = false;
        var launchAllowed = false;

        try
        {
            checkpoint = await _captureAsync(_codexHome, CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();

            var commandResult = await _switchAsync(selector.Value!, cancellationToken);
            if (!commandResult.Succeeded)
            {
                restoreRequired = true;
                result = new SwitchResult(false, SwitchFailedMessage, false);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                var registry = await _loadRegistryAsync(_codexHome, cancellationToken);
                var authAccountId = await _readAuthAccountIdAsync(_codexHome, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                if (!string.Equals(
                        registry.ActiveAccountKey,
                        target.AccountKey,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        authAccountId,
                        target.ChatGptAccountId,
                        StringComparison.Ordinal))
                {
                    restoreRequired = true;
                    result = new SwitchResult(false, SwitchFailedMessage, false);
                }
                else
                {
                    result = new SwitchResult(true, SwitchSucceededMessage, false);
                    launchAllowed = true;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            restoreRequired = true;
            result = new SwitchResult(false, CancellationMessage, false);
        }
        catch (Exception)
        {
            restoreRequired = true;
            result = new SwitchResult(false, SwitchFailedMessage, false);
        }
        finally
        {
            if (restoreRequired)
            {
                var restored = checkpoint is not null &&
                    await RestoreWithoutCancellationAsync(checkpoint);
                if (restored)
                {
                    launchAllowed = true;
                }
                else
                {
                    launchAllowed = false;
                    result = new SwitchResult(false, RecoveryFailureMessage, false);
                }
            }

            checkpoint?.Dispose();

            if (launchAllowed)
            {
                try
                {
                    await _processController.LaunchAsync(_package, CancellationToken.None);
                    result = result with { LaunchSucceeded = true };
                }
                catch (Exception)
                {
                    result = new SwitchResult(
                        result.Succeeded,
                        result.Succeeded
                            ? SuccessfulLaunchFailureMessage
                            : string.Equals(result.Message, CancellationMessage, StringComparison.Ordinal)
                                ? CancellationLaunchFailureMessage
                                : FailedLaunchFailureMessage,
                        false);
                }
            }
        }

        return result;
    }

    private static async Task<bool> RestoreWithoutCancellationAsync(IAuthStateCheckpoint checkpoint)
    {
        try
        {
            return await checkpoint.RestoreAndVerifyAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<string> ReadAuthAccountIdAsync(
        string codexHome,
        CancellationToken cancellationToken)
    {
        using var snapshot = await new AuthSnapshotReader().ReadAsync(
            Path.Combine(codexHome, "auth.json"),
            cancellationToken);
        return snapshot.AccountId;
    }

    private static async Task<IAuthStateCheckpoint> CaptureAuthStateAsync(
        string codexHome,
        CancellationToken cancellationToken) =>
        await AuthStateTransaction.CaptureAsync(codexHome, cancellationToken);

    private static Func<string, CancellationToken, Task<CommandResult>> CreateSwitchDelegate(
        CodexAuthService codexAuthService)
    {
        ArgumentNullException.ThrowIfNull(codexAuthService);
        return codexAuthService.SwitchAsync;
    }

    private static Func<string, CancellationToken, Task<AccountRegistry>> CreateRegistryDelegate(
        AccountRegistryService accountRegistryService)
    {
        ArgumentNullException.ThrowIfNull(accountRegistryService);
        return accountRegistryService.LoadAsync;
    }
}
