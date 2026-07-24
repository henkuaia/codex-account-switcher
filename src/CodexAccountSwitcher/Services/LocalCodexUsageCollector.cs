using System.Globalization;
using System.IO;
using System.Text.Json;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public sealed record LocalUsageCollectionResult(
    IReadOnlyList<LocalUsageEvent> Events,
    int InvalidLineCount);

public sealed class LocalCodexUsageCollector
{
    private readonly string _sessionRoot;

    public LocalCodexUsageCollector(string sessionRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoot);
        _sessionRoot = sessionRoot;
    }

    public async Task<LocalUsageCollectionResult> CollectAsync(
        DateTimeOffset earliestUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var events = new List<LocalUsageEvent>();
        var invalidLineCount = 0;

        if (!Directory.Exists(_sessionRoot))
        {
            return new LocalUsageCollectionResult(events, invalidLineCount);
        }

        foreach (var path in Directory.EnumerateFiles(_sessionRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.GetLastWriteTimeUtc(path) < earliestUtc.UtcDateTime)
            {
                continue;
            }

            invalidLineCount += await CollectFileAsync(
                path,
                earliestUtc,
                events,
                cancellationToken);
        }

        return new LocalUsageCollectionResult(events, invalidLineCount);
    }

    private static async Task<int> CollectFileAsync(
        string path,
        DateTimeOffset earliestUtc,
        List<LocalUsageEvent> events,
        CancellationToken cancellationToken)
    {
        var invalidLineCount = 0;
        var model = string.Empty;
        var serviceTier = string.Empty;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var document = JsonDocument.Parse(line);
                ProcessLine(document.RootElement, earliestUtc, ref model, ref serviceTier, events);
            }
            catch (JsonException)
            {
                invalidLineCount++;
            }
        }

        return invalidLineCount;
    }

    private static void ProcessLine(
        JsonElement root,
        DateTimeOffset earliestUtc,
        ref string model,
        ref string serviceTier,
        List<LocalUsageEvent> events)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetString(root, "type", out var type) ||
            !root.TryGetProperty("payload", out var payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (string.Equals(type, "turn_context", StringComparison.Ordinal) &&
            TryGetString(payload, "model", out var nextModel))
        {
            model = nextModel;
            return;
        }

        if (!string.Equals(type, "event_msg", StringComparison.Ordinal) ||
            !TryGetString(payload, "type", out var eventType))
        {
            return;
        }

        if (string.Equals(eventType, "thread_settings_applied", StringComparison.Ordinal) &&
            payload.TryGetProperty("thread_settings", out var threadSettings) &&
            threadSettings.ValueKind == JsonValueKind.Object &&
            TryGetString(threadSettings, "service_tier", out var nextServiceTier))
        {
            serviceTier = nextServiceTier;
            return;
        }

        if (!string.Equals(eventType, "token_count", StringComparison.Ordinal) ||
            !TryGetTimestamp(root, out var timestamp) ||
            timestamp < earliestUtc ||
            !TryGetLastTokenUsage(payload, out var inputTokens, out var cachedInputTokens, out var outputTokens))
        {
            return;
        }

        events.Add(new LocalUsageEvent(
            timestamp,
            model,
            serviceTier,
            inputTokens,
            cachedInputTokens,
            outputTokens));
    }

    private static bool TryGetLastTokenUsage(
        JsonElement payload,
        out long inputTokens,
        out long cachedInputTokens,
        out long outputTokens)
    {
        inputTokens = 0;
        cachedInputTokens = 0;
        outputTokens = 0;
        return payload.TryGetProperty("info", out var info) &&
               info.ValueKind == JsonValueKind.Object &&
               info.TryGetProperty("last_token_usage", out var usage) &&
               usage.ValueKind == JsonValueKind.Object &&
               TryGetInt64(usage, "input_tokens", out inputTokens) &&
               TryGetInt64(usage, "cached_input_tokens", out cachedInputTokens) &&
               TryGetInt64(usage, "output_tokens", out outputTokens);
    }

    private static bool TryGetTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return root.TryGetProperty("timestamp", out var value) &&
               value.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParse(
                   value.GetString(),
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind,
                   out timestamp);
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetInt64(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out value);
    }
}
