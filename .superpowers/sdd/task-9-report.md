# Task 9 Report: Transactional Safe Account Switch

## Scope and Base

- Base commit: `621318ca5c7b575f88a795eb8768a7b923a5945c`.
- Branch: `feature/codex-account-switcher`.
- Scope is limited to the safe-switch coordinator, exact auth-state checkpoint/restore,
  and fake/temp-only tests. No UI wiring, dependency-injection package, unrelated
  refactor, live Codex process action, real helper command, or live auth-state access
  was added or executed.

## Changed Files

- `src/CodexAccountSwitcher/Services/AuthStateTransaction.cs`
  - Captures exact presence and bytes of `auth.json` and
    `accounts/registry.json` into owned in-memory buffers.
  - Restores present files through same-directory temporary files followed by an
    overwrite move, deletes files that were absent at checkpoint time, then reads
    both files and verifies the pair byte-for-byte.
  - Clears checkpoint, verification, and failed partial-read buffers with
    `CryptographicOperations.ZeroMemory`.
  - Uses fixed checkpoint errors and a content-free `ToString()`.
- `src/CodexAccountSwitcher/Services/SafeSwitchCoordinator.cs`
  - Rejects pre-cancellation, already-active targets, and unavailable selectors
    before close side effects.
  - Closes with exactly eight seconds and passes only the immediately returned
    `RemainingProcessIds` to force termination.
  - Captures auth state after close/force and immediately before the helper.
  - Verifies both registry `ActiveAccountKey` and direct local `auth.json`
    `account_id` before reporting target success.
  - Restores and pair-verifies prior bytes for helper failure, verification failure,
    or post-close cancellation; all recovery and launch work uses
    `CancellationToken.None`.
  - Suppresses launch when recovery verification fails and reports switch success
    independently from launch success.
- `tests/CodexAccountSwitcher.Tests/AuthStateTransactionTests.cs`
  - Uses only temporary directories or a fake filesystem seam.
  - Covers exact binary restore, absent-file deletion, pair verification failure,
    sanitized checkpoint failure, buffer clearing, content-free rendering, and
    disposed use rejection.
- `tests/CodexAccountSwitcher.Tests/SafeSwitchCoordinatorTests.cs`
  - Uses operation-order fakes only; no real process, package, helper, auth, or launch
    action occurs.
  - Covers success order, helper nonzero after simulated mutation, registry and auth
    mismatches, verification error, exact force IDs, eight-second timeout,
    already-active and unavailable no-close paths, pre-close and post-close
    cancellation, restore verification failure/no launch, and target/recovery launch
    failures.

## TDD Evidence

### Initial RED

Command:

```powershell
.\.tools\dotnet\dotnet.exe test tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~AuthStateTransactionTests|FullyQualifiedName~SafeSwitchCoordinatorTests"
```

Exit code: `1`.

The compiler reported the intended missing production contracts:

```text
CS0246: IAuthStateFileSystem could not be found
CS0246: SafeSwitchCoordinator could not be found
CS0246: IAuthStateCheckpoint could not be found
```

No production Task 9 file existed at this RED point.

### Implementation-only Corrections

The first implementation build found `CS8978` for nullable method-group use in the
public coordinator constructor. Fixed helper methods now validate the concrete
services before returning delegates.

The following focused build found `CS8852` because a fake corruption-control property
was `init`-only but configured after checkpoint capture. Only the test fake setter was
changed.

Neither correction changed requested behavior or weakened a test.

### Initial Focused GREEN

The same focused command then passed:

```text
Passed: 17, Failed: 0, Skipped: 0
```

### Cancellation/Launch Message RED

Self-review added a focused regression test for post-close cancellation followed by
recovery-launch failure:

```powershell
.\.tools\dotnet\dotnet.exe test tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~Cancellation_recovery_launch_failure_is_reported_without_losing_cancellation_context
```

Exit code: `1`. The expected message retained cancellation context, while the actual
message only reported restored state plus launch failure. A fixed
cancellation-plus-launch-failure message corrected that result without changing
ordering or status fields.

### Final Focused GREEN

Command:

```powershell
.\.tools\dotnet\dotnet.exe test tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~AuthStateTransactionTests|FullyQualifiedName~SafeSwitchCoordinatorTests"
```

Result: exit `0`; `18/18` passed, `0` failed, `0` skipped.

### Final Full Solution

Command:

```powershell
.\.tools\dotnet\dotnet.exe test CodexAccountSwitcher.sln -c Debug --no-restore
```

Result: exit `0`; `115/115` passed, `0` failed, `0` skipped.

## Self-review

- Pre-cancellation is checked before selector/close side effects. Already-active and
  unavailable-selector paths return without any fake operation.
- The coordinator passes the caller token only through close, force, helper, and
  target verification. Checkpoint capture after closure, restore, and launch use
  non-cancelled recovery tokens.
- `ForceTerminateAsync` receives the exact `CloseAsync.RemainingProcessIds` object;
  the coordinator performs no PID discovery or expansion.
- Helper stdout/stderr and exception text are never incorporated into result messages.
  Verification, checkpoint, restore, and launch failures use fixed messages.
- Helper success alone is insufficient. The registry key and auth account ID are
  both read locally and must exactly match the requested target.
- Every failure after a checkpoint requires restore. Launch is enabled only after
  either target verification or successful pair verification of restored prior bytes.
- A restore exception or byte mismatch yields `Succeeded=false`,
  `LaunchSucceeded=false`, and no launch operation.
- The checkpoint is disposed before launch. Its owned byte arrays are zeroed, and
  temporary read buffers are zeroed on verification completion or failed partial read.
- Present files are written to a unique temporary file in the same directory,
  flushed to disk, and moved over the destination. Absent checkpoint files are
  deleted. Both final states are read before the pair result is accepted.
- No new public dependency abstraction or DI framework was introduced. Production
  construction still consumes the existing concrete auth/registry services and the
  existing `ICodexProcessController`; narrow delegates and internal filesystem/checkpoint
  seams exist only to make orchestration and file behavior deterministic in tests.
- `git diff --check` passed before staging; the staged check is run immediately before
  commit.

## Concerns and Unverified Boundaries

- Two filesystem paths cannot be replaced jointly atomically. The implementation
  follows the accepted contract: each file uses a same-directory atomic namespace
  replacement/delete, and Codex launch remains blocked until both resulting files
  are read and verified as one pair.
- Real Windows durability/atomicity behavior for `FileOptions.WriteThrough`,
  `Flush(true)`, and overwrite move was not fault-injected across power loss or disk
  failure. Unit tests validate exact bytes and pair gating in temporary directories.
- The real `codex-auth` helper, Microsoft Store package, process close/force, AppsFolder
  launch, and live `%USERPROFILE%\.codex` files were intentionally not exercised.
  Their integration remains a controlled manual-test boundary.
- External processes modifying auth files between checkpoint, helper completion, and
  verification are outside the coordinator's locking authority. Such a mismatch is
  detected and routed through exact-byte recovery; an unverifiable recovery suppresses
  launch.

## Review Fixes (2026-07-21)

### Findings Addressed

- Recovery responsibility now becomes active immediately before invoking
  `CloseAsync`. Cancellation or unexpected failure from close/force therefore enters
  non-cancelled launch cleanup even when no checkpoint exists yet.
- Close/force cancellation after a simulated process side effect returns the accepted
  structured cancellation result instead of rethrowing. It performs no checkpoint or
  restore because the helper has not been invoked.
- Routine results are limited to stage-specific operational exceptions. Unexpected
  close, force, capture, helper, verification, restore, dispose, and launch exceptions
  are captured with `ExceptionDispatchInfo`, cleanup runs, and the original exception
  is rethrown with its type/message/stack preserved.
- A dedicated `AuthStateCheckpointException` distinguishes the transaction's fixed
  operational checkpoint failure from arbitrary `InvalidOperationException` internal
  failures.
- Launch recognizes only the process controller's defined fixed
  `InvalidOperationException("Codex launch failed.")` plus OS/file failures as routine.
  Other invalid launch state is rethrown.
- Capture ownership transfer is explicit. A `finally` block zeroes every buffer read
  before any exception unless ownership was successfully transferred to the returned
  transaction.
- The concrete filesystem has a narrow replace delegate for fault injection. Both
  successful replacement and injected move failure tests assert that no
  `.<name>.<guid>.tmp` file remains.

### Review RED Evidence

The first focused run after adding the review tests stopped at the expected missing
fault-injection seam:

```text
CS1729: AuthStateFileSystem does not contain a constructor that takes 1 argument
```

After adding only that constructor/delegate seam, the focused command reached behavior:

```powershell
.\.tools\dotnet\dotnet.exe test tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~AuthStateTransactionTests|FullyQualifiedName~SafeSwitchCoordinatorTests"
```

Result: exit `1`; `8` failed, `19` passed.

The intended failures were:

```text
Cancellation_thrown_after_close_side_effect_relaunches_without_checkpoint
Cancellation_thrown_during_force_relaunches_without_checkpoint
Unexpected_close_error_relaunches_then_rethrows_original_exception
Unexpected_force_error_relaunches_then_rethrows_original_exception
Unexpected_helper_error_restores_and_launches_then_rethrows_original_exception
Unexpected_verification_error_restores_and_launches_then_rethrows_original_exception
Unexpected_helper_error_is_preserved_when_restore_cannot_be_verified
Capture_unexpected_second_read_failure_clears_first_owned_buffer_before_rethrow
```

Observed causes matched the findings: close/force cancellation escaped, pre-checkpoint
unexpected failures skipped launch, post-checkpoint unexpected failures were converted
to results, and the retained first capture buffer still contained `[1,2,3,4]`.

The capture-classification regression was then run separately. It failed because an
arbitrary `InvalidOperationException("unexpected-capture")` was converted to a normal
result instead of being rethrown after launch:

```powershell
.\.tools\dotnet\dotnet.exe test tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~Unexpected_capture_error_relaunches_then_rethrows_original_exception
```

The launch-classification regression likewise failed because arbitrary invalid launch
state was treated as the defined operational launch failure:

```powershell
.\.tools\dotnet\dotnet.exe test tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~Unexpected_launch_invalid_state_is_rethrown_after_verified_switch
```

Both failures were corrected by the dedicated checkpoint exception and exact launch
failure classification.

### Review Focused GREEN

Command:

```powershell
.\.tools\dotnet\dotnet.exe test tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~AuthStateTransactionTests|FullyQualifiedName~SafeSwitchCoordinatorTests"
```

Result: exit `0`; `29/29` passed, `0` failed, `0` skipped.

The transaction class alone passed `7/7`; the coordinator class passed `22/22` in the
combined final run.

### Review Full Solution

Command:

```powershell
.\.tools\dotnet\dotnet.exe test CodexAccountSwitcher.sln -c Debug --no-restore
```

Result: exit `0`; `126/126` passed, `0` failed, `0` skipped.

`git diff --check` also passed before staging.

### Review Self-review

- The pre-close caller-token check remains before any process side effect. Recovery
  responsibility is set synchronously immediately before the `CloseAsync` call.
- Close/force cancellation with no checkpoint attempts exactly one launch with
  `CancellationToken.None`; it does not read or rewrite auth files.
- Cancellation after checkpoint capture still restores and pair-verifies prior bytes
  before launch, preserving the accepted prior semantics.
- `ExceptionDispatchInfo` is populated only for unexpected failures. Operational
  filters are stage-specific: native/file close/force failures, the dedicated
  checkpoint failure, native/file helper failures, local verification data/file
  failures, restore file failures, and the controller's fixed launch failure.
- Broad catches exist only to record an unexpected exception, continue required
  cleanup, and rethrow afterward. No broad catch returns a routine `SwitchResult`.
- If an unexpected helper/verification failure is pending and restore verification
  fails, launch remains suppressed and the original unexpected exception is rethrown.
  Cleanup failures do not replace an earlier pending exception.
- A retained capture buffer is zeroed even when the second fake read throws an
  unexpected exception. Successful ownership transfer leaves buffers intact only for
  the transaction, whose existing dispose test proves later zeroing.
- Injected auth replacement failure leaves the existing destination bytes unchanged,
  still restores the registry independently, returns pair verification failure, and
  leaves no transaction temp file. Successful replacement also leaves no temp file.
- Tests continue to use only fakes and temporary directories. No live Codex process,
  package, helper, AppsFolder activation, or `%USERPROFILE%\.codex` file was touched.

### Review Residual Boundary

- The same real-Windows durability and cross-file non-atomicity boundaries remain.
  Fault injection covers managed write/replace cleanup and launch gating, not power
  loss or native filesystem behavior outside the process.
