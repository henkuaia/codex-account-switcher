# Monthly Quota Estimator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Estimate the current Monthly base quota as a USD range while excluding usage before the most recent redeemed reset credit.

**Architecture:** Parse reset-credit redemption history independently, generalize the existing daily-Credits range calculation without changing Weekly behavior, and add a Monthly-only service path that fails closed when reset history is unavailable. The UI reuses the existing estimate row with period-specific Chinese text.

**Tech Stack:** .NET 9, WPF, `HttpClient`, `System.Text.Json`, xUnit

## Global Constraints

- Refresh only after the user clicks the existing quota refresh button.
- Never multiply by available, recorded, or redeemed reset counts.
- Never infer “no reset” when reset history is unavailable.
- Never overwrite user-recorded quota metadata or the optional official monthly limit.
- Preserve the successfully refreshed percentage when any estimate request fails.
- Preserve all existing Weekly estimate behavior and output.
- Do not log tokens, authentication JSON, or full endpoint responses.

---

### Task 1: Parse redeemed reset history

**Files:**
- Create: `src/CodexAccountSwitcher/Services/ResetCreditHistoryParser.cs`
- Create: `tests/CodexAccountSwitcher.Tests/ResetCreditHistoryParserTests.cs`

**Interfaces:**
- Produces: `ResetCreditHistoryParser.TryFindLatestRedeemedAt(string json, DateTimeOffset windowStart, DateTimeOffset serverNow, out DateTimeOffset? latestRedeemedAt)`.
- Returns `false` for an invalid root, missing `credits` array, or a redeemed item with a missing/invalid `redeemed_at`.
- Returns `true` and `null` when the response is valid but has no redeemed credit inside the requested window.

- [x] **Step 1: Add failing parser tests**

Cover these concrete cases:

```csharp
[Fact]
public void Finds_latest_redeemed_credit_inside_window()
{
    const string json = """
        {"credits":[
          {"status":"redeemed","redeemed_at":"2026-07-20T10:00:00Z"},
          {"status":"available","redeemed_at":null},
          {"status":"redeemed","redeemed_at":"2026-07-26T12:30:00Z"},
          {"status":"redeemed","redeemed_at":"2026-08-31T00:00:00Z"}
        ]}
        """;

    var valid = ResetCreditHistoryParser.TryFindLatestRedeemedAt(
        json,
        DateTimeOffset.Parse("2026-07-23T22:06:00Z"),
        DateTimeOffset.Parse("2026-07-30T00:00:00Z"),
        out var latest);

    Assert.True(valid);
    Assert.Equal(DateTimeOffset.Parse("2026-07-26T12:30:00Z"), latest);
}
```

Also assert that an empty valid array returns `true`/`null`, while malformed JSON and redeemed entries without a valid timestamp return `false`.

- [x] **Step 2: Run the parser tests and verify RED**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ResetCreditHistoryParserTests
```

Expected: compilation fails because `ResetCreditHistoryParser` does not exist.

- [x] **Step 3: Implement the minimal parser**

Implement one static parser that:

```csharp
public static bool TryFindLatestRedeemedAt(
    string json,
    DateTimeOffset windowStart,
    DateTimeOffset serverNow,
    out DateTimeOffset? latestRedeemedAt)
```

It must require a root object with a `credits` array, compare `status` to `redeemed` case-insensitively, parse `redeemed_at` with invariant round-trip rules, and choose the latest timestamp in the inclusive `[windowStart, serverNow]` interval.

- [x] **Step 4: Run the parser tests and verify GREEN**

Run the command from Step 2.

Expected: all `ResetCreditHistoryParserTests` pass.

---

### Task 2: Generalize the Credits interval calculation

**Files:**
- Create: `src/CodexAccountSwitcher/Services/PeriodQuotaEstimator.cs`
- Modify: `src/CodexAccountSwitcher/Services/WeeklyQuotaEstimator.cs`
- Modify: `src/CodexAccountSwitcher/Models/QuotaModels.cs`
- Create: `tests/CodexAccountSwitcher.Tests/PeriodQuotaEstimatorTests.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/WeeklyQuotaEstimatorTests.cs`

**Interfaces:**
- Produces: `PeriodQuotaEstimate(decimal LowerUsd, decimal UpperUsd)`.
- Produces: `PeriodQuotaEstimator.TryEstimate(string json, double usedPercent, DateOnly segmentStartDate, bool includeStartDayInLower)`.
- Keeps `WeeklyQuotaEstimator.TryEstimate(...)` and `WeeklyQuotaEstimate` backward compatible by delegating to the shared estimator with `includeStartDayInLower: false`.

- [x] **Step 1: Add failing period-estimator tests**

Use 25% usage with 50 Credits on the boundary day and 100 Credits afterwards:

```csharp
var range = PeriodQuotaEstimator.TryEstimate(
    analyticsJson,
    usedPercent: 25,
    segmentStartDate: new DateOnly(2026, 7, 23),
    includeStartDayInLower: false);

Assert.Equal(16m, range!.LowerUsd);
Assert.Equal(24m, range.UpperUsd);
```

With `includeStartDayInLower: true`, assert both bounds are `US$24`. Keep the existing invalid-percentage, invalid-JSON, and Weekly range expectations unchanged.

- [x] **Step 2: Run focused estimator tests and verify RED**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PeriodQuotaEstimatorTests|FullyQualifiedName~WeeklyQuotaEstimatorTests"
```

Expected: compilation fails because the shared estimator and result type do not exist.

- [x] **Step 3: Implement the shared estimator and Weekly wrapper**

Move the existing Credits parsing and `1000 Credits = US$40` conversion into `PeriodQuotaEstimator`. Calculate:

```text
upperCredits = all returned Credits
lowerCredits = includeStartDayInLower
    ? upperCredits
    : upperCredits - Credits on segmentStartDate
```

Divide each bound by `usedPercent / 100`, convert to USD, and round to two decimals. Make `WeeklyQuotaEstimator` delegate with `includeStartDayInLower: false`.

- [x] **Step 4: Run focused estimator tests and verify GREEN**

Run the command from Step 2.

Expected: all period and Weekly estimator tests pass with unchanged Weekly values.

---

### Task 3: Fetch reset history and estimate Monthly quota

**Files:**
- Modify: `src/CodexAccountSwitcher/Services/QuotaService.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/QuotaServiceTests.cs`

**Interfaces:**
- Consumes: `ResetCreditHistoryParser.TryFindLatestRedeemedAt(...)`.
- Consumes: `PeriodQuotaEstimator.TryEstimate(...)`.
- Adds a best-effort authenticated GET to `https://chatgpt.com/backend-api/wham/rate-limit-reset-credits` only for eligible Monthly windows.
- Preserves `QuotaDisplay.EstimatedPeriodQuotaLowerUsd` and `EstimatedPeriodQuotaUpperUsd` as the UI contract.

- [x] **Step 1: Add failing Monthly service tests**

Add tests proving:

1. A Monthly response with 50% used, a latest redeemed credit at `2026-07-26T12:30:00Z`, and Analytics rows from July 26 onward produces the expected USD range and sends requests in the order usage → reset history → Analytics.
2. A valid empty reset history uses the natural Monthly window start.
3. A non-success or invalid reset-history response preserves the Monthly percentage and leaves estimate fields null.
4. Zero Monthly usage sends only the usage request.
5. Existing Weekly refresh still sends only usage → Analytics.

Assert the Monthly Analytics URL uses the latest redeemed date and an exclusive server-current-date-plus-one end date.

- [x] **Step 2: Run QuotaService tests and verify RED**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~QuotaServiceTests
```

Expected: the new Monthly request-count and estimate assertions fail.

- [x] **Step 3: Implement the Monthly service path**

Keep the existing Weekly path unchanged. For Monthly:

```text
naturalStart = ResetsAt - WindowDuration
latestRedeemed = latest valid redeemed_at in [naturalStart, ServerNow]
segmentStart = latestRedeemed ?? naturalStart
includeStartDayInLower = segmentStart UTC time is exactly 00:00:00
```

Return the original `QuotaDisplay` on timeout, HTTP failure, invalid reset history, invalid Analytics, or a zero/inconsistent estimate. Reuse authenticated request construction and redaction behavior.

- [x] **Step 4: Run QuotaService tests and verify GREEN**

Run the command from Step 2.

Expected: all QuotaService tests pass.

---

### Task 4: Display Monthly estimate status

**Files:**
- Modify: `src/CodexAccountSwitcher/ViewModels/AccountRowViewModel.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/MainWindowViewModelTests.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/WpfInterfaceContractTests.cs`

**Interfaces:**
- Produces: `估算单次月额度 US$<lower>–<upper>` or one value when bounds match.
- Produces: `估算单次月额度：产生用量后可计算` for zero usage.
- Produces: `估算单次月额度：暂不可用` when an eligible Monthly response has no estimate.

- [x] **Step 1: Add failing view-model tests**

Create Monthly `QuotaDisplay` cases for a range, an equal-bound value, zero usage, and unavailable estimate. Assert `HasEstimatedPeriodQuotaText` is true and the exact Chinese text matches the interface above. Keep all Weekly assertions unchanged.

- [x] **Step 2: Run view-model tests and verify RED**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~MainWindowViewModelTests|FullyQualifiedName~WpfInterfaceContractTests"
```

Expected: Monthly estimate visibility/text assertions fail.

- [x] **Step 3: Implement period-specific estimate text**

Extend only `UpdateMetadataDisplay()` so Weekly and Monthly share the existing estimate fields but render period-specific Chinese labels. Do not change XAML layout.

- [x] **Step 4: Run view-model tests and verify GREEN**

Run the command from Step 2.

Expected: all focused tests pass.

---

### Task 5: Verify, publish, install, and push

**Files:**
- Modify: `docs/superpowers/plans/2026-07-24-monthly-quota-estimator.md` checkboxes only.
- Build output: `dist/CodexAccountSwitcher`
- Installed output: `C:\Users\demax\Apps\CodexAccountSwitcher`

**Interfaces:**
- Preserves live authentication and account snapshots.
- Replaces only the installed application binaries after a successful publish.
- Keeps the local feature branch and pushes its HEAD to remote `main`.

- [x] **Step 1: Run the complete Release suite**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test CodexAccountSwitcher.sln -c Release --no-restore
```

Expected: all tests pass with zero failures and zero skips.

- [x] **Step 2: Publish and verify the artifact contract**

Record SHA-256 for `%USERPROFILE%\.codex\auth.json` and all existing account snapshot files, then run:

```powershell
& .\scripts\publish.ps1
```

Verify the expected nine-file output, helper and manifest hashes, and absence of staging/backup residue. Confirm authentication hashes remain unchanged.

- [x] **Step 3: Replace the installed build**

Stop only `CodexAccountSwitcher.exe`. Rename the existing
`C:\Users\demax\Apps\CodexAccountSwitcher` directory to a timestamped sibling
backup, copy the verified `dist\CodexAccountSwitcher` directory into the
original path, verify hashes, and restart `CodexAccountSwitcher.exe`.

Do not close Codex, perform a live account switch, consume a reset credit, or
automatically call the quota endpoints.

- [x] **Step 4: Commit and push**

Run:

```powershell
git add src tests docs/superpowers/plans/2026-07-24-monthly-quota-estimator.md
git commit -m "feat: estimate monthly quota after resets"
git -c http.proxy=http://127.0.0.1:7897 -c https.proxy=http://127.0.0.1:7897 push origin HEAD:main
```

Verify remote `main` equals local HEAD and the worktree is clean.
