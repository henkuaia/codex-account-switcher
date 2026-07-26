using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public interface IIndividualLimitReader
{
    Task<IndividualLimitSnapshot?> ReadAsync(
        string authSnapshotPath,
        CancellationToken cancellationToken);
}

public sealed class CodexAppServerIndividualLimitReader(
    string codexCliDirectory) : IIndividualLimitReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _codexCliDirectory = Path.GetFullPath(codexCliDirectory);
    private readonly CodexCliStager _stager = new();

    public async Task<IndividualLimitSnapshot?> ReadAsync(
        string authSnapshotPath,
        CancellationToken cancellationToken)
    {
        var stagedDirectory = await _stager.StageAsync(
            _codexCliDirectory,
            cancellationToken);
        var temporaryHome = Path.Combine(
            Path.GetTempPath(),
            "CodexAccountSwitcher",
            $"quota-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryHome);
        try
        {
            File.Copy(authSnapshotPath, Path.Combine(temporaryHome, "auth.json"));
            return await RunAsync(
                Path.Combine(stagedDirectory, "codex.exe"),
                temporaryHome,
                cancellationToken);
        }
        finally
        {
            if (Directory.Exists(temporaryHome))
            {
                Directory.Delete(temporaryHome, recursive: true);
            }
        }
    }

    private static async Task<IndividualLimitSnapshot?> RunAsync(
        string executablePath,
        string codexHome,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.Environment["CODEX_HOME"] = codexHome;

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.StandardInput.WriteLineAsync("""
                {"method":"initialize","id":1,"params":{"clientInfo":{"name":"codex_account_switcher","title":"Codex Account Switcher","version":"1.0.0"},"capabilities":{"experimentalApi":true}}}
                """);
            await process.StandardInput.FlushAsync(timeout.Token);

            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var value))
                {
                    continue;
                }

                if (value == 1)
                {
                    await process.StandardInput.WriteLineAsync(
                        """{"method":"initialized","params":{}}""");
                    await process.StandardInput.WriteLineAsync(
                        """{"method":"account/rateLimits/read","id":2,"params":{}}""");
                    await process.StandardInput.FlushAsync(timeout.Token);
                    continue;
                }

                if (value == 2)
                {
                    return ParseResponse(root);
                }
            }

            return null;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None);
            await errorTask;
        }
    }

    internal static IndividualLimitSnapshot? ParseResponse(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("rateLimits", out var rateLimits) ||
            !rateLimits.TryGetProperty("individualLimit", out var limit) ||
            limit.ValueKind != JsonValueKind.Object ||
            !TryReadDecimal(limit, "limit", out var total) ||
            !TryReadDecimal(limit, "used", out var used) ||
            !limit.TryGetProperty("remainingPercent", out var remaining) ||
            !remaining.TryGetInt32(out var remainingPercent) ||
            remainingPercent is < 0 or > 100 ||
            !limit.TryGetProperty("resetsAt", out var resetsAt) ||
            !resetsAt.TryGetInt64(out var resetsAtSeconds))
        {
            return null;
        }

        return new IndividualLimitSnapshot(
            total,
            used,
            remainingPercent,
            DateTimeOffset.FromUnixTimeSeconds(resetsAtSeconds),
            ReadAvailableResetCount(result));
    }

    private static bool TryReadDecimal(
        JsonElement element,
        string propertyName,
        out decimal value)
    {
        value = default;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        var parsed = property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(
                property.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var text) => text,
            _ => -1,
        };
        if (parsed < 0)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static int? ReadAvailableResetCount(JsonElement result)
    {
        if (!result.TryGetProperty("rateLimitResetCredits", out var resetCredits) ||
            resetCredits.ValueKind != JsonValueKind.Object ||
            !resetCredits.TryGetProperty("availableCount", out var count) ||
            !count.TryGetInt32(out var value) ||
            value < 0)
        {
            return null;
        }

        return value;
    }
}
