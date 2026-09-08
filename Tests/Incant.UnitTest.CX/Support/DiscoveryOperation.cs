namespace Incant.UnitTest.CX;

/// <summary>Owns a controlled discovery and waits for observable helper events without hiding early failures.</summary>
internal sealed class DiscoveryOperation<T> : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation;

    private readonly Task<T> _pending;

    private readonly Func<T, string> _describe;

    private bool _observed;

    internal DiscoveryOperation(Func<CancellationToken, Task<T>> start, Func<T, string> describe)
    {
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _describe = describe;
        try
        {
            _pending = start(_cancellation.Token);
        }
        catch
        {
            _cancellation.Dispose();
            throw;
        }
    }

    internal void Cancel() => _cancellation.Cancel();

    internal async Task<T> CompleteAsync()
    {
        try
        {
            return await _pending;
        }
        finally
        {
            _observed = true;
        }
    }

    internal async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            if (_pending.IsCompleted)
            {
                T result = await CompleteAsync();
                throw new InvalidOperationException(
                    "Discovery ended before the expected compiler event. " + _describe(result));
            }

            await Task.WhenAny(_pending, Task.Delay(10, deadline.Token));
            deadline.Token.ThrowIfCancellationRequested();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cancellation.Cancel();
            if (!_observed || !_pending.IsCompleted)
            {
                try
                {
                    await _pending;
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    // Discovery has reaped canceled child processes before fixture cleanup.
                }
            }
        }
        finally
        {
            _cancellation.Dispose();
        }
    }
}
