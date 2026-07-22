using CodexAccountSwitcher.Services;
using CodexAccountSwitcher.Views;

namespace CodexAccountSwitcher.Tests;

public sealed class DialogOperationRunnerTests
{
    [Fact]
    public async Task Login_shows_before_start_and_awaits_each_output_line_before_completion()
    {
        var events = new List<string>();
        var lineAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowLineAcceptance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runTask = DialogOperationRunner.RunLoginAsync(
            activityTracker: new ActiveOperationTracker(),
            showAsync: () =>
            {
                events.Add("shown");
                return Task.CompletedTask;
            },
            appendAsync: async (line, cancellationToken) =>
            {
                events.Add($"line:{line.Text}");
                lineAccepted.SetResult();
                await allowLineAcceptance.Task.WaitAsync(cancellationToken);
            },
            completeAsync: result =>
            {
                events.Add($"complete:{result.ExitCode}");
                return Task.CompletedTask;
            },
            failAsync: _ => Task.CompletedTask,
            operation: async (outputHandler, cancellationToken) =>
            {
                events.Add("started");
                await outputHandler(
                    new ProcessOutputLine(ProcessOutputStream.StandardOutput, "device-code"),
                    cancellationToken);
                events.Add("exited");
                return new CommandResult(0, string.Empty, string.Empty);
            },
            cancellationToken: CancellationToken.None);

        await lineAccepted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["shown", "started", "line:device-code"], events);
        Assert.False(runTask.IsCompleted);

        allowLineAcceptance.SetResult();
        var result = await runTask;

        Assert.True(result.Succeeded);
        Assert.Equal(
            ["shown", "started", "line:device-code", "exited", "complete:0"],
            events);
    }

    [Fact]
    public async Task Login_reports_and_propagates_output_callback_failure()
    {
        var failure = new InvalidOperationException("dispatcher rejected output");
        Exception? reported = null;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DialogOperationRunner.RunLoginAsync(
                activityTracker: new ActiveOperationTracker(),
                showAsync: () => Task.CompletedTask,
                appendAsync: (_, _) => ValueTask.FromException(failure),
                completeAsync: _ => Task.CompletedTask,
                failAsync: error =>
                {
                    reported = error;
                    return Task.CompletedTask;
                },
                operation: async (outputHandler, cancellationToken) =>
                {
                    await outputHandler(
                        new ProcessOutputLine(ProcessOutputStream.StandardError, "failure"),
                        cancellationToken);
                    return new CommandResult(0, string.Empty, string.Empty);
                },
                cancellationToken: CancellationToken.None));

        Assert.Same(failure, exception);
        Assert.Same(failure, reported);
    }

    [Fact]
    public async Task Removal_shows_before_visible_picker_and_reports_final_result()
    {
        var events = new List<string>();

        var result = await DialogOperationRunner.RunRemoveAsync(
            activityTracker: new ActiveOperationTracker(),
            showAsync: () =>
            {
                events.Add("shown");
                return Task.CompletedTask;
            },
            completeAsync: commandResult =>
            {
                events.Add($"complete:{commandResult.ExitCode}");
                return Task.CompletedTask;
            },
            failAsync: _ => Task.CompletedTask,
            operation: _ =>
            {
                events.Add("picker");
                return Task.FromResult(new CommandResult(7, string.Empty, "remove failed"));
            },
            cancellationToken: CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal(["shown", "picker", "complete:7"], events);
    }

    [Fact]
    public async Task Operation_waits_for_first_render_barrier_before_starting_child()
    {
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationStarted = false;

        var runTask = DialogOperationRunner.RunRemoveAsync(
            activityTracker: new ActiveOperationTracker(),
            showAsync: () => rendered.Task,
            completeAsync: _ => Task.CompletedTask,
            failAsync: _ => Task.CompletedTask,
            operation: _ =>
            {
                operationStarted = true;
                return Task.FromResult(new CommandResult(0, string.Empty, string.Empty));
            },
            cancellationToken: CancellationToken.None);

        Assert.False(operationStarted);
        Assert.False(runTask.IsCompleted);

        rendered.SetResult();
        await runTask;

        Assert.True(operationStarted);
    }

    [Fact]
    public async Task Exit_is_rejected_without_disposal_while_dialog_operation_owns_activity()
    {
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracker = new ActiveOperationTracker();
        var events = new List<string>();
        var exit = new ApplicationExitCoordinator(
            tracker,
            rejected: () => events.Add("rejected"),
            disposeTray: () => events.Add("disposed"),
            closeWindow: () => events.Add("closed"),
            shutdown: () => events.Add("shutdown"));

        var runTask = DialogOperationRunner.RunRemoveAsync(
            activityTracker: tracker,
            showAsync: () => Task.CompletedTask,
            completeAsync: _ => Task.CompletedTask,
            failAsync: _ => Task.CompletedTask,
            operation: async _ =>
            {
                operationStarted.SetResult();
                await releaseOperation.Task;
                return new CommandResult(0, string.Empty, string.Empty);
            },
            cancellationToken: CancellationToken.None);

        await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(exit.TryExit());
        Assert.Equal(["rejected"], events);

        releaseOperation.SetResult();
        await runTask;

        Assert.True(exit.TryExit());
        Assert.Equal(["rejected", "disposed", "closed", "shutdown"], events);
    }
}
