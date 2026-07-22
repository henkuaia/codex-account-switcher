# Login Snapshot Rollback Report

## Root Cause

`SafeLoginCoordinator` already routes failed, canceled, and unverifiable login
recovery through `IAuthStateCheckpoint.RestoreAndVerifyAsync`. Its default
`AuthStateTransaction`, however, checkpointed only `auth.json` and
`accounts/registry.json`. It neither enumerated nor retained the pre-login
`accounts/*.auth.json` set, so it could not restore overwritten or deleted
snapshots, remove snapshots created by a failed login, or verify their recovery.

## RED

Added three regression tests against real temporary Codex homes before changing
production code:

- remove a snapshot created after the checkpoint;
- restore the exact bytes of an overwritten snapshot;
- recreate a deleted snapshot with its exact original bytes.

Command:

```powershell
& '.\.tools\dotnet\dotnet.exe' test '.\tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj' --configuration Release --filter 'FullyQualifiedName~Failed_login_rollback' --no-restore
```

Result: 0 passed, 3 failed as expected. The new snapshot remained, the overwritten
snapshot retained its changed bytes, and the deleted snapshot remained absent.

## Change

- Extended `IAuthStateFileSystem` with top-level account-snapshot enumeration.
- Extended `AuthStateTransaction` to capture the exact case-insensitive path set
  and bytes for top-level `accounts/*.auth.json` files.
- Recovery now rewrites every pre-existing snapshot, deletes any newly created
  snapshot, then re-enumerates the set and verifies every retained byte buffer
  with `CryptographicOperations.FixedTimeEquals`.
- Enumeration excludes `registry.json`, nested files, and unrelated files under
  `accounts`. A regression assertion confirms an unrelated file is untouched.
- The shared transaction's success path and coordinator control flow are
  unchanged. Existing switch and cancellation recovery suites cover those paths.
- Updated the two in-memory `IAuthStateFileSystem` test doubles for the added
  enumeration contract; the removal transaction itself is unchanged.

## GREEN

Focused three-case regression command above: 3 passed, 0 failed, 0 skipped.

Transaction suite:

```powershell
& '.\.tools\dotnet\dotnet.exe' test '.\tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj' --configuration Release --filter 'FullyQualifiedName~AuthStateTransactionTests' --no-restore
```

Result: 11 passed, 0 failed, 0 skipped. This includes injected post-restore
snapshot corruption and checkpoint-buffer clearing coverage.

Related transaction/coordinator suites:

```powershell
& '.\.tools\dotnet\dotnet.exe' test '.\tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj' --configuration Release --filter 'FullyQualifiedName~AuthStateTransactionTests|FullyQualifiedName~SafeLoginCoordinatorTests|FullyQualifiedName~SafeSwitchCoordinatorTests|FullyQualifiedName~TargetedRemoveCoordinatorTests' --no-restore
```

Result: 95 passed, 0 failed, 0 skipped. The existing
`Cancellation_after_login_side_effect_restores_then_relaunches` test confirms
that post-login cancellation uses the same recovery helper.

Full Release solution:

```powershell
& '.\.tools\dotnet\dotnet.exe' test '.\CodexAccountSwitcher.sln' --configuration Release --no-restore
```

Result: 343 passed, 0 failed, 0 skipped.

`git diff --check` exited 0. It emitted only LF-to-CRLF working-copy warnings.

## Secret Handling

- Snapshot bytes remain byte arrays and are never rendered in UI text, logs,
  exception messages, or this report.
- Capture-time operational failures retain the fixed
  `Authentication state checkpoint failed.` message.
- All owned auth, registry, and account-snapshot buffers are zeroed when capture
  fails or the transaction is disposed. Tests inspect the owned snapshot buffers
  and verify every byte is zero after disposal.
- Temporary tests use isolated directories under the system test temp path and
  synthetic byte arrays only.

## Commit

`fix: restore account snapshots after failed login` (the single commit containing
this report; its final SHA is supplied in the task handoff because embedding that
SHA here would change the commit object).

## Unverified

- No real login, quota, switch, removal, or account operation was run.
- No file under `C:\Users\demax\.codex` was read or modified.
- No manual WPF runtime interaction was performed; startup, UI, and minor-version
  behavior were outside this task and unchanged.
- Concurrent external mutation during checkpoint capture was not exercised; the
  transaction fails closed if an enumerated snapshot disappears before its bytes
  are read.
