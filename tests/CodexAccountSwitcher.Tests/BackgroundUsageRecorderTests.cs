using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class BackgroundUsageRecorderTests
{
    [Fact]
    public async Task Uses_confirmed_intervals_and_runs_both_recorders()
    {
        Assert.Equal(TimeSpan.FromHours(2), BackgroundUsageRecorder.QuotaInterval);
        Assert.Equal(TimeSpan.FromMinutes(30), BackgroundUsageRecorder.TokenInterval);
        var quotaRecorded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tokensRecorded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var recorder = new BackgroundUsageRecorder(
            _ =>
            {
                quotaRecorded.TrySetResult();
                return Task.CompletedTask;
            },
            _ =>
            {
                tokensRecorded.TrySetResult();
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10));

        recorder.Start();

        await Task.WhenAll(
            quotaRecorded.Task,
            tokensRecorded.Task).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
