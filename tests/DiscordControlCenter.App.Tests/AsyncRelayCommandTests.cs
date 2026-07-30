using DiscordControlCenter.App.Mvvm;

namespace DiscordControlCenter.App.Tests;

public sealed class AsyncRelayCommandTests
{
    [Fact]
    public async Task ExecuteAsyncCompletesWhenCallbackDisposesCommand()
    {
        AsyncRelayCommand? command = null;
        command = new AsyncRelayCommand(
            async cancellationToken =>
            {
                await Task.Yield();
                command!.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            });

        await command.ExecuteAsync(null);

        Assert.False(command.IsRunning);
        Assert.False(command.CanExecute(null));
    }

    [Fact]
    public async Task ExecuteAsyncResetsRunningStateWhenCancelled()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(
            async cancellationToken =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });

        var execution = command.ExecuteAsync(null);
        await entered.Task;
        command.Cancel();
        await execution;

        Assert.False(command.IsRunning);
        Assert.True(command.CanExecute(null));
    }
}
