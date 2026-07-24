# Hybrid Estimator Final-Fixes Report

Date: 2026-07-25 (Asia/Shanghai)
Branch: `feature/codex-account-switcher`
Reviewed base: `2a4357f69338fa609d8abbe709ecd2fdacfa18d5`
Implementation commit: `a767ff1410ab8738726388c4035bef2fb16f1b6d`
Result: **PASS for source, focused tests, full Release tests, and diff checks**

## Scope and safety boundary

This wave addressed all 18 findings in
`.superpowers/sdd/hybrid-final-review-findings.md`.

- No real login, quota refresh, account switch, account removal, reset
  redemption, or session modification was executed.
- Collector, ledger, account, Analytics, and HTTP tests used temporary or
  synthetic fixtures only.
- `C:\Users\demax\.codex` was not modified or used as test input.
- The existing user-owned switcher PID `44896` was not killed, closed,
  activated, or otherwise touched.
- Publish/install, the exact nine-file release contract, release hashes, live
  auth hashes, and live tray/DPI checks were intentionally left to the final
  controller.

## Root causes

The defects came from five related assumptions:

1. Activation coverage was treated as proof that the local log scan was
   complete.
2. Local usage was reparsed as transient events, without durable
   rotation-safe offsets, pricing provenance, or partial-scan state.
3. Registry lifecycle, rate-card version, and activation identity were not
   part of local delta/intersection compatibility.
4. Persistence failures were treated as optional estimator failures rather
   than dirty state that must survive and be retried with a user-visible
   warning.
5. Parser, percentage, rounding, and UI fallback behavior were individually
   permissive enough to produce stale, falsely bounded, or misleading output.

## Finding-by-finding implementation

| # | Correction |
|---:|---|
| 1 | Added explicit local scan completeness, skipped-file and malformed-line metadata. Local full/delta estimates and historical local intersections are disabled for an incomplete current scan. Missing session roots and structurally incomplete token records are partial scans. |
| 2 | Zero-percent observations now store cumulative attributable local Credits while producing no interval until the percentage delta has a finite lower bound. |
| 3 | A no-active registry closes the open interval; a strictly newer same-key activation marker closes/reopens it. Delta generation and interval intersection require the same continuous activation. Half-open activation coverage now excludes an interval ending exactly at the server cutoff. |
| 4 | Every nonempty Analytics row must contain a valid `yyyy-MM-dd` date and nonnegative decimal Credits. Any unusable/mixed row makes the response `Invalid` and invokes conservative local fallback. |
| 5 | Full and delta percentage uncertainty is constructed once and clipped to `[0,100]`, including 0%, near-0%, near-100%, and 100% boundaries. |
| 6 | The hybrid service retains merged dirty state after load/save failures, retries later, reloads after transient read failures, and replays registry observations made while the ledger was blocked. Sanitized warnings propagate through `QuotaUpdate`, estimate detail, and `MainWindowViewModel.StatusText`; user cancellation remains primary. |
| 7 | Expected per-directory/per-file IO, ACL, deletion, metadata, open, and read races are isolated. Other files continue and only aggregate counts are exposed. |
| 8 | Added schema-2 relative-path file checkpoints with safe completed-line byte offsets, model/tier continuation state, priced aggregates, rate-card version, prefix hash, and completed-tail hash. Unchanged files resume incrementally; shrink, creation change, prefix change, or completed-tail change forces rescan. Checkpoints survive observation, registry, concurrent merge, and dirty retry paths. |
| 9 | Pricing returns explicit `UnknownModel`, `UnknownServiceTier`, and `InvalidUsage` reasons. Unknown tier shows `速度模式未知，部分用量无法计价` without guessing parent tier. |
| 10 | A failed `/usage` update keeps the previous live/cached display and timestamp/status context visible while adding the refresh error. |
| 11 | Ledger warnings do not replace server percentage/reset fields or disable account operations. |
| 12 | Monthly reset-history HTTP, parse, timeout, or data failure adds the specific `无法确定当前月额度片段` status. |
| 13 | Local fallback status includes aggregate skipped-file and malformed-line counts without paths or content. |
| 14 | Repeated unchanged evidence is deduplicated by segment, source, percentage/resolution, Credits, and interval before quality counting. |
| 15 | `CodexCreditRateCard.Version` is persisted on local observations/checkpoints. Delta and local intersection compatibility require the current matching version. |
| 16 | Every bounded weekly/monthly estimate displays `按 Credits 购买价格换算，非官方套餐额度`. |
| 17 | Weekly Analytics includes the complete start day in both bounds when the segment starts exactly at UTC midnight. |
| 18 | Full/delta intervals retain decimal precision through intersection and round to two USD decimals only for the final display/public rounded result. |

## TDD evidence

All production corrections were exercised through regression-first cycles.
Representative RED evidence:

- Initial percentage/Analytics/registry/cache batch: **11 failed, 139 passed,
  150 total** before production changes.
- Repeated registry observation without an activation marker: **1 failed**
  before the idempotency correction.
- Incremental collector/ledger work first failed to compile because
  `LocalUsageFileCheckpoint` did not exist, then exposed the append, restart,
  rotation, locked-file, and privacy regressions.
- Precise intersection work first failed to compile because the precise
  full/delta entrypoints did not exist.
- Quota completion-warning tests first failed with `CS1061` because
  `QuotaUpdate.Warning` did not exist.
- Disclaimer regression: **2 failed, 0 passed** before the exact UI text was
  added.
- View-model warning regression: **1 failed, 0 passed** because status was
  unconditionally `额度刷新完成。`.
- Checkpoint preservation through observation/registry mutation:
  **2 failed, 0 passed**.
- Cross-activation and cross-rate-card false intersection:
  **2 failed, 0 passed**.
- Missing session root, exact-cutoff activation, and incomplete token event:
  each failed independently before its correction.
- Adversarial-review blockers: **3 failed, 0 passed** for stale historical
  range on a partial scan, same-prefix grown rewrite, and transient load retry.
- Recovered-ledger activation replay: **1 failed, 0 passed**; the persisted
  account had one activation instead of the expected recovered two.

Focused GREEN milestones:

- Percentage/Analytics/registry/cache batch: **151 passed**.
- Collector, ledger, and rate-card checkpoint wave: **56 passed**.
- Hybrid and math wave: **46 passed**.
- Complete `QuotaServiceTests`: **30 passed**.
- Main-window and WPF disclaimer wave: **111 passed**.
- Final collector/ledger/hybrid focused set: **67 passed**.
- Adversarial-review blocker regressions: **3 passed**.
- Activation-replay regression: **1 passed**.

## Final verification

Repository-local SDK:

```powershell
.\.tools\dotnet\dotnet.exe
```

Focused touched-area Release aggregate:

```powershell
.\.tools\dotnet\dotnet.exe test `
  tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj `
  -c Release --no-restore `
  --filter "FullyQualifiedName~CodexCreditRateCardTests|FullyQualifiedName~LocalCodexUsageCollectorTests|FullyQualifiedName~QuotaEstimateLedgerServiceTests|FullyQualifiedName~QuotaEstimateMathTests|FullyQualifiedName~PeriodQuotaEstimatorTests|FullyQualifiedName~HybridQuotaEstimateServiceTests|FullyQualifiedName~QuotaServiceTests|FullyQualifiedName~QuotaCacheServiceTests|FullyQualifiedName~MainWindowViewModelTests|FullyQualifiedName~WpfInterfaceContractTests|FullyQualifiedName~WpfRuntimeTests"
```

Result: **289 passed, 0 failed, 0 skipped**.

Full Release suite:

```powershell
.\.tools\dotnet\dotnet.exe test `
  tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj `
  -c Release
```

Result: **645 passed, 0 failed, 0 skipped** in the reported **10 s** test
duration. Restore reported all projects current.

Whitespace validation:

```powershell
git diff --check
```

Result: exit `0`, no whitespace errors. Git emitted only the repository's
standard LF-to-CRLF working-copy notices.

An independent adversarial review found the partial-history, completed-tail,
transient-reload, and recovered-activation issues described above. After their
RED/GREEN corrections, the reviewer returned **CLEAN** with no remaining
Critical or Important blocker.

## Files changed

Production:

- `src/CodexAccountSwitcher/Models/QuotaEstimateModels.cs`
- `src/CodexAccountSwitcher/Models/QuotaModels.cs`
- `src/CodexAccountSwitcher/Services/CodexCreditRateCard.cs`
- `src/CodexAccountSwitcher/Services/HybridQuotaEstimateService.cs`
- `src/CodexAccountSwitcher/Services/LocalCodexUsageCollector.cs`
- `src/CodexAccountSwitcher/Services/PeriodQuotaEstimator.cs`
- `src/CodexAccountSwitcher/Services/QuotaEstimateLedgerService.cs`
- `src/CodexAccountSwitcher/Services/QuotaEstimateMath.cs`
- `src/CodexAccountSwitcher/Services/QuotaService.cs`
- `src/CodexAccountSwitcher/ViewModels/AccountRowViewModel.cs`
- `src/CodexAccountSwitcher/ViewModels/MainWindowViewModel.cs`

Tests:

- `tests/CodexAccountSwitcher.Tests/CodexCreditRateCardTests.cs`
- `tests/CodexAccountSwitcher.Tests/HybridQuotaEstimateServiceTests.cs`
- `tests/CodexAccountSwitcher.Tests/LocalCodexUsageCollectorTests.cs`
- `tests/CodexAccountSwitcher.Tests/MainWindowViewModelTests.cs`
- `tests/CodexAccountSwitcher.Tests/PeriodQuotaEstimatorTests.cs`
- `tests/CodexAccountSwitcher.Tests/QuotaEstimateLedgerServiceTests.cs`
- `tests/CodexAccountSwitcher.Tests/QuotaEstimateMathTests.cs`
- `tests/CodexAccountSwitcher.Tests/QuotaServiceTests.cs`

## Residual / controller-only verification

No source or test blocker remains. The following release-boundary checks were
not run in this fix task and remain explicitly assigned to the final
controller:

- publish and verify the exact nine-file contract;
- verify helper/archive/manifest hashes;
- take pre/post live auth and stored-account snapshot hashes;
- perform any permitted live tray/DPI smoke only if it does not interfere with
  the user-owned process.

---

## Hybrid rereview remediation

Date: 2026-07-25 (Asia/Shanghai)
Reviewed findings: `.superpowers/sdd/hybrid-rereview-findings.md`
Implementation commit: `14118f8b5a72dbd22992e052c89a64c6526076f8`
Result: **PASS for all five rereview findings, focused tests, full Release
tests, and diff checks**

### Finding-by-finding corrections

| # | Correction |
|---:|---|
| 1 | A missing, unreadable, or malformed-only recent session file now leaves an explicit incomplete tombstone. The tombstone remains relevant through the retained window and prevents older bounded observations from reviving as complete history. |
| 2 | Per-event checkpoint aggregates were replaced by UTC-hour compact buckets with first/last event timestamps, priced Credits, and pricing-failure counts. Schema 2 checkpoints migrate to schema 3 buckets. Refresh builds one reusable account attribution index, and a 25,000-event/48-hour regression verifies bounded bucket count and serialized ledger size without a wall-clock threshold. |
| 3 | Nonadvancing-clock registry validation errors are contained at the estimator boundary and return a sanitized warning; successful login, switch, and logout operations remain successful. Cancellation remains primary. |
| 4 | Refresh completion emits exactly one `QuotaUpdate` per account. Completion warnings are merged into the pending final update, and repeated failed refresh rendering prefixes cached status/tooltip text only once. |
| 5 | Enterprise accounts now disclose that local metadata cannot identify legacy token-rate eligibility. Business, Team, and Plus accounts do not receive that disclosure. |

Hour buckets that cross an account-activation or quota-segment boundary are not
split or assigned a midpoint. They contribute explicit lower/upper Credits
bounds and a conservative boundary-uncertainty status.

### Additional TDD evidence

Representative RED evidence recorded before the corresponding production
changes:

- malformed-only file deletion incorrectly allowed historical bounded output;
- a current unreadable file failed to retain/renew an incomplete checkpoint;
- login, switch, and logout each propagated `ArgumentOutOfRangeException` under
  a nonadvancing injected clock;
- the large-history regression initially failed to compile because compact
  buckets did not exist;
- lower/upper delta interval and boundary-attribution tests initially failed to
  compile because range properties/overloads did not exist;
- an invalid observation with upper Credits below lower Credits was accepted;
- completion emitted two updates for one account, and repeated failure
  rendering duplicated its prefix;
- Enterprise lacked the legacy-rate eligibility disclosure.

Focused GREEN milestones:

- malformed/tombstone lifecycle: **2 passed**, plus current-failure renewal:
  **1 passed**;
- nonadvancing login/switch/logout: **3 passed**;
- 25,000-event bounded-history regression: **1 passed**;
- collector and ledger focused set: **37 passed**;
- reset boundary, activation boundary, and single-enumeration index:
  **3 passed**;
- single-update, idempotent failure rendering, and cancellation preservation:
  **3 passed**;
- Enterprise/Business/Team/Plus plan disclosure matrix: **4 passed**.

### Final verification

Focused touched-area Release aggregate:

```powershell
.\.tools\dotnet\dotnet.exe test `
  tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj `
  -c Release --no-restore `
  --filter "FullyQualifiedName~CodexCreditRateCardTests|FullyQualifiedName~LocalCodexUsageCollectorTests|FullyQualifiedName~QuotaEstimateLedgerServiceTests|FullyQualifiedName~QuotaEstimateMathTests|FullyQualifiedName~PeriodQuotaEstimatorTests|FullyQualifiedName~HybridQuotaEstimateServiceTests|FullyQualifiedName~QuotaServiceTests|FullyQualifiedName~QuotaCacheServiceTests|FullyQualifiedName~MainWindowViewModelTests|FullyQualifiedName~WpfInterfaceContractTests|FullyQualifiedName~WpfRuntimeTests" `
  --logger "console;verbosity=minimal"
```

Result: **308 passed, 0 failed, 0 skipped** in the reported **3 s** test
duration.

Full Release suite:

```powershell
.\.tools\dotnet\dotnet.exe test `
  tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj `
  -c Release --no-restore `
  --logger "console;verbosity=minimal"
```

Result: **664 passed, 0 failed, 0 skipped** in the reported **10 s** test
duration.

`git diff --check` exited `0`; Git emitted only the repository's standard
LF-to-CRLF working-copy notices.

### Files changed in this remediation

Production:

- `src/CodexAccountSwitcher/Models/QuotaEstimateModels.cs`
- `src/CodexAccountSwitcher/Services/HybridQuotaEstimateService.cs`
- `src/CodexAccountSwitcher/Services/LocalCodexUsageCollector.cs`
- `src/CodexAccountSwitcher/Services/QuotaEstimateLedgerService.cs`
- `src/CodexAccountSwitcher/Services/QuotaEstimateMath.cs`
- `src/CodexAccountSwitcher/Services/QuotaService.cs`
- `src/CodexAccountSwitcher/ViewModels/AccountRowViewModel.cs`

Tests:

- `tests/CodexAccountSwitcher.Tests/HybridQuotaEstimateServiceTests.cs`
- `tests/CodexAccountSwitcher.Tests/LocalCodexUsageCollectorTests.cs`
- `tests/CodexAccountSwitcher.Tests/MainWindowViewModelTests.cs`
- `tests/CodexAccountSwitcher.Tests/QuotaEstimateLedgerServiceTests.cs`
- `tests/CodexAccountSwitcher.Tests/QuotaEstimateMathTests.cs`
- `tests/CodexAccountSwitcher.Tests/QuotaServiceTests.cs`

No source or test blocker remains. No live login, quota refresh, account switch,
logout, account removal, process control, publish, install, or user-owned
session mutation was performed.
