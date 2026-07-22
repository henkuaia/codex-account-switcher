# Invalid Registry Fix Report

## Root Cause

`System.Text.Json` accepts `null` elements in `accounts`. `AccountRegistryService`
declared the DTO collection as non-null elements and dereferenced each element in
the v2 and v3 loops, causing `NullReferenceException` instead of the registry
validation contract's `InvalidDataException`.

`MainWindowViewModel.LoadAsync` normalized `InvalidDataException` to
`AccountRegistry.Empty`, but the successful login, removal, and switch reloads
called `_loadRegistryAsync` directly. An invalid registry after login or removal
therefore left the prior rows visible after the command error handler ran.

## RED

Added parameterized regression coverage before production changes:

- v2 and v3 `accounts: [null]` must throw `InvalidDataException`.
- Login and removal reloads receiving `InvalidDataException` must replace rows
  with the empty registry and leave Add executable.

Command:

```powershell
& '.\.tools\dotnet\dotnet.exe' test 'tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj' --no-restore --filter 'FullyQualifiedName~Registry_account_null_element_throws_invalid_data|FullyQualifiedName~Login_and_removal_invalid_registry_reload_replaces_existing_rows_with_empty_state'
```

Result: 0 passed, 4 failed as expected. The v2/v3 cases threw
`NullReferenceException` from the respective account loops; login/removal
reloads retained two existing rows.

## Change

- Model `RegistryDto.Accounts` and the two account-loop inputs as nullable
  elements; reject a null element with the established `InvalidDataException`.
- Add `LoadRegistryOrEmptyAsync` and use it for initial, login, removal, and
  successful-switch reloads, preserving each caller's existing dispatcher and
  status behavior.
- Add the four parameterized regression cases described above.

## GREEN

Focused regression command above: 4 passed, 0 failed.

Full Debug test project:

```powershell
& '.\.tools\dotnet\dotnet.exe' test 'tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj' --no-restore
```

Result: 340 passed, 0 failed, 0 skipped.

Full Release solution:

```powershell
& '.\.tools\dotnet\dotnet.exe' test 'CodexAccountSwitcher.sln' --configuration Release --no-restore
```

Result: 339 passed, 0 failed, 0 skipped.

The one-test Debug/Release count difference is intentional and not a skipped
test or an omitted Release configuration: Debug discovers 340 tests and
Release discovers 339. The sole Debug-only test is
`ApplicationPathResolverTests.Debug_vendor_fallback_walks_ancestors_from_base_directory`,
which is explicitly enclosed in `#if DEBUG` in its test source. A
configuration-specific `--list-tests` comparison confirmed that all four new
regression cases are discovered in both configurations.

`git diff --check` exited 0. It emitted only LF-to-CRLF working-copy warnings.

## Commit

`fix: handle invalid registry reloads` (the single commit containing this
report; its final SHA is supplied in the task handoff because embedding that
SHA here would change the commit object).

## Unverified

- No real Codex login, switch, removal, quota, or account files under
  `C:\Users\demax\.codex` were accessed or invoked.
- No manual WPF runtime interaction was performed; automated Debug and Release
  test suites cover the changed behavior.
