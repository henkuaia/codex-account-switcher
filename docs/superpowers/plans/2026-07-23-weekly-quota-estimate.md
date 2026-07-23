# Weekly Quota Estimate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Automatically estimate a weekly account's single-period USD quota while keeping manual quota metadata separate.

**Architecture:** Preserve `/backend-api/wham/usage` as the authoritative percentage source. For weekly windows with nonzero usage, fetch daily Analytics credits for the current reset window and calculate the same include-reset-day/exclude-reset-day range used by `codex-quota-compass`.

**Tech Stack:** .NET 8, WPF, xUnit, `System.Text.Json`.

## Global Constraints

- Refresh only when the user clicks the quota refresh button.
- Never overwrite user-recorded quota metadata.
- A failed estimate must not discard the successfully refreshed percentage.
- `1000 Credits = US$40` is an estimate constant, not an official subscription price.

---

### Task 1: Weekly estimate calculation and display

**Files:**
- Create: `src/CodexAccountSwitcher/Services/WeeklyQuotaEstimator.cs`
- Modify: `src/CodexAccountSwitcher/Models/QuotaModels.cs`
- Modify: `src/CodexAccountSwitcher/Services/QuotaResponseParser.cs`
- Modify: `src/CodexAccountSwitcher/Services/QuotaService.cs`
- Modify: `src/CodexAccountSwitcher/ViewModels/AccountRowViewModel.cs`
- Modify: `src/CodexAccountSwitcher/MainWindow.xaml`
- Test: `tests/CodexAccountSwitcher.Tests/WeeklyQuotaEstimatorTests.cs`
- Test: `tests/CodexAccountSwitcher.Tests/QuotaServiceTests.cs`
- Test: `tests/CodexAccountSwitcher.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Produces: `WeeklyQuotaEstimate(LowerUsd, UpperUsd)`.
- Produces: optional estimate fields on `QuotaDisplay`.
- Consumes: daily Analytics `totals.credits`, exact `used_percent`, reset date.

- [ ] **Step 1: Write failing calculator, service, and display tests**

Test a US$8–US$24 estimate from 150 included Credits, 50 excluded Credits, and 25% usage. Test zero usage as “需产生用量后计算,” and verify Analytics failures preserve the weekly percentage.

- [ ] **Step 2: Run focused tests and verify failure**

Run:

```powershell
dotnet test tests/CodexAccountSwitcher.Tests/CodexAccountSwitcher.Tests.csproj -c Release --filter "FullyQualifiedName~WeeklyQuotaEstimatorTests|FullyQualifiedName~QuotaServiceTests|FullyQualifiedName~MainWindowViewModelTests"
```

Expected: FAIL because weekly estimate types and UI properties do not exist.

- [ ] **Step 3: Implement the minimal estimator and best-effort Analytics request**

Use UTC date buckets, include today through an exclusive next-day boundary, and keep the include/exclude reset-day range. Skip the Analytics request when usage is zero or reset timing is absent.

- [ ] **Step 4: Run focused and full tests**

Run the focused command above, then:

```powershell
dotnet test CodexAccountSwitcher.sln -c Release
```

Expected: all tests pass.

- [ ] **Step 5: Publish and replace the local installed build**

Publish through `scripts/publish.ps1`, verify the output contract and authentication hash, then replace the existing local installation without performing a live quota refresh.
