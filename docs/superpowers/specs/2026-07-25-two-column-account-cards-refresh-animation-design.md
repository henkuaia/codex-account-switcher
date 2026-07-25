# Two-Column Account Cards and Refresh Animation Design

## Goal

Improve the account overview for five or more accounts by replacing the narrow
single-column list with a fixed-size two-column card grid and by exposing clear,
per-account progress while quota refreshes run.

## Scope

This change covers:

- the main window size and account-card layout;
- quota refresh loading states and animations;
- a per-account refresh command;
- incremental UI updates and cache persistence during a bulk refresh;
- Chinese refresh and failure status text.

It does not change account login, removal, switching, authentication snapshots,
quota endpoints, reset tracking, quota estimation algorithms, or account data
formats.

## Window and Layout

- The main window is fixed at `780 × 620`.
- The window remains borderless, centered, non-resizable, and tray-aware.
- The account content area uses a `WrapPanel` with two fixed-width columns.
- Each card is approximately `355px` wide with a `10px` horizontal gap.
- Five accounts render as two cards, two cards, then one left-aligned card.
- The content area scrolls vertically when the cards exceed the available
  height. Horizontal scrolling remains disabled.
- The empty-state message remains centered when there are no accounts.

Each collapsed card presents, in order:

1. account identity, plan, and active-account state;
2. Weekly or Monthly period, reset time, and remaining percentage;
3. the existing value-colored progress bar;
4. per-account refresh, switch, and details actions.

Reset counts, estimated period quota, and the edit action remain inside the
collapsed `详情` section. The active account keeps its existing pale-green
background and border treatment.

## Refresh Interaction

### Refresh All

When the user invokes the top-level refresh:

- the top refresh icon rotates until the operation completes;
- every account card immediately enters a refreshing state;
- quota requests continue sequentially in registry order to avoid a burst of
  five simultaneous requests;
- each completed account immediately applies its quota result, exits its own
  refreshing state, and persists its successful cache entry;
- one failed account exits its refreshing state and shows a Chinese failure
  status, while remaining accounts continue;
- the top-level refreshing state ends only after every account has completed
  or the operation is cancelled.

### Refresh One Account

Each card includes a dedicated refresh action. Invoking it:

- refreshes only that account;
- animates only that card;
- disables the target card refresh action and the top-level refresh action for
  the duration of the request;
- immediately applies and persists a successful result;
- preserves the prior cached quota when the request fails.

## Loading Presentation

Refreshing cards show all of the following without opening another window:

- a compact rotating ring beside `正在刷新额度…`;
- a rotating refresh glyph on the card action;
- a subtle sweep animation across the existing quota progress track while
  retaining the remaining-value color.

The progress animation is visual feedback only. It does not fabricate a
percentage or replace the last known quota value. Animations stop when the
corresponding operation completes, fails, or is cancelled.

The top refresh glyph rotates only during a bulk refresh. Motion is implemented
with WPF storyboards and state triggers and does not introduce a timer or an
external animation dependency.

## State and Command Rules

- Each `AccountRowViewModel` exposes an independent refreshing state.
- The main view model exposes whether a bulk refresh is active.
- Account add, remove, and switch operations remain disabled while quota
  refresh work is active, preserving the existing authentication-operation
  boundary.
- Details remain readable during refresh.
- Bulk and single-account refreshes cannot overlap.
- A repeated click cannot enqueue duplicate refresh work.
- All operation states are restored in `finally` paths, including cancellation
  and failure.

## Data Flow and Persistence

Bulk refresh streams one `QuotaUpdate` at a time instead of withholding all
updates until the batch ends. The view model:

1. marks every row as refreshing;
2. consumes account updates in order;
3. applies each update to its matching row;
4. clears that row's refreshing state;
5. updates the in-memory quota cache and saves completed successes immediately.

If estimator finalization produces a warning after streamed updates, the
affected completed rows may receive a final warning update without re-entering
the loading state.

Single-account refresh reuses the existing account snapshot and quota service.
It updates only the target cache entry. No authentication file is activated,
rewritten, or switched as part of quota refresh.

## Error Handling

- Per-account refresh failures are rendered in Chinese on the affected card.
- A failed bulk item does not abort remaining accounts unless cancellation was
  explicitly requested.
- Cancellation clears all remaining loading states and preserves every result
  already persisted.
- Cache-save failures surface through the existing warning path and do not
  replace a successfully fetched quota result.
- Existing sensitive-text redaction remains unchanged.

## Accessibility

- Refresh actions keep explicit automation names and tooltips.
- Refresh state is exposed through visible Chinese text, not animation alone.
- Existing percentage text remains visible while the progress track animates.
- Keyboard focus and command enablement follow the same rules as pointer input.

## Verification

Automated verification covers:

- fixed `780 × 620` window dimensions and two-column `WrapPanel` contract;
- five-account `2 + 2 + 1` card layout contract;
- bulk refresh entering loading state on every row;
- incremental row completion and immediate successful cache persistence;
- a failed row clearing independently while later rows continue;
- single-account refresh affecting only its target;
- mutual exclusion between bulk refresh, single refresh, add, remove, and
  switch commands;
- loading state cleanup after success, failure, and cancellation;
- WPF animation triggers for card and top-level refresh states;
- existing account, quota-estimation, cache, tray, and publishing tests.

The final release must pass the complete Release test suite, the exact
nine-file publishing contract, and installed-file verification. Before and
after installation, `auth.json` and every account snapshot are hashed and must
remain unchanged.
