# Unified Estimated Period Quota Row Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the separate manual and estimated period-quota rows with one clearly labeled estimated Weekly/Monthly quota item.

**Architecture:** Keep the existing quota models and estimator unchanged. Continue using `EstimatedPeriodQuotaText` as the single card-facing presentation property, remove the `PeriodQuotaText` XAML binding, and format quality/source as context inside the unified estimate line.

**Tech Stack:** .NET 9, WPF, C#, xUnit

## Global Constraints

- Weekly displays as `单次周额度（估算）：…`.
- Monthly displays as `单次月额度（估算）：…`.
- Existing estimate source, quality, status, and disclaimer remain visible.
- Existing `PeriodQuotaUsd` data is neither deleted nor migrated.
- The edit window, quota endpoints, reset counts, and estimation algorithms do not change.
- Every production behavior change follows strict RED then GREEN.

---

### Task 1: Present one estimated period-quota item

**Files:**
- Modify: `tests/CodexAccountSwitcher.Tests/MainWindowViewModelTests.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/WpfInterfaceContractTests.cs`
- Modify: `src/CodexAccountSwitcher/ViewModels/AccountRowViewModel.cs`
- Modify: `src/CodexAccountSwitcher/MainWindow.xaml`

**Interfaces:**
- Consumes: `QuotaDisplay.Period`, `EstimatedPeriodQuotaLowerUsd`, `EstimatedPeriodQuotaUpperUsd`, `EstimateQuality`, `EstimateSource`, `EstimateStatus`, and `UsedPercent`.
- Produces: `AccountRowViewModel.EstimatedPeriodQuotaText` as the only Weekly/Monthly period-quota text bound by the account card.
- Preserves: `AccountRowViewModel.PeriodQuotaText` and `AccountMetadata.PeriodQuotaUsd` for data/API compatibility, but the card no longer binds that property.

- [ ] **Step 1: Write failing presentation tests**

Update the existing Weekly/Monthly assertions to require these exact first lines:

```csharp
Assert.Equal(
    $"单次周额度（估算）：US$8–24（初步 · 本机用量）{Environment.NewLine}" +
    $"按 Credits 购买价格换算，非官方套餐额度{Environment.NewLine}" +
    "Analytics 无数据，已改用本机用量估算",
    row.EstimatedPeriodQuotaText);

Assert.Equal(
    $"单次月额度（估算）：US$160–200（多点 · 服务器 Analytics）{Environment.NewLine}" +
    "按 Credits 购买价格换算，非官方套餐额度",
    row.EstimatedPeriodQuotaText);

Assert.Equal(
    $"单次月额度（估算）：US$180（初步 · 服务器 Analytics）{Environment.NewLine}" +
    "按 Credits 购买价格换算，非官方套餐额度",
    row.EstimatedPeriodQuotaText);
```

Update the no-usage assertions:

```csharp
Assert.Equal(
    "单次周额度（估算）：产生用量后可计算",
    row.EstimatedPeriodQuotaText);

Assert.Equal(
    "单次月额度（估算）：产生用量后可计算",
    row.EstimatedPeriodQuotaText);
```

Add a regression for a used window with no estimate but an existing status:

```csharp
[Fact]
public void Unavailable_estimate_keeps_one_labeled_quota_item_before_status()
{
    var account = Accounts.Record("first-key", "first@example.com");
    var row = new AccountRowViewModel(
        account,
        isActive: true,
        canSwitch: false,
        switchUnavailableReason: null);
    row.ApplyQuota(new QuotaUpdate(
        account.AccountKey,
        new QuotaDisplay(
            QuotaPeriod.Weekly,
            75,
            null,
            TimeSpan.FromDays(7),
            "weekly")
        {
            UsedPercent = 25,
            EstimateStatus = "本机用量扫描不完整",
        },
        null));

    Assert.Equal(
        $"单次周额度（估算）：暂不可用{Environment.NewLine}" +
        "本机用量扫描不完整",
        row.EstimatedPeriodQuotaText);
}
```

Change the WPF contract to prove the duplicate manual row is absent:

```csharp
Assert.DoesNotContain(
    "Text=\"{Binding PeriodQuotaText}\"",
    xaml,
    StringComparison.Ordinal);
Assert.Contains(
    "Text=\"{Binding EstimatedPeriodQuotaText}\"",
    xaml,
    StringComparison.Ordinal);
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
.\.tools\dotnet\dotnet.exe test `
  tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj `
  -c Release --no-restore `
  --filter "FullyQualifiedName~MainWindowViewModelTests|FullyQualifiedName~WpfInterfaceContractTests" `
  --logger "console;verbosity=minimal"
```

Expected: failures show the old `初步估算单次…` / `估算单次…` wording, the missing `暂不可用` base line, and the still-present `PeriodQuotaText` XAML binding.

- [ ] **Step 3: Implement the minimal unified formatter**

In `FormatEstimatedPeriodQuota`, format the bounded result with one labeled item and a compact context suffix:

```csharp
var quality = display.EstimateQuality switch
{
    QuotaEstimateQuality.Initial => "初步",
    QuotaEstimateQuality.MultiPoint => "多点",
    _ => string.Empty,
};
var source = display.EstimateSource switch
{
    QuotaEstimateSource.Analytics => "服务器 Analytics",
    QuotaEstimateSource.Local => "本机用量",
    _ => string.Empty,
};
var context = string.Join(
    " · ",
    new[] { quality, source }.Where(value => !string.IsNullOrEmpty(value)));
var contextSuffix = string.IsNullOrEmpty(context)
    ? string.Empty
    : $"（{context}）";

text =
    $"单次{period}额度（估算）：US${range}{contextSuffix}{Environment.NewLine}" +
    "按 Credits 购买价格换算，非官方套餐额度";
```

Use these exact fallback labels:

```csharp
text = $"单次{period}额度（估算）：产生用量后可计算";
```

```csharp
text = $"单次{period}额度（估算）：暂不可用";
```

Keep `AppendDetailLine(text, display.EstimateStatus)` so existing diagnostic status remains below the unified item.

In `MainWindow.xaml`, remove only the `TextBlock` bound to `PeriodQuotaText`:

```xml
<TextBlock Margin="0,0,12,0"
           FontSize="11"
           Foreground="{StaticResource TextSecondaryBrush}"
           Text="{Binding PeriodQuotaText}" />
```

Keep `OfficialMonthlyLimitText`, the estimate `TextBlock`, the edit button, and all existing visibility behavior unchanged.

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run the Step 2 command.

Expected: every selected test passes with zero failures and zero skips.

- [ ] **Step 5: Run the complete Release suite**

Run:

```powershell
.\.tools\dotnet\dotnet.exe test `
  tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj `
  -c Release --no-restore `
  --logger "console;verbosity=minimal"
```

Expected: all tests pass with zero failures and zero skips.

- [ ] **Step 6: Validate and commit**

Run:

```powershell
git diff --check
git status --short
```

Expected: no whitespace errors; only the four intended source/test files are modified.

Commit:

```powershell
git add `
  src/CodexAccountSwitcher/MainWindow.xaml `
  src/CodexAccountSwitcher/ViewModels/AccountRowViewModel.cs `
  tests/CodexAccountSwitcher.Tests/MainWindowViewModelTests.cs `
  tests/CodexAccountSwitcher.Tests/WpfInterfaceContractTests.cs
git commit -m "fix: unify estimated period quota display"
```

### Task 2: Publish and replace the installed build

**Files:**
- No tracked source changes.
- Produce: `dist/CodexAccountSwitcher`
- Replace: `C:\Users\demax\Apps\CodexAccountSwitcher`

**Interfaces:**
- Consumes: the committed Task 1 source and repository-local `.tools\dotnet`.
- Produces: the exact nine-file Windows x64 distribution and a restarted installed process.
- Preserves: `.codex/auth.json`, `.codex/accounts`, and a timestamped backup of the previous installation.

- [ ] **Step 1: Run the release publisher**

Run:

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File .\scripts\publish.ps1
```

Expected: build has zero warnings/errors, all tests pass, and
`dist\CodexAccountSwitcher` contains exactly nine files.

- [ ] **Step 2: Verify the release contract**

Verify:

- published `tools\codex-auth.exe` SHA-256 equals the vendor helper and manifest executable hash;
- the manifest archive hash remains
  `CDF2C4D9CC827C91C24EB4C032B9F6792F581B42808DF5DB167C39B255EA7108`;
- no staging or backup residue remains under `dist`.

- [ ] **Step 3: Merge and push**

Fast-forward the feature branch into local `main`, rerun the complete Release suite on the merged checkout, and push `main` to `origin`.

Expected: local `main`, `origin/main`, and the feature tip identify the same source commit before branch cleanup.

- [ ] **Step 4: Replace the installed build atomically**

Before replacement:

- hash `C:\Users\demax\.codex\auth.json` and every regular file under
  `C:\Users\demax\.codex\accounts`;
- copy the nine-file distribution to a sibling staging directory and compare
  every relative path, length, and SHA-256 value;
- stop only the process whose executable is exactly
  `C:\Users\demax\Apps\CodexAccountSwitcher\CodexAccountSwitcher.exe`.

Then:

- move the old install to
  `C:\Users\demax\Apps\CodexAccountSwitcher.backup-yyyyMMdd-HHmmss`, using
  the replacement start time;
- move staging to `C:\Users\demax\Apps\CodexAccountSwitcher`;
- start the new executable;
- verify one responding installed process, nine matching files, zero staging
  residue, and unchanged auth/account hashes.

- [ ] **Step 5: Clean up the feature worktree**

After the merged-main tests, push, installation, and integrity checks pass:

```powershell
git worktree remove "C:\Users\demax\Documents\Codex\2026-07-20\new-chat-3\codex-account-switcher\.worktrees\feature-unify-estimated-quota-row"
git worktree prune
git branch -d feature/unify-estimated-quota-row
```

Expected: only the main worktree remains and the main checkout is clean.
