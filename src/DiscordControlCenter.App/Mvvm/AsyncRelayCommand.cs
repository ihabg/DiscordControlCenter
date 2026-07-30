using System.Windows.Input;

namespace DiscordControlCenter.App.Mvvm;

public sealed class AsyncRelayCommand(
    Func<CancellationToken, Task> execute,
    Func<bool>? canExecute = null,
    Action<Exception>? errorHandler = null) : ICommand, IDisposable
{
    private readonly object _syncRoot = new();
    private CancellationTokenSource? _cancellation;
    private int _isRunning;
    private bool _disposed;

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

    public bool CanExecute(object? parameter)
    {
        lock (_syncRoot)
        {
            return !_disposed
                && !IsRunning
                && (canExecute?.Invoke() ?? true);
        }
    }

    public async void Execute(object? parameter) =>
        await ExecuteAsync(parameter);

    internal async Task ExecuteAsync(object? parameter)
    {
        CancellationTokenSource cancellation;
        lock (_syncRoot)
        {
            if (_disposed
                || IsRunning
                || !(canExecute?.Invoke() ?? true))
            {
                return;
            }

            Interlocked.Exchange(ref _isRunning, 1);
            cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
        }

        NotifyCanExecuteChanged();
        try
        {
            await execute(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            errorHandler?.Invoke(exception);
        }
        finally
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_cancellation, cancellation))
                {
                    _cancellation = null;
                }

                Interlocked.Exchange(ref _isRunning, 0);
            }

            cancellation.Dispose();
            NotifyCanExecuteChanged();
        }
    }

    public void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_syncRoot)
        {
            cancellation = _cancellation;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The execution completed between capturing and cancelling the source.
        }
    }

    public void NotifyCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancellation = _cancellation;
            _cancellation = null;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The in-flight execution owns and may already have disposed the source.
        }

        NotifyCanExecuteChanged();
    }
}
