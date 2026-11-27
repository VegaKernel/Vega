using System.Buffers;
using System.Collections.Concurrent;
using NUnit.Framework;
using Terraria.Net.Sockets;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class NativeOutboundQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [TestCase(false)]
    [TestCase(true)]
    public async Task BorrowedDeferredWakeCopiesBeforeReturnAndFlushesInOrder(bool normalSendReleasesWake)
    {
        var pool = new TrackingPool();
        var queue = new NativeOutboundQueue(8, 128, 16, pool, maxBatchRequests: 4);
        var received = new List<byte>();
        var writer = queue.RunWriterAsync((bytes, _) =>
        {
            received.AddRange(bytes.ToArray());
            return ValueTask.FromResult(bytes.Length);
        });
        try
        {
            Assert.That(SpinWait.SpinUntil(() => queue.IsWriterWaiting, Timeout), Is.True);
            byte[] reusable = [1, 2, 3];
            Assert.That(queue.TryEnqueue(reusable.AsSpan(), 7, deferWake: true), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
            Array.Fill(reusable, (byte)99);
            Assert.That(queue.Snapshot.PendingRequests, Is.EqualTo(1));
            Assert.That(queue.IsWriterWaiting, Is.True);
            Assert.That(queue.TryEnqueue(new byte[] { 4, 5 }.AsSpan(), 7, deferWake: !normalSendReleasesWake), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
            if (!normalSendReleasesWake) queue.FlushDeferredWake();
            queue.Complete();
            Assert.That((await writer.WaitAsync(Timeout)).Status, Is.EqualTo(NativeOutboundWriterStatus.Completed));
            Assert.That(received, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
            Assert.That(queue.TryReadCompletion(out var first) && first.Sequence == 1, Is.True);
            Assert.That(queue.TryReadCompletion(out var second) && second.Sequence == 2, Is.True);
            Assert.That(pool.Retained, Is.Zero);
        }
        finally { queue.Abort(); await writer.WaitAsync(Timeout); }
    }

    [Test]
    public async Task AbortRetiresDeferredRequestsAndReleasesSleepingWriter()
    {
        var pool = new TrackingPool();
        var queue = new NativeOutboundQueue(8, 128, 16, pool);
        var writer = queue.RunWriterAsync((_, _) => throw new InvalidOperationException("Deferred request must not write."));
        try
        {
            Assert.That(SpinWait.SpinUntil(() => queue.IsWriterWaiting, Timeout), Is.True);
            Assert.That(queue.TryEnqueue(new byte[] { 1 }.AsSpan(), 7, deferWake: true), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
            queue.Abort();
            Assert.That((await writer.WaitAsync(Timeout)).Status, Is.EqualTo(NativeOutboundWriterStatus.Aborted));
            Assert.That(queue.TryReadCompletion(out _), Is.False);
            Assert.That(pool.Retained, Is.Zero);
        }
        finally { queue.Abort(); await writer.WaitAsync(Timeout); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task QueueAgeTracksPendingWriteAndUndeliveredCompletionOnlyWhenEnabled(bool measureAge)
    {
        var queue = new NativeOutboundQueue(4, 64, 16, new TrackingPool(), measureAge: measureAge);
        Assert.That(queue.ReadAge(), Is.EqualTo(new NativeOutboundQueueAge(TimeSpan.Zero, TimeSpan.Zero)));
        Assert.That(queue.TryEnqueue([1, 2, 3], 0, 3, 1), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
        await Task.Delay(5);
        var pending = queue.ReadAge();
        Assert.That(pending.Send > TimeSpan.Zero, Is.EqualTo(measureAge));
        Assert.That(pending.Completion, Is.EqualTo(TimeSpan.Zero));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Complete();
        var writer = queue.RunWriterAsync(async (bytes, _) =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return bytes.Length;
        });
        try
        {
            await entered.Task.WaitAsync(Timeout);
            var inFlight = queue.ReadAge();
            Assert.That(inFlight.Send, Is.GreaterThanOrEqualTo(pending.Send));
            Assert.That(inFlight.Completion, Is.EqualTo(TimeSpan.Zero));
            release.TrySetResult();
            Assert.That((await writer.WaitAsync(Timeout)).Status, Is.EqualTo(NativeOutboundWriterStatus.Completed));
            await Task.Delay(5);
            var completed = queue.ReadAge();
            Assert.That(completed.Send, Is.EqualTo(TimeSpan.Zero));
            Assert.That(completed.Completion > TimeSpan.Zero, Is.EqualTo(measureAge));
            Assert.That(queue.TryReadCompletion(out var completion), Is.True);
            Assert.That(completion.EnqueuedAt > 0, Is.EqualTo(measureAge));
            Assert.That(completion.WrittenAt >= completion.EnqueuedAt, Is.True);
            Assert.That(completion.WrittenAt > 0, Is.EqualTo(measureAge));
            Assert.That(queue.ReadAge(), Is.EqualTo(new NativeOutboundQueueAge(TimeSpan.Zero, TimeSpan.Zero)));
        }
        finally
        {
            release.TrySetResult();
            queue.Abort();
            await writer.WaitAsync(Timeout);
        }
    }

    [Test]
    public async Task OwnedSlicesAndCompletionMetadataKeepOrderAcrossPartialWrites()
    {
        var pool = new TrackingPool();
        var queue = new NativeOutboundQueue(4, 64, 16, pool);
        byte[] first = [0, 1, 2, 3, 0];
        byte[] second = [4, 5, 6];
        object firstState = new(), secondState = new();
        int invoked = 0;
        SocketSendCallback callback = _ => Interlocked.Increment(ref invoked);
        Assert.That(queue.TryEnqueue(first, 1, 3, 7, callback, firstState), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
        Assert.That(queue.TryEnqueue(second, 0, 3, 7, callback, secondState), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
        Array.Fill(first, (byte)99);
        Array.Fill(second, (byte)99);
        queue.Complete();

        var received = new List<byte>();
        var result = await queue.RunWriterAsync((bytes, _) =>
        {
            int count = Math.Min(2, bytes.Length);
            received.AddRange(bytes[..count].ToArray());
            return ValueTask.FromResult(count);
        }).WaitAsync(Timeout);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(new NativeOutboundWriterResult(NativeOutboundWriterStatus.Completed, 2, 6)));
            Assert.That(received, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6 }));
            Assert.That(pool.Retained, Is.Zero);
            Assert.That(queue.Snapshot, Is.EqualTo(new NativeOutboundQueueSnapshot(0, 2, 0, 2, false, false)));
            Assert.That(invoked, Is.Zero, "Writer must not invoke the native callback.");
        });
        Assert.That(queue.TryReadCompletion(out NativeSendCompletion firstCompletion), Is.True);
        Assert.That(queue.TryReadCompletion(out NativeSendCompletion secondCompletion), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(firstCompletion, Is.EqualTo(new NativeSendCompletion(1, 7, 3, callback, firstState)));
            Assert.That(secondCompletion, Is.EqualTo(new NativeSendCompletion(2, 7, 3, callback, secondState)));
            Assert.That(queue.Snapshot.OutstandingRequests, Is.Zero);
            Assert.That(queue.TryReadCompletion(out _), Is.False);
        });
    }

    [Test]
    public async Task ByteLimitIncludesActualRentalAndActiveWriteUntilIoReturns()
    {
        var pool = new TrackingPool(padding: 4);
        var queue = new NativeOutboundQueue(4, 20, 8, pool);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] data = [1, 2, 3, 4];
        Assert.That(queue.TryEnqueue(data, 0, 4, 1), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
        var writer = queue.RunWriterAsync(async (bytes, _) =>
        {
            entered.TrySetResult();
            // Model a real asynchronous operation which has not relinquished its memory yet.
            await releaseIo.Task.ConfigureAwait(false);
            return bytes.Length;
        });
        try
        {
            await entered.Task.WaitAsync(Timeout);
            Assert.That(queue.TryEnqueue(data, 0, 4, 1), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
            Assert.That(queue.Snapshot.RetainedBytes, Is.EqualTo(16));
            // Four logical bytes fit, but the actual eight-byte rental does not.
            Assert.That(queue.TryEnqueue(data, 0, 4, 1), Is.EqualTo(NativeOutboundEnqueueResult.ByteLimit));
            Assert.That(queue.TryEnqueue(data, 0, 4, 1), Is.EqualTo(NativeOutboundEnqueueResult.Closed));
            Assert.Multiple(() =>
            {
                Assert.That(queue.Snapshot, Is.EqualTo(new NativeOutboundQueueSnapshot(0, 1, 8, 0, false, true)));
                Assert.That(pool.Retained, Is.EqualTo(1), "Do not return the active I/O buffer prematurely.");
            });
        }
        finally { releaseIo.TrySetResult(); }
        Assert.That((await writer.WaitAsync(Timeout)).Status, Is.EqualTo(NativeOutboundWriterStatus.Aborted));
        Assert.That(queue.Snapshot, Is.EqualTo(new NativeOutboundQueueSnapshot(0, 0, 0, 0, false, true)));
        Assert.That(pool.Retained, Is.Zero);
    }

    [Test]
    public async Task CompletedButUndeliveredMetadataStillConsumesEntryCredit()
    {
        var pool = new TrackingPool();
        var queue = new NativeOutboundQueue(1, 16, 16, pool);
        Assert.That(queue.TryEnqueue([1], 0, 1, 3, _ => Assert.Fail("Native callback invoked from writer.")), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
        var writer = queue.RunWriterAsync((bytes, _) => ValueTask.FromResult(bytes.Length));
        try
        {
            using var timeout = new CancellationTokenSource(Timeout);
            while (queue.Snapshot.PendingCompletions == 0)
                await Task.Delay(1, timeout.Token);
            Assert.That(queue.Snapshot.RetainedBytes, Is.Zero);
            Assert.That(queue.TryEnqueue([2], 0, 1, 3), Is.EqualTo(NativeOutboundEnqueueResult.EntryLimit));
        }
        finally { queue.Abort(); }
        Assert.That((await writer.WaitAsync(Timeout)).Status, Is.EqualTo(NativeOutboundWriterStatus.Aborted));
        Assert.That(queue.TryReadCompletion(out _), Is.False);
        Assert.That(queue.Snapshot.OutstandingRequests, Is.Zero);
        Assert.That(pool.Retained, Is.Zero);
    }

    [Test]
    public void SharedBudgetChargesActualPoolCapacityWithoutRetiringOtherQueues()
    {
        var pool = new TrackingPool(padding: 4);
        var budget = new NativeOutboundBudget(4, 12);
        var first = new NativeOutboundQueue(2, 12, 8, pool, budget);
        var second = new NativeOutboundQueue(2, 12, 8, pool, budget);
        try
        {
            Assert.That(first.TryEnqueue([1, 2, 3, 4], 0, 4, 1), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
            Assert.That(second.TryEnqueue([5, 6, 7, 8], 0, 4, 2), Is.EqualTo(NativeOutboundEnqueueResult.ByteLimit));
            Assert.That(second.Snapshot.IsAborted, Is.True);
            Assert.That(first.Snapshot.IsAccepting, Is.True);
            Assert.That(budget.Snapshot, Is.EqualTo((1, 8L)));
        }
        finally { first.Abort(); second.Abort(); }
        Assert.That(budget.Snapshot, Is.EqualTo((0, 0L)));
        Assert.That(pool.Retained, Is.Zero);
    }

    [Test]
    public void SharedBudgetConcurrentReservationsNeverExceedCapsOrLeakCredits()
    {
        const int entryCap = 7, byteCap = 96;
        var budget = new NativeOutboundBudget(entryCap, byteCap);
        int accepted = 0;
        int invalid = 0;
        Parallel.For(0, 16, worker =>
        {
            int requestBytes = 8 + worker % 4 * 8;
            for (int i = 0; i < 2000; i++)
            {
                if (budget.TryReserve(requestBytes) == NativeOutboundEnqueueResult.Accepted)
                {
                    Interlocked.Increment(ref accepted);
                    bool merged = budget.TryReserveBytes(16);
                    var (entries, bytes) = budget.Snapshot;
                    if (entries is < 0 or > entryCap || bytes is < 0 or > byteCap)
                        Interlocked.Increment(ref invalid);
                    if (merged) budget.ReleaseBytes(16);
                    budget.ReleaseBytes(requestBytes);
                    budget.ReleaseEntries(1);
                }
            }
        });
        Assert.That(accepted, Is.GreaterThan(0));
        Assert.That(invalid, Is.Zero);
        Assert.That(budget.Snapshot, Is.EqualTo((0, 0L)));
    }

    [Test]
    public async Task FaultAfterPartialWriteRetiresAllUncompletedRequests()
    {
        var pool = new TrackingPool();
        var queue = new NativeOutboundQueue(4, 32, 8, pool);
        Assert.That(queue.TryEnqueue([1, 2], 0, 2, 1), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
        Assert.That(queue.TryEnqueue([3, 4], 0, 2, 1), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
        int writes = 0;
        var result = await queue.RunWriterAsync((_, _) => ++writes == 1
            ? ValueTask.FromResult(1)
            : ValueTask.FromException<int>(new IOException("controlled local sink failure"))).WaitAsync(Timeout);
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(NativeOutboundWriterStatus.Faulted));
            Assert.That(result.Error, Is.TypeOf<IOException>());
            Assert.That(result.WrittenBytes, Is.EqualTo(1));
            Assert.That(result.SentRequests, Is.Zero);
            Assert.That(queue.Snapshot, Is.EqualTo(new NativeOutboundQueueSnapshot(0, 0, 0, 0, false, true)));
            Assert.That(pool.Retained, Is.Zero);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExternalCancellationReleasesActiveAndPendingBuffers(bool singleCancellationWake)
    {
        var pool = new TrackingPool();
        var queue = new NativeOutboundQueue(4, 32, 8, pool, singleCancellationWake: singleCancellationWake);
        Assert.That(queue.TryEnqueue([1, 2], 0, 2, 1), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
        Assert.That(queue.TryEnqueue([3, 4], 0, 2, 1), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
        using var stopping = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = queue.RunWriterAsync(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return 0;
        }, stopping.Token);
        try { await entered.Task.WaitAsync(Timeout); }
        finally { await stopping.CancelAsync(); }
        Assert.That((await writer.WaitAsync(Timeout)).Status, Is.EqualTo(NativeOutboundWriterStatus.Cancelled));
        Assert.That(queue.Snapshot, Is.EqualTo(new NativeOutboundQueueSnapshot(0, 0, 0, 0, false, true)));
        Assert.That(pool.Retained, Is.Zero);
    }

    [Test]
    public async Task ConcurrentProducersPublishOneFifoSequence()
    {
        var queue = new NativeOutboundQueue(64, 4096, 1);
        Parallel.For(0, 32, i =>
        {
            Assert.That(queue.TryEnqueue([(byte)i], 0, 1, 3, callbackState: i), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
        });
        queue.Complete();
        var received = new List<byte>();
        Assert.That((await queue.RunWriterAsync((bytes, _) =>
        {
            received.Add(bytes.Span[0]);
            return ValueTask.FromResult(1);
        }).WaitAsync(Timeout)).Status, Is.EqualTo(NativeOutboundWriterStatus.Completed));
        for (int i = 0; i < received.Count; i++)
        {
            Assert.That(queue.TryReadCompletion(out NativeSendCompletion completion), Is.True);
            Assert.That(completion.Sequence, Is.EqualTo(i + 1));
            Assert.That(completion.State, Is.EqualTo((int)received[i]));
        }
        Assert.That(received, Has.Count.EqualTo(32));
        Assert.That(queue.Snapshot.OutstandingRequests, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AdjacentBatchKeepsPerRequestMetadataAndMemoryAccountingAcrossPartialWrites(bool edgeTriggeredWake)
    {
        var pool = new TrackingPool(padding: 4);
        var budget = new NativeOutboundBudget(3, 64);
        var queue = new NativeOutboundQueue(3, 64, 16, pool, budget, maxBatchRequests: 3, edgeTriggeredWake: edgeTriggeredWake);
        for (int i = 0; i < 3; i++)
            Assert.That(queue.TryEnqueue([(byte)(i * 2), (byte)(i * 2 + 1)], 0, 2, 8, callbackState: i), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
        queue.Complete();
        var received = new List<byte>();
        var lengths = new List<int>();
        var result = await queue.RunWriterAsync((bytes, _) =>
        {
            Assert.That(queue.Snapshot.RetainedBytes, Is.EqualTo(10));
            Assert.That(budget.Snapshot, Is.EqualTo((3, 10L)));
            Assert.That(pool.Retained, Is.EqualTo(1));
            lengths.Add(bytes.Length);
            int count = Math.Min(4, bytes.Length);
            received.AddRange(bytes[..count].ToArray());
            return ValueTask.FromResult(count);
        }).WaitAsync(Timeout);
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(new NativeOutboundWriterResult(NativeOutboundWriterStatus.Completed, 3, 6)));
            Assert.That(lengths, Is.EqualTo(new[] { 6, 2 }));
            Assert.That(received, Is.EqualTo(new byte[] { 0, 1, 2, 3, 4, 5 }));
            Assert.That(pool.Retained, Is.Zero);
            Assert.That(budget.Snapshot, Is.EqualTo((3, 0L)));
        });
        for (int i = 0; i < 3; i++)
        {
            Assert.That(queue.TryReadCompletion(out NativeSendCompletion completion), Is.True);
            Assert.That(completion, Is.EqualTo(new NativeSendCompletion(i + 1, 8, 2, null, i)));
        }
        Assert.That(queue.TryReadCompletion(out _), Is.False);
        Assert.That(budget.Snapshot, Is.EqualTo((0, 0L)));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task CoalescingFallsBackWithoutAbortingWhenActualRentalWouldExceedCap(bool connectionCap)
    {
        var pool = new TrackingPool(padding: 4);
        // Two original 8-byte rentals fit. The proposed 12-byte merged rental does not fit in the remaining 10 bytes.
        var budget = new NativeOutboundBudget(2, connectionCap ? 64 : 26);
        var queue = new NativeOutboundQueue(2, connectionCap ? 26 : 64, 16, pool, budget, maxBatchRequests: 2);
        Assert.That(queue.TryEnqueue([1, 2, 3, 4], 0, 4, 1), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
        Assert.That(queue.TryEnqueue([5, 6, 7, 8], 0, 4, 1), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
        queue.Complete();
        var writes = new List<byte[]>();
        var result = await queue.RunWriterAsync((bytes, _) =>
        {
            writes.Add(bytes.ToArray());
            return ValueTask.FromResult(bytes.Length);
        }).WaitAsync(Timeout);
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(new NativeOutboundWriterResult(NativeOutboundWriterStatus.Completed, 2, 8)));
            Assert.That(writes, Is.EqualTo(new[] { new byte[] { 1, 2, 3, 4 }, new byte[] { 5, 6, 7, 8 } }));
            Assert.That(pool.Retained, Is.Zero);
            Assert.That(budget.Snapshot, Is.EqualTo((2, 0L)));
        });
        Assert.That(queue.TryReadCompletion(out _), Is.True);
        Assert.That(queue.TryReadCompletion(out _), Is.True);
        Assert.That(budget.Snapshot, Is.EqualTo((0, 0L)));
    }

    [Test]
    public async Task PartialBatchFaultRetiresEveryUndeliverableCompletionAndReturnsRental()
    {
        var pool = new TrackingPool();
        var budget = new NativeOutboundBudget(3, 64);
        var queue = new NativeOutboundQueue(3, 64, 16, pool, budget, maxBatchRequests: 3);
        for (int i = 0; i < 3; i++)
            Assert.That(queue.TryEnqueue([1, 2], 0, 2, 1), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
        int calls = 0;
        var result = await queue.RunWriterAsync((_, _) => ++calls == 1
            ? ValueTask.FromResult(3)
            : ValueTask.FromException<int>(new IOException("controlled batch write fault"))).WaitAsync(Timeout);
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(NativeOutboundWriterStatus.Faulted));
            Assert.That(result.WrittenBytes, Is.EqualTo(3));
            Assert.That(result.SentRequests, Is.EqualTo(1), "One complete original request reached the byte sink.");
            Assert.That(queue.Snapshot.IsAborted, Is.True);
            Assert.That(queue.TryReadCompletion(out _), Is.False, "Do not publish callbacks for a retired connection.");
            Assert.That(pool.Retained, Is.Zero);
            Assert.That(budget.Snapshot, Is.EqualTo((0, 0L)));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AbortKeepsMergedRentalUntilActiveIoReturns(bool edgeTriggeredWake)
    {
        var pool = new TrackingPool();
        var budget = new NativeOutboundBudget(3, 64);
        var queue = new NativeOutboundQueue(3, 64, 16, pool, budget, maxBatchRequests: 2, edgeTriggeredWake: edgeTriggeredWake);
        for (int i = 0; i < 3; i++)
            Assert.That(queue.TryEnqueue([1, 2], 0, 2, 1), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = queue.RunWriterAsync(async (bytes, _) => { entered.TrySetResult(); await release.Task; return bytes.Length; });
        try
        {
            await entered.Task.WaitAsync(Timeout);
            queue.Abort();
            Assert.Multiple(() =>
            {
                Assert.That(pool.Retained, Is.EqualTo(1));
                Assert.That(queue.Snapshot, Is.EqualTo(new NativeOutboundQueueSnapshot(0, 2, 4, 0, false, true)));
                Assert.That(budget.Snapshot, Is.EqualTo((2, 4L)));
            });
        }
        finally { release.TrySetResult(); }
        Assert.That((await writer.WaitAsync(Timeout)).Status, Is.EqualTo(NativeOutboundWriterStatus.Aborted));
        Assert.That(pool.Retained, Is.Zero);
        Assert.That(budget.Snapshot, Is.EqualTo((0, 0L)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task EmptyCompletionProbePreservesFifoAndCreditAcrossIdleAndAbort(bool skipEmptyCompletionGate)
    {
        var budget = new NativeOutboundBudget(16, 4096);
        var queue = new NativeOutboundQueue(16, 4096, 8, sharedBudget: budget, maxBatchRequests: 4,
            skipEmptyCompletionGate: skipEmptyCompletionGate);
        using var deadline = new CancellationTokenSource(Timeout);
        var writer = queue.RunWriterAsync((bytes, _) => ValueTask.FromResult(bytes.Length));
        try
        {
            int sequence = 0;
            for (int round = 0; round < 8; round++)
            {
                Assert.That(queue.TryReadCompletion(out _), Is.False);
                for (int i = 0; i < 4; i++)
                    Assert.That(queue.TryEnqueue([1], 0, 1, 123), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
                for (int i = 0; i < 4; i++)
                {
                    NativeSendCompletion completion;
                    while (!queue.TryReadCompletion(out completion)) await Task.Delay(1, deadline.Token);
                    Assert.That(completion.Sequence, Is.EqualTo(++sequence));
                    Assert.That(completion.Generation, Is.EqualTo(123));
                }
                Assert.That(budget.Snapshot, Is.EqualTo((0, 0L)));
            }

            Assert.That(queue.TryEnqueue([1], 0, 1, 123), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
            while (queue.Snapshot.PendingCompletions == 0) await Task.Delay(1, deadline.Token);
            queue.Abort();
            await writer.WaitAsync(deadline.Token);
            Assert.That(queue.TryReadCompletion(out _), Is.False);
            Assert.That(budget.Snapshot, Is.EqualTo((0, 0L)));
        }
        finally { queue.Abort(); }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task WakeModeDoesNotLosePublicationsDuringIdleAndConcurrentDrain(bool edgeTriggeredWake, bool singleCancellationWake)
    {
        const int rounds = 12, requestsPerRound = 32;
        var queue = new NativeOutboundQueue(64, 4096, 1, edgeTriggeredWake: edgeTriggeredWake, singleCancellationWake: singleCancellationWake);
        var received = new ConcurrentQueue<byte>();
        var writer = queue.RunWriterAsync((bytes, _) => { received.Enqueue(bytes.Span[0]); return ValueTask.FromResult(1); });
        using var deadline = new CancellationTokenSource(Timeout);
        int sequence = 0;
        try
        {
            for (int round = 0; round < rounds; round++)
            {
                // Alternate idle waits with concurrent publications while the same writer drains.
                await Task.Delay(1, deadline.Token);
                Parallel.For(0, requestsPerRound, i =>
                {
                    Assert.That(queue.TryEnqueue([(byte)i], 0, 1, 1, callbackState: i), Is.EqualTo(NativeOutboundEnqueueResult.Accepted));
                });
                for (int i = 0; i < requestsPerRound; i++)
                {
                    NativeSendCompletion completion;
                    while (!queue.TryReadCompletion(out completion)) await Task.Delay(1, deadline.Token);
                    Assert.That(completion.Sequence, Is.EqualTo(++sequence));
                    Assert.That(received.TryDequeue(out byte actual), Is.True);
                    Assert.That(completion.State, Is.EqualTo((int)actual));
                }
            }
            await Task.Delay(1, deadline.Token);
            queue.Complete(); // Complete must also wake an otherwise idle writer.
            Assert.That(await writer.WaitAsync(deadline.Token), Is.EqualTo(new NativeOutboundWriterResult(
                NativeOutboundWriterStatus.Completed, rounds * requestsPerRound, rounds * requestsPerRound)));
            Assert.That(queue.Snapshot.OutstandingRequests, Is.Zero);
        }
        finally { queue.Abort(); }
    }

    [TestCase(false, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, false, false)]
    [TestCase(true, true, false)]
    [TestCase(false, true, true)]
    [TestCase(true, true, true)]
    public async Task CancellationWakesAnIdleWriter(bool edgeTriggeredWake, bool singleCancellationWake, bool cancelBeforeStart)
    {
        var queue = new NativeOutboundQueue(2, 32, 8, edgeTriggeredWake: edgeTriggeredWake, singleCancellationWake: singleCancellationWake);
        using var cancellation = new CancellationTokenSource();
        if (cancelBeforeStart) await cancellation.CancelAsync();
        var writer = queue.RunWriterAsync((_, _) => throw new InvalidOperationException("No writes were published."), cancellation.Token);
        if (!cancelBeforeStart)
        {
            await Task.Delay(10);
            await cancellation.CancelAsync();
        }
        Assert.That((await writer.WaitAsync(Timeout)).Status, Is.EqualTo(NativeOutboundWriterStatus.Cancelled));
        Assert.That(queue.Snapshot, Is.EqualTo(new NativeOutboundQueueSnapshot(0, 0, 0, 0, false, true)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AbortWakesAnIdleWriterWithoutExternalCancellation(bool edgeTriggeredWake)
    {
        var queue = new NativeOutboundQueue(2, 32, 8, edgeTriggeredWake: edgeTriggeredWake);
        var writer = queue.RunWriterAsync((_, _) => throw new InvalidOperationException("No writes were published."));
        await Task.Delay(10);
        queue.Abort();
        Assert.That((await writer.WaitAsync(Timeout)).Status, Is.EqualTo(NativeOutboundWriterStatus.Aborted));
        Assert.That(queue.Snapshot, Is.EqualTo(new NativeOutboundQueueSnapshot(0, 0, 0, 0, false, true)));
    }

    private sealed class TrackingPool(int padding = 0) : ArrayPool<byte>
    {
        private readonly ConcurrentDictionary<byte[], byte> retained = new();
        public int Retained => retained.Count;

        public override byte[] Rent(int minimumLength)
        {
            var buffer = new byte[minimumLength + padding];
            if (!retained.TryAdd(buffer, 0)) throw new InvalidOperationException("Unexpected duplicate rental.");
            return buffer;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            if (!retained.TryRemove(array, out _)) throw new InvalidOperationException("Duplicate or foreign pool return.");
        }
    }
}
