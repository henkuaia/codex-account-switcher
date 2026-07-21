using System.Diagnostics;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class ProcessRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pre_cancelled_call_does_not_start_process(bool visible)
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var process = new FakeStartedProcess();
        var factory = new FakeProcessFactory(process);
        var runner = new ProcessRunner(factory);
        var request = new ProcessRequest("fake.exe", ["command"], Visible: visible);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => visible
            ? runner.RunVisibleAsync(request, cancellationSource.Token)
            : runner.RunCapturedAsync(request, cancellationSource.Token));

        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
        Assert.Equal(0, process.StartCallCount);
        Assert.Equal(visible, factory.StartInfo!.UseShellExecute);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_terminates_process_tree_waits_for_exit_and_rethrows(bool visible)
    {
        using var cancellationSource = new CancellationTokenSource();
        var process = new FakeStartedProcess { OnStart = cancellationSource.Cancel };
        var factory = new FakeProcessFactory(process);
        var runner = new ProcessRunner(factory);
        var request = new ProcessRequest("fake.exe", ["command"], Visible: visible);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => visible
            ? runner.RunVisibleAsync(request, cancellationSource.Token)
            : runner.RunCapturedAsync(request, cancellationSource.Token));

        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
        Assert.True(process.KillCalled);
        Assert.True(process.KilledEntireProcessTree);
        Assert.Equal(2, process.WaitTokens.Count);
        Assert.Equal(cancellationSource.Token, process.WaitTokens[0]);
        Assert.False(process.WaitTokens[1].CanBeCanceled);
        Assert.Equal(1, process.StartCallCount);
        Assert.Equal(visible, factory.StartInfo!.UseShellExecute);
    }

    [Fact]
    public async Task Cancellation_after_child_exit_does_not_kill_process()
    {
        using var cancellationSource = new CancellationTokenSource();
        var process = new FakeStartedProcess
        {
            ExitsBeforeCancellationIsObserved = true,
            OnStart = cancellationSource.Cancel,
        };
        var runner = new ProcessRunner(new FakeProcessFactory(process));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["command"]),
            cancellationSource.Token));

        Assert.False(process.KillCalled);
        Assert.Equal(2, process.WaitTokens.Count);
        Assert.False(process.WaitTokens[1].CanBeCanceled);
    }

    private sealed class FakeProcessFactory(FakeStartedProcess process) : IProcessFactory
    {
        public ProcessStartInfo? StartInfo { get; private set; }

        public IStartedProcess Create(ProcessStartInfo startInfo)
        {
            StartInfo = startInfo;
            return process;
        }
    }

    private sealed class FakeStartedProcess : IStartedProcess
    {
        public List<CancellationToken> WaitTokens { get; } = [];

        public bool ExitsBeforeCancellationIsObserved { get; init; }

        public bool HasExited { get; private set; }

        public bool KillCalled { get; private set; }

        public bool KilledEntireProcessTree { get; private set; }

        public Action? OnStart { get; init; }

        public int StartCallCount { get; private set; }

        public int ExitCode => 0;

        public bool Start()
        {
            StartCallCount++;
            OnStart?.Invoke();
            return true;
        }

        public Task<string> ReadStandardOutputAsync(CancellationToken cancellationToken) =>
            Task.FromResult(string.Empty);

        public Task<string> ReadStandardErrorAsync(CancellationToken cancellationToken) =>
            Task.FromResult(string.Empty);

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitTokens.Add(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                if (ExitsBeforeCancellationIsObserved)
                {
                    HasExited = true;
                }

                return Task.FromCanceled(cancellationToken);
            }

            HasExited = true;
            return Task.CompletedTask;
        }

        public void Kill(bool entireProcessTree)
        {
            KillCalled = true;
            KilledEntireProcessTree = entireProcessTree;
        }

        public void Dispose()
        {
        }
    }
}
