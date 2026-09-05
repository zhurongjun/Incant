namespace Incant.Base;

/// <summary>Limits concurrent operations through asynchronously acquired leases.</summary>
/// <remarks>
/// Each instance has an independent, fixed capacity and does not schedule or track tasks.
/// Acquisition and lease disposal are thread-safe, but limiter disposal requires all operations to have ended.
/// </remarks>
public sealed class TaskLimiter : IDisposable
{
    /// <summary>Creates a limiter with all concurrency slots available.</summary>
    /// <param name="maxConcurrency">The maximum number of simultaneously held leases.</param>
    /// <exception cref="ArgumentOutOfRangeException">The maximum concurrency is not positive.</exception>
    public TaskLimiter(int maxConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        MaxConcurrency = maxConcurrency;
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    /// <summary>Gets the fixed maximum number of simultaneously held leases.</summary>
    public int MaxConcurrency { get; }

    /// <summary>Asynchronously waits for one concurrency slot and returns its lease.</summary>
    /// <param name="cancellationToken">Cancels waiting, but does not revoke an acquired lease.</param>
    /// <returns>A lease with a reusable slot ID that the caller must dispose when the limited operation ends.</returns>
    /// <exception cref="OperationCanceledException">Waiting was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The limiter has been disposed.</exception>
    /// <remarks>
    /// No FIFO ordering is guaranteed. Leases may span awaits and may be disposed on another thread.
    /// Lease only the limited operation: holding a lease while awaiting work that needs another lease from
    /// the same limiter can deadlock. Cancellation of the operation itself remains the caller's responsibility.
    /// </remarks>
    public async ValueTask<TaskLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            // Allocate IDs on demand rather than allocating storage for the entire configured capacity.
            int id = _availableIds.Count > 0 ? _availableIds.Pop() : _nextId++;
            return new TaskLease(this, id);
        }
    }

    /// <summary>Releases the limiter's resources without canceling or waiting for operations.</summary>
    /// <remarks>
    /// The caller must finish all acquisitions and return all leases before disposal.
    /// This method must not run concurrently with acquisition or lease disposal.
    /// </remarks>
    public void Dispose() => _semaphore.Dispose();

    internal void Release(int id)
    {
        lock (_gate)
        {
            _availableIds.Push(id);
        }

        // Publish the returned ID before making a slot available to another waiter.
        _semaphore.Release();
    }

    private readonly SemaphoreSlim _semaphore;

    private readonly Lock _gate = new();

    private readonly Stack<int> _availableIds = new();

    private int _nextId;
}

/// <summary>Returns one acquired concurrency slot when disposed.</summary>
/// <remarks>
/// References share one acquisition. Repeated or concurrent disposal returns the slot only once.
/// Use a using scope to return the slot even when the operation throws.
/// </remarks>
public sealed class TaskLease : IDisposable
{
    internal TaskLease(TaskLimiter limiter, int id)
    {
        _limiter = limiter;
        Id = id;
    }

    /// <summary>Gets this slot's reusable ID, from zero through the owning limiter's MaxConcurrency minus one.</summary>
    /// <remarks>
    /// IDs are unique among held leases from the same limiter, not across different limiters.
    /// The value stays unchanged after disposal, but another caller may then acquire the same slot.
    /// </remarks>
    public int Id { get; }

    /// <summary>Returns this lease's slot, or does nothing if it has already been returned.</summary>
    public void Dispose() => Interlocked.Exchange(ref _limiter, null)?.Release(Id);

    private TaskLimiter? _limiter;
}
