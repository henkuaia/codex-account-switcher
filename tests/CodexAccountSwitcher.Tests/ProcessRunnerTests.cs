using System.Diagnostics;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task Streaming_delivers_standard_output_before_process_exit_and_preserves_stream_identity()
    {
        var process = new FakeStartedProcess { WaitForExplicitExit = true };
        process.StandardOutputLines.Enqueue("Open https://example.test/device and enter ABCD-EFGH");
        process.StandardErrorLines.Enqueue("Waiting for authorization");
        var runner = new ProcessRunner(new FakeProcessFactory(process));
        var lines = new List<ProcessOutputLine>();
        var outputDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress<ProcessOutputLine>(line =>
        {
            lines.Add(line);
            if (line.Stream == ProcessOutputStream.StandardOutput)
            {
                outputDelivered.TrySetResult();
            }
        });

        var runTask = runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["login"]),
            progress,
            CancellationToken.None);

        await outputDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(runTask.IsCompleted);
        process.AllowExit();
        var result = await runTask;

        Assert.Contains(lines, line =>
            line.Stream == ProcessOutputStream.StandardOutput
            && line.Text == "Open https://example.test/device and enter ABCD-EFGH");
        Assert.Contains(lines, line =>
            line.Stream == ProcessOutputStream.StandardError
            && line.Text == "Waiting for authorization");
        Assert.Equal("Open https://example.test/device and enter ABCD-EFGH" + Environment.NewLine, result.StandardOutput);
        Assert.Equal("Waiting for authorization" + Environment.NewLine, result.StandardError);
    }

    [Fact]
    public async Task Streaming_redacts_each_line_before_delivery_and_final_result()
    {
        const string bearerSecret = "bearer-secret";
        const string tokenSecret = "token-secret";
        var process = new FakeStartedProcess();
        process.StandardOutputLines.Enqueue($"Authorization: Bearer {bearerSecret}");
        process.StandardErrorLines.Enqueue($"{{\"access_token\":\"{tokenSecret}\"}}");
        var runner = new ProcessRunner(new FakeProcessFactory(process));
        var lines = new List<ProcessOutputLine>();

        var result = await runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["login"]),
            new InlineProgress<ProcessOutputLine>(lines.Add),
            CancellationToken.None);

        Assert.Equal(2, lines.Count);
        Assert.All(lines, line =>
        {
            Assert.Contains("[REDACTED]", line.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(bearerSecret, line.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(tokenSecret, line.Text, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(bearerSecret, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(tokenSecret, result.StandardError, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Streaming_cancellation_terminates_process_tree_waits_for_exit_and_rethrows_caller_token()
    {
        using var cancellationSource = new CancellationTokenSource();
        var process = new FakeStartedProcess
        {
            OnStart = cancellationSource.Cancel,
            WaitForExplicitExit = true,
        };
        var runner = new ProcessRunner(new FakeProcessFactory(process));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["login"]),
            new InlineProgress<ProcessOutputLine>(_ => { }),
            cancellationSource.Token));

        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
        Assert.True(process.KillCalled);
        Assert.True(process.KilledEntireProcessTree);
        Assert.Equal(cancellationSource.Token, process.WaitTokens[0]);
        Assert.Contains(process.WaitTokens, token => !token.CanBeCanceled);
    }

    [Fact]
    public async Task Streaming_observer_failure_terminates_process_tree_waits_for_exit_and_propagates()
    {
        var process = new FakeStartedProcess { WaitForExplicitExit = true };
        process.StandardOutputLines.Enqueue("device code");
        var runner = new ProcessRunner(new FakeProcessFactory(process));
        var expected = new InvalidOperationException("observer failed");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["login"]),
            new InlineProgress<ProcessOutputLine>(_ => throw expected),
            CancellationToken.None));

        Assert.Same(expected, exception);
        Assert.True(process.KillCalled);
        Assert.True(process.KilledEntireProcessTree);
        Assert.Contains(process.WaitTokens, token => !token.CanBeCanceled);
    }

    [Fact]
    public async Task Default_streaming_overload_keeps_legacy_runner_source_compatible()
    {
        var legacyRunner = new LegacyProcessRunner();
        IProcessRunner runner = legacyRunner;
        var lines = new List<ProcessOutputLine>();

        var runTask = runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["login"]),
            new InlineProgress<ProcessOutputLine>(lines.Add),
            CancellationToken.None);

        Assert.Empty(lines);
        var expected = new CommandResult(0, "first" + Environment.NewLine + "second", "warning");
        legacyRunner.Complete(expected);
        var result = await runTask;

        Assert.Same(expected, result);
        Assert.Equal(1, legacyRunner.CapturedCallCount);
        Assert.Equal(
            [
                new ProcessOutputLine(ProcessOutputStream.StandardOutput, "first"),
                new ProcessOutputLine(ProcessOutputStream.StandardOutput, "second"),
                new ProcessOutputLine(ProcessOutputStream.StandardError, "warning"),
            ],
            lines);
    }

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
        private readonly TaskCompletionSource _exitSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<CancellationToken> WaitTokens { get; } = [];

        public Queue<string> StandardOutputLines { get; } = new();

        public Queue<string> StandardErrorLines { get; } = new();

        public bool ExitsBeforeCancellationIsObserved { get; init; }

        public bool HasExited { get; private set; }

        public bool KillCalled { get; private set; }

        public bool KilledEntireProcessTree { get; private set; }

        public Action? OnStart { get; init; }

        public bool WaitForExplicitExit { get; init; }

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

        public ValueTask<string?> ReadStandardOutputLineAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(StandardOutputLines.TryDequeue(out var line) ? line : null);

        public ValueTask<string?> ReadStandardErrorLineAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(StandardErrorLines.TryDequeue(out var line) ? line : null);

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitTokens.Add(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                if (ExitsBeforeCancellationIsObserved)
                {
                    HasExited = true;
                }

                await Task.FromCanceled(cancellationToken);
            }

            if (WaitForExplicitExit && !HasExited)
            {
                await _exitSignal.Task.WaitAsync(cancellationToken);
            }

            HasExited = true;
        }

        public void Kill(bool entireProcessTree)
        {
            KillCalled = true;
            KilledEntireProcessTree = entireProcessTree;
            HasExited = true;
            _exitSignal.TrySetResult();
        }

        public void AllowExit() => _exitSignal.TrySetResult();

        public void Dispose()
        {
        }
    }

    private sealed class LegacyProcessRunner : IProcessRunner
    {
        private readonly TaskCompletionSource<CommandResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CapturedCallCount { get; private set; }

        public Task<CommandResult> RunCapturedAsync(
            ProcessRequest request,
            CancellationToken cancellationToken)
        {
            CapturedCallCount++;
            return _completion.Task.WaitAsync(cancellationToken);
        }

        public Task<CommandResult> RunVisibleAsync(
            ProcessRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CommandResult(0, string.Empty, string.Empty));

        public void Complete(CommandResult result) => _completion.TrySetResult(result);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
