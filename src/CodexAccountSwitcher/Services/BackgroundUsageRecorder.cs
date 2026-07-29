namespace CodexAccountSwitcher.Services;

public sealed class BackgroundUsageRecorder : IDisposable
{
    public static readonly TimeSpan QuotaInterval = TimeSpan.FromHours(2);
    public static readonly TimeSpan TokenInterval = TimeSpan.FromMinutes(30);

    private readonly Func<CancellationToken, Task> _recordQuotaAsync;
    private readonly Func<CancellationToken, Task> _recordTokensAsync;
    private readonly TimeSpan _quotaInterval;
    private readonly TimeSpan _tokenInterval;
    private readonly CancellationTokenSource _cancellation = new();
    private int _started;

    public BackgroundUsageRecorder(
        Func<CancellationToken, Task> recordQuotaAsync,
        Func<CancellationToken, Task> recordTokensAsync)
        : this(recordQuotaAsync, recordTokensAsync, QuotaInterval, TokenInterval)
    {
    }

    internal BackgroundUsageRecorder(
        Func<CancellationToken, Task> recordQuotaAsync,
        Func<CancellationToken, Task> recordTokensAsync,
        TimeSpan quotaInterval,
        TimeSpan tokenInterval)
    {
        _recordQuotaAsync = recordQuotaAsync
            ?? throw new ArgumentNullException(nameof(recordQuotaAsync));
        _recordTokensAsync = recordTokensAsync
            ?? throw new ArgumentNullException(nameof(recordTokensAsync));
        _quotaInterval = quotaInterval;
        _tokenInterval = tokenInterval;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _ = RunAsync(_recordQuotaAsync, _quotaInterval, _cancellation.Token);
        _ = RunAsync(_recordTokensAsync, _tokenInterval, _cancellation.Token);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }

    private static async Task RunAsync(
        Func<CancellationToken, Task> recordAsync,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await recordAsync(cancellationToken);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
