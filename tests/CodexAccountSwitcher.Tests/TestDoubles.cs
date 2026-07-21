using CodexAccountSwitcher.Models;
using System.Collections.Concurrent;
using System.Net.Http;

namespace CodexAccountSwitcher.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "CodexAccountSwitcher.Tests", Guid.NewGuid().ToString("N"));

    public TemporaryDirectory() => Directory.CreateDirectory(Path);

    public void Write(string relativePath, string content)
    {
        var fullPath = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}

internal sealed class TemporaryFile : IDisposable
{
    public string Path { get; }

    private TemporaryFile(string content)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cas-{Guid.NewGuid():N}.json");
        File.WriteAllText(Path, content);
    }

    public static TemporaryFile Json(string content) => new(content);

    public void Dispose()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }
}

internal static class Accounts
{
    public static AccountRecord Record(
        string key,
        string email,
        string alias = "",
        string accountId = "acct-1") =>
        new(key, accountId, "user", email, alias, null, "plus", "chatgpt");
}

internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _sendAsync;
    private int _activeRequests;
    private int _maximumActiveRequests;

    public RecordingHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
    {
        _sendAsync = sendAsync;
    }

    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = [];

    public int MaximumActiveRequests => _maximumActiveRequests;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        var activeRequests = Interlocked.Increment(ref _activeRequests);
        UpdateMaximumActiveRequests(activeRequests);

        try
        {
            return await _sendAsync(request, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _activeRequests);
        }
    }

    private void UpdateMaximumActiveRequests(int activeRequests)
    {
        int observed;
        do
        {
            observed = _maximumActiveRequests;
            if (observed >= activeRequests)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _maximumActiveRequests, activeRequests, observed) != observed);
    }
}

internal sealed class CollectingProgress<T> : IProgress<T>
{
    public List<T> Values { get; } = [];

    public void Report(T value) => Values.Add(value);
}
