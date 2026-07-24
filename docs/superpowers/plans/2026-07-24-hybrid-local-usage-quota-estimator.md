# Hybrid Local Usage Quota Estimator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When the server Analytics endpoint returns no usage rows, estimate the current Weekly or Monthly quota from server percentage snapshots plus locally attributable Codex token usage, then refine the result across manual refreshes.

**Architecture:** Keep `/wham/usage` authoritative for percentage, period, and reset time. Add a local JSONL usage collector, an official model rate card, a versioned account-activation/observation ledger, and a pure interval estimator. `QuotaService.RefreshAllAsync` scans local sessions once per manual refresh and applies Analytics-first/local-fallback estimates per account; the existing quota cache persists the resulting display.

**Tech Stack:** C# 13, .NET 9, WPF, `System.Text.Json`, xUnit 2.9.3. No new dependencies.

## Global Constraints

- Preserve the existing `QuotaService.RefreshAccountAsync` and `RefreshAllAsync` public entrypoints.
- Do not add automatic refresh, automatic account switching, or automatic reset redemption.
- Never modify `.codex/auth.json`, account snapshots, session JSONL files, conversation history, or personalization.
- Never store access tokens, emails, prompt/response content, request headers, or raw endpoint responses in the estimate ledger.
- Only events with unambiguous account activation coverage may be attributed.
- `reasoning_output_tokens` is already included in `output_tokens` and must never be charged again.
- Unknown or preview models without an official rate are unpriced; do not guess.
- Standard/Fast multipliers must match the current official OpenAI rate card and Speed documentation.
- Keep `1000 Credits = US$40` as the existing display conversion and label it as an estimate.
- Tests must not perform real login, account switch, account removal, reset redemption, or live quota calls.
- Use TDD for every production change and commit after every completed task.

---

## File Map

**Create**

- `src/CodexAccountSwitcher/Models/QuotaEstimateModels.cs` — local usage, activation interval, segment, observation, and ledger records.
- `src/CodexAccountSwitcher/Services/CodexCreditRateCard.cs` — official model rates and corrected token-to-Credits formula.
- `src/CodexAccountSwitcher/Services/LocalCodexUsageCollector.cs` — streaming parser for relevant `.codex/sessions/**/*.jsonl` files.
- `src/CodexAccountSwitcher/Services/QuotaEstimateMath.cs` — percentage uncertainty, full/delta interval construction, and recent-compatible intersection.
- `src/CodexAccountSwitcher/Services/QuotaEstimateLedgerService.cs` — versioned atomic persistence and account activation tracking.
- `src/CodexAccountSwitcher/Services/HybridQuotaEstimateService.cs` — one-scan-per-refresh orchestration and observation recording.
- Corresponding xUnit test files under `tests/CodexAccountSwitcher.Tests`.

**Modify**

- `src/CodexAccountSwitcher/Models/AccountModels.cs` — expose registry activation timestamp.
- `src/CodexAccountSwitcher/Models/QuotaModels.cs` — carry estimate source, quality, status, and observation count.
- `src/CodexAccountSwitcher/Services/AccountRegistryService.cs` — parse `active_account_activated_at_ms`.
- `src/CodexAccountSwitcher/Services/PeriodQuotaEstimator.cs` — distinguish valid, empty, and invalid Analytics payloads.
- `src/CodexAccountSwitcher/Services/QuotaService.cs` — Analytics-first/local-fallback integration.
- `src/CodexAccountSwitcher/Services/QuotaCacheService.cs` — validate new optional display fields.
- `src/CodexAccountSwitcher/ViewModels/MainWindowViewModel.cs` — observe registry lifecycle.
- `src/CodexAccountSwitcher/ViewModels/AccountRowViewModel.cs` — Chinese source/status copy.
- `src/CodexAccountSwitcher/App.xaml.cs` — construct and inject the new services.
- Existing tests for registry, quota service, cache, view model, and row display.

---

### Task 1: Registry Activation Time and Estimate Domain Models

**Files:**

- Create: `src/CodexAccountSwitcher/Models/QuotaEstimateModels.cs`
- Modify: `src/CodexAccountSwitcher/Models/AccountModels.cs`
- Modify: `src/CodexAccountSwitcher/Models/QuotaModels.cs`
- Modify: `src/CodexAccountSwitcher/Services/AccountRegistryService.cs`
- Test: `tests/CodexAccountSwitcher.Tests/AccountRegistryServiceTests.cs`

**Interfaces:**

- Produces: `AccountRegistry.ActiveAccountActivatedAt`
- Produces: `QuotaEstimateSource`, `QuotaEstimateQuality`, `LocalUsageEvent`, `AccountActivationInterval`, `QuotaSegment`, `QuotaUsageObservation`, `QuotaEstimateLedgerState`
- Produces optional `QuotaDisplay.EstimateSource`, `EstimateQuality`, `EstimateStatus`, and `EstimateObservationCount`

- [ ] **Step 1: Write the failing registry and model tests**

Add a registry fixture with:

```json
{
  "schema_version": 3,
  "active_account_key": "user-1::acct-1",
  "active_account_activated_at_ms": 1784892480313,
  "accounts": [{
    "account_key": "user-1::acct-1",
    "chatgpt_account_id": "acct-1",
    "chatgpt_user_id": "user-1",
    "email": "first@example.com"
  }]
}
```

Assert:

```csharp
Assert.Equal(
    DateTimeOffset.FromUnixTimeMilliseconds(1784892480313),
    registry.ActiveAccountActivatedAt);
Assert.Equal(QuotaEstimateSource.None, display.EstimateSource);
Assert.Equal(QuotaEstimateQuality.None, display.EstimateQuality);
Assert.Equal(0, display.EstimateObservationCount);
```

Also add theory cases proving missing, negative, and future-overflow activation timestamps become `null` rather than invalidating an otherwise valid registry.

- [ ] **Step 2: Run the focused tests and verify failure**

Run:

```powershell
dotnet test tests/CodexAccountSwitcher.Tests/CodexAccountSwitcher.Tests.csproj -c Release --filter "FullyQualifiedName~AccountRegistryServiceTests"
```

Expected: FAIL because `ActiveAccountActivatedAt` and estimate-domain types do not exist.

- [ ] **Step 3: Add the domain records and parse activation time**

Use non-positional optional properties to preserve existing `AccountRegistry` call sites:

```csharp
public sealed record AccountRegistry(
    int SchemaVersion,
    string? ActiveAccountKey,
    IReadOnlyList<AccountRecord> Accounts)
{
    public DateTimeOffset? ActiveAccountActivatedAt { get; init; }

    public static AccountRegistry Empty { get; } =
        new(3, null, Array.Empty<AccountRecord>());
}
```

Add these exact domain types:

```csharp
public enum QuotaEstimateSource { None, Analytics, Local }
public enum QuotaEstimateQuality { None, Initial, MultiPoint }
public enum QuotaObservationKind { FullSegment, Delta }

public sealed record LocalUsageEvent(
    DateTimeOffset Timestamp,
    string Model,
    string ServiceTier,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens);

public sealed record AccountActivationInterval(
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);

public sealed record QuotaSegment(
    QuotaPeriod Period,
    DateTimeOffset SegmentStart,
    DateTimeOffset ResetsAt);

public sealed record QuotaUsageObservation(
    QuotaSegment Segment,
    DateTimeOffset ObservedAt,
    double UsedPercent,
    double PercentResolution,
    decimal AttributedCredits,
    bool HasFullSegmentCoverage,
    decimal? LowerUsd,
    decimal? UpperUsd,
    QuotaEstimateSource Source,
    QuotaObservationKind Kind);

public sealed record AccountQuotaEstimateLedger(
    IReadOnlyList<AccountActivationInterval> Activations,
    IReadOnlyList<QuotaUsageObservation> Observations);

public sealed record QuotaEstimateLedgerState(
    IReadOnlyDictionary<string, AccountQuotaEstimateLedger> Accounts)
{
    public static QuotaEstimateLedgerState Empty { get; } = new(
        new Dictionary<string, AccountQuotaEstimateLedger>(StringComparer.Ordinal));
}
```

Add optional display fields:

```csharp
public QuotaEstimateSource EstimateSource { get; init; }
public QuotaEstimateQuality EstimateQuality { get; init; }
public string? EstimateStatus { get; init; }
public int EstimateObservationCount { get; init; }
```

Parse `active_account_activated_at_ms` as nullable `long`; accept only values that
`DateTimeOffset.FromUnixTimeMilliseconds` can convert and return `null` on
`ArgumentOutOfRangeException`.

- [ ] **Step 4: Run the focused tests**

Run the command from Step 2.

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/CodexAccountSwitcher/Models/AccountModels.cs src/CodexAccountSwitcher/Models/QuotaModels.cs src/CodexAccountSwitcher/Models/QuotaEstimateModels.cs src/CodexAccountSwitcher/Services/AccountRegistryService.cs tests/CodexAccountSwitcher.Tests/AccountRegistryServiceTests.cs
git commit -m "feat: model quota estimate observations"
```

---

### Task 2: Correct Local Token Collection and Official Rate Card

**Files:**

- Create: `src/CodexAccountSwitcher/Services/CodexCreditRateCard.cs`
- Create: `src/CodexAccountSwitcher/Services/LocalCodexUsageCollector.cs`
- Create: `tests/CodexAccountSwitcher.Tests/CodexCreditRateCardTests.cs`
- Create: `tests/CodexAccountSwitcher.Tests/LocalCodexUsageCollectorTests.cs`

**Interfaces:**

- Consumes: `LocalUsageEvent`
- Produces: `CodexCreditRateCard.TryCalculateCredits(LocalUsageEvent usage, out decimal credits)`
- Produces: `LocalCodexUsageCollector.CollectAsync(DateTimeOffset earliestUtc, CancellationToken cancellationToken)`
- Produces: `LocalUsageCollectionResult(Events, InvalidLineCount)`

- [ ] **Step 1: Write failing rate-card tests**

Cover the official Credits per one million tokens:

```csharp
[Theory]
[InlineData("gpt-5.6-sol", 125, 12.5, 750)]
[InlineData("gpt-5.6-terra", 62.5, 6.25, 375)]
[InlineData("gpt-5.6-luna", 25, 2.5, 150)]
[InlineData("gpt-5.5", 125, 12.5, 750)]
[InlineData("gpt-5.5-cyber", 500, 50, 3000)]
[InlineData("gpt-5.4", 62.5, 6.25, 375)]
[InlineData("gpt-5.4-mini", 18.75, 1.875, 113)]
[InlineData("gpt-5.3-codex", 43.75, 4.375, 350)]
[InlineData("gpt-5.2", 43.75, 4.375, 350)]
```

Use a concrete regression event:

```csharp
var usage = new LocalUsageEvent(
    DateTimeOffset.Parse("2026-07-24T05:00:00Z"),
    "gpt-5.4",
    "default",
    InputTokens: 20_203,
    CachedInputTokens: 10_000,
    OutputTokens: 397);
```

Assert the formula charges `10_203` uncached input, `10_000` cached input, and
`397` output exactly once. Add:

- `priority` multiplies GPT-5.6/GPT-5.5 by `2.5`;
- `priority` multiplies GPT-5.4 by `2.0`;
- unknown service tiers and unsupported priority/model combinations return `false`;
- cached input greater than input, negative tokens, blank model, and unknown model return `false`;
- no context-window multiplier exists.

- [ ] **Step 2: Run rate-card tests and verify failure**

```powershell
dotnet test tests/CodexAccountSwitcher.Tests/CodexAccountSwitcher.Tests.csproj -c Release --filter "FullyQualifiedName~CodexCreditRateCardTests"
```

Expected: FAIL because `CodexCreditRateCard` does not exist.

- [ ] **Step 3: Implement the immutable rate card and formula**

Use:

```csharp
private sealed record Rates(decimal Input, decimal CachedInput, decimal Output);

private static readonly IReadOnlyDictionary<string, Rates> StandardRates =
    new Dictionary<string, Rates>(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5.6-sol"] = new(125m, 12.5m, 750m),
        ["gpt-5.6-terra"] = new(62.5m, 6.25m, 375m),
        ["gpt-5.6-luna"] = new(25m, 2.5m, 150m),
        ["gpt-5.5"] = new(125m, 12.5m, 750m),
        ["gpt-5.5-cyber"] = new(500m, 50m, 3000m),
        ["gpt-5.4"] = new(62.5m, 6.25m, 375m),
        ["gpt-5.4-mini"] = new(18.75m, 1.875m, 113m),
        ["gpt-5.3-codex"] = new(43.75m, 4.375m, 350m),
        ["gpt-5.2"] = new(43.75m, 4.375m, 350m),
    };
```

Calculate:

```csharp
var uncachedInput = usage.InputTokens - usage.CachedInputTokens;
credits = (
    uncachedInput * rates.Input +
    usage.CachedInputTokens * rates.CachedInput +
    usage.OutputTokens * rates.Output) / 1_000_000m;
credits *= ResolveFastMultiplier(usage.Model, usage.ServiceTier);
credits = Math.Round(credits, 9, MidpointRounding.AwayFromZero);
```

Do not accept invalid counts and do not read `reasoning_output_tokens`.

- [ ] **Step 4: Write failing local collector tests**

Create temporary JSONL fixtures containing:

```json
{"timestamp":"2026-07-24T04:59:00Z","type":"turn_context","payload":{"model":"gpt-5.4"}}
{"timestamp":"2026-07-24T05:00:00Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"service_tier":"priority"}}}
{"timestamp":"2026-07-24T05:01:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":20203,"cached_input_tokens":10000,"output_tokens":397,"reasoning_output_tokens":201}}}}
```

Assert:

- one `LocalUsageEvent` is returned with model `gpt-5.4`, tier `priority`, and output `397`;
- reasoning `201` is not added;
- `total_token_usage` is ignored;
- model/tier changes apply only to later token events;
- lines before `earliestUtc` are omitted;
- files with `LastWriteTimeUtc < earliestUtc` are skipped;
- one malformed/in-progress final line increments `InvalidLineCount` without losing valid events;
- cancellation is honored.

- [ ] **Step 5: Implement the streaming collector**

Add:

```csharp
public sealed record LocalUsageCollectionResult(
    IReadOnlyList<LocalUsageEvent> Events,
    int InvalidLineCount);

public sealed class LocalCodexUsageCollector
{
    public LocalCodexUsageCollector(string sessionRoot);

    public Task<LocalUsageCollectionResult> CollectAsync(
        DateTimeOffset earliestUtc,
        CancellationToken cancellationToken);
}
```

Enumerate only `*.jsonl` files whose `LastWriteTimeUtc >= earliestUtc.UtcDateTime`.
Read with `FileShare.ReadWrite | FileShare.Delete`, parse line by line, maintain
the current model and service tier per file, and copy only numeric token counts
plus timestamp/model/tier into memory. Never retain raw lines.

- [ ] **Step 6: Run both focused suites**

```powershell
dotnet test tests/CodexAccountSwitcher.Tests/CodexAccountSwitcher.Tests.csproj -c Release --filter "FullyQualifiedName~CodexCreditRateCardTests|FullyQualifiedName~LocalCodexUsageCollectorTests"
```

Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add src/CodexAccountSwitcher/Services/CodexCreditRateCard.cs src/CodexAccountSwitcher/Services/LocalCodexUsageCollector.cs tests/CodexAccountSwitcher.Tests/CodexCreditRateCardTests.cs tests/CodexAccountSwitcher.Tests/LocalCodexUsageCollectorTests.cs
git commit -m "feat: collect and price local codex usage"
```

---

### Task 3: Versioned Estimate Ledger and Activation Tracking

**Files:**

- Create: `src/CodexAccountSwitcher/Services/QuotaEstimateLedgerService.cs`
- Create: `tests/CodexAccountSwitcher.Tests/QuotaEstimateLedgerServiceTests.cs`

**Interfaces:**

- Consumes: `AccountRegistry`, `QuotaEstimateLedgerState`
- Produces: `LoadAsync`, `SaveAsync`, and pure `ObserveRegistry`
- Storage: `%LOCALAPPDATA%\CodexAccountSwitcher\quota-estimate-ledger.json`

- [ ] **Step 1: Write failing persistence and activation tests**

Prove:

- a missing file returns an empty state;
- schema version `1` round-trips account keys, activation intervals, segment
  identity, observations, source, and bounds;
- malformed/unsupported files are retained and block overwrite;
- save uses a same-directory temporary file and leaves no residue;
- no serialized property can contain email, token, prompt, response, header, or
  raw JSON fields;
- the first registry observation opens the active account interval at
  `ActiveAccountActivatedAt` when it is valid and not later than observation time;
- a switch closes the previous interval at the new activation time and opens the
  new account interval;
- repeated registry loads do not duplicate intervals;
- a missing/invalid activation timestamp starts at observation time;
- overlapping intervals, including overlap between different accounts, are rejected on load.

Example:

```csharp
var first = new AccountRegistry(3, "a", accounts)
{
    ActiveAccountActivatedAt = DateTimeOffset.Parse("2026-07-24T04:00:00Z"),
};
var second = new AccountRegistry(3, "b", accounts)
{
    ActiveAccountActivatedAt = DateTimeOffset.Parse("2026-07-24T06:00:00Z"),
};

var afterFirst = QuotaEstimateLedgerService.ObserveRegistry(
    QuotaEstimateLedgerState.Empty,
    first,
    DateTimeOffset.Parse("2026-07-24T05:00:00Z"));
var afterSecond = QuotaEstimateLedgerService.ObserveRegistry(
    afterFirst,
    second,
    DateTimeOffset.Parse("2026-07-24T06:01:00Z"));
```

Assert account `a` has `[04:00, 06:00)` and `b` has `[06:00, null)`.

- [ ] **Step 2: Run focused tests and verify failure**

```powershell
dotnet test tests/CodexAccountSwitcher.Tests/CodexAccountSwitcher.Tests.csproj -c Release --filter "FullyQualifiedName~QuotaEstimateLedgerServiceTests"
```

Expected: FAIL because the service does not exist.

- [ ] **Step 3: Implement validated load/save and registry observation**

Expose:

```csharp
public sealed record QuotaEstimateLedgerLoadResult(
    QuotaEstimateLedgerState State,
    string? Error);

public sealed class QuotaEstimateLedgerService
{
    public QuotaEstimateLedgerService(string path);
    public static QuotaEstimateLedgerService CreateDefault();
    public Task<QuotaEstimateLedgerLoadResult> LoadAsync(CancellationToken cancellationToken);
    public Task SaveAsync(QuotaEstimateLedgerState state, CancellationToken cancellationToken);

    public static QuotaEstimateLedgerState ObserveRegistry(
        QuotaEstimateLedgerState state,
        AccountRegistry registry,
        DateTimeOffset observedAt);
}
```

Match `QuotaCacheService` atomic write and preserve-on-invalid behavior. Validate
nonblank account keys; UTC-ordered nonoverlapping activation intervals; valid
periods and segment times; finite percentages in `[0,100]`; positive resolution;
nonnegative Credits/bounds; paired ordered bounds; and observations ordered by
`ObservedAt`.

- [ ] **Step 4: Run focused tests**

Run the command from Step 2.

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/CodexAccountSwitcher/Services/QuotaEstimateLedgerService.cs tests/CodexAccountSwitcher.Tests/QuotaEstimateLedgerServiceTests.cs
git commit -m "feat: persist quota estimate observations"
```

---

### Task 4: Pure Interval Estimation and Multi-Point Convergence

**Files:**

- Create: `src/CodexAccountSwitcher/Services/QuotaEstimateMath.cs`
- Create: `tests/CodexAccountSwitcher.Tests/QuotaEstimateMathTests.cs`
- Modify: `src/CodexAccountSwitcher/Services/PeriodQuotaEstimator.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/PeriodQuotaEstimatorTests.cs`

**Interfaces:**

- Produces: `TryCreateFullInterval`, `TryCreateDeltaInterval`, `IntersectRecentCompatible`
- Produces: `AnalyticsUsageParseResult` with `Valid`, `Empty`, or `Invalid`

- [ ] **Step 1: Write failing interval tests**

Use `UsdPerCredit = 40m / 1000m`.

For a full observation with `100 Credits`, displayed `25%`, resolution `1`:

```csharp
var result = QuotaEstimateMath.TryCreateFullInterval(
    lowerCredits: 100m,
    upperCredits: 100m,
    usedPercent: 25,
    percentResolution: 1);
```

Assert:

```text
pLow = 24.5
pHigh = 25.5
lower = 100 / 0.255 × 0.04
upper = 100 / 0.245 × 0.04
```

Also prove:

- `usedPercent <= resolution / 2` produces no finite interval;
- Analytics start-day uncertainty accepts different lower/upper Credits;
- delta uses `laterLow - earlierHigh` and `laterHigh - earlierLow`;
- nonpositive delta Credits or delta-percent lower bound returns null;
- intersection uses maximum lower and minimum upper;
- walking newest to oldest stops at the first empty intersection;
- one compatible interval has quality `Initial`;
- two or more have `MultiPoint`;
- old segments and different accounts are never mixed;
- rounded output uses two decimals, away from zero.

- [ ] **Step 2: Run focused tests and verify failure**

```powershell
dotnet test tests/CodexAccountSwitcher.Tests/CodexAccountSwitcher.Tests.csproj -c Release --filter "FullyQualifiedName~QuotaEstimateMathTests"
```

Expected: FAIL because the math service does not exist.

- [ ] **Step 3: Implement the pure estimator**

Expose:

```csharp
public sealed record QuotaEstimateIntersection(
    PeriodQuotaEstimate Estimate,
    QuotaEstimateQuality Quality,
    int ObservationCount,
    bool IgnoredConflictingHistory);

public static PeriodQuotaEstimate? TryCreateFullInterval(
    decimal lowerCredits,
    decimal upperCredits,
    double usedPercent,
    double percentResolution);

public static PeriodQuotaEstimate? TryCreateDeltaInterval(
    decimal deltaCredits,
    double earlierPercent,
    double earlierResolution,
    double laterPercent,
    double laterResolution);

public static QuotaEstimateIntersection? IntersectRecentCompatible(
    IReadOnlyList<QuotaUsageObservation> observations,
    QuotaSegment segment);
```

Keep all calculations in `decimal` after validating finite doubles. Do not
average incompatible observations.

- [ ] **Step 4: Write failing Analytics payload-state tests**

Add:

```csharp
Assert.Equal(
    AnalyticsUsageState.Empty,
    PeriodQuotaEstimator.Parse("""{"data":[]}""").State);
Assert.Equal(
    AnalyticsUsageState.Invalid,
    PeriodQuotaEstimator.Parse("""{"data":{}}""").State);
Assert.Equal(
    AnalyticsUsageState.Valid,
    PeriodQuotaEstimator.Parse(validRows).State);
```

For valid rows, assert `LowerCredits` excludes the non-midnight segment-start
date and `UpperCredits` includes it; a UTC-midnight start includes it in both.

- [ ] **Step 5: Refactor Analytics parsing without changing valid results**

Add:

```csharp
public enum AnalyticsUsageState { Valid, Empty, Invalid }

public sealed record AnalyticsUsageParseResult(
    AnalyticsUsageState State,
    decimal LowerCredits,
    decimal UpperCredits);

public static AnalyticsUsageParseResult Parse(
    string json,
    DateOnly segmentStartDate,
    bool includeStartDayInLower);
```

Keep `TryEstimate` as a compatibility wrapper that calls `Parse` and
`QuotaEstimateMath.TryCreateFullInterval`.

- [ ] **Step 6: Run focused estimator suites**

```powershell
dotnet test tests/CodexAccountSwitcher.Tests/CodexAccountSwitcher.Tests.csproj -c Release --filter "FullyQualifiedName~QuotaEstimateMathTests|FullyQualifiedName~PeriodQuotaEstimatorTests"
```

Expected: PASS and existing valid Analytics ranges remain unchanged except for
the intentional percentage-rounding interval.

- [ ] **Step 7: Commit**

```powershell
git add src/CodexAccountSwitcher/Services/QuotaEstimateMath.cs src/CodexAccountSwitcher/Services/PeriodQuotaEstimator.cs tests/CodexAccountSwitcher.Tests/QuotaEstimateMathTests.cs tests/CodexAccountSwitcher.Tests/PeriodQuotaEstimatorTests.cs
git commit -m "feat: calculate honest quota estimate intervals"
```

---

### Task 5: Hybrid Estimator and Quota Service Integration

**Files:**

- Create: `src/CodexAccountSwitcher/Services/HybridQuotaEstimateService.cs`
- Create: `tests/CodexAccountSwitcher.Tests/HybridQuotaEstimateServiceTests.cs`
- Modify: `src/CodexAccountSwitcher/Services/QuotaService.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/QuotaServiceTests.cs`

**Interfaces:**

- Consumes: collector, rate card, ledger state, account activation intervals,
  server display, segment, and Analytics payload state
- Produces: one `HybridQuotaRefreshContext` per refresh batch
- Produces: updated `QuotaDisplay` plus persisted observation ledger

- [ ] **Step 1: Write failing hybrid-service tests**

Use in-memory delegates for collector and ledger. Prove:

- `BeginRefreshAsync` scans local files exactly once;
- local events are attributed only inside one activation interval;
- events before `segmentStart`, after `ServerNow`, in gaps, or in overlapping
  intervals are ignored;
- the server `ServerNow` timestamp, not the local wall clock, is the observation
  cutoff used for event aggregation;
- full activation coverage produces an initial local estimate immediately;
- mid-segment activation stores a baseline with no estimate;
- a later same-segment observation with positive percent/Credits deltas produces
  a delta estimate;
- a natural or redeemed reset creates a new segment and ignores old observations;
- an unknown model increments unpriced count and produces the required status;
- mixed priced/unpriced events produce an estimate plus the “可能偏低” status;
- two compatible observations produce `MultiPoint`;
- conflicting old history is ignored and flagged;
- completion saves the ledger once.

Required batch API:

```csharp
public sealed record HybridQuotaRefreshContext(
    LocalUsageCollectionResult LocalUsage,
    QuotaEstimateLedgerState Ledger);

public Task<HybridQuotaRefreshContext> BeginRefreshAsync(
    CancellationToken cancellationToken);

public QuotaDisplay ApplyObservation(
    HybridQuotaRefreshContext context,
    AccountRecord account,
    QuotaDisplay display,
    QuotaSegment segment,
    AnalyticsUsageParseResult? analytics,
    AnalyticsAvailability analyticsAvailability);

public Task CompleteRefreshAsync(
    HybridQuotaRefreshContext context,
    CancellationToken cancellationToken);

public Task<string?> ObserveRegistryAsync(
    AccountRegistry registry,
    CancellationToken cancellationToken);
```

Inject an optional `Func<DateTimeOffset>` UTC clock into the service constructor
for deterministic activation and observation tests; production uses
`DateTimeOffset.UtcNow`.

- [ ] **Step 2: Run focused hybrid tests and verify failure**

```powershell
dotnet test tests/CodexAccountSwitcher.Tests/CodexAccountSwitcher.Tests.csproj -c Release --filter "FullyQualifiedName~HybridQuotaEstimateServiceTests"
```

Expected: FAIL because the hybrid service does not exist.

- [ ] **Step 3: Implement Analytics-first/local-fallback behavior**

Rules:

```text
Analytics Valid  -> build/record Analytics interval; source Analytics
Analytics Empty  -> aggregate attributable local Credits; source Local
Analytics Failed -> try local Credits; status notes Analytics failure
Analytics Invalid -> try local Credits; status notes invalid Analytics data
```

For local cumulative Credits, select only events whose timestamps fall in both
the current segment and an unambiguous activation interval for the account.
Set `HasFullSegmentCoverage` only when continuous activation starts at or before
`segmentStart`. Use the latest earlier raw observation in the same segment for
delta estimation. Record observations even when they only establish a baseline.

Map results onto `QuotaDisplay`:

```csharp
return display with
{
    EstimatedPeriodQuotaLowerUsd = intersection?.Estimate.LowerUsd,
    EstimatedPeriodQuotaUpperUsd = intersection?.Estimate.UpperUsd,
    EstimateSource = source,
    EstimateQuality = intersection?.Quality ?? QuotaEstimateQuality.None,
    EstimateObservationCount = intersection?.ObservationCount ?? 0,
    EstimateStatus = status,
};
```

- [ ] **Step 4: Write failing QuotaService integration tests**

Extend `QuotaServiceTests` to prove:

- Analytics `{"data":[]}` with a full-window local event produces a Weekly estimate;
- the same path works for Monthly after reset-history segment selection;
- Analytics nonempty remains preferred and does not use the local result;
- Analytics HTTP failure still attempts local fallback;
- zero server usage skips Analytics/local estimation;
- `RefreshAllAsync` with five accounts scans sessions once and saves ledger once;
- an estimate failure never removes the successful server percentage/reset data;
- user cancellation is propagated.

- [ ] **Step 5: Integrate the hybrid service into QuotaService**

Add an optional constructor parameter to preserve existing tests:

```csharp
public QuotaService(
    HttpClient httpClient,
    AuthSnapshotReader? authSnapshotReader = null,
    HybridQuotaEstimateService? hybridEstimator = null)
```

`BeginRefreshAsync` scans at most the previous 32 days, which safely covers the
current 30-day Monthly window before exact server segment boundaries are known.
The later per-account filter enforces the exact `segmentStart` and `ServerNow`.

Refactor `RefreshAccountAsync` to create/complete a one-account context only when
the hybrid estimator exists. Refactor `RefreshAllAsync` to:

1. call `BeginRefreshAsync` once;
2. refresh accounts sequentially with the shared context;
3. call `CompleteRefreshAsync` once in `finally` after any recorded observations;
4. keep auth snapshots disposed exactly as before.

Do not change endpoint authentication, timeout, or redaction behavior.

- [ ] **Step 6: Run quota integration suites**

```powershell
dotnet test tests/CodexAccountSwitcher.Tests/CodexAccountSwitcher.Tests.csproj -c Release --filter "FullyQualifiedName~HybridQuotaEstimateServiceTests|FullyQualifiedName~QuotaServiceTests"
```

Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add src/CodexAccountSwitcher/Services/HybridQuotaEstimateService.cs src/CodexAccountSwitcher/Services/QuotaService.cs tests/CodexAccountSwitcher.Tests/HybridQuotaEstimateServiceTests.cs tests/CodexAccountSwitcher.Tests/QuotaServiceTests.cs
git commit -m "feat: fall back to local usage for quota estimates"
```

---

### Task 6: Lifecycle Wiring, Cache Validation, and Chinese UI Status

**Files:**

- Modify: `src/CodexAccountSwitcher/App.xaml.cs`
- Modify: `src/CodexAccountSwitcher/ViewModels/MainWindowViewModel.cs`
- Modify: `src/CodexAccountSwitcher/ViewModels/AccountRowViewModel.cs`
- Modify: `src/CodexAccountSwitcher/Services/QuotaCacheService.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/MainWindowViewModelTests.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/QuotaCacheServiceTests.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/WpfInterfaceContractTests.cs`

**Interfaces:**

- Consumes: `QuotaEstimateLedgerService.ObserveRegistry`
- Displays: source, quality, range, and actionable Chinese status
- Preserves: last estimate through existing quota cache

- [ ] **Step 1: Write failing lifecycle and cache tests**

Prove:

- startup load observes the current registry once;
- successful login reload observes the new registry;
- successful switch observes the new active account and activation timestamp;
- failed/cancelled login or switch does not invent an activation interval;
- repeated reload of the same registry is idempotent;
- quota cache round-trips all four new display fields;
- an old schema-1 cache entry with estimate bounds but no source metadata is
  migrated in memory to `Analytics`, `Initial`, and observation count `1`;
- after migration, invalid enum, negative observation count, or bounds without a
  valid source are rejected without overwriting the cache.

Inject into the internal view-model constructor:

```csharp
Func<AccountRegistry, CancellationToken, Task<string?>>? observeRegistryAsync = null
```

Default to a completed task so existing unrelated fixtures stay unchanged.

- [ ] **Step 2: Run focused lifecycle tests and verify failure**

```powershell
dotnet test tests/CodexAccountSwitcher.Tests/CodexAccountSwitcher.Tests.csproj -c Release --filter "FullyQualifiedName~MainWindowViewModelTests|FullyQualifiedName~QuotaCacheServiceTests"
```

Expected: FAIL on missing observation delegate and new validation.

- [ ] **Step 3: Wire ledger lifecycle and application composition**

In `App.OnStartup` create:

```csharp
var ledgerService = QuotaEstimateLedgerService.CreateDefault();
var collector = new LocalCodexUsageCollector(Path.Combine(codexHome, "sessions"));
var hybridEstimator = new HybridQuotaEstimateService(
    collector,
    ledgerService,
    new CodexCreditRateCard());
var quotaService = new QuotaService(_httpClient, hybridEstimator: hybridEstimator);
```

`HybridQuotaEstimateService.ObserveRegistryAsync` lazily loads the ledger before
the first registry observation and returns a sanitized error string instead of
throwing for a ledger read/write failure. In
`MainWindowViewModel.LoadRegistryAsync`, successful login reload, and successful
switch reload, await the observation delegate before applying the registry.
Ledger errors update `StatusText` but do not disable account operations or quota
percentage display.

- [ ] **Step 4: Write failing Chinese display tests**

Cover exact visible strings:

```text
初步估算单次周额度：US$X–Y（本机用量）
多点估算单次月额度：US$X–Y（服务器 Analytics）
Analytics 无数据，已改用本机用量估算
已建立估算基线，继续使用后再次刷新
当前片段没有可计价的本机用量
当前模型暂无官方费率
部分用量无法计价，区间可能偏低
账号历史归属不明确，将从本次刷新开始记录
```

Also assert:

- equal lower/upper values render a single dollar value;
- zero usage renders “产生用量后可计算”;
- Analytics-empty/local-fallback no longer renders generic “暂不可用”;
- cached estimates retain source and quality text after restart.

- [ ] **Step 5: Update row formatting and cache validation**

Format by `QuotaDisplay.Period`, `EstimateQuality`, and `EstimateSource`.
Append `EstimateStatus` as a separate detail line/tool tip without replacing
the server reset status. When loading a schema-1 cache entry that has both
estimate bounds but default source metadata, normalize it to
`Analytics`/`Initial`/`1` before validation. Validate new writes with:

```csharp
Enum.IsDefined(display.EstimateSource)
&& Enum.IsDefined(display.EstimateQuality)
&& display.EstimateObservationCount >= 0
&& (display.EstimatedPeriodQuotaLowerUsd is null
    ? display.EstimateQuality == QuotaEstimateQuality.None
    : display.EstimateSource != QuotaEstimateSource.None
      && display.EstimateQuality != QuotaEstimateQuality.None
      && display.EstimateObservationCount > 0)
```

- [ ] **Step 6: Run focused UI/cache tests**

```powershell
dotnet test tests/CodexAccountSwitcher.Tests/CodexAccountSwitcher.Tests.csproj -c Release --filter "FullyQualifiedName~MainWindowViewModelTests|FullyQualifiedName~QuotaCacheServiceTests|FullyQualifiedName~WpfInterfaceContractTests"
```

Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add src/CodexAccountSwitcher/App.xaml.cs src/CodexAccountSwitcher/ViewModels/MainWindowViewModel.cs src/CodexAccountSwitcher/ViewModels/AccountRowViewModel.cs src/CodexAccountSwitcher/Services/QuotaCacheService.cs tests/CodexAccountSwitcher.Tests/MainWindowViewModelTests.cs tests/CodexAccountSwitcher.Tests/QuotaCacheServiceTests.cs tests/CodexAccountSwitcher.Tests/WpfInterfaceContractTests.cs
git commit -m "feat: show persistent hybrid quota estimates"
```

---

### Task 7: Full Verification and Release Artifact

**Files:**

- Modify only if a verification failure directly requires a feature-scope fix.

**Interfaces:**

- Produces: test evidence, publish artifact evidence, and unchanged live auth evidence.

- [ ] **Step 1: Capture live authentication hashes before verification**

Run:

```powershell
Get-FileHash "$env:USERPROFILE\.codex\auth.json" -Algorithm SHA256
if (Test-Path "$env:USERPROFILE\.codex\accounts") {
    Get-ChildItem "$env:USERPROFILE\.codex\accounts" -File |
        Sort-Object FullName |
        Get-FileHash -Algorithm SHA256
}
```

Expected: hashes are recorded; no files are modified.

- [ ] **Step 2: Run the complete Release suite**

```powershell
dotnet test tests/CodexAccountSwitcher.Tests/CodexAccountSwitcher.Tests.csproj -c Release
```

Expected: all tests pass, zero failed, zero skipped.

- [ ] **Step 3: Run whitespace and worktree checks**

```powershell
git diff --check
git status --short
```

Expected: no whitespace errors and only intentional feature changes, if any.

- [ ] **Step 4: Publish outside the repository**

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\scripts\publish.ps1
```

Expected: the publish script succeeds, the exact existing nine-file contract
passes, helper/manifest/archive hashes match, and no staging/backup residue remains.

- [ ] **Step 5: Recheck authentication hashes**

Repeat Step 1.

Expected: every pre/post hash is identical. No real login, switch, removal,
reset redemption, or quota refresh has been performed.

- [ ] **Step 6: Launch the newly published executable for UI-only smoke testing**

Verify:

- startup restores cached quota and estimate text without network activity;
- main window, tray, single-instance behavior, and existing login dialog still open;
- Weekly/Monthly cards remain compact at 100%, 125%, and 150% DPI;
- no account operation is invoked.

- [ ] **Step 7: Commit any verification-only feature-scope corrections**

If no correction was required, do not create an empty commit. Otherwise, stage
the explicit paths printed by `git status --short`, then run:

```powershell
git add -u -- src/CodexAccountSwitcher tests/CodexAccountSwitcher.Tests
git commit -m "fix: complete hybrid quota estimator verification"
```

Expected: clean worktree after the final commit.
