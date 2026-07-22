# Final UI Minor Fix Report

## Scope

- `AccountRowViewModel` now displays non-empty aliases, otherwise email; `AccountName` remains untouched for selector/helper logic.
- Busy `LoadAsync` requests record one pending registry reload and coalesce it after the active operation has released both its gate and activity tracker.
- Helper recovery clears `StatusText` only when it still contains the preceding helper-unavailable error.

## RED

Focused tests initially ran with 3 failures and 1 pass:

- Empty alias plus present `AccountName` displayed the account name instead of email.
- Three busy `LoadAsync` calls left `LoadCallCount` at 1 instead of running one deferred reload.
- Helper recovery retained the old helper-unavailable status.
- An unrelated retry-launch status was already preserved.

## GREEN and verification

- Focused regression tests: 4 passed, 0 failed.
- `MainWindowViewModelTests`: 63 passed, 0 failed.
- Release solution: 351 passed, 0 failed.
- `git diff --check`: passed.

## Follow-up: fault and cancellation handoff

- RED: queued reload was not consumed when the active operation faulted or was canceled; both new tests observed `LoadCallCount` 1 instead of 2.
- GREEN: `RunBusyAsync` records the original exception/cancellation, consumes or hands off the pending reload after release, and then rethrows the original completion state.
- Focused follow-up regressions: 3 passed, 0 failed.
- Updated `MainWindowViewModelTests`: 66 passed, 0 failed.

## Unverified

No live `.codex` data was accessed. The physical tray/window interaction was not manually exercised; the behavior is covered through `MainWindowViewModel` tests.
