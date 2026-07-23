# Quota Reset Metadata Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Display current reset credits, locally recorded used-reset count, and per-period USD quota while reducing account login to one closable app window.

**Architecture:** Extend the existing `/wham/usage` parser without changing the manual-refresh transport. Store user-owned values in an app-specific JSON file keyed by `AccountKey`, project server and local values through each account-row view model, and edit them through one small WPF dialog. Remove the redundant add-account confirmation while retaining the existing safe login coordinator and operation window.

**Tech Stack:** .NET 8, WPF, `System.Text.Json`, xUnit

## Global Constraints

- Only a user-triggered quota refresh calls the non-public endpoint.
- Do not infer used-reset history from changes in available credits.
- Do not consume reset credits or automatically switch accounts.
- Do not modify `auth.json`, Codex history, personalization files, or the `codex-auth` registry schema.
- Treat `credits.balance` as extra credits, never as a subscription period total.
- Keep server `individualLimit` separate from the user-recorded period quota.
- No new NuGet dependencies.

---

### Task 1: Parse reset credits and official monthly limit

**Files:**
- Modify: `src/CodexAccountSwitcher/Models/QuotaModels.cs`
- Modify: `src/CodexAccountSwitcher/Services/QuotaResponseParser.cs`
- Test: `tests/CodexAccountSwitcher.Tests/QuotaResponseParserTests.cs`

**Interfaces:**
- Produces: `QuotaDisplay.AvailableResetCount : int?`
- Produces: `QuotaDisplay.IndividualLimitUsd : decimal?`
- Keeps the current positional `QuotaDisplay` constructor source-compatible.

- [ ] **Step 1: Write failing parser tests**

Add tests that parse `rate_limit_reset_credits.available_count` as a JSON number and digit string, return `null` for missing/negative/malformed values, parse a non-negative `individual_limit`, and prove that `credits.balance` does not populate `IndividualLimitUsd`.

```csharp
var result = QuotaResponseParser.Parse("""
{
  "rate_limit": {
    "primary_window": {
      "used_percent": 20,
      "limit_window_seconds": 604800
    }
  },
  "rate_limit_reset_credits": { "available_count": 2 },
  "individual_limit": 200,
  "credits": { "balance": "9000" }
}
""");

Assert.Equal(2, result.Display!.AvailableResetCount);
Assert.Equal(200m, result.Display.IndividualLimitUsd);
```

- [ ] **Step 2: Run the focused tests and verify failure**

Run:

```powershell
dotnet test tests/CodexAccountSwitcher.Tests/CodexAccountSwitcher.Tests.csproj -c Release --filter FullyQualifiedName~QuotaResponseParserTests
```

Expected: FAIL because the new properties do not exist.

- [ ] **Step 3: Add optional snapshot properties and minimal parsing**

Keep the current record constructor and add:

```csharp
public int? AvailableResetCount { get; init; }
public decimal? IndividualLimitUsd { get; init; }
```

Read only non-negative integral reset counts and non-negative finite decimal limits. Apply the same snapshot values to the selected long-window display after the limiting window is chosen.

- [ ] **Step 4: Run the focused tests**

Expected: all `QuotaResponseParserTests` pass.

- [ ] **Step 5: Commit**

```powershell
git add src/CodexAccountSwitcher/Models/QuotaModels.cs src/CodexAccountSwitcher/Services/QuotaResponseParser.cs tests/CodexAccountSwitcher.Tests/QuotaResponseParserTests.cs
git commit -m "feat: parse reset credits and monthly limit"
```

### Task 2: Persist app-owned account metadata

**Files:**
- Create: `src/CodexAccountSwitcher/Models/AccountMetadataModels.cs`
- Create: `src/CodexAccountSwitcher/Services/AccountMetadataService.cs`
- Create: `tests/CodexAccountSwitcher.Tests/AccountMetadataServiceTests.cs`

**Interfaces:**
- Produces: `AccountMetadata(decimal? PeriodQuotaUsd, int UsedResetCount)`
- Produces: `AccountMetadataLoadResult(IReadOnlyDictionary<string, AccountMetadata> Accounts, string? Error)`
- Produces: `AccountMetadataService.LoadAsync(CancellationToken)`
- Produces: `AccountMetadataService.SaveAsync(IReadOnlyDictionary<string, AccountMetadata>, CancellationToken)`

- [ ] **Step 1: Write failing persistence tests**

Cover missing-file defaults, per-key round trip, non-negative validation, malformed JSON preserving the original file, and atomic replacement without `.tmp` residue.

```csharp
var service = new AccountMetadataService(temp.Path);
await service.SaveAsync(
    new Dictionary<string, AccountMetadata>
    {
        ["account-a"] = new(40m, 3),
    },
    default);

var loaded = await service.LoadAsync(default);
Assert.Equal(new AccountMetadata(40m, 3), loaded.Accounts["account-a"]);
```

- [ ] **Step 2: Run focused tests and verify failure**

Run:

```powershell
dotnet test tests/CodexAccountSwitcher.Tests/CodexAccountSwitcher.Tests.csproj -c Release --filter FullyQualifiedName~AccountMetadataServiceTests
```

Expected: FAIL because the service and models do not exist.

- [ ] **Step 3: Implement the JSON store**

Use schema version `1` and this payload:

```json
{
  "schemaVersion": 1,
  "accounts": {
    "account-a": {
      "periodQuotaUsd": 40.00,
      "usedResetCount": 3
    }
  }
}
```

The production path is `%LOCALAPPDATA%\CodexAccountSwitcher\account-metadata.json`. Write a sibling temporary file, flush it, then replace/move it. Reject invalid values before opening the destination. Return a load error for malformed/unsupported files and never overwrite that file from a failed load session.

- [ ] **Step 4: Run focused tests**

Expected: all `AccountMetadataServiceTests` pass.

- [ ] **Step 5: Commit**

```powershell
git add src/CodexAccountSwitcher/Models/AccountMetadataModels.cs src/CodexAccountSwitcher/Services/AccountMetadataService.cs tests/CodexAccountSwitcher.Tests/AccountMetadataServiceTests.cs
git commit -m "feat: persist account quota metadata"
```

### Task 3: Display and edit quota metadata

**Files:**
- Create: `src/CodexAccountSwitcher/Views/EditAccountMetadataWindow.xaml`
- Create: `src/CodexAccountSwitcher/Views/EditAccountMetadataWindow.xaml.cs`
- Modify: `src/CodexAccountSwitcher/ViewModels/AccountRowViewModel.cs`
- Modify: `src/CodexAccountSwitcher/ViewModels/MainWindowViewModel.cs`
- Modify: `src/CodexAccountSwitcher/MainWindow.xaml`
- Modify: `src/CodexAccountSwitcher/App.xaml.cs`
- Test: `tests/CodexAccountSwitcher.Tests/MainWindowViewModelTests.cs`
- Test: `tests/CodexAccountSwitcher.Tests/WpfInterfaceContractTests.cs`
- Test: `tests/CodexAccountSwitcher.Tests/WpfRuntimeTests.cs`

**Interfaces:**
- Consumes: `AccountMetadataService`
- Produces: `IAccountDialogService.EditMetadataAsync(AccountRowViewModel, CancellationToken)`
- Produces: `AccountRowViewModel.AvailableResetText`
- Produces: `AccountRowViewModel.UsedResetText`
- Produces: `AccountRowViewModel.PeriodQuotaText`
- Produces: `AccountRowViewModel.OfficialMonthlyLimitText`
- Produces: `MainWindowViewModel.EditMetadataCommand`

- [ ] **Step 1: Write failing view-model and WPF contract tests**

Assert that:

```csharp
Assert.Equal("可用重置 2", row.AvailableResetText);
Assert.Equal("已用重置 3（本机）", row.UsedResetText);
Assert.Equal("单次周额度 US$40", row.PeriodQuotaText);
Assert.Equal("官方月度上限 US$200", row.OfficialMonthlyLimitText);
```

Also assert that each account row has a metadata edit button bound to `EditMetadataCommand`, and the edit dialog rejects negative/invalid input.

- [ ] **Step 2: Run focused tests and verify failure**

Run:

```powershell
dotnet test tests/CodexAccountSwitcher.Tests/CodexAccountSwitcher.Tests.csproj -c Release --filter "FullyQualifiedName~MainWindowViewModelTests|FullyQualifiedName~WpfInterfaceContractTests|FullyQualifiedName~WpfRuntimeTests"
```

Expected: FAIL because the properties, command, and dialog do not exist.

- [ ] **Step 3: Wire metadata loading and saving**

Load metadata with the account registry, apply it by exact ordinal `AccountKey`, and retain it across registry reloads. On an accepted edit, save first and update the row only after the save succeeds. If metadata loading failed, retain the original file and surface the error in `StatusText`.

- [ ] **Step 4: Add the compact row presentation**

Add a compact metadata line below quota status:

```xml
<TextBlock Text="{Binding AvailableResetText}" />
<TextBlock Text="{Binding UsedResetText}" />
<TextBlock Text="{Binding PeriodQuotaText}" />
```

Show `OfficialMonthlyLimitText` only when present. Add one small pencil button per row; keep the window width and existing switch/active controls.

- [ ] **Step 5: Add the edit dialog**

The dialog contains two labeled text boxes, Save, Cancel, and an always-visible top-right `X`. Parse USD with invariant decimal rules after trimming an optional `$` prefix; permit empty USD; accept only a non-negative integer used count. Display validation inline in the same dialog.

- [ ] **Step 6: Run focused tests**

Expected: all selected tests pass.

- [ ] **Step 7: Commit**

```powershell
git add src/CodexAccountSwitcher tests/CodexAccountSwitcher.Tests
git commit -m "feat: display and edit account quota metadata"
```

### Task 4: Remove the redundant add-account confirmation

**Files:**
- Modify: `src/CodexAccountSwitcher/ViewModels/MainWindowViewModel.cs`
- Modify: `src/CodexAccountSwitcher/App.xaml.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/MainWindowViewModelTests.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/WpfRuntimeTests.cs`

**Interfaces:**
- Removes: `IAccountDialogService.ConfirmAddAsync`
- Keeps: `IAccountDialogService.RunLoginAsync`

- [ ] **Step 1: Change tests to require one app-owned login window**

Replace confirmation-order tests with:

```csharp
await fixture.ViewModel.AddCommand.ExecuteAsync();
Assert.Equal(1, fixture.Dialog.RunLoginCallCount);
Assert.DoesNotContain("confirm-add", fixture.Dialog.AddEvents);
```

Retain runtime assertions that the operation window exposes a visible top-right `X` and cancellation waits for safe rollback.

- [ ] **Step 2: Run focused tests and verify failure**

Expected: FAIL while `ConfirmAddAsync` remains in the flow.

- [ ] **Step 3: Remove the confirmation method and call**

Delete `ConfirmAddAsync` from the interface, production dialog service, and test doubles. Start `RunLoginAsync` immediately after the existing helper-availability check. Do not alter `SafeLoginCoordinator`, cancellation, checkpoint restore, or Codex restart behavior.

- [ ] **Step 4: Run focused tests**

Expected: selected view-model and WPF tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/CodexAccountSwitcher/ViewModels/MainWindowViewModel.cs src/CodexAccountSwitcher/App.xaml.cs tests/CodexAccountSwitcher.Tests
git commit -m "feat: simplify add account flow"
```

### Task 5: Full verification, publish, and stable install

**Files:**
- Modify only if required by a failing contract: `scripts/publish.ps1`

**Interfaces:**
- Produces the existing exact nine-file release contract.

- [ ] **Step 1: Run complete Release tests**

```powershell
dotnet test tests/CodexAccountSwitcher.Tests/CodexAccountSwitcher.Tests.csproj -c Release --no-restore
```

Expected: zero failures and zero skipped tests.

- [ ] **Step 2: Run repository checks**

```powershell
git diff --check
git status --short
```

Expected: no whitespace errors; only intended files before the final commit.

- [ ] **Step 3: Back up live authentication state**

Copy `%USERPROFILE%\.codex\auth.json` and `%USERPROFILE%\.codex\accounts` to a timestamped directory under `%USERPROFILE%\CodexAccountSwitcherBackups`. Record SHA-256 before installation without printing authentication contents.

- [ ] **Step 4: Publish outside the repository**

Invoke `scripts/publish.ps1` with a fresh destination outside the worktree. Verify the exact nine-file contract, helper hash, manifest archive hash, and absence of staging/backup residue.

- [ ] **Step 5: Replace the stable install**

Exit only `CodexAccountSwitcher`, replace `C:\Users\demax\Apps\CodexAccountSwitcher`, recreate/update the desktop shortcut, and launch the installed executable.

- [ ] **Step 6: Verify the installed build**

Confirm one running app process, taskbar/tray behavior, embedded icon, and unchanged authentication SHA-256. Do not perform a real login, quota refresh, reset consumption, switch, or removal without the user's interactive participation.

- [ ] **Step 7: Commit any remaining intended changes**

```powershell
git add .
git commit -m "chore: finalize quota metadata release"
```
