# Final-review fixes: native title and AccountDialogService integration

## Scope and implementation

- `OperationWindow(OperationWindowText)` now assigns `Window.Title` from the
  same per-instance heading used by the visible title. The public
  `OperationWindow(string heading, string phase)` still delegates through the
  English text value, so remove remains `Remove account` / `Close`.
- The existing STA WPF runtime test now drives real
  `AccountDialogService.RunLoginAsync` and `RunRemoveAsync` through
  `WpfUiDispatcher`. It discovers only the newly-created operation window from
  `Application.Current.Windows`; no production test hook was added.
- The login path receives ANSI-colored output via its real operation callback.
  The assertions cover native and visible Chinese text, final phase, close
  content/tooltip/automation name, ANSI removal and translation, exact
  indented URL/device-code display, window closure, and English remove chrome.

No authentication transaction, account snapshot, command result, switching,
removal behavior, formatter implementation, or release contract changed. No
`auth.json` or account snapshot content was read or modified.

## TDD evidence

### RED

Command:

```powershell
& .\.tools\dotnet\dotnet.exe test tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Account_dialog_service_localizes_login_chrome_and_keeps_removal_english --logger 'console;verbosity=minimal'
```

Result: exit 1; 0 passed, 1 failed. The real STA
`AccountDialogService.RunLoginAsync` assertion expected native title `添加账号`
but observed `Account operation`. This was the missing production behavior, not
a test compilation failure.

### GREEN

Minimum production change: added `Title = text.Heading;` immediately after
`InitializeComponent()` in the per-instance `OperationWindow` constructor.

The same focused service-level command then passed: exit 0; 1 passed, 0 failed,
0 skipped.

The first combined focused run exposed a test-harness constraint rather than a
product failure: two WPF facts each constructed `Application`, which WPF
forbids in one AppDomain. The integration assertions were therefore moved into
the existing single-`Application` STA runtime fact. Existing unshown operation
windows are captured before each service call, and the test identifies the new
window through the real application window collection. No production change was
made for that harness correction.

## Verification

Focused formatter/dialog/WPF command:

```powershell
& .\.tools\dotnet\dotnet.exe test tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~WpfRuntimeTests|FullyQualifiedName~AddAccountOutputFormatterTests|FullyQualifiedName~DialogOperationRunnerTests' --logger 'console;verbosity=minimal'
```

Result: exit 0; 24 passed, 0 failed, 0 skipped.

Full Release command:

```powershell
& .\.tools\dotnet\dotnet.exe test CodexAccountSwitcher.sln -c Release --no-restore --logger 'console;verbosity=minimal'
```

Result: exit 0; 393 passed, 0 failed, 0 skipped.

Commands:

```powershell
git diff --check
git diff --cached --check
```

Result: no whitespace errors. Git emitted only existing LF-to-CRLF conversion
warnings for the two edited source/test files.

## Files changed

- `src/CodexAccountSwitcher/Views/OperationWindow.xaml.cs`
- `tests/CodexAccountSwitcher.Tests/WpfRuntimeTests.cs`
- `.superpowers/sdd/chinese-output-final-fixes-report.md`

## Commit

`test: cover localized account dialog wiring`

## Self-review

- The constructor sets native title from the immutable per-instance text, so
  Chinese applies only where `RunLoginAsync` supplies `AddAccount`; the public
  English constructor and `RunRemoveAsync` preserve English labels.
- The integration test uses the actual service, dispatcher, WPF window
  collection, output callback, formatter call-site, and close-gating behavior.
  It does not test formatter or component methods in isolation.
- URL and device-code assertions include their original leading spaces after
  ANSI removal. The test also rejects ESC and SGR remnants (`90m`, `94m`, and
  `0m`).
- Every operation window created by the new paths is closed, and `finally`
  cleanup closes an unexpected remaining dialog only after enabling close.

## Concerns / unverified boundaries

- No live login, auth file access, publish/install action, or account snapshot
  operation was performed; all service operations are in-process delegates.
- The test proves WPF wiring and displayed stream behavior, not real browser or
  device-login behavior.
