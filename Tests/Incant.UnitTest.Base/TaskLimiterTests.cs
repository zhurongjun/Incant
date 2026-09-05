using System.Collections.Concurrent;
using Incant.Base;

namespace Incant.UnitTest.Base;

public sealed class TaskLimiterTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ConstructorRejectsNonPositiveLimits(int maxConcurrency)
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new TaskLimiter(maxConcurrency));

        Assert.Equal(nameof(maxConcurrency), exception.ParamName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public async Task ConstructorPreservesTheLimitAndMakesSlotsAvailable(int maxConcurrency)
    {
        using var limiter = new TaskLimiter(maxConcurrency);
        using CancellationTokenSource timeout = CreateTimeout();

        Assert.Equal(maxConcurrency, limiter.MaxConcurrency);
        using TaskLease lease = await limiter.AcquireAsync(timeout.Token);
        Assert.InRange(lease.Id, 0, maxConcurrency - 1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(20)]
    public async Task AllSlotsCanBeReusedWithoutExceedingCapacity(int maxConcurrency)
    {
        using var limiter = new TaskLimiter(maxConcurrency);
        using CancellationTokenSource timeout = CreateTimeout();

        await AssertAllSlotsAvailableAsync(limiter, timeout.Token);
        await AssertAllSlotsAvailableAsync(limiter, timeout.Token);
        Assert.Equal(maxConcurrency, limiter.MaxConcurrency);
    }

    [Fact]
    public async Task ReturningOneLeaseMakesOnlyItsIdAvailable()
    {
        using var limiter = new TaskLimiter(20);
        using CancellationTokenSource timeout = CreateTimeout();
        var leases = new List<TaskLease>();
        Task<TaskLease>? waiting = null;
        try
        {
            for (int index = 0; index < limiter.MaxConcurrency; ++index)
            {
                leases.Add(await limiter.AcquireAsync(timeout.Token));
            }

            waiting = limiter.AcquireAsync(timeout.Token).AsTask();
            Assert.False(waiting.IsCompleted);
            TaskLease returned = leases[7];
            int id = returned.Id;
            returned.Dispose();

            using TaskLease replacement = await waiting.WaitAsync(timeout.Token);
            Assert.Equal(id, replacement.Id);
            Assert.Equal(id, returned.Id);
            returned.Dispose();
            await AssertBlockedAsync(limiter, timeout.Token);
        }
        finally
        {
            foreach (TaskLease lease in leases)
            {
                lease.Dispose();
            }

            if (waiting is not null)
            {
                await ReturnIfAcquiredAsync(waiting);
            }
        }
    }

    [Fact]
    public async Task LeaseRemainsHeldAcrossAwaitAndCanBeReturnedByAnotherTask()
    {
        using var limiter = new TaskLimiter(1);
        using CancellationTokenSource timeout = CreateTimeout();
        using TaskLease lease = await limiter.AcquireAsync(timeout.Token);
        Task<TaskLease> waiting = limiter.AcquireAsync(timeout.Token).AsTask();
        try
        {
            Assert.False(waiting.IsCompleted);
            await Task.Yield();
            Assert.False(waiting.IsCompleted);

            await Task.Run(lease.Dispose, timeout.Token);
            using TaskLease next = await waiting.WaitAsync(timeout.Token);
            await AssertBlockedAsync(limiter, timeout.Token);
        }
        finally
        {
            lease.Dispose();
            using TaskLease remaining = await waiting.WaitAsync(timeout.Token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsingReturnsTheLeaseWhenAnOperationThrows(bool canceled)
    {
        using var limiter = new TaskLimiter(1);
        using CancellationTokenSource timeout = CreateTimeout();
        Exception expected = canceled
            ? new OperationCanceledException("Operation canceled.")
            : new InvalidOperationException("Operation failed.");

        async Task RunAsync()
        {
            using TaskLease lease = await limiter.AcquireAsync(timeout.Token);
            await Task.Yield();
            throw expected;
        }

        Exception actual = await Assert.ThrowsAnyAsync<Exception>(RunAsync);

        Assert.Same(expected, actual);
        await AssertAllSlotsAvailableAsync(limiter, timeout.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedDisposalReturnsOnlyOneSlot(bool concurrent)
    {
        using var limiter = new TaskLimiter(1);
        using CancellationTokenSource timeout = CreateTimeout();
        using TaskLease lease = await limiter.AcquireAsync(timeout.Token);
        TaskLease alias = lease;

        if (concurrent)
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task[] releases = Enumerable.Range(0, 32)
                .Select(_ => Task.Run(async () =>
                {
                    await start.Task.WaitAsync(timeout.Token);
                    alias.Dispose();
                }, timeout.Token))
                .ToArray();
            start.SetResult();
            await Task.WhenAll(releases).WaitAsync(timeout.Token);
        }
        else
        {
            lease.Dispose();
            alias.Dispose();
        }

        await AssertAllSlotsAvailableAsync(limiter, timeout.Token);
        using TaskLease next = await limiter.AcquireAsync(timeout.Token);
        alias.Dispose();
        await AssertBlockedAsync(limiter, timeout.Token);
    }

    [Fact]
    public async Task DifferentLimitersHaveIndependentSlots()
    {
        using var compile = new TaskLimiter(1);
        using var download = new TaskLimiter(1);
        using CancellationTokenSource timeout = CreateTimeout();
        using TaskLease compileLease = await compile.AcquireAsync(timeout.Token);
        using TaskLease downloadLease = await download.AcquireAsync(timeout.Token);

        Assert.Equal(0, compileLease.Id);
        Assert.Equal(0, downloadLease.Id);
        await AssertBlockedAsync(compile, timeout.Token);
        await AssertBlockedAsync(download, timeout.Token);
        compileLease.Dispose();
        using TaskLease nextCompile = await compile.AcquireAsync(timeout.Token);
        await AssertBlockedAsync(download, timeout.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlreadyCanceledRequestsNeverTakeASlot(bool occupied)
    {
        using var limiter = new TaskLimiter(1);
        using CancellationTokenSource timeout = CreateTimeout();
        using var cancellation = new CancellationTokenSource();
        using TaskLease? held = occupied ? await limiter.AcquireAsync(timeout.Token) : null;
        cancellation.Cancel();

        await AssertCanceledAsync(limiter.AcquireAsync(cancellation.Token).AsTask(), cancellation.Token);

        held?.Dispose();
        await AssertAllSlotsAvailableAsync(limiter, timeout.Token);
    }

    [Fact]
    public async Task CancelingAQueuedRequestDoesNotCancelOtherRequests()
    {
        using var limiter = new TaskLimiter(1);
        using CancellationTokenSource timeout = CreateTimeout();
        using var cancellation = new CancellationTokenSource();
        using TaskLease held = await limiter.AcquireAsync(timeout.Token);
        Task<TaskLease> canceled = limiter.AcquireAsync(cancellation.Token).AsTask();
        Task<TaskLease> surviving = limiter.AcquireAsync(timeout.Token).AsTask();
        try
        {
            Assert.False(canceled.IsCompleted);
            Assert.False(surviving.IsCompleted);
            cancellation.Cancel();
            await AssertCanceledAsync(canceled, cancellation.Token);
            Assert.False(surviving.IsCompleted);

            held.Dispose();
            using TaskLease lease = await surviving.WaitAsync(timeout.Token);
            await AssertBlockedAsync(limiter, timeout.Token);
        }
        finally
        {
            cancellation.Cancel();
            held.Dispose();
            await ReturnIfAcquiredAsync(canceled);
            await ReturnIfAcquiredAsync(surviving);
        }
    }

    [Fact]
    public async Task CancellationAfterAcquisitionDoesNotRevokeTheLease()
    {
        using var limiter = new TaskLimiter(1);
        using CancellationTokenSource timeout = CreateTimeout();
        using var cancellation = new CancellationTokenSource();
        using TaskLease lease = await limiter.AcquireAsync(cancellation.Token);

        cancellation.Cancel();
        await AssertBlockedAsync(limiter, timeout.Token);
        lease.Dispose();
        await AssertAllSlotsAvailableAsync(limiter, timeout.Token);
    }

    [Fact]
    public async Task CancellationRacingWithReleaseDoesNotLeakOrDuplicateSlots()
    {
        using var limiter = new TaskLimiter(1);
        using CancellationTokenSource timeout = CreateTimeout();

        for (int iteration = 0; iteration < 100; ++iteration)
        {
            using var cancellation = new CancellationTokenSource();
            using TaskLease held = await limiter.AcquireAsync(timeout.Token);
            Task<TaskLease> waiting = limiter.AcquireAsync(cancellation.Token).AsTask();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task cancel = Task.Run(async () =>
            {
                await start.Task.WaitAsync(timeout.Token);
                cancellation.Cancel();
            }, timeout.Token);
            Task release = Task.Run(async () =>
            {
                await start.Task.WaitAsync(timeout.Token);
                held.Dispose();
            }, timeout.Token);
            start.SetResult();
            try
            {
                await Task.WhenAll(cancel, release).WaitAsync(timeout.Token);
                try
                {
                    using TaskLease lease = await waiting.WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException exception)
                {
                    Assert.Equal(cancellation.Token, exception.CancellationToken);
                }

                await AssertAllSlotsAvailableAsync(limiter, timeout.Token);
            }
            finally
            {
                cancellation.Cancel();
                held.Dispose();
                await Task.WhenAll(cancel, release);
                await ReturnIfAcquiredAsync(waiting);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(20)]
    public async Task ConcurrentTasksRespectTheLimitAndEventuallyAllFinish(int maxConcurrency)
    {
        const int TaskCount = 256;
        using var limiter = new TaskLimiter(maxConcurrency);
        using CancellationTokenSource timeout = CreateTimeout();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var filled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeIds = new ConcurrentDictionary<int, byte>();
        int active = 0;
        int entered = 0;
        int completed = 0;
        Task[] tasks = Enumerable.Range(0, TaskCount)
            .Select(taskIndex => Task.Run(async () =>
            {
                await start.Task.WaitAsync(timeout.Token);
                using TaskLease lease = await limiter.AcquireAsync(timeout.Token);
                bool added = activeIds.TryAdd(lease.Id, 0);
                int count = Interlocked.Increment(ref active);
                try
                {
                    Assert.InRange(lease.Id, 0, maxConcurrency - 1);
                    Assert.True(added, "Simultaneous leases must have different IDs.");
                    Assert.InRange(count, 1, maxConcurrency);
                    if (Interlocked.Increment(ref entered) == maxConcurrency)
                    {
                        filled.TrySetResult();
                    }

                    await release.Task.WaitAsync(timeout.Token);
                }
                finally
                {
                    if (added)
                    {
                        Assert.True(activeIds.TryRemove(lease.Id, out _));
                    }

                    Interlocked.Decrement(ref active);
                }

                Interlocked.Increment(ref completed);
            }, timeout.Token))
            .ToArray();
        start.SetResult();

        try
        {
            await filled.Task.WaitAsync(timeout.Token);
            Assert.Equal(maxConcurrency, Volatile.Read(ref active));
            Assert.Equal(maxConcurrency, Volatile.Read(ref entered));
            Assert.Equal(Enumerable.Range(0, maxConcurrency), activeIds.Keys.Order());
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(tasks).WaitAsync(timeout.Token);
        }

        Assert.Equal(0, active);
        Assert.Empty(activeIds);
        Assert.Equal(TaskCount, completed);
        await AssertAllSlotsAvailableAsync(limiter, timeout.Token);
    }

    [Fact]
    public async Task DisposedLimiterRejectsAcquisition()
    {
        using var limiter = new TaskLimiter(1);
        using CancellationTokenSource timeout = CreateTimeout();
        using (TaskLease lease = await limiter.AcquireAsync(timeout.Token))
        {
        }

        limiter.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => limiter.AcquireAsync(timeout.Token).AsTask());
    }

    private static async Task AssertAllSlotsAvailableAsync(TaskLimiter limiter, CancellationToken cancellationToken)
    {
        var leases = new List<TaskLease>();
        try
        {
            for (int index = 0; index < limiter.MaxConcurrency; ++index)
            {
                leases.Add(await limiter.AcquireAsync(cancellationToken));
            }

            Assert.Equal(Enumerable.Range(0, limiter.MaxConcurrency), leases.Select(lease => lease.Id).Order());
            await AssertBlockedAsync(limiter, cancellationToken);
        }
        finally
        {
            foreach (TaskLease lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    private static async Task AssertBlockedAsync(TaskLimiter limiter, CancellationToken cancellationToken)
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<TaskLease> waiting = limiter.AcquireAsync(cancellation.Token).AsTask();
        try
        {
            Assert.False(waiting.IsCompleted);
            cancellation.Cancel();
            await AssertCanceledAsync(waiting, cancellation.Token);
        }
        finally
        {
            cancellation.Cancel();
            await ReturnIfAcquiredAsync(waiting);
        }
    }

    private static async Task AssertCanceledAsync(Task task, CancellationToken cancellationToken)
    {
        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
        Assert.Equal(cancellationToken, exception.CancellationToken);
    }

    private static async Task ReturnIfAcquiredAsync(Task<TaskLease> acquisition)
    {
        try
        {
            using TaskLease lease = await acquisition.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
        }
        catch (OperationCanceledException) when (acquisition.IsCanceled)
        {
            // Cleanup accepts canceled requests; the test body verifies their expected outcome.
        }
    }

    private static CancellationTokenSource CreateTimeout()
    {
        CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        // This is only a deadlock guard, not a performance assertion.
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        return timeout;
    }
}
