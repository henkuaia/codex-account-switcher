using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public interface ICodexConversationService
{
    Task<IReadOnlyList<CodexConversation>> LoadAsync(CancellationToken cancellationToken);

    void Open(CodexConversation conversation);
}

public sealed class CodexConversationService(
    string codexCliDirectory,
    string codexHome) : ICodexConversationService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
    private readonly string _codexCliDirectory = Path.GetFullPath(codexCliDirectory);
    private readonly string _codexHome = Path.GetFullPath(codexHome);
    private readonly CodexCliStager _stager = new();

    public async Task<IReadOnlyList<CodexConversation>> LoadAsync(
        CancellationToken cancellationToken)
    {
        var stagedDirectory = await _stager.StageAsync(
            _codexCliDirectory,
            cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        return await RunAsync(
            Path.Combine(stagedDirectory, "codex.exe"),
            timeout.Token);
    }

    public void Open(CodexConversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        Process.Start(new ProcessStartInfo(BuildThreadUri(conversation.Id))
        {
            UseShellExecute = true,
        });
    }

    internal static string BuildThreadUri(string threadId) =>
        $"codex://threads/{threadId}";

    private async Task<IReadOnlyList<CodexConversation>> RunAsync(
        string executablePath,
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
            StandardInputEncoding = Utf8WithoutBom,
            StandardOutputEncoding = Utf8WithoutBom,
            StandardErrorEncoding = Utf8WithoutBom,
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.Environment["CODEX_HOME"] = _codexHome;

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await SendAsync(
                process,
                new
                {
                    method = "initialize",
                    id = 1,
                    @params = new
                    {
                        clientInfo = new
                        {
                            name = "codex_account_switcher",
                            title = "Codex Account Switcher",
                            version = "1.0.0",
                        },
                        capabilities = new { experimentalApi = true },
                    },
                },
                cancellationToken);
            await ReadResponseAsync(process, 1, cancellationToken);
            await SendAsync(
                process,
                new { method = "initialized", @params = new { } },
                cancellationToken);

            var conversations = new List<CodexConversation>();
            var requestId = 2;
            foreach (var archived in new[] { false, true })
            {
                string? cursor = null;
                do
                {
                    var currentId = requestId++;
                    await SendAsync(
                        process,
                        new
                        {
                            method = "thread/list",
                            id = currentId,
                            @params = new
                            {
                                archived,
                                cursor,
                                limit = 100,
                                sortKey = "updated_at",
                                sortDirection = "desc",
                                useStateDbOnly = true,
                            },
                        },
                        cancellationToken);
                    using var response = await ReadResponseAsync(
                        process,
                        currentId,
                        cancellationToken);
                    var page = ParsePage(response.RootElement, archived);
                    conversations.AddRange(page.Conversations);
                    cursor = page.NextCursor;
                }
                while (cursor is not null);
            }

            return conversations
                .OrderByDescending(conversation => conversation.UpdatedAt)
                .ToArray();
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

    private static async Task SendAsync(
        Process process,
        object message,
        CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task<JsonDocument> ReadResponseAsync(
        Process process,
        int requestId,
        CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) ||
                !id.TryGetInt32(out var value) ||
                value != requestId)
            {
                document.Dispose();
                continue;
            }

            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var text)
                    ? text.GetString()
                    : null;
                document.Dispose();
                throw new InvalidOperationException(
                    message ?? "无法读取 Codex 历史对话。");
            }

            return document;
        }

        throw new InvalidOperationException("Codex 历史对话接口已意外关闭。");
    }

    internal static ConversationPage ParsePage(JsonElement response, bool archived)
    {
        var result = response.GetProperty("result");
        var conversations = result
            .GetProperty("data")
            .EnumerateArray()
            .Select(thread =>
            {
                var id = thread.GetProperty("id").GetString()!;
                var preview = thread.GetProperty("preview").GetString() ?? string.Empty;
                var title = thread.TryGetProperty("name", out var name) &&
                            name.ValueKind == JsonValueKind.String
                    ? name.GetString()
                    : null;
                return new CodexConversation(
                    id,
                    string.IsNullOrWhiteSpace(title)
                        ? FirstLineOrId(preview, id)
                        : title,
                    preview,
                    thread.GetProperty("cwd").GetString() ?? string.Empty,
                    DateTimeOffset.FromUnixTimeSeconds(
                        thread.GetProperty("updatedAt").GetInt64()),
                    archived);
            })
            .ToArray();
        var nextCursor = result.TryGetProperty("nextCursor", out var next) &&
                         next.ValueKind == JsonValueKind.String
            ? next.GetString()
            : null;
        return new ConversationPage(conversations, nextCursor);
    }

    private static string FirstLineOrId(string preview, string id)
    {
        var firstLine = preview
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine) ? id : firstLine;
    }
}

internal sealed record ConversationPage(
    IReadOnlyList<CodexConversation> Conversations,
    string? NextCursor);
