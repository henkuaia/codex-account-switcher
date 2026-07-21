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
