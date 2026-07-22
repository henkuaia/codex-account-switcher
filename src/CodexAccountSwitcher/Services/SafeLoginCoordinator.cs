using CodexAccountSwitcher.Models;
using System.ComponentModel;
using System.IO;
using System.Runtime.ExceptionServices;

namespace CodexAccountSwitcher.Services;

public sealed record LoginResult(bool Succeeded, string Message, bool LaunchSucceeded)
{
    public bool CanRetryLaunch { get; init; }
}

public sealed class SafeLoginCoordinator
{
    private const string LoginSucceededMessage = "Account login verified.";
    private const string LoginFailedMessage =
        "Account login failed. The prior authentication state was restored.";
    private const string CancellationMessage =
        "Account login was canceled after Codex closed. The prior authentication state was restored.";
    private const string PreMutationCancellationMessage =
        "Account login was canceled before authentication changed. Codex was restarted.";
    private const string RecoveryFailureMessage =
        "Authentication state recovery could not be verified. Codex was not launched.";
    private const string SuccessfulLaunchFailureMessage =
        "Account login was verified, but Codex launch failed.";
    private const string FailedLaunchFailureMessage =
        "The prior authentication state was restored, but Codex launch failed.";
    private const string PreMutationFailureMessage =
        "Account login failed before authentication changed.";
    private const string PreMutationLaunchFailureMessage =
        "Account login failed before authentication changed, and Codex launch failed.";
    private const string CancellationLaunchFailureMessage =
        "Account login was canceled after Codex closed. " +
        "The prior authentication state was restored, but Codex launch failed.";
    private const string PreMutationCancellationLaunchFailureMessage =
        "Account login was canceled before authentication changed. Codex restart failed.";
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(8);

    private readonly CodexPackageInfo _package;
    private readonly string _codexHome;
    private readonly ICodexProcessController _processController;
    private readonly Func<ProcessOutputHandler, CancellationToken, Task<CommandResult>> _loginAsync;
    private readonly Func<string, CancellationToken, Task<AccountRegistry>> _loadRegistryAsync;
    private readonly Func<string, CancellationToken, Task<string>> _readAuthAccountIdAsync;
    private readonly Func<string, CancellationToken, Task<IAuthStateCheckpoint>> _captureAsync;
    private readonly Func<HelperAvailability> _checkHelperAvailability;

    public SafeLoginCoordinator(
        CodexPackageInfo package,
        string codexHome,
        ICodexProcessController processController,
        CodexAuthService codexAuthService,
        AccountRegistryService accountRegistryService)
        : this(
            package,
            codexHome,
            processController,
            codexAuthService.LoginAsync,
            accountRegistryService.LoadAsync,
            ReadAuthAccountIdAsync,
            CaptureAuthStateAsync,
            codexAuthService.CheckAvailability)
    {
    }

    internal SafeLoginCoordinator(
        CodexPackageInfo package,
        string codexHome,
        ICodexProcessController processController,
        Func<ProcessOutputHandler, CancellationToken, Task<CommandResult>> loginAsync,
        Func<string, CancellationToken, Task<AccountRegistry>> loadRegistryAsync,
        Func<string, CancellationToken, Task<string>> readAuthAccountIdAsync,
        Func<string, CancellationToken, Task<IAuthStateCheckpoint>> captureAsync,
        Func<HelperAvailability> checkHelperAvailability)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        _codexHome = codexHome;
        _processController = processController ?? throw new ArgumentNullException(nameof(processController));
        _loginAsync = loginAsync ?? throw new ArgumentNullException(nameof(loginAsync));
        _loadRegistryAsync = loadRegistryAsync ?? throw new ArgumentNullException(nameof(loadRegistryAsync));
        _readAuthAccountIdAsync = readAuthAccountIdAsync
            ?? throw new ArgumentNullException(nameof(readAuthAccountIdAsync));
        _captureAsync = captureAsync ?? throw new ArgumentNullException(nameof(captureAsync));
        _checkHelperAvailability = checkHelperAvailability
            ?? throw new ArgumentNullException(nameof(checkHelperAvailability));
    }

    public async Task<LoginResult> LoginAsync(
        ProcessOutputHandler outputHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outputHandler);
        cancellationToken.ThrowIfCancellationRequested();

        var helperAvailability = _checkHelperAvailability();
        if (!helperAvailability.IsAvailable)
        {
            return new LoginResult(false, helperAvailability.Error, true);
        }

        IAuthStateCheckpoint? checkpoint = null;
        var result = new LoginResult(false, LoginFailedMessage, false);
        var stage = LoginStage.Close;
        var recoveryResponsible = false;
        var restoreRequired = false;
        var launchAllowed = false;
        var priorStateRestored = false;
        var closeSideEffectsStarted = false;
        var forceRecoveryCommitted = false;
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
                stage = LoginStage.Force;
                await _processController.ForceTerminateAsync(
                    closeResult.RemainingProcessIds,
                    cancellationToken);
                forceRecoveryCommitted = true;
                cancellationToken.ThrowIfCancellationRequested();
            }

            stage = LoginStage.Capture;
            checkpoint = await _captureAsync(_codexHome, CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();

            stage = LoginStage.Login;
            var commandResult = await _loginAsync(outputHandler, cancellationToken);
            if (!commandResult.Succeeded)
            {
                restoreRequired = true;
                result = new LoginResult(false, LoginFailedMessage, false);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                stage = LoginStage.Verify;
                var registry = await _loadRegistryAsync(_codexHome, cancellationToken);
                var authAccountId = await _readAuthAccountIdAsync(_codexHome, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                var activeAccounts = registry.Accounts.Where(account => string.Equals(
                    account.AccountKey,
                    registry.ActiveAccountKey,
                    StringComparison.Ordinal)).ToArray();
                if (activeAccounts.Length != 1 ||
                    !string.Equals(
                        activeAccounts[0].ChatGptAccountId,
                        authAccountId,
                        StringComparison.Ordinal))
                {
                    restoreRequired = true;
                    result = new LoginResult(false, LoginFailedMessage, false);
                }
                else
                {
                    result = new LoginResult(true, LoginSucceededMessage, false);
                    launchAllowed = true;
                }
            }
        }
        catch (CodexCloseCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            if (exception.SideEffectsStarted)
            {
                launchAllowed = true;
                result = new LoginResult(false, PreMutationCancellationMessage, false);
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
                launchAllowed = true;
                result = new LoginResult(false, PreMutationCancellationMessage, false);
            }
            else
            {
                pendingException = ExceptionDispatchInfo.Capture(exception);
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            if (stage == LoginStage.Close ||
                (stage == LoginStage.Force &&
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
                result = new LoginResult(
                    false,
                    checkpoint is null ? PreMutationCancellationMessage : CancellationMessage,
                    false);
            }
        }
        catch (Exception exception) when (IsOperationalFailure(stage, exception))
        {
            restoreRequired = checkpoint is not null;
            launchAllowed = checkpoint is null && recoveryResponsible;
            result = new LoginResult(
                false,
                checkpoint is null ? PreMutationFailureMessage : LoginFailedMessage,
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
                    result = new LoginResult(false, RecoveryFailureMessage, false);
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
                        result = new LoginResult(
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
        return result;
    }

    private static bool IsOperationalFailure(LoginStage stage, Exception exception) => stage switch
    {
        LoginStage.Close or LoginStage.Force =>
            exception is Win32Exception or IOException or UnauthorizedAccessException,
        LoginStage.Capture =>
            exception is AuthStateCheckpointException or IOException or UnauthorizedAccessException,
        LoginStage.Login =>
            exception is Win32Exception or IOException or UnauthorizedAccessException,
        LoginStage.Verify =>
            exception is InvalidDataException or IOException or UnauthorizedAccessException,
        _ => false,
    };

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

    private enum LoginStage
    {
        Close,
        Force,
        Capture,
        Login,
        Verify,
    }
}
