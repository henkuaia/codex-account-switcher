using System.IO;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public sealed record SwitchResult(bool Succeeded, string Message, bool LaunchSucceeded)
{
    public bool CanRetryLaunch { get; init; }

    public HelperAvailability? HelperAvailability { get; init; }
}

public sealed class SafeSwitchCoordinator
{
    private const string AlreadyActiveMessage = "The requested account is already active.";
    private const string SelectorUnavailableMessage = "No unique account selector is available.";
    private const string SwitchSucceededMessage = "Account switch verified.";
    private const string SwitchFailedMessage =
        "Account switch failed. The prior authentication state was restored.";
    private const string CancellationMessage =
        "Account switch was canceled after Codex closed. The prior authentication state was restored.";
    private const string PreMutationCancellationMessage =
        "Account switch was canceled before authentication changed. Codex was restarted.";
    private const string UnverifiedExitCancellationMessage =
        "Account switch was canceled before authentication changed. " +
        "Codex was not launched because process exit could not be verified.";
    private const string RecoveryFailureMessage =
        "Authentication state recovery could not be verified. Codex was not launched.";
    private const string UnknownHelperExitMessage =
        "Codex remains closed because helper process exit could not be verified.";
    private const string SuccessfulLaunchFailureMessage =
        "Account switch was verified, but Codex launch failed.";
    private const string FailedLaunchFailureMessage =
        "The prior authentication state was restored, but Codex launch failed.";
    private const string PreMutationFailureMessage =
        "Account switch failed before authentication changed.";
    private const string PreMutationLaunchFailureMessage =
        "Account switch failed before authentication changed, and Codex launch failed.";
    private const string CancellationLaunchFailureMessage =
        "Account switch was canceled after Codex closed. " +
        "The prior authentication state was restored, but Codex launch failed.";
    private const string PreMutationCancellationLaunchFailureMessage =
        "Account switch was canceled before authentication changed. Codex restart failed.";
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(8);

    private readonly CodexPackageInfo _package;
    private readonly string _codexHome;
    private readonly ICodexProcessController _processController;
    private readonly Func<string, CancellationToken, Task<CommandResult>> _switchAsync;
    private readonly Func<string, CancellationToken, Task<AccountRegistry>> _loadRegistryAsync;
    private readonly Func<string, CancellationToken, Task<string>> _readAuthAccountIdAsync;
    private readonly Func<string, CancellationToken, Task<IAuthStateCheckpoint>> _captureAsync;
    private readonly Func<HelperAvailability> _checkHelperAvailability;

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
            CaptureAuthStateAsync,
            CreateAvailabilityDelegate(codexAuthService))
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
        : this(
            package,
            codexHome,
            processController,
            switchAsync,
            loadRegistryAsync,
            readAuthAccountIdAsync,
            captureAsync,
            static () => new HelperAvailability(true, "codex-auth.exe", string.Empty))
    {
    }

    internal SafeSwitchCoordinator(
        CodexPackageInfo package,
        string codexHome,
        ICodexProcessController processController,
        Func<string, CancellationToken, Task<CommandResult>> switchAsync,
        Func<string, CancellationToken, Task<AccountRegistry>> loadRegistryAsync,
        Func<string, CancellationToken, Task<string>> readAuthAccountIdAsync,
        Func<string, CancellationToken, Task<IAuthStateCheckpoint>> captureAsync,
        Func<HelperAvailability> checkHelperAvailability)
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
        _checkHelperAvailability = checkHelperAvailability
            ?? throw new ArgumentNullException(nameof(checkHelperAvailability));
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
            try
            {
                var activeAuthAccountId = await _readAuthAccountIdAsync(_codexHome, cancellationToken);
                if (string.Equals(
                    activeAuthAccountId,
                    target.ChatGptAccountId,
                    StringComparison.Ordinal))
                {
                    return new SwitchResult(true, AlreadyActiveMessage, true);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsOperationalNoOpVerificationFailure(exception))
            {
            }
        }

        var selector = AccountSelectorResolver.Resolve(target, before.Accounts);
        if (!selector.IsAvailable)
        {
            return new SwitchResult(false, SelectorUnavailableMessage, true);
        }

        var helperAvailability = _checkHelperAvailability();
        if (!helperAvailability.IsAvailable)
        {
            return new SwitchResult(false, helperAvailability.Error, true)
            {
                HelperAvailability = helperAvailability,
            };
        }

        IAuthStateCheckpoint? checkpoint = null;
        var result = new SwitchResult(false, SwitchFailedMessage, false);
        var stage = SwitchStage.Close;
        var recoveryResponsible = false;
        var restoreRequired = false;
        var launchAllowed = false;
        var priorStateRestored = false;
        var closeSideEffectsStarted = false;
        var forceRecoveryCommitted = false;
        HelperAvailability? resultHelperAvailability = null;
        ExceptionDispatchInfo? pendingException = null;

        try
        {
            recoveryResponsible = true;
            var closeResult = await _processController.CloseAsync(
                _package,
                CloseTimeout,
                cancellationToken);
            closeSideEffectsStarted = closeResult.SideEffectsStarted;
            if (!closeResult.AllExited)
            {
                stage = SwitchStage.Force;
                await _processController.ForceTerminateAsync(
                    closeResult.RemainingProcessIds,
                    cancellationToken);
                forceRecoveryCommitted = true;
                cancellationToken.ThrowIfCancellationRequested();
            }

            stage = SwitchStage.Capture;
            checkpoint = await _captureAsync(_codexHome, CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();

            stage = SwitchStage.Switch;
            var commandResult = await _switchAsync(selector.Value!, cancellationToken);
            if (!commandResult.Succeeded)
            {
                resultHelperAvailability = GetUnavailableHelperAvailability();
                restoreRequired = true;
                result = new SwitchResult(false, SwitchFailedMessage, false);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                stage = SwitchStage.Verify;
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
        catch (CodexCloseCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            if (exception.SideEffectsStarted)
            {
                launchAllowed = false;
                result = new SwitchResult(false, UnverifiedExitCancellationMessage, false);
            }
            else
            {
                pendingException = ExceptionDispatchInfo.Capture(exception);
            }
        }
        catch (CodexForceTerminateCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            if (closeSideEffectsStarted || exception.SideEffectsStarted)
            {
                launchAllowed = false;
                result = new SwitchResult(false, UnverifiedExitCancellationMessage, false);
            }
            else
            {
                pendingException = ExceptionDispatchInfo.Capture(exception);
            }
        }
        catch (HelperProcessExitUnverifiedException)
        {
            restoreRequired = false;
            launchAllowed = false;
            result = new SwitchResult(false, UnknownHelperExitMessage, false);
        }
        catch (CodexProcessDiscoveryException) when (stage is SwitchStage.Close or SwitchStage.Force)
        {
            restoreRequired = false;
            launchAllowed = false;
            result = new SwitchResult(false, PreMutationFailureMessage, false);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            if (stage == SwitchStage.Close ||
                (stage == SwitchStage.Force &&
                    checkpoint is null &&
                    !closeSideEffectsStarted &&
                    !forceRecoveryCommitted))
            {
                pendingException = ExceptionDispatchInfo.Capture(exception);
            }
            else
            {
                restoreRequired = checkpoint is not null;
                launchAllowed = checkpoint is null && recoveryResponsible;
                result = new SwitchResult(
                    false,
                    checkpoint is null ? PreMutationCancellationMessage : CancellationMessage,
                    false);
            }
        }
        catch (Exception exception) when (IsOperationalFailure(stage, exception))
        {
            if (stage == SwitchStage.Switch)
            {
                resultHelperAvailability = GetUnavailableHelperAvailability();
            }

            restoreRequired = checkpoint is not null;
            launchAllowed = checkpoint is null && recoveryResponsible;
            result = new SwitchResult(
                false,
                checkpoint is null ? PreMutationFailureMessage : SwitchFailedMessage,
                false);
        }
        catch (Exception exception)
        {
            pendingException = ExceptionDispatchInfo.Capture(exception);
            restoreRequired = checkpoint is not null;
            launchAllowed = checkpoint is null && recoveryResponsible;
        }
        finally
        {
            if (restoreRequired)
            {
                var restored = false;
                try
                {
                    restored = checkpoint is not null &&
                        await checkpoint.RestoreAndVerifyAsync(CancellationToken.None);
                }
                catch (Exception exception) when (IsOperationalRestoreFailure(exception))
                {
                    restored = false;
                }
                catch (Exception exception)
                {
                    pendingException ??= ExceptionDispatchInfo.Capture(exception);
                }

                if (restored)
                {
                    launchAllowed = true;
                    priorStateRestored = true;
                }
                else
                {
                    launchAllowed = false;
                    result = new SwitchResult(false, RecoveryFailureMessage, false);
                }
            }

            try
            {
                checkpoint?.Dispose();
            }
            catch (Exception exception)
            {
                pendingException ??= ExceptionDispatchInfo.Capture(exception);
            }

            if (launchAllowed)
            {
                try
                {
                    await _processController.LaunchAsync(_package, CancellationToken.None);
                    result = result with { LaunchSucceeded = true };
                }
                catch (Exception exception) when (IsOperationalLaunchFailure(exception))
                {
                    if (pendingException is null)
                    {
                        result = new SwitchResult(
                            result.Succeeded,
                            result.Succeeded
                                ? SuccessfulLaunchFailureMessage
                                : string.Equals(
                                    result.Message,
                                    PreMutationCancellationMessage,
                                    StringComparison.Ordinal)
                                    ? PreMutationCancellationLaunchFailureMessage
                                : string.Equals(result.Message, CancellationMessage, StringComparison.Ordinal)
                                    ? CancellationLaunchFailureMessage
                                    : priorStateRestored
                                        ? FailedLaunchFailureMessage
                                        : PreMutationLaunchFailureMessage,
                            false)
                        {
                            CanRetryLaunch = true,
                        };
                    }
                }
                catch (Exception exception)
                {
                    pendingException ??= ExceptionDispatchInfo.Capture(exception);
                }
            }
        }

        pendingException?.Throw();
        if (resultHelperAvailability is not null)
        {
            result = result with { HelperAvailability = resultHelperAvailability };
        }

        return result;
    }

    private HelperAvailability? GetUnavailableHelperAvailability()
    {
        var availability = _checkHelperAvailability();
        return availability.IsAvailable ? null : availability;
    }

    public async Task<bool> RetryLaunchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _processController.LaunchAsync(_package, cancellationToken);
            return true;
        }
        catch (Exception exception) when (IsOperationalLaunchFailure(exception))
        {
            return false;
        }
    }

    private static bool IsOperationalFailure(SwitchStage stage, Exception exception) => stage switch
    {
        SwitchStage.Close or SwitchStage.Force =>
            exception is Win32Exception or IOException or UnauthorizedAccessException,
        SwitchStage.Capture =>
            exception is AuthStateCheckpointException or IOException or UnauthorizedAccessException,
        SwitchStage.Switch =>
            exception is HelperProcessStartException or Win32Exception or IOException or UnauthorizedAccessException,
        SwitchStage.Verify =>
            exception is InvalidDataException or IOException or UnauthorizedAccessException,
        _ => false,
    };

    private static bool IsOperationalNoOpVerificationFailure(Exception exception) =>
        exception is InvalidDataException or IOException or UnauthorizedAccessException;

    private static bool IsOperationalRestoreFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;

    private static bool IsOperationalLaunchFailure(Exception exception) =>
        exception is CodexLaunchException or Win32Exception or IOException or UnauthorizedAccessException;

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

    private static Func<HelperAvailability> CreateAvailabilityDelegate(CodexAuthService codexAuthService)
    {
        ArgumentNullException.ThrowIfNull(codexAuthService);
        return codexAuthService.CheckAvailability;
    }

    private enum SwitchStage
    {
        Close,
        Force,
        Capture,
        Switch,
        Verify,
    }
}
