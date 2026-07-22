using CodexAccountSwitcher.Models;
using System.ComponentModel;
using System.IO;
using System.Runtime.ExceptionServices;

namespace CodexAccountSwitcher.Services;

public sealed record RemovalResult(bool Succeeded, string Message);

internal interface IRemovalStateCheckpoint : IDisposable
{
    Task<bool> RestoreAndVerifyAsync(CancellationToken cancellationToken);

    Task<bool> VerifyAuthUnchangedAsync(CancellationToken cancellationToken);
}

public sealed class TargetedRemoveCoordinator
{
    private const string ActiveRemovalMessage =
        "The active account cannot be removed. Switch to another account first.";
    private const string SelectorUnavailableMessage = "No unique account selector is available.";
    private const string RemovalSucceededMessage = "Account removal verified.";
    private const string RemovalFailedMessage =
        "Account removal failed. The prior account state was restored.";
    private const string CancellationMessage =
        "Account removal was canceled. The prior account state was restored.";
    private const string RecoveryFailureMessage = "Account removal recovery could not be verified.";
    private const string PreMutationFailureMessage = "Account removal failed before account state changed.";

    private readonly string _codexHome;
    private readonly Func<string, CancellationToken, Task<CommandResult>> _removeAsync;
    private readonly Func<string, CancellationToken, Task<AccountRegistry>> _loadRegistryAsync;
    private readonly Func<string, CancellationToken, Task<string>> _readAuthAccountIdAsync;
    private readonly Func<string, string, CancellationToken, Task<IRemovalStateCheckpoint>> _captureAsync;
    private readonly Func<HelperAvailability> _checkHelperAvailability;

    public TargetedRemoveCoordinator(
        string codexHome,
        CodexAuthService codexAuthService,
        AccountRegistryService accountRegistryService)
        : this(
            codexHome,
            codexAuthService.RemoveAsync,
            accountRegistryService.LoadAsync,
            ReadAuthAccountIdAsync,
            CaptureRemovalStateAsync,
            codexAuthService.CheckAvailability)
    {
    }

    internal TargetedRemoveCoordinator(
        string codexHome,
        Func<string, CancellationToken, Task<CommandResult>> removeAsync,
        Func<string, CancellationToken, Task<AccountRegistry>> loadRegistryAsync,
        Func<string, CancellationToken, Task<string>> readAuthAccountIdAsync,
        Func<string, string, CancellationToken, Task<IRemovalStateCheckpoint>> captureAsync,
        Func<HelperAvailability> checkHelperAvailability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        _codexHome = codexHome;
        _removeAsync = removeAsync ?? throw new ArgumentNullException(nameof(removeAsync));
        _loadRegistryAsync = loadRegistryAsync ?? throw new ArgumentNullException(nameof(loadRegistryAsync));
        _readAuthAccountIdAsync = readAuthAccountIdAsync
            ?? throw new ArgumentNullException(nameof(readAuthAccountIdAsync));
        _captureAsync = captureAsync ?? throw new ArgumentNullException(nameof(captureAsync));
        _checkHelperAvailability = checkHelperAvailability
            ?? throw new ArgumentNullException(nameof(checkHelperAvailability));
    }

    public async Task<RemovalResult> RemoveAsync(
        AccountRecord target,
        AccountRegistry before,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(before);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.Equals(before.ActiveAccountKey, target.AccountKey, StringComparison.Ordinal))
        {
            return new RemovalResult(false, ActiveRemovalMessage);
        }

        var selector = AccountSelectorResolver.Resolve(target, before.Accounts);
        if (!selector.IsAvailable)
        {
            return new RemovalResult(false, SelectorUnavailableMessage);
        }

        var helperAvailability = _checkHelperAvailability();
        if (!helperAvailability.IsAvailable)
        {
            return new RemovalResult(false, helperAvailability.Error);
        }

        var beforeActiveAccounts = before.Accounts.Where(account => string.Equals(
            account.AccountKey,
            before.ActiveAccountKey,
            StringComparison.Ordinal)).ToArray();
        if (beforeActiveAccounts.Length != 1)
        {
            return new RemovalResult(false, PreMutationFailureMessage);
        }

        IRemovalStateCheckpoint? checkpoint = null;
        var result = new RemovalResult(false, RemovalFailedMessage);
        var stage = RemovalStage.Capture;
        var restoreRequired = false;
        ExceptionDispatchInfo? pendingException = null;

        try
        {
            checkpoint = await _captureAsync(_codexHome, target.AccountKey, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            stage = RemovalStage.Remove;
            var commandResult = await _removeAsync(selector.Value!, cancellationToken);
            if (!commandResult.Succeeded)
            {
                restoreRequired = true;
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                stage = RemovalStage.Verify;
                var registry = await _loadRegistryAsync(_codexHome, cancellationToken);
                var authAccountId = await _readAuthAccountIdAsync(_codexHome, cancellationToken);
                var authUnchanged = await checkpoint.VerifyAuthUnchangedAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                var afterActiveAccounts = registry.Accounts.Where(account => string.Equals(
                    account.AccountKey,
                    registry.ActiveAccountKey,
                    StringComparison.Ordinal)).ToArray();
                var targetRemoved = registry.Accounts.All(account => !string.Equals(
                    account.AccountKey,
                    target.AccountKey,
                    StringComparison.Ordinal));
                var activePreserved = string.Equals(
                        registry.ActiveAccountKey,
                        before.ActiveAccountKey,
                        StringComparison.Ordinal) &&
                    afterActiveAccounts.Length == 1 &&
                    string.Equals(
                        afterActiveAccounts[0].ChatGptAccountId,
                        beforeActiveAccounts[0].ChatGptAccountId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        authAccountId,
                        beforeActiveAccounts[0].ChatGptAccountId,
                        StringComparison.Ordinal);

                if (!targetRemoved || !activePreserved || !authUnchanged)
                {
                    restoreRequired = true;
                }
                else
                {
                    result = new RemovalResult(true, RemovalSucceededMessage);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (checkpoint is null)
            {
                throw;
            }

            restoreRequired = true;
            result = new RemovalResult(false, CancellationMessage);
        }
        catch (Exception exception) when (IsOperationalFailure(stage, exception))
        {
            restoreRequired = checkpoint is not null;
            result = new RemovalResult(
                false,
                checkpoint is null ? PreMutationFailureMessage : RemovalFailedMessage);
        }
        catch (Exception exception)
        {
            pendingException = ExceptionDispatchInfo.Capture(exception);
            restoreRequired = checkpoint is not null;
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

                if (!restored)
                {
                    result = new RemovalResult(false, RecoveryFailureMessage);
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
        }

        pendingException?.Throw();
        return result;
    }

    private static bool IsOperationalFailure(RemovalStage stage, Exception exception) => stage switch
    {
        RemovalStage.Capture =>
            exception is RemovalStateCheckpointException or IOException or UnauthorizedAccessException,
        RemovalStage.Remove => exception is Win32Exception or IOException or UnauthorizedAccessException,
        RemovalStage.Verify => exception is InvalidDataException or IOException or UnauthorizedAccessException,
        _ => false,
    };

    private static bool IsOperationalRestoreFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;

    private static async Task<string> ReadAuthAccountIdAsync(
        string codexHome,
        CancellationToken cancellationToken)
    {
        using var snapshot = await new AuthSnapshotReader().ReadAsync(
            Path.Combine(codexHome, "auth.json"),
            cancellationToken);
        return snapshot.AccountId;
    }

    private static async Task<IRemovalStateCheckpoint> CaptureRemovalStateAsync(
        string codexHome,
        string accountKey,
        CancellationToken cancellationToken) =>
        await RemovalStateTransaction.CaptureAsync(codexHome, accountKey, cancellationToken);

    private enum RemovalStage
    {
        Capture,
        Remove,
        Verify,
    }
}
