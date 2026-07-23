# Add-Account Chinese Output Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove ANSI terminal control codes and show the Codex device-login instructions and add-account operation chrome in Chinese without changing URLs, device codes, authentication behavior, or other operation windows.

**Architecture:** Add a pure `AddAccountOutputFormatter` at the add-account display boundary. It strips ANSI CSI sequences and maps only known Codex login lines; `OperationWindow` receives instance-specific labels so only the add-account instance is localized.

**Tech Stack:** C# 13, .NET 9, WPF, xUnit, generated regular expressions.

## Global Constraints

- Only the add-account window and its displayed login stream may change.
- URLs and one-time device codes must remain byte-for-byte unchanged after ANSI removal.
- Unknown text must be preserved after ANSI removal.
- `CommandResult`, authentication transactions, account snapshots, switch, and remove behavior must not change.
- No new dependency and no change to the exact 9-file release contract.

---

### Task 1: Add-account output formatter

**Files:**
- Create: `src/CodexAccountSwitcher/Views/AddAccountOutputFormatter.cs`
- Create: `tests/CodexAccountSwitcher.Tests/AddAccountOutputFormatterTests.cs`
- Modify: `src/CodexAccountSwitcher/App.xaml.cs`
- Test: `tests/CodexAccountSwitcher.Tests/DialogOperationRunnerTests.cs`

**Interfaces:**
- Consumes: `ProcessOutputLine` from the already-redacted login stream.
- Produces: `internal static string AddAccountOutputFormatter.Format(string text)`.

- [ ] **Step 1: Write failing formatter tests**

Cover the exact screenshot-shaped inputs and these expected results:

```csharp
[Theory]
[InlineData("\u001b[90mWelcome to Codex [v0.145.0-alpha.30]\u001b[0m", "欢迎使用 Codex [v0.145.0-alpha.30]")]
[InlineData("\u001b[90mOpenAI's command-line coding agent\u001b[0m", "OpenAI 命令行编程助手")]
[InlineData("Follow these steps to sign in with ChatGPT using device code authorization:", "请按以下步骤使用设备验证码登录 ChatGPT：")]
[InlineData("1. Open this link in your browser and sign in to your account", "1. 在浏览器中打开以下链接并登录账号")]
[InlineData("2. Enter this one-time code (expires in 15 minutes)", "2. 输入以下一次性验证码（15 分钟内有效）")]
[InlineData("Continue only if you started this login in Codex. If a website or another person gave you this code, cancel.", "安全提醒：只有在你本人从 Codex 发起本次登录时才继续。如果验证码来自网站或其他人，请取消。")]
public void Formats_known_device_login_lines(string input, string expected)
{
    Assert.Equal(expected, AddAccountOutputFormatter.Format(input));
}

[Theory]
[InlineData("https://auth.openai.com/codex/device")]
[InlineData("ABCD-EFGH")]
[InlineData("unknown future CLI text")]
[InlineData("")]
public void Preserves_dynamic_and_unknown_text(string input)
{
    Assert.Equal(input, AddAccountOutputFormatter.Format(input));
}
```

Also test a line containing multiple CSI/SGR sequences and assert that no escape character or bracketed color suffix remains.

- [ ] **Step 2: Run the formatter tests and verify RED**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test CodexAccountSwitcher.sln -c Release --no-restore --filter FullyQualifiedName~AddAccountOutputFormatterTests
```

Expected: build/test failure because `AddAccountOutputFormatter` does not exist.

- [ ] **Step 3: Implement the minimal pure formatter**

Create a generated ANSI CSI regex equivalent to `\x1B\[[0-?]*[ -/]*[@-~]`. Strip it first, then use exact mappings for fixed lines and one anchored regex for the expiry-minute line:

```csharp
internal static partial class AddAccountOutputFormatter
{
    public static string Format(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var clean = AnsiCsiRegex().Replace(text, string.Empty);
        // Exact known-line mapping; otherwise return clean unchanged.
    }
}
```

Do not match or rewrite URL/code-shaped lines.

- [ ] **Step 4: Verify GREEN and wire only the login display callback**

In `AccountDialogService.RunLoginAsync`, create a new `ProcessOutputLine` with the same stream and `AddAccountOutputFormatter.Format(line.Text)` immediately before `OperationWindow.AppendLine`. Do not modify `ProcessRunner` or `CommandResult`.

Run the formatter and dialog-runner tests. Expected: all selected tests pass.

- [ ] **Step 5: Commit Task 1**

```powershell
git add src/CodexAccountSwitcher/Views/AddAccountOutputFormatter.cs src/CodexAccountSwitcher/App.xaml.cs tests/CodexAccountSwitcher.Tests/AddAccountOutputFormatterTests.cs tests/CodexAccountSwitcher.Tests/DialogOperationRunnerTests.cs
git commit -m "fix: localize add-account login output"
```

### Task 2: Add-account-only Chinese operation chrome

**Files:**
- Modify: `src/CodexAccountSwitcher/Views/OperationWindow.xaml`
- Modify: `src/CodexAccountSwitcher/Views/OperationWindow.xaml.cs`
- Modify: `src/CodexAccountSwitcher/App.xaml.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/WpfRuntimeTests.cs`

**Interfaces:**
- Consumes: per-instance `OperationWindowText` containing heading, phase, completed, failed-prefix, operation-failed, and close labels.
- Produces: localized add-account window while remove-account construction keeps current English defaults.

- [ ] **Step 1: Write failing WPF runtime tests**

Construct the add-account `OperationWindow` through the same localized text configuration used by `AccountDialogService`. Assert:

```csharp
Assert.Equal("添加账号", heading.Text);
Assert.Equal("等待设备登录", phase.Text);
Assert.Equal("关闭", closeButton.Content);
```

Call `Complete` with success and failure results and assert `已完成` and `失败（退出代码 1）`. Call `Fail` and assert `操作失败`. Add a regression assertion that the remove-account window still uses its existing English text.

- [ ] **Step 2: Run the WPF tests and verify RED**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test CodexAccountSwitcher.sln -c Release --no-restore --filter FullyQualifiedName~WpfRuntimeTests
```

Expected: localized-text assertions fail against the current English-only window.

- [ ] **Step 3: Add instance-specific labels and wire add-account Chinese text**

Keep the existing two-string constructor as an English-compatible overload. Add an internal immutable label value and use it in `Complete`, `Fail`, and the close button content. `AccountDialogService.RunLoginAsync` supplies:

```text
添加账号
等待设备登录
已完成
失败（退出代码 {0}）
操作失败
关闭
```

`RunRemoveAsync` must continue using the existing English constructor/defaults.

- [ ] **Step 4: Verify focused and full Release tests**

Run the WPF tests, then:

```powershell
& .\.tools\dotnet\dotnet.exe test CodexAccountSwitcher.sln -c Release --no-restore
git diff --check
```

Expected: zero failures, zero skipped tests, and no whitespace errors.

- [ ] **Step 5: Commit Task 2**

```powershell
git add src/CodexAccountSwitcher/Views/OperationWindow.xaml src/CodexAccountSwitcher/Views/OperationWindow.xaml.cs src/CodexAccountSwitcher/App.xaml.cs tests/CodexAccountSwitcher.Tests/WpfRuntimeTests.cs
git commit -m "feat: localize add-account operation window"
```

### Task 3: Review, publish, install, and live verification

**Files:**
- Verify: `scripts/publish.ps1`
- Verify: `dist/CodexAccountSwitcher/**`
- Install: `C:\Users\demax\Apps\CodexAccountSwitcher`

**Interfaces:**
- Consumes: reviewed commits from Tasks 1 and 2.
- Produces: exact 9-file stable installation and a live Chinese device-login window.

- [ ] **Step 1: Run an independent read-only code review**

Review the Task 1/2 commits for ANSI correctness, secret/code preservation, add-only scope, WPF compatibility, and regression risk. Fix all Critical/Important findings using RED-GREEN tests and re-review.

- [ ] **Step 2: Publish from outside the repository**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\demax\Documents\Codex\2026-07-20\new-chat-3\codex-account-switcher\.worktrees\feature-codex-account-switcher\scripts\publish.ps1"
```

Expected: full Release tests pass and publish succeeds.

- [ ] **Step 3: Verify the release contract and install atomically**

Assert exactly 9 files, helper and manifest hashes, no staging/backup residue, clean `git status`, and matching SHA-256 values between `dist` and `C:\Users\demax\Apps\CodexAccountSwitcher`.

- [ ] **Step 4: Verify live UI without completing authentication**

Before the live attempt, verify `auth.json` remains byte-identical to the retained pre-live backup. Launch the stable app, click add account, and confirm the window shows Chinese labels, contains no ANSI fragments, and preserves the displayed URL/code. The user performs browser authorization/MFA; no agent enters or exposes the device code.
