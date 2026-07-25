# Minimize and Zero-Credit Estimate Fallback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add standard taskbar minimization and make zero-credit Analytics responses use the existing local quota estimator, while showing estimate status directly on each card.

**Architecture:** Preserve the current window/tray lifecycle and hybrid estimator. Change only the Analytics source-selection predicate, the compact card visibility, and the title-bar command set.

**Tech Stack:** .NET 9, WPF, xUnit

## Global Constraints

- The close button and title-bar X continue to hide the window to the tray.
- Do not modify authentication files, account snapshots, switching, quota percentage, reset time, or cache format.
- Do not fabricate estimates when local evidence is insufficient.

---

### Task 1: Zero-credit Analytics fallback and card status

**Files:**
- Modify: `tests/CodexAccountSwitcher.Tests/HybridQuotaEstimateServiceTests.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/MainWindowViewModelTests.cs`
- Modify: `src/CodexAccountSwitcher/Services/HybridQuotaEstimateService.cs`
- Modify: `src/CodexAccountSwitcher/ViewModels/AccountRowViewModel.cs`
- Modify: `src/CodexAccountSwitcher/MainWindow.xaml`

**Interfaces:**
- Consumes: `HybridQuotaEstimateService.ApplyObservation(...)`
- Produces: local-source observation for Analytics payloads whose `UpperCredits` is zero; visible `EstimatedPeriodQuotaText`.

- [ ] **Step 1: Write failing tests**

Add a hybrid-estimator test using `AnalyticsUsageState.Valid`, zero lower/upper Credits, and attributable local usage. Assert `EstimateSource == QuotaEstimateSource.Local` and a non-null estimate. Update view-model/contract tests to expect the collection status text and direct card visibility.

- [ ] **Step 2: Verify the tests fail**

Run:
`dotnet test tests/CodexAccountSwitcher.Tests/CodexAccountSwitcher.Tests.csproj -c Release --filter "FullyQualifiedName~HybridQuotaEstimateServiceTests|FullyQualifiedName~MainWindowViewModelTests|FullyQualifiedName~WpfInterfaceContractTests"`

Expected: failure because zero Credits still selects Analytics and the compact card does not expose the status as required.

- [ ] **Step 3: Implement the minimum change**

Require `analytics.UpperCredits > 0` in the Analytics-preferred branch. Let zero Credits follow the existing local fallback and add a concise zero-Credits status. Change unavailable copy to `额度估算：采集中，还需使用后刷新`, preserving the existing period-specific US-dollar copy when bounds exist. Place the bound text directly in the compact card.

- [ ] **Step 4: Verify targeted tests pass**

Run the Step 2 command and expect all selected tests to pass.

### Task 2: Standard minimize button

**Files:**
- Modify: `tests/CodexAccountSwitcher.Tests/WpfInterfaceContractTests.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/WpfRuntimeTests.cs`
- Modify: `src/CodexAccountSwitcher/MainWindow.xaml`
- Modify: `src/CodexAccountSwitcher/MainWindow.xaml.cs`

**Interfaces:**
- Produces: `WindowMinimizeButton` and `MinimizeButton_Click`.

- [ ] **Step 1: Write failing tests**

Assert the XAML contains `WindowMinimizeButton` wired to `MinimizeButton_Click`, and the runtime window exposes the named button.

- [ ] **Step 2: Verify the tests fail**

Run:
`dotnet test tests/CodexAccountSwitcher.Tests/CodexAccountSwitcher.Tests.csproj -c Release --filter "FullyQualifiedName~WpfInterfaceContractTests|FullyQualifiedName~WpfRuntimeTests"`

Expected: failure because the named minimize button does not exist.

- [ ] **Step 3: Implement the minimum change**

Add a title-bar button immediately before the close button and implement:

```csharp
private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
    WindowState = WindowState.Minimized;
```

- [ ] **Step 4: Verify targeted tests pass**

Run the Step 2 command and expect all selected tests to pass.

### Task 3: Release verification and installation

**Files:**
- Modify only generated publication output outside the repository.

**Interfaces:**
- Produces: verified Release build and updated local installation.

- [ ] **Step 1: Run the complete Release suite**

Run:
`dotnet test CodexAccountSwitcher.sln -c Release`

Expected: zero failed and zero skipped.

- [ ] **Step 2: Publish and replace the current installation**

Run the existing `scripts/publish.ps1`, stop the installed app, back up the current installation, replace it with the exact publish output, and restart it.

- [ ] **Step 3: Verify installation**

Confirm the installed executable starts and responds, the installation contains the expected publish files, and `git diff --check` reports no whitespace errors.

- [ ] **Step 4: Commit and push**

Commit the implementation and push `main` to `origin/main`.
