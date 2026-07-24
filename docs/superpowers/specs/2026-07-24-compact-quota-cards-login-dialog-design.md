# Compact Quota Cards and Login Dialog Design

## Goal

Reduce visual clutter in the account list and add-account flow while keeping all
existing account, quota, cache, and authentication behavior unchanged.

## Main Account Cards

Each account becomes a compact card inspired by the supplied quota-manager
reference.

The always-visible content is:

1. account identity, plan, and Active/Switch state;
2. quota period, remaining percentage, and reset or cached-status time;
3. one remaining-quota progress bar.

The following secondary information moves into a collapsed `详情` section:

- available reset count;
- locally recorded used reset count;
- manually recorded single-period quota;
- optional official monthly limit;
- estimated single-period quota;
- metadata edit action.

The details section is collapsed by default for every row. Expanding one row
does not change other rows or persist expansion state.

Existing empty, unavailable, cached, and expired-cache status text remains
available. `Not queried` rows show no percentage and use a neutral gray track.

## Remaining-Quota Color

The filled progress indicator uses a value-derived solid color rather than a
fixed gradient painted across the bar:

- `0%`: red `#D9534F`;
- `50%`: amber `#E6A23C`;
- `100%`: green `#16B364`.

Values between those points use linear RGB interpolation in two segments,
red-to-amber and amber-to-green. Values are clamped to `0..100`. Missing or
invalid quota values use the existing neutral gray presentation.

The percentage text uses the same derived color with sufficient contrast. The
progress track remains pale gray. This makes the color describe the current
remaining value instead of merely decorating the bar.

## Add-Account Dialog

The add-account window changes from `560×430` to a fixed `420×230` and uses
four visual states.

### Waiting

- animated circular spinner;
- primary text `请在浏览器完成登录`;
- secondary text `浏览器通常会自动打开`;
- visible header close button and `取消登录` button.

Normal command-line output is not shown. A small `查看登录信息` disclosure is
available for the uncommon case where the browser did not open or device
authorization information is needed.

### Success

- green check icon;
- primary text `账号添加成功`;
- spinner and cancel action disappear;
- the window closes automatically after `1000 ms`;
- the existing login coordinator then reloads the account registry.

The user can still close the window immediately with the header close button.

### Failure

- red error icon;
- concise Chinese failure state;
- `查看详情` disclosure for sanitized diagnostic output;
- visible `关闭` button and header close button.

Authentication tokens and other sensitive values continue to pass through the
existing redaction path before entering the window.

### Cancelling or Cancelled

- spinner remains while cancellation is in progress;
- both close controls are disabled while cancellation is being confirmed;
- after cancellation completes, show `登录已取消` with a normal close action.

## Output Handling

`OperationWindow` continues accepting streamed output so the login process and
tests retain their existing interface. It stores sanitized lines in the
collapsible details area instead of displaying a permanently visible terminal.

The disclosure is hidden when there is no output. Local OAuth URLs and device
codes remain copyable when the user explicitly opens the disclosure. No raw
output is written to disk.

Remove-account operations continue using `OperationWindow`; they retain the
same state machine but use the compact layout and English text supplied by
their existing `OperationWindowText`.

## Implementation Boundaries

- No changes to `codex-auth`, Codex CLI discovery, login orchestration, account
  registry behavior, quota endpoints, quota parsing, quota caching, or account
  switching.
- No new third-party UI dependency.
- Spinner animation uses WPF resources and storyboard animation.
- The quota-color calculation is isolated in a small converter with unit tests.
- Existing keyboard, cancellation, close-prevention, and accessibility behavior
  remains functional.

## Verification

Automated tests will verify:

- color endpoints, midpoint, interpolation, and clamping;
- compact card structure and collapsed details contract;
- unqueried quota remains neutral;
- the pending dialog exposes the spinner and cancellation controls;
- streamed output is collapsed but copyable;
- success changes to the success state and schedules automatic close;
- failure and cancellation retain sanitized diagnostics and close behavior;
- all existing WPF runtime, login, quota, publish, and full Release tests pass.

Visual checks will inspect the installed application at 100%, 125%, and 150%
Windows scaling without performing a real login, quota refresh, account switch,
or account removal.
