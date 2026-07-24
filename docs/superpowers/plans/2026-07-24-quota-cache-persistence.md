# Quota Cache Persistence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore each account's last successfully refreshed quota after application or Windows restart without automatically calling quota endpoints.

**Architecture:** Add a dedicated, versioned `QuotaCacheService` under Local AppData, keyed by the stable account key and written atomically. `MainWindowViewModel` loads cached entries alongside account metadata, applies them only to rows without live data, and merges successful manual refresh results back into the cache. `AccountRowViewModel` marks restored results with their refresh time and an expired warning when the saved reset time has passed.

**Tech Stack:** .NET 9, WPF, `System.Text.Json`, xUnit

## Global Constraints

- Never call quota endpoints automatically during startup.
- Cache only successful, non-null `QuotaDisplay` results.
- Never persist access tokens, authentication snapshots, request headers, or raw endpoint responses.
- Never change `.codex/auth.json` or account snapshots.
- A failed refresh must not replace a previously successful cached entry.
- A cache write failure must not hide the newly refreshed live result.
- Preserve existing account switching, login, removal, Weekly/Monthly estimation, and manual metadata behavior.

---

### Task 1: Add the versioned quota cache service

**Files:**
- Create: `src/CodexAccountSwitcher/Models/QuotaCacheModels.cs`
- Create: `src/CodexAccountSwitcher/Services/QuotaCacheService.cs`
- Create: `tests/CodexAccountSwitcher.Tests/QuotaCacheServiceTests.cs`

**Interfaces:**
- Produces: `QuotaCacheEntry(QuotaDisplay Display, DateTimeOffset RefreshedAt)`.
- Produces: `QuotaCacheLoadResult(IReadOnlyDictionary<string, QuotaCacheEntry> Accounts, string? Error)`.
- Produces: `QuotaCacheService.CreateDefault()`.
- Produces: `Task<QuotaCacheLoadResult> LoadAsync(CancellationToken cancellationToken)`.
- Produces: `Task SaveAsync(IReadOnlyDictionary<string, QuotaCacheEntry> accounts, CancellationToken cancellationToken)`.

- [ ] **Step 1: Write failing round-trip and preservation tests**

Create tests that construct a Monthly `QuotaDisplay` containing reset count,
official limit, server time, and estimate bounds, then assert every field and
`RefreshedAt` round-trips by account key. Also assert:

```csharp
[Fact]
public async Task Invalid_existing_cache_blocks_overwrite_and_preserves_original_file()
{
    using var directory = new TemporaryDirectory();
    var path = Path.Combine(directory.Path, "quota-cache.json");
    await File.WriteAllTextAsync(path, """{"schemaVersion":99,"accounts":{}}""");
    var service = new QuotaCacheService(path);

    var loaded = await service.LoadAsync(default);

    Assert.NotNull(loaded.Error);
    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        service.SaveAsync(new Dictionary<string, QuotaCacheEntry>(), default));
    Assert.Equal("""{"schemaVersion":99,"accounts":{}}""", await File.ReadAllTextAsync(path));
    Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
}
```

Cover a missing file, malformed JSON, empty account keys, invalid percentages,
negative monetary values, inconsistent estimate bounds, and atomic save without
temporary-file residue.

- [ ] **Step 2: Run the cache tests and verify RED**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~QuotaCacheServiceTests
```

Expected: compilation fails because the cache models and service do not exist.

- [ ] **Step 3: Implement the minimal cache models and service**

Use schema version `1` and the default path:

```csharp
Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "CodexAccountSwitcher",
    "quota-cache.json")
```

Follow `AccountMetadataService` for asynchronous reads, same-directory temporary
writes, `FlushAsync`, atomic `File.Move(..., overwrite: true)`, and save blocking
after an invalid existing document. Validate the complete graph before returning
or saving it.

- [ ] **Step 4: Run the cache tests and verify GREEN**

Run the command from Step 2.

Expected: all `QuotaCacheServiceTests` pass.

- [ ] **Step 5: Commit the cache service**

```powershell
git add src/CodexAccountSwitcher/Models/QuotaCacheModels.cs src/CodexAccountSwitcher/Services/QuotaCacheService.cs tests/CodexAccountSwitcher.Tests/QuotaCacheServiceTests.cs
git commit -m "feat: persist successful quota snapshots"
```

---

### Task 2: Render restored and expired cached quota

**Files:**
- Modify: `src/CodexAccountSwitcher/ViewModels/AccountRowViewModel.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: `QuotaCacheEntry`.
- Produces: `AccountRowViewModel.ApplyCachedQuota(QuotaCacheEntry entry, DateTimeOffset now)`.
- A normal `ApplyQuota(QuotaUpdate)` call clears all cached/expired state.

- [ ] **Step 1: Write failing row display tests**

Add tests for a future reset:

```csharp
row.ApplyCachedQuota(
    new QuotaCacheEntry(display with
    {
        ResetsAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
    }, DateTimeOffset.Parse("2026-07-24T12:00:00Z")),
    DateTimeOffset.Parse("2026-07-25T00:00:00Z"));

Assert.Equal(display.RemainingPercent, row.QuotaDisplay!.RemainingPercent);
Assert.Contains("上次刷新 2026-07-24 12:00 UTC", row.QuotaStatusText);
Assert.DoesNotContain("缓存已过期", row.QuotaStatusText);
```

Add a reset at or before `now` and assert exact prefix
`缓存已过期，需要刷新`. Then apply a live `QuotaUpdate` and assert the cached
and expired text disappears.

- [ ] **Step 2: Run the row tests and verify RED**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~MainWindowViewModelTests
```

Expected: compilation fails because `ApplyCachedQuota` does not exist.

- [ ] **Step 3: Implement cached quota display state**

Make `ApplyCachedQuota` call the existing quota application path, then format:

```text
缓存已过期，需要刷新 · 上次刷新 yyyy-MM-dd HH:mm UTC
```

when `ResetsAt <= now`; otherwise format:

```text
<existing reset status> · 上次刷新 yyyy-MM-dd HH:mm UTC
```

Do not change XAML layout. Ensure the next live `ApplyQuota` restores the
existing live status and tooltip.

- [ ] **Step 4: Run the row tests and verify GREEN**

Run the command from Step 2.

Expected: all `MainWindowViewModelTests` pass.

- [ ] **Step 5: Commit the row behavior**

```powershell
git add src/CodexAccountSwitcher/ViewModels/AccountRowViewModel.cs tests/CodexAccountSwitcher.Tests/MainWindowViewModelTests.cs
git commit -m "feat: mark restored quota snapshots"
```

---

### Task 3: Restore and update cache in the main view model

**Files:**
- Modify: `src/CodexAccountSwitcher/ViewModels/MainWindowViewModel.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: `QuotaCacheLoadResult` and `QuotaCacheEntry`.
- Adds optional internal delegates:
  - `Func<CancellationToken, Task<QuotaCacheLoadResult>> loadQuotaCacheAsync`.
  - `Func<IReadOnlyDictionary<string, QuotaCacheEntry>, CancellationToken, Task> saveQuotaCacheAsync`.
- Produces the same public commands and existing constructor behavior.

- [ ] **Step 1: Write failing startup and refresh persistence tests**

Add one startup test whose refresh delegate increments a counter. Load a cached
entry, call `LoadAsync`, and assert:

```csharp
Assert.Equal(0, refreshCallCount);
Assert.Equal(64, fixture.Row(account).QuotaDisplay!.RemainingPercent);
Assert.Contains("上次刷新", fixture.Row(account).QuotaStatusText);
```

Add refresh tests proving:

1. a successful update is saved with its account key and a UTC refresh time;
2. a failed/null update leaves the previous cache entry unchanged;
3. a cache save exception leaves the live quota visible and reports
   `额度刷新完成，但本地缓存失败。`;
4. opening/reloading the window does not replace newer live data with an older
   cached entry;
5. a renamed account restores by stable account key.

- [ ] **Step 2: Run focused view-model tests and verify RED**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~MainWindowViewModelTests
```

Expected: constructor/cache assertions fail because the main view model does not
load or save quota cache entries.

- [ ] **Step 3: Implement startup restore and refresh merge**

In `LoadRegistryAsync`, load registry, metadata, and cache without invoking the
refresh delegate. Apply cached entries only to newly created rows or rows whose
`QuotaDisplay` is null.

In `RefreshQuotaAsync`, keep existing UI updates. Build a merged dictionary from
the current cache plus updates satisfying:

```csharp
update.Error is null && update.Display is not null
```

Assign one `DateTimeOffset.UtcNow` value to all successful updates in the same
refresh. Save the merged dictionary. Catch only expected local persistence
failures (`IOException`, `UnauthorizedAccessException`,
`InvalidOperationException`) so cancellation and programming errors are not
hidden. Preserve the live display on failure.

- [ ] **Step 4: Run focused view-model tests and verify GREEN**

Run the command from Step 2.

Expected: all `MainWindowViewModelTests` pass.

- [ ] **Step 5: Commit view-model orchestration**

```powershell
git add src/CodexAccountSwitcher/ViewModels/MainWindowViewModel.cs tests/CodexAccountSwitcher.Tests/MainWindowViewModelTests.cs
git commit -m "feat: restore cached quota on startup"
```

---

### Task 4: Wire production, verify, publish, and install

**Files:**
- Modify: `src/CodexAccountSwitcher/App.xaml.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/WpfRuntimeTests.cs`
- Modify: `docs/superpowers/plans/2026-07-24-quota-cache-persistence.md` checkboxes only
- Build output: `dist/CodexAccountSwitcher`
- Installed output: `C:\Users\demax\Apps\CodexAccountSwitcher`

**Interfaces:**
- Consumes: `QuotaCacheService.CreateDefault()`.
- Preserves the existing nine-file published artifact contract.

- [ ] **Step 1: Write the failing production-wiring test**

Update the WPF runtime construction test to require a `QuotaCacheService`
dependency and assert initial loading does not invoke quota refresh. Run:

```powershell
& .\.tools\dotnet\dotnet.exe test tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WpfRuntimeTests|FullyQualifiedName~PublishContractTests"
```

Expected: the wiring assertion fails until `App` creates and passes the cache
service.

- [ ] **Step 2: Wire the default cache service**

Create one `QuotaCacheService` during application startup and pass it into
`MainWindowViewModel`. Add no background timer or startup refresh.

- [ ] **Step 3: Run the complete Release suite**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test CodexAccountSwitcher.sln -c Release --no-restore
```

Expected: every test passes with zero failures and zero skips.

- [ ] **Step 4: Publish and verify artifacts**

Record SHA-256 for `%USERPROFILE%\.codex\auth.json` and existing account
snapshots, then run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish.ps1
```

Verify exactly nine published files, helper/manifest hash agreement, no
staging/backup residue, and unchanged authentication hashes.

- [ ] **Step 5: Replace the installed build**

Stop only `CodexAccountSwitcher.exe`, rename the existing installation to a
timestamped sibling backup, copy the verified `dist\CodexAccountSwitcher` into
`C:\Users\demax\Apps\CodexAccountSwitcher`, verify file hashes, and restart the
application. Do not refresh quota, switch accounts, log in, remove accounts, or
consume reset credits.

- [ ] **Step 6: Verify restart restoration locally**

Because the existing application has previously refreshed values only in
memory, perform no endpoint call. Verify the first restart creates no cache and
still shows `Not queried`. The user performs one manual refresh later; after that
refresh, a second application restart must restore the saved values without
another endpoint call.

- [ ] **Step 7: Commit, push, and preserve the feature branch**

```powershell
git add src tests docs/superpowers/plans/2026-07-24-quota-cache-persistence.md
git commit -m "feat: persist quota across restarts"
git -c http.proxy=http://127.0.0.1:7897 -c https.proxy=http://127.0.0.1:7897 push origin HEAD:main
```

Verify remote `main` equals local `HEAD`, the worktree is clean, and the local
feature branch remains present.

