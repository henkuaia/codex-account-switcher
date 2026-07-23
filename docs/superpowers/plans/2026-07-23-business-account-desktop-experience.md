# Business Account Desktop Experience Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make five independent Business accounts addable through normal browser login, make login safely cancelable, expose the main window in the taskbar, and ship one dedicated icon everywhere.

**Architecture:** Keep the existing `SafeLoginCoordinator` transaction and replace only the helper login arguments. Give `AccountDialogService` a linked per-login cancellation source and let `OperationWindow` request cancellation without bypassing process-exit or checkpoint recovery. Embed one generated multi-size ICO and reuse it for the executable, WPF windows, tray, and shortcut.

**Tech Stack:** .NET 9, WPF, Windows Forms `NotifyIcon`, xUnit, PowerShell publishing.

## Global Constraints

- Do not modify Codex history or personalization files.
- Do not add automatic account rotation or background `auth.json` monitoring.
- Keep `codex-auth` v0.2.10 and the existing helper/manifest publish contract.
- Main-window close hides to tray; tray Exit remains the explicit full exit.
- Use normal `codex-auth login`; do not pass `--device-auth`.
- Preserve fail-closed helper-exit and authentication rollback behavior.

---

### Task 1: Use normal Business browser login

**Files:**
- Modify: `tests/CodexAccountSwitcher.Tests/CodexAuthServiceTests.cs`
- Modify: `src/CodexAccountSwitcher/Services/CodexAuthService.cs`

**Interfaces:**
- Consumes: `CodexAuthService.LoginAsync(...)`.
- Produces: helper request arguments exactly `["login"]`.

- [ ] **Step 1: Change both login argument assertions to `["login"]`.**

```csharp
Assert.Equal(["login"], runner.LastRequest!.Arguments);
```

- [ ] **Step 2: Run the two tests and verify RED.**

Run:
`dotnet test tests/CodexAccountSwitcher.Tests/CodexAccountSwitcher.Tests.csproj -c Release --filter "FullyQualifiedName~Login_uses_exact_stable_command_arguments|FullyQualifiedName~Streaming_login_uses_streaming_overload"`.

Expected: both fail because actual arguments contain `--device-auth`.

- [ ] **Step 3: Remove `--device-auth` from both captured login calls.**

```csharp
return outputHandler is null
    ? await RunCapturedAsync(["login"], environment, cancellationToken)
    : await RunCapturedAsync(["login"], environment, outputHandler, cancellationToken);
```

- [ ] **Step 4: Re-run the focused tests and verify GREEN.**

- [ ] **Step 5: Commit as `fix: use browser login for Business accounts`.**

### Task 2: Add safe login cancellation

**Files:**
- Modify: `tests/CodexAccountSwitcher.Tests/WpfRuntimeTests.cs`
- Modify: `src/CodexAccountSwitcher/Views/OperationWindow.xaml`
- Modify: `src/CodexAccountSwitcher/Views/OperationWindow.xaml.cs`
- Modify: `src/CodexAccountSwitcher/App.xaml.cs`

**Interfaces:**
- Produces: `OperationWindow(OperationWindowText, Action requestCancel, Func<bool> confirmCancel)`.
- Produces: `OperationWindow.Cancelled()` and per-login linked cancellation in `AccountDialogService`.

- [ ] **Step 1: Add an STA regression test.**

The test constructs a localized operation window with `confirmCancel: () => true`, clicks `CloseButton`, and asserts:

```csharp
Assert.Equal(Visibility.Visible, close.Visibility);
Assert.Equal("取消登录", close.Content);
Assert.Equal(1, cancelRequests);
Assert.Equal("正在取消登录…", phase.Text);
```

It clicks again and asserts the callback is still called once, then invokes `Cancelled()` and asserts phase `登录已取消`, button content `关闭`, and normal close succeeds. Extend the dialog integration test so clicking cancel makes the operation token cancel and the dialog finishes in canceled state.

- [ ] **Step 2: Run the focused WPF test and verify RED.**

Run:
`dotnet test tests/CodexAccountSwitcher.Tests/CodexAccountSwitcher.Tests.csproj -c Release --filter FullyQualifiedName~Concrete_windows_render_bind_and_enforce_accessible_close_contracts`.

Expected: constructor/state API or visible cancel assertions fail.

- [ ] **Step 3: Add localized cancel state and one-shot request handling.**

`OperationWindowText.AddAccount` gains `Cancel`, `Cancelling`, `Cancelled`, confirmation title/text. For cancelable windows, show both controls immediately. Active clicks confirm once, change phase, disable repeat requests, and invoke `requestCancel`; completed/failed/canceled state converts the controls back to normal Close behavior. Noncancelable remove windows retain current behavior.

- [ ] **Step 4: Link cancellation to the login operation.**

In `AccountDialogService.RunLoginAsync`:

```csharp
using var loginCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
window = new OperationWindow(
    OperationWindowText.AddAccount,
    loginCancellation.Cancel,
    () => MessageBox.Show(window, "确定要取消当前登录吗？", "取消登录",
        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
```

Pass `loginCancellation.Token` to first-render waiting, output dispatch, and the login operation. Completion/failure callbacks call `Cancelled()` when this source was canceled; otherwise retain `Complete`/`Fail`.

- [ ] **Step 5: Run the focused WPF test and existing login cancellation coordinator tests; verify GREEN.**

- [ ] **Step 6: Commit as `feat: safely cancel account login`.**

### Task 3: Restore taskbar presence while keeping tray-close behavior

**Files:**
- Modify: `tests/CodexAccountSwitcher.Tests/WpfRuntimeTests.cs`
- Modify: `src/CodexAccountSwitcher/MainWindow.xaml`

**Interfaces:**
- Produces: main window `ShowInTaskbar=True`; owned operation windows remain false.

- [ ] **Step 1: Add assertions before showing windows.**

```csharp
Assert.True(mainWindow.ShowInTaskbar);
Assert.False(localizedWindow.ShowInTaskbar);
```

Keep the existing assertion that `mainWindow.Close()` hides it while `AllowClose()` permits shutdown.

- [ ] **Step 2: Run the focused WPF test and verify RED on the main-window assertion.**

- [ ] **Step 3: Change only `MainWindow.xaml` to `ShowInTaskbar="True"`.**

- [ ] **Step 4: Re-run the focused test and verify GREEN.**

- [ ] **Step 5: Commit as `fix: show main window in taskbar`.**

### Task 4: Embed and reuse a dedicated multi-size icon

**Files:**
- Create: `scripts/generate-icon.ps1`
- Create: `src/CodexAccountSwitcher/Assets/CodexAccountSwitcher.ico`
- Modify: `src/CodexAccountSwitcher/CodexAccountSwitcher.csproj`
- Modify: `src/CodexAccountSwitcher/App.xaml`
- Modify: `src/CodexAccountSwitcher/Tray/TrayIconHost.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/PublishContractTests.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/WpfRuntimeTests.cs`

**Interfaces:**
- Produces: one ICO containing 16, 20, 24, 32, 48, 64, 128, and 256 pixel images.
- Produces: `ApplicationIcon=Assets\CodexAccountSwitcher.ico` and WPF resource `AppIcon`.

- [ ] **Step 1: Add failing contract/runtime assertions.**

Assert the project contains `ApplicationIcon`, the ICO exists and has eight directory entries, `MainWindow.Icon` is non-null, and tray construction loads the embedded icon rather than calling `CreateSwitchIcon`.

- [ ] **Step 2: Run focused contract/WPF tests and verify RED.**

- [ ] **Step 3: Add the deterministic icon generator and run it.**

The PowerShell generator draws a deep-teal rounded square, two white account cards, and opposing arrows at each required size, encodes each frame as PNG, and writes an ICO directory followed by the PNG payloads. No OpenAI/Codex trademark artwork is used.

- [ ] **Step 4: Wire the icon into the project and UI.**

```xml
<ApplicationIcon>Assets\CodexAccountSwitcher.ico</ApplicationIcon>
<Resource Include="Assets\CodexAccountSwitcher.ico" />
```

Set the WPF application/window icon from the resource. Replace runtime `System.Drawing` drawing in `TrayIconHost` with `new Icon(resourceStream)` and keep lifetime disposal unchanged.

- [ ] **Step 5: Re-run focused tests and verify GREEN.**

- [ ] **Step 6: Publish once and verify the published EXE exposes the embedded icon while the exact file count remains unchanged.**

- [ ] **Step 7: Commit as `feat: add unified account switcher icon`.**

### Task 5: Full verification and stable install

**Files:**
- Modify only if a verification failure identifies a scoped regression.

- [ ] **Step 1: Run complete Release tests.**

Run:
`dotnet test CodexAccountSwitcher.sln -c Release --no-restore`.

Expected: zero failed and zero skipped.

- [ ] **Step 2: Run `git diff --check` and confirm a clean working tree after commits.**

- [ ] **Step 3: Back up current `.codex/auth.json` and `.codex/accounts` before any live account action.**

- [ ] **Step 4: Run `scripts/publish.ps1` to a repository-external staging directory and verify exact publish files, helper/manifest/archive hashes, and no staging/backup residue.**

- [ ] **Step 5: Replace the stable installation only after stopping the switcher, recreate the desktop shortcut with `IconLocation` pointing to the installed EXE, and verify installed hashes equal staging hashes.**

- [ ] **Step 6: Start the installed app and verify one taskbar icon, one matching tray icon, hide-to-tray, reopen, single-instance behavior, and cancelable normal browser login launch without completing credentials.**

- [ ] **Step 7: Re-check the live authentication hash and confirm it is unchanged because no real login was completed.**
