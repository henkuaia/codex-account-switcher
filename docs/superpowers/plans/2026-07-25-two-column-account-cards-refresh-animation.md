# Two-Column Account Cards and Refresh Animation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the fixed narrow account list with a fixed `780 × 620` two-column card grid and provide animated, incrementally completing bulk and per-account quota refreshes.

**Architecture:** `QuotaService` streams each account result through an awaited callback and returns a batch warning separately. `MainWindowViewModel` owns bulk/single refresh orchestration, per-row loading state, and immediate successful-cache persistence. `MainWindow.xaml` renders the same account data in a two-column `WrapPanel` with WPF storyboard animations driven only by view-model state.

**Tech Stack:** .NET 9, WPF, C#, xUnit

## Global Constraints

- The main window is fixed at exactly `780 × 620`, remains borderless, centered, non-resizable, and tray-aware.
- The account area uses a two-column `WrapPanel`; five accounts render as `2 + 2 + 1`, with the final card left-aligned.
- Vertical scrolling is allowed; horizontal scrolling remains disabled.
- Bulk quota requests remain sequential in registry order.
- Bulk refresh marks all cards refreshing immediately and clears each card as its result arrives.
- Per-account refresh animates and updates only its target.
- Bulk and per-account refresh operations cannot overlap with each other, add, remove, or switch operations.
- Each successful result is persisted to the quota cache before the next successful result replaces it.
- Failures clear the affected loading state and do not stop later bulk items.
- Existing login, removal, switching, authentication snapshots, quota endpoints, reset tracking, quota estimation algorithms, and account data formats do not change.
- No external animation dependency or timer is added.
- Every production behavior change follows strict RED then GREEN.

---

### Task 1: Stream sequential quota results through an awaited callback

**Files:**
- Modify: `src/CodexAccountSwitcher/Services/QuotaService.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/QuotaServiceTests.cs`

**Interfaces:**
- Consumes: `AccountRecord`, `QuotaUpdate`, `HybridQuotaRefreshContext`, and the existing sequential `RefreshAccountCoreAsync`.
- Produces:

```csharp
public async Task<string?> RefreshAllAsync(
    IReadOnlyList<AccountRecord> accounts,
    string codexHome,
    Func<QuotaUpdate, CancellationToken, Task> reportAsync,
    CancellationToken cancellationToken)
```

- Returns the final estimator/cache warning instead of attaching that warning by delaying every account result.
- Guarantees that `reportAsync` is awaited before the next account begins.

- [ ] **Step 1: Write failing streaming tests**

In `QuotaServiceTests.cs`, replace the batch-only progress assertions with an
awaited callback test:

```csharp
[Fact]
public async Task Refresh_all_awaits_each_report_before_starting_the_next_account()
{
    using var home = new TemporaryDirectory();
    var first = Accounts.Record(
        "first-key",
        "first@example.com",
        accountId: "first-account");
    var second = Accounts.Record(
        "second-key",
        "second@example.com",
        accountId: "second-account");
    WriteSnapshot(home, first, "first-token", first.ChatGptAccountId);
    WriteSnapshot(home, second, "second-token", second.ChatGptAccountId);

    var secondRequestStarted = false;
    var firstReportStarted = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseFirstReport = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    using var handler = new RecordingHttpMessageHandler(async (request, _) =>
    {
        var accountId = request.Headers.GetValues("ChatGPT-Account-Id").Single();
        if (accountId == second.ChatGptAccountId)
        {
            secondRequestStarted = true;
        }

        await Task.Yield();
        return JsonResponse();
    });
    using var client = new HttpClient(handler);
    var reports = new List<string>();
    var refreshTask = new QuotaService(client).RefreshAllAsync(
        [first, second],
        home.Path,
        async (update, cancellationToken) =>
        {
            reports.Add(update.AccountKey);
            if (update.AccountKey == first.AccountKey)
            {
                firstReportStarted.SetResult();
                await releaseFirstReport.Task.WaitAsync(cancellationToken);
            }
        },
        CancellationToken.None);

    await firstReportStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.False(secondRequestStarted);

    releaseFirstReport.SetResult();
    var warning = await refreshTask;

    Assert.Null(warning);
    Assert.True(secondRequestStarted);
    Assert.Equal([first.AccountKey, second.AccountKey], reports);
}
```

Update the existing completion-warning test to assert that the returned value
contains the warning while each account is reported exactly once:

```csharp
var reports = new List<QuotaUpdate>();
var warning = await service.RefreshAllAsync(
    accounts,
    home.Path,
    (update, _) =>
    {
        reports.Add(update);
        return Task.CompletedTask;
    },
    CancellationToken.None);

Assert.Equal(accounts.Count, reports.Count);
Assert.All(reports, update => Assert.Null(update.Warning));
Assert.Equal("expected completion warning", warning);
```

- [ ] **Step 2: Run the focused service tests and verify RED**

Run:

```powershell
.\.tools\dotnet\dotnet.exe test `
  tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj `
  -c Release --no-restore `
  --filter "FullyQualifiedName~QuotaServiceTests" `
  --logger "console;verbosity=minimal"
```

Expected: compile failures identify the old `IProgress<QuotaUpdate>` signature
and the old `Task` return type.

- [ ] **Step 3: Implement the awaited streaming contract**

Change `RefreshAllAsync` to:

```csharp
public async Task<string?> RefreshAllAsync(
    IReadOnlyList<AccountRecord> accounts,
    string codexHome,
    Func<QuotaUpdate, CancellationToken, Task> reportAsync,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(accounts);
    ArgumentNullException.ThrowIfNull(reportAsync);

    var hybridContext = await TryBeginHybridRefreshAsync(cancellationToken);
    try
    {
        foreach (var account in accounts)
        {
            var update = await RefreshAccountCoreAsync(
                account,
                codexHome,
                hybridContext,
                cancellationToken);
            await reportAsync(update, cancellationToken);
        }
    }
    catch
    {
        await TryCompleteHybridRefreshAsync(
            hybridContext,
            CancellationToken.None);
        throw;
    }

    return await TryCompleteHybridRefreshAsync(
        hybridContext,
        CancellationToken.None);
}
```

Delete the obsolete `completedUpdates` batch and its post-completion reporting
loop. Keep `RefreshAccountAsync` unchanged.

- [ ] **Step 4: Run the focused service tests and verify GREEN**

Run the Step 2 command.

Expected: all `QuotaServiceTests` pass with zero skips.

- [ ] **Step 5: Commit Task 1**

```powershell
git add `
  src/CodexAccountSwitcher/Services/QuotaService.cs `
  tests/CodexAccountSwitcher.Tests/QuotaServiceTests.cs
git commit -m "refactor: stream quota refresh results"
```

---

### Task 2: Add independent row loading state and bulk/single refresh orchestration

**Files:**
- Modify: `src/CodexAccountSwitcher/ViewModels/AccountRowViewModel.cs`
- Modify: `src/CodexAccountSwitcher/ViewModels/MainWindowViewModel.cs`
- Modify: `tests/CodexAccountSwitcher.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes Task 1's awaited refresh delegate:

```csharp
Func<
    IReadOnlyList<AccountRecord>,
    Func<QuotaUpdate, CancellationToken, Task>,
    CancellationToken,
    Task<string?>>
```

- Produces:

```csharp
public bool IsBulkRefreshing { get; }
public AsyncCommand RefreshAccountCommand { get; }
public bool AccountRowViewModel.IsRefreshing { get; }
internal void AccountRowViewModel.SetRefreshing(bool value)
```

- [ ] **Step 1: Write failing row-state and command tests**

Add a row-state unit test:

```csharp
[Fact]
public void Account_row_refreshing_state_is_independent()
{
    var first = new AccountRowViewModel(
        Accounts.Record("first-key", "first@example.com"),
        isActive: true,
        canSwitch: false,
        switchUnavailableReason: null);
    var second = new AccountRowViewModel(
        Accounts.Record("second-key", "second@example.com"),
        isActive: false,
        canSwitch: true,
        switchUnavailableReason: null);

    first.SetRefreshing(true);

    Assert.True(first.IsRefreshing);
    Assert.False(second.IsRefreshing);

    first.SetRefreshing(false);
    Assert.False(first.IsRefreshing);
}
```

Add a bulk incremental-completion test using the existing
`MainWindowViewModelTests.Fixture`:

```csharp
[Fact]
public async Task Bulk_refresh_clears_and_saves_each_row_as_its_update_arrives()
{
    var fixture = new Fixture();
    await fixture.ViewModel.LoadAsync();
    var first = fixture.ViewModel.Accounts[0];
    var second = fixture.ViewModel.Accounts[1];
    var releaseSecond = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var firstSaved = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);

    fixture.QuotaRefreshOperation = async (accounts, reportAsync, cancellationToken) =>
    {
        await reportAsync(
            new QuotaUpdate(
                accounts[0].AccountKey,
                CreateQuotaCacheEntry(
                    75,
                    "2026-07-25T10:00:00Z",
                    "2100-08-01T00:00:00Z").Display with
                {
                    Period = QuotaPeriod.Weekly,
                    WindowDuration = TimeSpan.FromDays(7),
                },
                null),
            cancellationToken);
        await releaseSecond.Task.WaitAsync(cancellationToken);
        await reportAsync(
            new QuotaUpdate(
                accounts[1].AccountKey,
                CreateQuotaCacheEntry(
                    40,
                    "2026-07-25T10:01:00Z",
                    "2100-08-01T00:00:00Z").Display with
                {
                    Period = QuotaPeriod.Weekly,
                    WindowDuration = TimeSpan.FromDays(7),
                },
                null),
            cancellationToken);
        return null;
    };
    fixture.QuotaCacheSaveOperation = (cache, _) =>
    {
        if (cache.ContainsKey(first.Account.AccountKey))
        {
            firstSaved.TrySetResult();
        }

        return Task.CompletedTask;
    };

    var refresh = fixture.ViewModel.RefreshCommand.ExecuteAsync();
    await firstSaved.Task.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.False(first.IsRefreshing);
    Assert.True(second.IsRefreshing);
    Assert.Equal(75, first.QuotaDisplay!.RemainingPercent);

    releaseSecond.SetResult();
    await refresh;

    Assert.False(fixture.ViewModel.IsBulkRefreshing);
    Assert.False(second.IsRefreshing);
    Assert.Equal(40, second.QuotaDisplay!.RemainingPercent);
}
```

Add a single-account test:

```csharp
[Fact]
public async Task Account_refresh_requests_and_updates_only_its_target()
{
    var fixture = new Fixture();
    await fixture.ViewModel.LoadAsync();
    var first = fixture.ViewModel.Accounts[0];
    var second = fixture.ViewModel.Accounts[1];
    IReadOnlyList<AccountRecord>? requested = null;

    fixture.QuotaRefreshOperation = async (accounts, reportAsync, cancellationToken) =>
    {
        requested = accounts;
        await reportAsync(
            new QuotaUpdate(
                accounts[0].AccountKey,
                CreateQuotaCacheEntry(
                    88,
                    "2026-07-25T10:00:00Z",
                    "2100-08-22T00:00:00Z").Display,
                null),
            cancellationToken);
        return null;
    };

    await fixture.ViewModel.RefreshAccountCommand.ExecuteAsync(second);

    Assert.Single(requested!);
    Assert.Equal(second.Account.AccountKey, requested![0].AccountKey);
    Assert.Null(first.QuotaDisplay);
    Assert.Equal(88, second.QuotaDisplay!.RemainingPercent);
    Assert.False(second.IsRefreshing);
}
```

Add cancellation and failure cases that assert every affected
`IsRefreshing` value returns to `false` in `finally`. The failure case must
also assert:

```csharp
Assert.Equal("额度刷新失败，请稍后重试。", failedRow.QuotaError);
```

Update `Fixture` so its refresh test double and cache persistence use the new
contracts:

```csharp
public Func<
    IReadOnlyList<AccountRecord>,
    Func<QuotaUpdate, CancellationToken, Task>,
    CancellationToken,
    Task<string?>>? QuotaRefreshOperation { get; set; }

public Func<
    IReadOnlyDictionary<string, QuotaCacheEntry>,
    CancellationToken,
    Task>? QuotaCacheSaveOperation { get; set; }

public Dictionary<string, QuotaCacheEntry> QuotaCache { get; } =
    new(StringComparer.Ordinal);

private async Task<string?> RefreshQuotaAsync(
    IReadOnlyList<AccountRecord> accounts,
    Func<QuotaUpdate, CancellationToken, Task> reportAsync,
    CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    QuotaRefreshCallCount++;
    if (QuotaRefreshOperation is not null)
    {
        return await QuotaRefreshOperation(accounts, reportAsync, cancellationToken);
    }

    foreach (var update in QuotaUpdates)
    {
        await reportAsync(update, cancellationToken);
    }

    return null;
}

private Task SaveQuotaCacheAsync(
    IReadOnlyDictionary<string, QuotaCacheEntry> cache,
    CancellationToken cancellationToken)
{
    if (QuotaCacheSaveOperation is not null)
    {
        return QuotaCacheSaveOperation(cache, cancellationToken);
    }

    QuotaCache.Clear();
    foreach (var pair in cache)
    {
        QuotaCache[pair.Key] = pair.Value;
    }

    return Task.CompletedTask;
}
```

Construct the fixture view model through the existing longest internal
constructor, passing `RefreshQuotaAsync`, a load callback that returns a copy
of `QuotaCache`, and `SaveQuotaCacheAsync`. Keep all existing login, remove,
switch, dispatcher, dialog, and activity-tracker delegates unchanged.

- [ ] **Step 2: Run focused view-model tests and verify RED**

Run:

```powershell
.\.tools\dotnet\dotnet.exe test `
  tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj `
  -c Release --no-restore `
  --filter "FullyQualifiedName~MainWindowViewModelTests" `
  --logger "console;verbosity=minimal"
```

Expected: compile failures identify missing state, command, and the old refresh
delegate signature.

- [ ] **Step 3: Add row refresh state**

In `AccountRowViewModel.cs`, add:

```csharp
private bool _isRefreshing;

public bool IsRefreshing
{
    get => _isRefreshing;
    private set => SetProperty(ref _isRefreshing, value);
}

internal void SetRefreshing(bool value) => IsRefreshing = value;
```

Do not modify quota formatting or metadata formatting.

- [ ] **Step 4: Replace the refresh delegate and expose commands**

In `MainWindowViewModel.cs`, change `_refreshQuotaAsync` and matching
constructors to Task 1's async callback signature. Add:

```csharp
private bool _isBulkRefreshing;

public bool IsBulkRefreshing
{
    get => _isBulkRefreshing;
    private set => SetProperty(ref _isBulkRefreshing, value);
}

public AsyncCommand RefreshAccountCommand { get; }
```

Construct the command:

```csharp
RefreshAccountCommand = new AsyncCommand(
    _dispatcher,
    (parameter, cancellationToken) => RunBusyAsync(
        token => RefreshAccountQuotaAsync(parameter, token),
        cancellationToken),
    parameter => !IsBusy &&
        IsHelperAvailable &&
        parameter is AccountRowViewModel,
    HandleCommandErrorAsync);
```

Add `RefreshAccountCommand.NotifyCanExecuteChanged()` to
`RaiseCommandCanExecuteChanged`.

- [ ] **Step 5: Implement incremental apply and cache persistence**

Replace the old list-buffering `RefreshQuotaAsync` with:

```csharp
private async Task RefreshQuotaAsync(CancellationToken cancellationToken)
{
    if (!await RecheckHelperAvailabilityAsync(cancellationToken))
    {
        return;
    }

    await _dispatcher.InvokeAsync(
        () =>
        {
            IsBulkRefreshing = true;
            foreach (var row in Accounts)
            {
                row.SetRefreshing(true);
            }
        },
        cancellationToken);

    try
    {
        var warning = await _refreshQuotaAsync(
            _registry.Accounts.ToArray(),
            ApplyAndPersistQuotaUpdateAsync,
            cancellationToken);
        await _dispatcher.InvokeAsync(
            () => StatusText = warning ?? "额度刷新完成。",
            CancellationToken.None);
    }
    finally
    {
        await _dispatcher.InvokeAsync(
            () =>
            {
                IsBulkRefreshing = false;
                foreach (var row in Accounts)
                {
                    row.SetRefreshing(false);
                }
            },
            CancellationToken.None);
    }
}
```

Add target refresh:

```csharp
private async Task RefreshAccountQuotaAsync(
    object? parameter,
    CancellationToken cancellationToken)
{
    if (parameter is not AccountRowViewModel target ||
        !await RecheckHelperAvailabilityAsync(cancellationToken))
    {
        return;
    }

    await _dispatcher.InvokeAsync(
        () => target.SetRefreshing(true),
        cancellationToken);
    try
    {
        var warning = await _refreshQuotaAsync(
            [target.Account],
            ApplyAndPersistQuotaUpdateAsync,
            cancellationToken);
        await _dispatcher.InvokeAsync(
            () => StatusText = warning ?? "该账号额度刷新完成。",
            CancellationToken.None);
    }
    finally
    {
        await _dispatcher.InvokeAsync(
            () => target.SetRefreshing(false),
            CancellationToken.None);
    }
}
```

Implement the awaited callback:

```csharp
private async Task ApplyAndPersistQuotaUpdateAsync(
    QuotaUpdate update,
    CancellationToken cancellationToken)
{
    await _dispatcher.InvokeAsync(
        () =>
        {
            var row = Accounts.FirstOrDefault(candidate => string.Equals(
                candidate.Account.AccountKey,
                update.AccountKey,
                StringComparison.Ordinal));
            var displayUpdate = update.Error is null
                ? update
                : update with { Error = "额度刷新失败，请稍后重试。" };
            row?.ApplyQuota(displayUpdate);
            row?.SetRefreshing(false);
        },
        cancellationToken);

    if (update.Error is not null || update.Display is null)
    {
        return;
    }

    var merged = new Dictionary<string, QuotaCacheEntry>(
        _quotaCache,
        StringComparer.Ordinal)
    {
        [update.AccountKey] = new(
            update.Display,
            DateTimeOffset.UtcNow),
    };

    try
    {
        await _saveQuotaCacheAsync(merged, cancellationToken);
        _quotaCache = merged;
    }
    catch (Exception exception) when (
        exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException)
    {
        await _dispatcher.InvokeAsync(
            () => StatusText = "额度已刷新，但本地缓存保存失败。",
            CancellationToken.None);
    }
}
```

Update `CreateRefreshQuotaDelegate` to return the new signature and delegate
directly to `QuotaService.RefreshAllAsync`.

- [ ] **Step 6: Run focused view-model tests and verify GREEN**

Run the Step 2 command.

Expected: every selected test passes with zero skips.

- [ ] **Step 7: Commit Task 2**

```powershell
git add `
  src/CodexAccountSwitcher/ViewModels/AccountRowViewModel.cs `
  src/CodexAccountSwitcher/ViewModels/MainWindowViewModel.cs `
  tests/CodexAccountSwitcher.Tests/MainWindowViewModelTests.cs
git commit -m "feat: add incremental quota refresh states"
```

---

### Task 3: Render the fixed two-column grid and WPF loading animations

**Files:**
- Modify: `src/CodexAccountSwitcher/MainWindow.xaml`
- Modify: `tests/CodexAccountSwitcher.Tests/WpfInterfaceContractTests.cs`

**Interfaces:**
- Consumes: `MainWindowViewModel.IsBulkRefreshing`,
  `MainWindowViewModel.RefreshAccountCommand`, and
  `AccountRowViewModel.IsRefreshing`.
- Produces: a fixed `780 × 620` main window, two-column card layout, rotating
  glyphs, visible Chinese loading text, and a progress-track sweep.

- [ ] **Step 1: Write failing XAML contract assertions**

Extend `WpfInterfaceContractTests.cs`:

```csharp
[Fact]
public void Main_window_uses_fixed_two_column_account_cards()
{
    var xaml = File.ReadAllText(Path.Combine(
        FindDirectory("src", "CodexAccountSwitcher"),
        "MainWindow.xaml"));

    Assert.Contains("Width=\"780\"", xaml, StringComparison.Ordinal);
    Assert.Contains("MinWidth=\"780\"", xaml, StringComparison.Ordinal);
    Assert.Contains("MaxWidth=\"780\"", xaml, StringComparison.Ordinal);
    Assert.Contains("Height=\"620\"", xaml, StringComparison.Ordinal);
    Assert.Contains("MinHeight=\"620\"", xaml, StringComparison.Ordinal);
    Assert.Contains("MaxHeight=\"620\"", xaml, StringComparison.Ordinal);
    Assert.Contains("<WrapPanel", xaml, StringComparison.Ordinal);
    Assert.Contains("ItemWidth=\"355\"", xaml, StringComparison.Ordinal);
    Assert.Contains(
        "HorizontalScrollBarVisibility=\"Disabled\"",
        xaml,
        StringComparison.Ordinal);
}

[Fact]
public void Main_window_binds_bulk_and_account_refresh_animations()
{
    var xaml = File.ReadAllText(Path.Combine(
        FindDirectory("src", "CodexAccountSwitcher"),
        "MainWindow.xaml"));

    Assert.Contains("Binding=\"{Binding IsBulkRefreshing}\"", xaml, StringComparison.Ordinal);
    Assert.Contains("Binding=\"{Binding IsRefreshing}\"", xaml, StringComparison.Ordinal);
    Assert.Contains("Text=\"正在刷新额度…\"", xaml, StringComparison.Ordinal);
    Assert.Contains(
        "Command=\"{Binding DataContext.RefreshAccountCommand",
        xaml,
        StringComparison.Ordinal);
    Assert.Contains("RepeatBehavior=\"Forever\"", xaml, StringComparison.Ordinal);
    Assert.Contains(
        "Storyboard.TargetProperty=\"(UIElement.RenderTransform).(RotateTransform.Angle)\"",
        xaml,
        StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run XAML contract tests and verify RED**

Run:

```powershell
.\.tools\dotnet\dotnet.exe test `
  tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj `
  -c Release --no-restore `
  --filter "FullyQualifiedName~WpfInterfaceContractTests" `
  --logger "console;verbosity=minimal"
```

Expected: the new fixed size, `WrapPanel`, bindings, command, and storyboards
are absent.

- [ ] **Step 3: Change the window and items panel**

Set the window contract:

```xml
Width="780"
MinWidth="780"
MaxWidth="780"
Height="620"
MinHeight="620"
MaxHeight="620"
ResizeMode="NoResize"
```

Inside `AccountItems`, add:

```xml
<ItemsControl.ItemsPanel>
    <ItemsPanelTemplate>
        <WrapPanel ItemWidth="355" />
    </ItemsPanelTemplate>
</ItemsControl.ItemsPanel>
<ItemsControl.ItemContainerStyle>
    <Style TargetType="ContentPresenter">
        <Setter Property="Width" Value="355" />
        <Setter Property="Margin" Value="5" />
    </Style>
</ItemsControl.ItemContainerStyle>
```

Remove the old row separator. Keep the existing active-card triggers,
progress coloring, details, switch, and edit bindings.

- [ ] **Step 4: Add top and card refresh storyboards**

Give the top glyph a centered rotate transform and trigger:

```xml
<TextBlock x:Name="RefreshGlyph"
           FontFamily="Segoe Fluent Icons"
           FontSize="15"
           RenderTransformOrigin="0.5,0.5"
           Text="&#xE72C;">
    <TextBlock.RenderTransform>
        <RotateTransform />
    </TextBlock.RenderTransform>
    <TextBlock.Style>
        <Style TargetType="TextBlock">
            <Style.Triggers>
                <DataTrigger Binding="{Binding IsBulkRefreshing}" Value="True">
                    <DataTrigger.EnterActions>
                        <BeginStoryboard>
                            <Storyboard>
                                <DoubleAnimation
                                    Storyboard.TargetProperty="(UIElement.RenderTransform).(RotateTransform.Angle)"
                                    From="0"
                                    To="360"
                                    Duration="0:0:0.8"
                                    RepeatBehavior="Forever" />
                            </Storyboard>
                        </BeginStoryboard>
                    </DataTrigger.EnterActions>
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </TextBlock.Style>
</TextBlock>
```

Add a card refresh button bound to the target row:

```xml
<Button Width="30"
        Height="30"
        Command="{Binding DataContext.RefreshAccountCommand,
            RelativeSource={RelativeSource AncestorType=Window}}"
        CommandParameter="{Binding}"
        Style="{StaticResource ActionButtonStyle}"
        ToolTip="刷新该账号额度"
        AutomationProperties.Name="刷新该账号额度">
    <TextBlock FontFamily="Segoe Fluent Icons"
               FontSize="13"
               RenderTransformOrigin="0.5,0.5"
               Text="&#xE72C;">
        <TextBlock.RenderTransform>
            <RotateTransform />
        </TextBlock.RenderTransform>
        <TextBlock.Style>
            <Style TargetType="TextBlock">
                <Style.Triggers>
                    <DataTrigger Binding="{Binding IsRefreshing}" Value="True">
                        <DataTrigger.EnterActions>
                            <BeginStoryboard>
                                <Storyboard>
                                    <DoubleAnimation
                                        Storyboard.TargetProperty="(UIElement.RenderTransform).(RotateTransform.Angle)"
                                        From="0"
                                        To="360"
                                        Duration="0:0:0.8"
                                        RepeatBehavior="Forever" />
                                </Storyboard>
                            </BeginStoryboard>
                        </DataTrigger.EnterActions>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </TextBlock.Style>
    </TextBlock>
</Button>
```

Add a loading line visible only while `IsRefreshing`:

```xml
<StackPanel Orientation="Horizontal"
            Margin="0,6,0,0">
    <StackPanel.Style>
        <Style TargetType="StackPanel">
            <Setter Property="Visibility" Value="Collapsed" />
            <Style.Triggers>
                <DataTrigger Binding="{Binding IsRefreshing}" Value="True">
                    <Setter Property="Visibility" Value="Visible" />
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </StackPanel.Style>
    <TextBlock Margin="0,0,6,0"
               Foreground="{StaticResource TextSecondaryBrush}"
               Text="正在刷新额度…" />
</StackPanel>
```

Use the rotating card glyph as the visual ring; do not create another timer.

- [ ] **Step 5: Add a progress-track sweep**

Place the existing progress bar inside a clipping `Grid` and overlay:

```xml
<Border x:Name="QuotaSweep"
        Width="72"
        HorizontalAlignment="Left"
        IsHitTestVisible="False"
        CornerRadius="3">
    <Border.Background>
        <LinearGradientBrush StartPoint="0,0" EndPoint="1,0">
            <GradientStop Offset="0" Color="#00FFFFFF" />
            <GradientStop Offset="0.5" Color="#70FFFFFF" />
            <GradientStop Offset="1" Color="#00FFFFFF" />
        </LinearGradientBrush>
    </Border.Background>
    <Border.RenderTransform>
        <TranslateTransform X="-72" />
    </Border.RenderTransform>
    <Border.Style>
        <Style TargetType="Border">
            <Setter Property="Opacity" Value="0" />
            <Style.Triggers>
                <DataTrigger Binding="{Binding IsRefreshing}" Value="True">
                    <Setter Property="Opacity" Value="1" />
                    <DataTrigger.EnterActions>
                        <BeginStoryboard>
                            <Storyboard>
                                <DoubleAnimation
                                    Storyboard.TargetProperty="(UIElement.RenderTransform).(TranslateTransform.X)"
                                    From="-72"
                                    To="355"
                                    Duration="0:0:1.2"
                                    RepeatBehavior="Forever" />
                            </Storyboard>
                        </BeginStoryboard>
                    </DataTrigger.EnterActions>
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </Border.Style>
</Border>
```

Keep the percentage text and last known progress value visible underneath.

- [ ] **Step 6: Run XAML contract and complete Release tests**

Run the Step 2 command, then:

```powershell
.\.tools\dotnet\dotnet.exe test `
  tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj `
  -c Release --no-restore `
  --logger "console;verbosity=minimal"
```

Expected: all tests pass with zero failures and zero skips.

- [ ] **Step 7: Validate and commit Task 3**

```powershell
git diff --check
git status --short
git add `
  src/CodexAccountSwitcher/MainWindow.xaml `
  tests/CodexAccountSwitcher.Tests/WpfInterfaceContractTests.cs
git commit -m "feat: add two-column quota refresh cards"
```

---

### Task 4: Publish, merge, push, and replace the installed build

**Files:**
- No tracked source changes.
- Produce: `dist/CodexAccountSwitcher`
- Replace: `C:\Users\demax\Apps\CodexAccountSwitcher`

**Interfaces:**
- Consumes: committed Tasks 1–3 and repository-local `.tools\dotnet`.
- Produces: the exact nine-file Windows x64 distribution and one responding
  installed process.
- Preserves: `.codex/auth.json`, `.codex/accounts`, and a timestamped backup of
  the previous installation.

- [ ] **Step 1: Run the release publisher**

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File .\scripts\publish.ps1
```

Expected: build has zero warnings/errors, complete Release tests pass, and
`dist\CodexAccountSwitcher` contains exactly nine files.

- [ ] **Step 2: Verify the release contract**

Verify:

- published `tools\codex-auth.exe` SHA-256 equals the vendor helper and manifest
  executable hash;
- the manifest archive hash remains
  `CDF2C4D9CC827C91C24EB4C032B9F6792F581B42808DF5DB167C39B255EA7108`;
- no staging or backup residue remains under `dist`;
- `git diff --check` succeeds.

- [ ] **Step 3: Merge and push**

Fast-forward the feature branch into local `main`, rerun the complete Release
suite on the merged checkout, and push `main` to `origin`.

Expected: local `main`, `origin/main`, and the feature tip identify the same
commit before branch cleanup.

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
  `C:\Users\demax\Apps\CodexAccountSwitcher.backup-yyyyMMdd-HHmmss`;
- move staging to `C:\Users\demax\Apps\CodexAccountSwitcher`;
- start the new executable;
- verify one responding installed process, nine matching files, zero staging
  residue, and unchanged authentication/account hashes.

- [ ] **Step 5: Clean up the feature worktree**

After merged-main tests, push, installation, and integrity checks pass:

```powershell
git worktree remove `
  "C:\Users\demax\Documents\Codex\2026-07-20\new-chat-3\codex-account-switcher\.worktrees\feature-two-column-refresh-cards"
git worktree prune
git branch -d feature/two-column-refresh-cards
```

Expected: only the main worktree remains and the main checkout is clean.
