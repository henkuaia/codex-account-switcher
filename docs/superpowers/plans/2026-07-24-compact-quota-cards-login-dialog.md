# Compact Quota Cards and Login Dialog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the crowded account rows and terminal-style add-account window with compact quota cards, value-derived progress colors, and a focused login state dialog.

**Architecture:** A small WPF converter maps remaining percentage to the specified red/amber/green color scale. The account template keeps only identity and quota status visible while moving metadata into a collapsed details section. `OperationWindow` retains its streamed-output and cancellation interfaces but presents an explicit waiting/success/failure state, with output hidden behind a disclosure and add-account success closing after 1000 ms.

**Tech Stack:** .NET 9, WPF XAML, xUnit

## Global Constraints

- Do not change `codex-auth`, Codex CLI discovery, login orchestration, account registry behavior, quota endpoints, quota parsing, quota caching, or account switching.
- Do not add a third-party UI dependency.
- Keep URLs and device codes copyable through the login-information disclosure.
- Preserve existing output redaction before text reaches the window.
- Keep the add-account header close button and cancel button available while waiting.
- Auto-close only successful add-account operations; removal operations keep their existing explicit close behavior.
- Do not perform a real login, quota refresh, account switch, account removal, or reset-credit action during verification.

---

### Task 1: Add continuous remaining-quota colors

**Files:**
- Create: `src/CodexAccountSwitcher/Converters/QuotaRemainingBrushConverter.cs`
- Modify: `src/CodexAccountSwitcher/App.xaml`
- Create: `tests/CodexAccountSwitcher.Tests/QuotaRemainingBrushConverterTests.cs`

**Interfaces:**
- Produces: `QuotaRemainingBrushConverter : IValueConverter`.
- `Convert` accepts numeric `0..100`, clamps out-of-range values, and returns a frozen `SolidColorBrush`.
- `ConvertBack` throws `NotSupportedException`.
- Missing, non-numeric, NaN, or infinite input returns neutral `#667178`.

- [x] **Step 1: Write failing converter tests**

Cover exact endpoints and midpoint:

```csharp
[Theory]
[InlineData(0, "#FFD9534F")]
[InlineData(50, "#FFE6A23C")]
[InlineData(100, "#FF16B364")]
public void Converts_exact_color_anchors(double value, string expected)
{
    var brush = Assert.IsType<SolidColorBrush>(
        new QuotaRemainingBrushConverter().Convert(value, typeof(Brush), null, CultureInfo.InvariantCulture));

    Assert.Equal(expected, brush.Color.ToString());
    Assert.True(brush.IsFrozen);
}
```

Also assert `25` and `75` equal the piecewise linear RGB interpolation, values
below/above the range clamp to the endpoint colors, invalid input is neutral,
and `ConvertBack` throws.

- [x] **Step 2: Run converter tests and verify RED**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~QuotaRemainingBrushConverterTests
```

Expected: compilation fails because `QuotaRemainingBrushConverter` does not exist.

- [x] **Step 3: Implement the converter and resource**

Use two linear interpolation segments:

```text
0..50:  #D9534F -> #E6A23C
50..100: #E6A23C -> #16B364
```

Register one application resource:

```xml
<converters:QuotaRemainingBrushConverter x:Key="QuotaRemainingBrushConverter" />
```

- [x] **Step 4: Run converter tests and verify GREEN**

Run the command from Step 2.

Expected: all converter tests pass.

- [x] **Step 5: Commit**

```powershell
git add src/CodexAccountSwitcher/Converters/QuotaRemainingBrushConverter.cs src/CodexAccountSwitcher/App.xaml tests/CodexAccountSwitcher.Tests/QuotaRemainingBrushConverterTests.cs
git commit -m "feat: color quota by remaining percentage"
```

---

### Task 2: Replace account rows with compact quota cards

**Files:**
- Modify: `src/CodexAccountSwitcher/MainWindow.xaml`
- Modify: `tests/CodexAccountSwitcher.Tests/WpfInterfaceContractTests.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/WpfRuntimeTests.cs`

**Interfaces:**
- Consumes: application resource `QuotaRemainingBrushConverter`.
- Keeps all existing view-model bindings and command names.
- Produces: an `Expander` named `QuotaDetailsExpander`, collapsed by default.

- [x] **Step 1: Write failing card-contract tests**

Assert the XAML contains:

```csharp
Assert.Contains("x:Name=\"QuotaDetailsExpander\"", xaml, StringComparison.Ordinal);
Assert.Contains("Header=\"详情\"", xaml, StringComparison.Ordinal);
Assert.Contains("IsExpanded=\"False\"", xaml, StringComparison.Ordinal);
Assert.Contains("Converter={StaticResource QuotaRemainingBrushConverter}", xaml, StringComparison.Ordinal);
```

Update the WPF runtime test to retrieve the first generated account card,
assert the details expander starts collapsed, and assert an unqueried row has no
visible percentage text.

- [x] **Step 2: Run card tests and verify RED**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WpfInterfaceContractTests|FullyQualifiedName~WpfRuntimeTests"
```

Expected: the compact-card and expander assertions fail.

- [x] **Step 3: Implement the compact card template**

Keep always-visible content in this order:

```text
identity + plan                         Active/Switch
period                                 remaining%  reset/cache status
[value-colored progress indicator]
详情
```

Move `AvailableResetText`, `UsedResetText`, `PeriodQuotaText`,
`OfficialMonthlyLimitText`, `EstimatedPeriodQuotaText`, and
`EditMetadataButton` inside `QuotaDetailsExpander`. Bind both percentage text
foreground and progress-bar foreground through
`QuotaRemainingBrushConverter`. Preserve the neutral track and all existing
visibility triggers.

- [x] **Step 4: Run card tests and verify GREEN**

Run the command from Step 2.

Expected: all focused WPF tests pass.

- [x] **Step 5: Commit**

```powershell
git add src/CodexAccountSwitcher/MainWindow.xaml tests/CodexAccountSwitcher.Tests/WpfInterfaceContractTests.cs tests/CodexAccountSwitcher.Tests/WpfRuntimeTests.cs
git commit -m "feat: compact account quota cards"
```

---

### Task 3: Replace the terminal login view with compact states

**Files:**
- Modify: `src/CodexAccountSwitcher/Views/OperationWindow.xaml`
- Modify: `src/CodexAccountSwitcher/Views/OperationWindow.xaml.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/WpfRuntimeTests.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/WpfInterfaceContractTests.cs`

**Interfaces:**
- Keeps: `AppendLine(ProcessOutputLine)`, `Complete(CommandResult)`, `Fail()`, and `Cancelled()`.
- Adds named UI elements:
  - `LoadingSpinner`
  - `StateIcon`
  - `StateTitleText`
  - `StateSubtitleText`
  - `DetailsExpander`
  - existing `OutputTextBox`, `CloseButton`, and `HeaderCloseButton`
- Adds `OperationWindowText.AutoCloseOnSuccess`, true only for `AddAccount`.
- Successful add-account close delay is exactly `TimeSpan.FromMilliseconds(1000)`.

- [x] **Step 1: Write failing compact-state tests**

Add XAML contract assertions for fixed `Width="420"`, `Height="230"`, spinner
animation, and the named state/details controls.

In the WPF runtime test assert:

1. an add-account window starts with spinner visible, details collapsed, cancel
   and header close visible;
2. `AppendLine` keeps the output copyable and reveals only the disclosure;
3. successful completion shows `账号添加成功`, hides the spinner and details,
   and marks the window closable;
4. failure shows the concise failure state and retains sanitized details;
5. cancellation keeps the existing confirmation and disabled-button behavior;
6. an English remove window does not auto-close on successful completion.

- [x] **Step 2: Run operation-window tests and verify RED**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WpfRuntimeTests|FullyQualifiedName~WpfInterfaceContractTests|FullyQualifiedName~DialogOperationRunnerTests"
```

Expected: compact-state element and behavior assertions fail.

- [x] **Step 3: Implement the compact dialog XAML**

Use a fixed `420×230` window. Replace the permanent dark terminal area with:

- a centered animated spinner and state text;
- a small collapsed disclosure whose content is the existing read-only
  `OutputTextBox`;
- the existing footer action button.

Use WPF `Storyboard` rotation with no external library. Keep the output text
selectable and vertically scrollable when disclosed.

- [x] **Step 4: Implement the window state transitions**

`AppendLine` appends sanitized text and makes the disclosure available without
expanding it. `Complete` transitions to success/failure, stops the spinner, and
keeps existing close rules. For successful `OperationWindowText.AddAccount`,
start a one-shot dispatcher timer for `1000 ms`; stop the timer during window
close. `Fail` and `Cancelled` transition to their corresponding concise states.

- [x] **Step 5: Run operation-window tests and verify GREEN**

Run the command from Step 2.

Expected: all focused operation and WPF tests pass.

- [x] **Step 6: Commit**

```powershell
git add src/CodexAccountSwitcher/Views/OperationWindow.xaml src/CodexAccountSwitcher/Views/OperationWindow.xaml.cs tests/CodexAccountSwitcher.Tests/WpfRuntimeTests.cs tests/CodexAccountSwitcher.Tests/WpfInterfaceContractTests.cs
git commit -m "feat: simplify account login dialog"
```

---

### Task 4: Verify, publish, install, and push

**Files:**
- Modify: `docs/superpowers/plans/2026-07-24-compact-quota-cards-login-dialog.md` checkboxes only.
- Build output: `dist/CodexAccountSwitcher`
- Installed output: `C:\Users\demax\Apps\CodexAccountSwitcher`

**Interfaces:**
- Preserves the existing nine-file publish contract.
- Preserves the local feature branch and pushes its HEAD to remote `main`.

- [x] **Step 1: Run complete Release tests**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test CodexAccountSwitcher.sln -c Release --no-restore
```

Expected: all tests pass with zero failures and zero skips.

- [x] **Step 2: Publish and verify authentication preservation**

Hash `%USERPROFILE%\.codex\auth.json` and every file below
`%USERPROFILE%\.codex\accounts`, then run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish.ps1
```

Verify unchanged authentication hashes, exactly nine output files, matching
helper/manifest SHA-256, and no staging/backup residue.

- [x] **Step 3: Replace the installed application**

Stage and hash-verify the new distribution under `C:\Users\demax\Apps`. Stop
only `CodexAccountSwitcher.exe`, move the current installation to a timestamped
backup, move the staged distribution into the stable install path, verify all
hashes, and restart the app.

- [x] **Step 4: Perform read-only visual inspection**

Inspect the installed main window and add-account window without initiating a
login. Verify compact layout, default-collapsed details, neutral unqueried rows,
spinner visibility, header close, and cancel control. Use WPF layout/runtime
tests to cover 100%, 125%, and 150% scale constraints; do not change the user's
Windows display settings.

- [ ] **Step 5: Commit, push, and preserve the feature branch**

```powershell
git add docs/superpowers/plans/2026-07-24-compact-quota-cards-login-dialog.md
git commit -m "docs: complete compact interface plan"
git -c http.proxy=http://127.0.0.1:7897 -c https.proxy=http://127.0.0.1:7897 push origin HEAD:main
```

Verify remote `main` equals local `HEAD`, the worktree is clean, authentication
hashes are unchanged, and the installed process runs from the stable path.
