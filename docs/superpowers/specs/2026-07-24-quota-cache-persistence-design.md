# Quota Cache Persistence Design

## Goal

Keep the last successfully refreshed quota for each account across application
and Windows restarts. Startup must restore the cached display without calling
the quota endpoints.

## Scope

- Cache successful `QuotaDisplay` results by stable account key.
- Store the time when each result was refreshed.
- Restore matching cached results when the account registry loads.
- Clearly distinguish restored data from a live result.
- Mark restored data as expired when its server-provided reset time has passed.
- Preserve the existing manual refresh behavior.

The change does not add automatic refresh, automatic switching, reset-credit
consumption, authentication storage, or changes to `auth.json`.

## Storage

Add a dedicated versioned JSON file:

```text
%LOCALAPPDATA%\CodexAccountSwitcher\quota-cache.json
```

The file contains only:

- account key;
- quota period and remaining percentage;
- server reset and observation times;
- reset count and optional official limit;
- estimated period quota bounds;
- tooltip text already produced by the quota parser;
- local UTC refresh timestamp.

It must never contain access tokens, authentication snapshots, request headers,
or raw endpoint responses.

Writes use the existing metadata-service pattern: validate all entries, write a
temporary file in the same directory, flush it, then atomically replace the
destination. A malformed or unsupported existing file is retained and blocks
overwriting until the next successful load, matching the current
`AccountMetadataService` preservation behavior.

## Data Flow

### Startup

1. Load the account registry, manual account metadata, and quota cache.
2. Create the account rows from the registry.
3. Apply a cached quota only when its account key still exists.
4. Do not make any network request.

A restored row shows its saved quota immediately. Its status includes the last
refresh time. If `ResetsAt <= current UTC time`, the status is
`缓存已过期，需要刷新 · 上次刷新 <time>`; otherwise it is
`上次刷新 <time> · <existing reset status>`.

### Manual refresh

1. Run the existing refresh operation.
2. Apply every returned update to the current UI as today.
3. Merge only updates containing a successful non-null `QuotaDisplay` into the
   cache, with the current UTC refresh time.
4. Preserve the previous cached value for an account when its refresh fails.
5. Save the merged cache atomically.

After a successful live refresh, the row is no longer marked as cached or
expired. A cache-write failure must not hide the newly refreshed live result;
the status bar reports that refresh succeeded but local caching failed.

## Account Lifecycle

- Newly added accounts have no cached value until their first successful
  refresh.
- Removed accounts are ignored on load. Their old cache entry may remain so an
  accidental removal and re-addition does not lose the last result.
- Account renames do not lose cached data because storage uses the stable
  account key rather than email or alias.

## Failure Handling

- Missing cache file: start normally with `Not queried`.
- Invalid or unsupported cache file: keep the original file, do not overwrite
  it, show a concise local-cache error, and keep account operations available.
- Cache read failure: start normally without restored quota.
- Cache write failure: retain the live UI result and report the caching failure.
- Refresh failure: do not replace a previously successful cached entry.

## Verification

Tests will prove:

- cache round-trips all supported `QuotaDisplay` fields and refresh time;
- invalid documents are rejected without overwrite;
- startup restores cached quota without invoking the refresh delegate;
- fresh and expired cached rows show the correct status;
- a successful manual refresh replaces the cached entry;
- a failed refresh preserves the previous cached entry;
- account-key isolation and rename behavior remain correct;
- the full Release suite and publish contract pass.

