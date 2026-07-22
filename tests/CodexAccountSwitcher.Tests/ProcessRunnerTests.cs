using System.Diagnostics;
using System.Threading.Channels;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task Streaming_awaits_standard_output_handler_after_process_exit()
    {
        var process = new BlockingStartedProcess();
        var runner = new ProcessRunner(new FakeProcessFactory(process));
        var lines = new List<ProcessOutputLine>();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProcessOutputHandler outputHandler = async (line, cancellationToken) =>
        {
            lines.Add(line);
            if (line.Stream == ProcessOutputStream.StandardOutput)
            {
                handlerStarted.TrySetResult();
                await releaseHandler.Task.WaitAsync(cancellationToken);
            }
        };

        var runTask = runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["login"]),
            outputHandler,
            CancellationToken.None);

        await Task.WhenAll(process.StandardOutputReadStarted.Task, process.StandardErrorReadStarted.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        process.WriteStandardOutput("Open https://example.test/device and enter ABCD-EFGH");
        process.CompleteStandardOutput();
        process.CompleteStandardError();
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        process.AllowExit();
        await Task.Yield();
        Assert.False(runTask.IsCompleted);
        releaseHandler.TrySetResult();
        var result = await runTask;

        Assert.Equal(
            new ProcessOutputLine(
                ProcessOutputStream.StandardOutput,
                "Open https://example.test/device and enter ABCD-EFGH"),
            Assert.Single(lines));
        Assert.Equal("Open https://example.test/device and enter ABCD-EFGH" + Environment.NewLine, result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task Streaming_preserves_within_stream_order_and_stream_identity_with_concurrent_pipes()
    {
        var process = new BlockingStartedProcess();
        var runner = new ProcessRunner(new FakeProcessFactory(process));
        var lines = new List<ProcessOutputLine>();
        var linesLock = new object();
        ProcessOutputHandler outputHandler = async (line, _) =>
        {
            await Task.Yield();
            lock (linesLock)
            {
                lines.Add(line);
            }
        };

        var runTask = runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["login"]),
            outputHandler,
            CancellationToken.None);

        await Task.WhenAll(process.StandardOutputReadStarted.Task, process.StandardErrorReadStarted.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        process.WriteStandardOutput("stdout-1");
        process.WriteStandardError("stderr-1");
        process.WriteStandardOutput("stdout-2");
        process.WriteStandardError("stderr-2");
        process.WriteStandardOutput("stdout-3");
        process.CompleteStandardOutput();
        process.CompleteStandardError();
        process.AllowExit();
        var result = await runTask;

        Assert.Equal(
            ["stdout-1", "stdout-2", "stdout-3"],
            lines.Where(line => line.Stream == ProcessOutputStream.StandardOutput).Select(line => line.Text));
        Assert.Equal(
            ["stderr-1", "stderr-2"],
            lines.Where(line => line.Stream == ProcessOutputStream.StandardError).Select(line => line.Text));
        Assert.Equal(
            string.Join(Environment.NewLine, "stdout-1", "stdout-2", "stdout-3") + Environment.NewLine,
            result.StandardOutput);
        Assert.Equal(
            string.Join(Environment.NewLine, "stderr-1", "stderr-2") + Environment.NewLine,
            result.StandardError);
    }

    [Fact]
    public async Task Streaming_redacts_each_line_before_awaited_delivery_and_final_result()
    {
        const string bearerSecret = "bearer-secret";
        const string tokenSecret = "token-secret";
        var process = new FakeStartedProcess();
        process.StandardOutputLines.Enqueue($"Authorization: Bearer {bearerSecret}");
        process.StandardErrorLines.Enqueue($"{{\"access_token\":\"{tokenSecret}\"}}");
        var runner = new ProcessRunner(new FakeProcessFactory(process));
        var lines = new List<ProcessOutputLine>();
        ProcessOutputHandler outputHandler = (line, _) =>
        {
            lines.Add(line);
            return ValueTask.CompletedTask;
        };

        var result = await runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["login"]),
            outputHandler,
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
        var process = new BlockingStartedProcess();
        var runner = new ProcessRunner(new FakeProcessFactory(process));
        ProcessOutputHandler outputHandler = (_, _) => ValueTask.CompletedTask;

        var runTask = runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["login"]),
            outputHandler,
            cancellationSource.Token);

        await Task.WhenAll(process.StandardOutputReadStarted.Task, process.StandardErrorReadStarted.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        cancellationSource.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
        Assert.True(process.KillCalled);
        Assert.True(process.KilledEntireProcessTree);
        Assert.Equal(cancellationSource.Token, process.WaitTokens[0]);
        Assert.Contains(process.WaitTokens, token => !token.CanBeCanceled);
        Assert.True(process.StandardOutputReadCanceled.Task.IsCompleted);
        Assert.True(process.StandardErrorReadCanceled.Task.IsCompleted);
    }

    [Fact]
    public async Task Streaming_observer_failure_terminates_process_tree_waits_for_exit_and_propagates()
    {
        var process = new BlockingStartedProcess();
        var runner = new ProcessRunner(new FakeProcessFactory(process));
        var expected = new InvalidOperationException("observer failed");
        var callbackCount = 0;
        ProcessOutputHandler outputHandler = (line, _) =>
        {
            Interlocked.Increment(ref callbackCount);
            return line.Stream == ProcessOutputStream.StandardOutput
                ? ValueTask.FromException(expected)
                : ValueTask.CompletedTask;
        };

        var runTask = runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["login"]),
            outputHandler,
            CancellationToken.None);

        await Task.WhenAll(process.StandardOutputReadStarted.Task, process.StandardErrorReadStarted.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        process.WriteStandardOutput("device code");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runTask);

        Assert.Same(expected, exception);
        Assert.True(process.KillCalled);
        Assert.True(process.KilledEntireProcessTree);
        Assert.Contains(process.WaitTokens, token => !token.CanBeCanceled);
        Assert.True(process.StandardErrorReadCanceled.Task.IsCompleted);
        var countAfterReturn = Volatile.Read(ref callbackCount);
        await Task.Yield();
        Assert.Equal(countAfterReturn, Volatile.Read(ref callbackCount));
    }

    [Fact]
    public async Task Streaming_observer_failure_remains_original_when_caller_cancels_during_cleanup()
    {
        using var cancellationSource = new CancellationTokenSource();
        var process = new BlockingStartedProcess
        {
            BlockNonCancelableWait = true,
        };
        var runner = new ProcessRunner(new FakeProcessFactory(process));
        var expected = new InvalidOperationException("observer failed");
        ProcessOutputHandler outputHandler = (line, _) =>
            line.Stream == ProcessOutputStream.StandardOutput
                ? ValueTask.FromException(expected)
                : ValueTask.CompletedTask;

        var runTask = runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["login"]),
            outputHandler,
            cancellationSource.Token);

        await Task.WhenAll(process.StandardOutputReadStarted.Task, process.StandardErrorReadStarted.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        process.WriteStandardOutput("device code");
        await process.NonCancelableWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(runTask.IsCompleted);
        cancellationSource.Cancel();
        process.ReleaseNonCancelableWait();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runTask);

        Assert.Same(expected, exception);
        Assert.True(process.HasExited);
        Assert.True(process.StandardErrorReadCanceled.Task.IsCompleted);
    }

    [Fact]
    public async Task Streaming_observer_failure_reports_unknown_exit_when_kill_fails_and_process_remains_alive()
    {
        var process = new BlockingStartedProcess
        {
            KillException = new InvalidOperationException("secret kill failure"),
        };
        var runner = new ProcessRunner(new FakeProcessFactory(process));
        ProcessOutputHandler outputHandler = (line, _) =>
            line.Stream == ProcessOutputStream.StandardOutput
                ? ValueTask.FromException(new InvalidOperationException("observer failed"))
                : ValueTask.CompletedTask;

        var runTask = runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["login"]),
            outputHandler,
            CancellationToken.None);

        await Task.WhenAll(process.StandardOutputReadStarted.Task, process.StandardErrorReadStarted.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        process.WriteStandardOutput("device code");

        var exception = await Record.ExceptionAsync(() => runTask);

        Assert.NotNull(exception);
        Assert.Equal("HelperProcessExitUnverifiedException", exception.GetType().Name);
        Assert.Equal("Helper process exit could not be verified.", exception.Message);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
        Assert.True(process.KillCalled);
        Assert.False(process.HasExited);
        Assert.True(process.StandardErrorReadCanceled.Task.IsCompleted);
    }

    [Fact]
    public async Task Streaming_cancellation_reports_unknown_exit_when_kill_fails_and_process_remains_alive()
    {
        using var cancellationSource = new CancellationTokenSource();
        var process = new BlockingStartedProcess
        {
            KillException = new InvalidOperationException("secret kill failure"),
        };
        var runner = new ProcessRunner(new FakeProcessFactory(process));

        var runTask = runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["login"]),
            (_, _) => ValueTask.CompletedTask,
            cancellationSource.Token);

        await Task.WhenAll(process.StandardOutputReadStarted.Task, process.StandardErrorReadStarted.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        cancellationSource.Cancel();

        var exception = await Record.ExceptionAsync(() => runTask);

        Assert.NotNull(exception);
        Assert.Equal("HelperProcessExitUnverifiedException", exception.GetType().Name);
        Assert.Equal("Helper process exit could not be verified.", exception.Message);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
        Assert.True(process.KillCalled);
        Assert.False(process.HasExited);
        Assert.True(process.StandardOutputReadCanceled.Task.IsCompleted);
        Assert.True(process.StandardErrorReadCanceled.Task.IsCompleted);
    }

    [Fact]
    public async Task Streaming_observer_failure_reports_unknown_exit_when_final_wait_fails_and_exit_is_unverifiable()
    {
        var process = new BlockingStartedProcess
        {
            KeepAliveAfterKill = true,
            NonCancelableWaitException = new InvalidOperationException("secret wait failure"),
        };
        var runner = new ProcessRunner(new FakeProcessFactory(process));
        ProcessOutputHandler outputHandler = (line, _) =>
            line.Stream == ProcessOutputStream.StandardOutput
                ? ValueTask.FromException(new InvalidOperationException("observer failed"))
                : ValueTask.CompletedTask;

        var runTask = runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["login"]),
            outputHandler,
            CancellationToken.None);

        await Task.WhenAll(process.StandardOutputReadStarted.Task, process.StandardErrorReadStarted.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        process.WriteStandardOutput("device code");

        var exception = await Record.ExceptionAsync(() => runTask);

        Assert.NotNull(exception);
        Assert.Equal("HelperProcessExitUnverifiedException", exception.GetType().Name);
        Assert.Equal("Helper process exit could not be verified.", exception.Message);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
        Assert.True(process.KillCalled);
        Assert.False(process.HasExited);
        Assert.Contains(process.WaitTokens, token => !token.CanBeCanceled);
        Assert.True(process.StandardErrorReadCanceled.Task.IsCompleted);
    }

    [Fact]
    public async Task Default_streaming_overload_keeps_legacy_runner_source_compatible()
    {
        var legacyRunner = new LegacyProcessRunner();
        IProcessRunner runner = legacyRunner;
        var lines = new List<ProcessOutputLine>();
        var firstDeliveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstDelivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProcessOutputHandler outputHandler = async (line, cancellationToken) =>
        {
            lines.Add(line);
            if (lines.Count == 1)
            {
                firstDeliveryStarted.TrySetResult();
                await releaseFirstDelivery.Task.WaitAsync(cancellationToken);
            }
        };

        var runTask = runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["login"]),
            outputHandler,
            CancellationToken.None);

        Assert.Empty(lines);
        var expected = new CommandResult(0, "first" + Environment.NewLine + "second", "warning");
        legacyRunner.Complete(expected);
        await firstDeliveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(runTask.IsCompleted);
        Assert.Single(lines);
        releaseFirstDelivery.TrySetResult();
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initial_wait_failure_reports_unknown_exit_when_final_exit_is_unverified(bool visible)
    {
        var process = new FakeStartedProcess
        {
            InitialWaitException = new IOException("secret initiating failure"),
            KeepAliveAfterKill = true,
            NonCancelableWaitException = new IOException("secret final wait failure"),
        };
        var runner = new ProcessRunner(new FakeProcessFactory(process));
        var request = new ProcessRequest("fake.exe", ["command"], Visible: visible);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => visible
            ? runner.RunVisibleAsync(request, default)
            : runner.RunCapturedAsync(request, default));

        Assert.Equal("HelperProcessExitUnverifiedException", exception.GetType().Name);
        Assert.Equal("Helper process exit could not be verified.", exception.Message);
        Assert.True(process.KillCalled);
        Assert.False(process.HasExited);
        Assert.Equal(2, process.WaitTokens.Count);
        Assert.False(process.WaitTokens[1].CanBeCanceled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initial_wait_failure_is_rethrown_when_cleanup_proves_exit(bool visible)
    {
        var initiatingException = new IOException("simulated initial wait failure");
        var process = new FakeStartedProcess { InitialWaitException = initiatingException };
        var runner = new ProcessRunner(new FakeProcessFactory(process));
        var request = new ProcessRequest("fake.exe", ["command"], Visible: visible);

        var exception = await Assert.ThrowsAsync<IOException>(() => visible
            ? runner.RunVisibleAsync(request, default)
            : runner.RunCapturedAsync(request, default));

        Assert.Same(initiatingException, exception);
        Assert.True(process.KillCalled);
        Assert.True(process.HasExited);
        Assert.Equal(2, process.WaitTokens.Count);
        Assert.False(process.WaitTokens[1].CanBeCanceled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task False_start_uses_fixed_typed_process_start_failure(bool visible)
    {
        var process = new FakeStartedProcess { StartResult = false };
        var runner = new ProcessRunner(new FakeProcessFactory(process));
        var request = new ProcessRequest("fake.exe", ["command"], Visible: visible);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => visible
            ? runner.RunVisibleAsync(request, default)
            : runner.RunCapturedAsync(request, default));

        Assert.Equal("HelperProcessStartException", exception.GetType().Name);
        Assert.Equal("The process did not start.", exception.Message);
        Assert.Equal(1, process.StartCallCount);
        Assert.Empty(process.WaitTokens);
        Assert.False(process.KillCalled);
    }

    [Fact]
    public async Task Non_streamed_cancellation_reports_unknown_exit_when_kill_fails()
    {
        using var cancellationSource = new CancellationTokenSource();
        var process = new FakeStartedProcess
        {
            OnStart = cancellationSource.Cancel,
            KillException = new InvalidOperationException("simulated kill failure"),
        };
        var runner = new ProcessRunner(new FakeProcessFactory(process));

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["command"]),
            cancellationSource.Token));

        Assert.Equal("HelperProcessExitUnverifiedException", exception.GetType().Name);
        Assert.Equal("Helper process exit could not be verified.", exception.Message);
        Assert.True(process.KillCalled);
        Assert.False(process.HasExited);
    }

    [Fact]
    public async Task Non_streamed_cancellation_reports_unknown_exit_when_final_wait_fails()
    {
        using var cancellationSource = new CancellationTokenSource();
        var process = new FakeStartedProcess
        {
            OnStart = cancellationSource.Cancel,
            KeepAliveAfterKill = true,
            NonCancelableWaitException = new IOException("simulated final wait failure"),
        };
        var runner = new ProcessRunner(new FakeProcessFactory(process));

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => runner.RunCapturedAsync(
            new ProcessRequest("fake.exe", ["command"]),
            cancellationSource.Token));

        Assert.Equal("HelperProcessExitUnverifiedException", exception.GetType().Name);
        Assert.Equal("Helper process exit could not be verified.", exception.Message);
        Assert.True(process.KillCalled);
        Assert.False(process.HasExited);
        Assert.Equal(2, process.WaitTokens.Count);
        Assert.False(process.WaitTokens[1].CanBeCanceled);
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

    [Fact]
    public async Task Visible_process_applies_environment_without_hiding_console()
    {
        var process = new FakeStartedProcess();
        var factory = new FakeProcessFactory(process);
        var runner = new ProcessRunner(factory);

        var result = await runner.RunVisibleAsync(
            new ProcessRequest(
                "fake.exe",
                ["remove"],
                Visible: true,
                Environment: new Dictionary<string, string>
                {
                    ["CODEX_AUTH_SKIP_SERVICE_RECONCILE"] = "1",
                }),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(factory.StartInfo);
        Assert.False(factory.StartInfo.UseShellExecute);
        Assert.False(factory.StartInfo.CreateNoWindow);
        Assert.Equal("1", factory.StartInfo.Environment["CODEX_AUTH_SKIP_SERVICE_RECONCILE"]);
        Assert.Equal(1, process.StartCallCount);
    }

    private sealed class FakeProcessFactory(IStartedProcess process) : IProcessFactory
    {
        public ProcessStartInfo? StartInfo { get; private set; }

        public IStartedProcess Create(ProcessStartInfo startInfo)
        {
            StartInfo = startInfo;
            return process;
        }
    }

    private sealed class BlockingStartedProcess : IStartedProcess
    {
        private readonly Channel<string> _standardOutput = CreateChannel();
        private readonly Channel<string> _standardError = CreateChannel();
        private readonly TaskCompletionSource _exitSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseNonCancelableWait =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource StandardOutputReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource StandardErrorReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource StandardOutputReadCanceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource StandardErrorReadCanceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<CancellationToken> WaitTokens { get; } = [];

        public bool HasExited { get; private set; }

        public int ExitCode => 0;

        public bool KillCalled { get; private set; }

        public bool KilledEntireProcessTree { get; private set; }

        public bool BlockNonCancelableWait { get; init; }

        public Exception? KillException { get; init; }

        public bool KeepAliveAfterKill { get; init; }

        public Exception? NonCancelableWaitException { get; init; }

        public TaskCompletionSource NonCancelableWaitStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Start() => true;

        public Task<string> ReadStandardOutputAsync(CancellationToken cancellationToken) =>
            Task.FromResult(string.Empty);

        public Task<string> ReadStandardErrorAsync(CancellationToken cancellationToken) =>
            Task.FromResult(string.Empty);

        public ValueTask<string?> ReadStandardOutputLineAsync(CancellationToken cancellationToken) =>
            ReadLineAsync(
                _standardOutput.Reader,
                StandardOutputReadStarted,
                StandardOutputReadCanceled,
                cancellationToken);

        public ValueTask<string?> ReadStandardErrorLineAsync(CancellationToken cancellationToken) =>
            ReadLineAsync(
                _standardError.Reader,
                StandardErrorReadStarted,
                StandardErrorReadCanceled,
                cancellationToken);

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitTokens.Add(cancellationToken);
            if (!cancellationToken.CanBeCanceled && BlockNonCancelableWait)
            {
                NonCancelableWaitStarted.TrySetResult();
                await _releaseNonCancelableWait.Task;
            }

            if (!cancellationToken.CanBeCanceled && NonCancelableWaitException is not null)
            {
                throw NonCancelableWaitException;
            }

            await _exitSignal.Task.WaitAsync(cancellationToken);
            HasExited = true;
        }

        public void Kill(bool entireProcessTree)
        {
            KillCalled = true;
            KilledEntireProcessTree = entireProcessTree;
            if (KillException is not null)
            {
                _exitSignal.TrySetException(KillException);
                throw KillException;
            }

            HasExited = !KeepAliveAfterKill;
            if (NonCancelableWaitException is not null)
            {
                _exitSignal.TrySetException(NonCancelableWaitException);
            }
            else
            {
                _exitSignal.TrySetResult();
            }
        }

        public void WriteStandardOutput(string line) => _standardOutput.Writer.TryWrite(line);

        public void WriteStandardError(string line) => _standardError.Writer.TryWrite(line);

        public void CompleteStandardOutput() => _standardOutput.Writer.TryComplete();

        public void CompleteStandardError() => _standardError.Writer.TryComplete();

        public void AllowExit() => _exitSignal.TrySetResult();

        public void ReleaseNonCancelableWait() => _releaseNonCancelableWait.TrySetResult();

        public void Dispose()
        {
        }

        private static Channel<string> CreateChannel() =>
            Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        private static async ValueTask<string?> ReadLineAsync(
            ChannelReader<string> reader,
            TaskCompletionSource readStarted,
            TaskCompletionSource readCanceled,
            CancellationToken cancellationToken)
        {
            readStarted.TrySetResult();
            try
            {
                return await reader.WaitToReadAsync(cancellationToken) && reader.TryRead(out var line)
                    ? line
                    : null;
            }
            catch (OperationCanceledException)
            {
                readCanceled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class FakeStartedProcess : IStartedProcess
    {
        private readonly TaskCompletionSource _exitSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _waitCallCount;

        public List<CancellationToken> WaitTokens { get; } = [];

        public Queue<string> StandardOutputLines { get; } = new();

        public Queue<string> StandardErrorLines { get; } = new();

        public bool ExitsBeforeCancellationIsObserved { get; init; }

        public bool HasExited { get; private set; }

        public bool KillCalled { get; private set; }

        public bool KilledEntireProcessTree { get; private set; }

        public Exception? KillException { get; init; }

        public bool KeepAliveAfterKill { get; init; }

        public Exception? NonCancelableWaitException { get; init; }

        public Exception? InitialWaitException { get; init; }

        public Action? OnStart { get; init; }

        public bool StartResult { get; init; } = true;

        public bool WaitForExplicitExit { get; init; }

        public int StartCallCount { get; private set; }

        public int ExitCode => 0;

        public bool Start()
        {
            StartCallCount++;
            OnStart?.Invoke();
            return StartResult;
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
            _waitCallCount++;
            if (_waitCallCount == 1 && InitialWaitException is not null)
            {
                throw InitialWaitException;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                if (ExitsBeforeCancellationIsObserved)
                {
                    HasExited = true;
                }

                await Task.FromCanceled(cancellationToken);
            }

            if (!cancellationToken.CanBeCanceled && NonCancelableWaitException is not null)
            {
                throw NonCancelableWaitException;
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
            if (KillException is not null)
            {
                throw KillException;
            }

            HasExited = !KeepAliveAfterKill;
            if (NonCancelableWaitException is not null)
            {
                _exitSignal.TrySetException(NonCancelableWaitException);
            }
            else
            {
                _exitSignal.TrySetResult();
            }
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

}
