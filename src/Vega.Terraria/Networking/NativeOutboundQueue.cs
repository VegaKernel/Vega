using System.Buffers;
using System.Diagnostics;
using Terraria.Net.Sockets;

namespace Vega.Terraria.Networking;

internal enum NativeOutboundEnqueueResult { Accepted, Closed, EntryLimit, ByteLimit, RequestTooLarge }
internal enum NativeOutboundWriterStatus { Completed, Aborted, Cancelled, Faulted }

internal readonly record struct NativeOutboundWriterResult(NativeOutboundWriterStatus Status, long SentRequests, long WrittenBytes, Exception? Error = null);

/// <summary>Only the native lifecycle-bound completion pump may invoke Callback, after checking Generation.</summary>
internal readonly record struct NativeSendCompletion(long Sequence, long Generation, int Length, SocketSendCallback? Callback, object? State)
{
    public long EnqueuedAt { get; init; }
    public long WrittenAt { get; init; }
}

internal readonly record struct NativeOutboundQueueSnapshot(int PendingRequests, int OutstandingRequests,
    long RetainedBytes, int PendingCompletions, bool IsAccepting, bool IsAborted);

internal readonly record struct NativeOutboundQueueAge(TimeSpan Send, TimeSpan Completion);

/// <summary>
/// Candidate per-connection native transport queue. Counts owned buffer capacity, including the active write,
/// and keeps an entry credit until its completion metadata is consumed or retired. No Terraria state or native
/// callbacks are accessed on the writer. The socket owner must dispose/cancel its I/O on abort.
/// </summary>
internal sealed class NativeOutboundQueue
{
    internal const int BatchRequestLimit = 16;
    internal const int BatchByteLimit = 32 * 1024;
    private const int ProducerPacketLimit = 256;
    private const int ProducerBlockLimit = 2048;
    private enum QueueState { Open, Completing, Aborted }

    private readonly object gate = new();
    // Payload ownership/coalescing can hold gate during pool operations and copying.
    // The world-thread completion pump does not need to wait for that work.
    // When both are needed, always acquire gate before completionGate.
    private readonly object completionGate = new();
    private readonly Queue<OwnedSend> pending = new();
    private readonly Queue<NativeSendCompletion> completions = new();
    private readonly SemaphoreSlim available;
    private readonly CancellationTokenSource abort = new();
    private readonly ArrayPool<byte> pool;
    private readonly NativeOutboundBudget? sharedBudget;
    private readonly int maxEntries;
    private readonly long maxRetainedBytes;
    private readonly int maxRequestBytes;
    private readonly int maxBatchRequests;
    private readonly bool edgeTriggeredWake;
    private readonly bool measureAge;
    private readonly bool singleCancellationWake;
    private readonly bool skipEmptyCompletionGate;
    private readonly bool producerBatching;
    private QueueState state;
    // The accepted sequence is owned by the payload gate; consumers only advance
    // the release counter. This avoids another atomic operation on every enqueue.
    private long releasedEntries;
    private long retainedBytes;
    private long sequence;
    private long inFlightSince;
    private bool writerStarted;
    private bool writerWaiting;
    private bool writerWakeDeferred;
    // Only the pending tail may accept more data. The writer detaches the entire
    // block under gate before I/O, so subsequent producers cannot change its bytes.
    private byte[]? pendingTailBuffer;
    private int pendingTailLength, pendingTailCount;
    // Published under completionGate with the queue; a concurrent publication after an
    // empty read remains eligible for the next pump probe. Never read Queue<T>.Count outside its gate.
    private bool hasCompletions;

    public NativeOutboundQueue(int maxEntries, long maxRetainedBytes, int maxRequestBytes, ArrayPool<byte>? pool = null,
        NativeOutboundBudget? sharedBudget = null, int maxBatchRequests = 1, bool edgeTriggeredWake = true, bool measureAge = false,
        bool singleCancellationWake = true, bool skipEmptyCompletionGate = true, bool producerBatching = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetainedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRequestBytes);
        if (maxRequestBytes > maxRetainedBytes) throw new ArgumentOutOfRangeException(nameof(maxRequestBytes));
        if (maxBatchRequests is < 1 or > BatchRequestLimit) throw new ArgumentOutOfRangeException(nameof(maxBatchRequests));
        this.maxEntries = maxEntries;
        this.maxRetainedBytes = maxRetainedBytes;
        this.maxRequestBytes = maxRequestBytes;
        this.pool = pool ?? ArrayPool<byte>.Shared;
        this.sharedBudget = sharedBudget;
        this.maxBatchRequests = maxBatchRequests;
        this.edgeTriggeredWake = edgeTriggeredWake;
        this.measureAge = measureAge;
        this.singleCancellationWake = singleCancellationWake;
        this.skipEmptyCompletionGate = skipEmptyCompletionGate;
        this.producerBatching = producerBatching && maxBatchRequests > 1;
        available = new(0, edgeTriggeredWake ? 1 : int.MaxValue);
    }

    public NativeOutboundQueueAge ReadAge()
    {
        lock (gate)
        {
            long now = Stopwatch.GetTimestamp();
            long oldestSend = inFlightSince != 0 ? inFlightSince :
                pending.TryPeek(out OwnedSend request) ? request.Completion.EnqueuedAt : 0;
            long oldestCompletion;
            lock (completionGate) oldestCompletion = completions.TryPeek(out NativeSendCompletion completion) ? completion.WrittenAt : 0;
            return new(oldestSend == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(oldestSend, now),
                oldestCompletion == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(oldestCompletion, now));
        }
    }

    public NativeOutboundQueueSnapshot Snapshot
    {
        get
        {
            lock (gate)
            lock (completionGate)
                return new(pending.Count, OutstandingEntries, retainedBytes, completions.Count,
                    state == QueueState.Open, state == QueueState.Aborted);
        }
    }

    /// <summary>
    /// The caller may reuse its buffer immediately after this returns. A capacity rejection aborts the queue,
    /// so later sends cannot overtake a rejected request; the socket owner must close the offending peer.
    /// </summary>
    public NativeOutboundEnqueueResult TryEnqueue(byte[] source, int offset, int length, long generation,
        SocketSendCallback? callback = null, object? callbackState = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        return TryEnqueue(source.AsSpan(offset, length), generation, callback, callbackState);
    }

    /// <summary>Copies a synchronous borrowed packet into the connection-owned FIFO.</summary>
    public NativeOutboundEnqueueResult TryEnqueue(ReadOnlySpan<byte> bytes, long generation,
        SocketSendCallback? callback = null, object? callbackState = null, bool deferWake = false)
    {
        int length = bytes.Length;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        NativeOutboundEnqueueResult result;
        lock (gate)
        {
            if (state != QueueState.Open) return NativeOutboundEnqueueResult.Closed;
            if (length > maxRequestBytes) result = NativeOutboundEnqueueResult.RequestTooLarge;
            else if (OutstandingEntries >= maxEntries) result = NativeOutboundEnqueueResult.EntryLimit;
            else if (producerBatching && pendingTailBuffer is { } tail && pendingTailCount < maxBatchRequests &&
                length <= BatchByteLimit - pendingTailLength && length <= tail.Length - pendingTailLength)
            {
                // The block's actual capacity is already charged. Only the new
                // request/completion entry needs a shared credit.
                result = sharedBudget?.TryReserve(0) ?? NativeOutboundEnqueueResult.Accepted;
                if (result == NativeOutboundEnqueueResult.Accepted)
                {
                    bytes.CopyTo(tail.AsSpan(pendingTailLength));
                    pendingTailLength += length;
                    pendingTailCount++;
                    PublishLocked(tail, length, generation, callback, callbackState, deferWake);
                    return result;
                }
            }
            else if (length > maxRetainedBytes - retainedBytes) result = NativeOutboundEnqueueResult.ByteLimit;
            else
            {
                int desired = ProducerRentSize(length, deferWake);
                result = TryRentLocked(desired, out byte[]? owned);
                if (result == NativeOutboundEnqueueResult.ByteLimit && desired != length)
                    result = TryRentLocked(length, out owned); // speculative spare capacity must not force rejection
                if (result == NativeOutboundEnqueueResult.Accepted)
                {
                    bytes.CopyTo(owned!);
                    retainedBytes += owned!.Length;
                    if (producerBatching)
                    {
                        pendingTailBuffer = owned;
                        pendingTailLength = length;
                        pendingTailCount = 1;
                    }
                    PublishLocked(owned!, length, generation, callback, callbackState, deferWake);
                    return result;
                }
            }

            AbortLocked();
        }
        SignalAbort();
        return result;
    }

    private int ProducerRentSize(int length, bool deferWake)
    {
        // Never speculate a large block for an ordinary idle/single send. Small
        // burst blocks are bounded by actual bytes and request count.
        if (!producerBatching || length > ProducerPacketLimit || (!deferWake && pending.Count == 0)) return length;
        int desired = Math.Min(ProducerBlockLimit, Math.Max(128, length * maxBatchRequests));
        return (int)Math.Min(desired, maxRetainedBytes - retainedBytes);
    }

    private NativeOutboundEnqueueResult TryRentLocked(int length, out byte[]? owned)
    {
        owned = pool.Rent(length);
        NativeOutboundEnqueueResult result = owned.Length > maxRetainedBytes - retainedBytes ? NativeOutboundEnqueueResult.ByteLimit :
            sharedBudget?.TryReserve(owned.Length) ?? NativeOutboundEnqueueResult.Accepted;
        if (result != NativeOutboundEnqueueResult.Accepted) { pool.Return(owned); owned = null; }
        return result;
    }

    private void PublishLocked(byte[] owned, int length, long generation, SocketSendCallback? callback, object? callbackState, bool deferWake)
    {
        pending.Enqueue(new(owned, new NativeSendCompletion(++sequence, generation, length, callback, callbackState)
        { EnqueuedAt = measureAge ? Stopwatch.GetTimestamp() : 0 }));
        if (deferWake && edgeTriggeredWake && writerWaiting) Volatile.Write(ref writerWakeDeferred, true);
        else WakeWriterLocked();
    }

    /// <summary>Starts exactly one writer on a worker, even if queued work and writes complete synchronously.</summary>
    public Task<NativeOutboundWriterResult> RunWriterAsync(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<int>> writeSome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeSome);
        lock (gate)
        {
            if (writerStarted) throw new InvalidOperationException("An outbound writer has already been started.");
            writerStarted = true;
        }
        return Task.Run(() => WriteLoopAsync(writeSome, cancellationToken));
    }

    /// <summary>Stops admission and lets the writer finish accepted requests; completion metadata remains readable.</summary>
    public void Complete()
    {
        lock (gate)
        {
            if (state != QueueState.Open) return;
            state = QueueState.Completing;
            WakeWriterLocked();
        }
    }

    /// <summary>Retires this generation immediately. The active write retains its buffer until I/O actually returns.</summary>
    public void Abort()
    {
        lock (gate)
        {
            if (state == QueueState.Aborted) return;
            AbortLocked();
        }
        SignalAbort();
    }

    public bool TryReadCompletion(out NativeSendCompletion completion)
    {
        if (skipEmptyCompletionGate && !Volatile.Read(ref hasCompletions))
        {
            completion = default;
            return false;
        }
        lock (completionGate)
        {
            if (!completions.TryDequeue(out completion)) return false;
            if (skipEmptyCompletionGate && completions.Count == 0) Volatile.Write(ref hasCompletions, false);
            ReleaseEntries(1);
            return true;
        }
    }

    private async Task<NativeOutboundWriterResult> WriteLoopAsync(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<int>> writeSome, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(abort.Token, cancellationToken);
        CancellationToken token = linked.Token;
        // Cancellation has one lifetime registration instead of
        // attaching a cancellable Task wrapper to every idle semaphore wait.
        using var wakeOnCancellation = singleCancellationWake
            ? token.UnsafeRegister(static state => ((NativeOutboundQueue)state!).WakeCancelledWriter(), this)
            : default;
        long sent = 0, written = 0;
        // One small metadata array per coalescing writer, not per batch; disabled mode remains allocation-free here.
        NativeSendCompletion[]? batch = maxBatchRequests > 1 ? new NativeSendCompletion[maxBatchRequests] : null;
        try
        {
            while (true)
            {
                if (singleCancellationWake) token.ThrowIfCancellationRequested();
                if (!edgeTriggeredWake) await WaitForSignalAsync(token).ConfigureAwait(false);
                OwnedSend request;
                byte[]? buffer = null;
                int length = 0, count = 1;
                lock (gate)
                {
                    if (state == QueueState.Aborted) return new(NativeOutboundWriterStatus.Aborted, sent, written);
                    // Cancellation and publication of writerWaiting use the same gate,
                    // preventing a lost wake between the outer check and the idle wait.
                    if (singleCancellationWake) token.ThrowIfCancellationRequested();
                    if (!pending.TryDequeue(out request))
                    {
                        if (state == QueueState.Completing) return new(NativeOutboundWriterStatus.Completed, sent, written);
                        // Record the waiting state under the same gate as publication. A producer or
                        // Complete cannot miss a wake even before WaitAsync actually begins.
                        if (edgeTriggeredWake) writerWaiting = true;
                    }
                    else
                    {
                        buffer = request.Buffer;
                        inFlightSince = request.Completion.EnqueuedAt;
                        length = request.Completion.Length;
                        if (batch is not null && producerBatching)
                        {
                            TakeProducerBlockLocked(request, batch, out length, out count);
                        }
                        else if (batch is not null && TryCoalesceLocked(request, batch, out byte[]? merged, out int mergedLength, out int mergedCount))
                        {
                            buffer = merged!;
                            length = mergedLength;
                            count = mergedCount;
                        }
                    }
                }
                if (buffer is null)
                {
                    if (edgeTriggeredWake) await WaitForSignalAsync(token).ConfigureAwait(false);
                    continue;
                }

                bool succeeded = false;
                try
                {
                    int offset = 0;
                    int completed = 0, nextBoundary = request.Completion.Length;
                    while (offset < length)
                    {
                        token.ThrowIfCancellationRequested();
                        int bytes = await writeSome(buffer.AsMemory(offset, length - offset), token).ConfigureAwait(false);
                        if (bytes <= 0 || bytes > length - offset) throw new IOException("Socket writer returned an invalid byte count.");
                        offset += bytes;
                        written += bytes;
                        while (completed < count && offset >= nextBoundary)
                        {
                            sent++;
                            completed++;
                            if (completed < count) nextBoundary += batch![completed].Length;
                        }
                    }
                    succeeded = true;
                }
                finally
                {
                    // Return only after the asynchronous I/O has finished reading the owned memory.
                    lock (gate)
                    {
                        ReturnBuffer(buffer);
                        inFlightSince = 0;
                        if (succeeded && state != QueueState.Aborted && !token.IsCancellationRequested)
                        {
                            long writtenAt = measureAge ? Stopwatch.GetTimestamp() : 0;
                            lock (completionGate)
                            {
                                if (count == 1) completions.Enqueue(request.Completion with { WrittenAt = writtenAt });
                                else for (int i = 0; i < count; i++) completions.Enqueue(batch![i] with { WrittenAt = writtenAt });
                                if (skipEmptyCompletionGate && !hasCompletions) Volatile.Write(ref hasCompletions, true);
                            }
                        }
                        else ReleaseEntries(count);
                        if (count > 1) Array.Clear(batch!, 0, count);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Abort();
            return new(cancellationToken.IsCancellationRequested ? NativeOutboundWriterStatus.Cancelled : NativeOutboundWriterStatus.Aborted, sent, written);
        }
        catch (Exception error)
        {
            Abort();
            return new(NativeOutboundWriterStatus.Faulted, sent, written, error);
        }
    }

    // All requests have been published under gate and in counted mode one credit already consumed for first.
    // Inspect the adjacent prefix before reserving: if the temporary rental cannot fit, leave it untouched.
    private bool TryCoalesceLocked(OwnedSend first, NativeSendCompletion[] batch, out byte[]? buffer, out int length, out int count)
    {
        buffer = null;
        length = first.Completion.Length;
        count = 1;
        if (length >= BatchByteLimit || pending.Count == 0) return false;
        foreach (OwnedSend next in pending)
        {
            if (count >= maxBatchRequests || next.Completion.Length > BatchByteLimit - length) break;
            length += next.Completion.Length;
            count++;
        }
        if (count == 1 || length > maxRetainedBytes - retainedBytes) return false;
        byte[] merged = pool.Rent(length);
        if (merged.Length > maxRetainedBytes - retainedBytes || (sharedBudget is not null && !sharedBudget.TryReserveBytes(merged.Length)))
        {
            pool.Return(merged);
            return false;
        }
        retainedBytes += merged.Length;
        // Charge the additional capacity before returning any original rental. Retain only the merged
        // bytes while the asynchronous write is in flight, but preserve every original completion credit.
        int offset = 0;
        long returnedBytes = 0;
        OwnedSend current = first;
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                // Counted mode: every additional pending request owns a credit; no second reader can consume it.
                if (!edgeTriggeredWake) _ = available.Wait(0);
                current = pending.Dequeue();
            }
            current.Buffer.AsSpan(0, current.Completion.Length).CopyTo(merged.AsSpan(offset));
            offset += current.Completion.Length;
            batch[i] = current.Completion;
            pool.Return(current.Buffer);
            returnedBytes += current.Buffer.Length;
        }
        retainedBytes -= returnedBytes;
        sharedBudget?.ReleaseBytes(returnedBytes);
        buffer = merged;
        return true;
    }

    private void TakeProducerBlockLocked(OwnedSend first, NativeSendCompletion[] batch, out int length, out int count)
    {
        if (ReferenceEquals(first.Buffer, pendingTailBuffer))
        {
            pendingTailBuffer = null;
            pendingTailLength = pendingTailCount = 0;
        }
        length = first.Completion.Length;
        count = 1;
        while (pending.TryPeek(out OwnedSend next) && ReferenceEquals(next.Buffer, first.Buffer))
        {
            if (!edgeTriggeredWake) _ = available.Wait(0);
            pending.Dequeue();
            if (count == 1) batch[0] = first.Completion;
            batch[count++] = next.Completion;
            length += next.Completion.Length;
        }
    }

    private void WakeWriterLocked()
    {
        if (writerWakeDeferred) Volatile.Write(ref writerWakeDeferred, false);
        if (!edgeTriggeredWake) available.Release();
        else if (writerWaiting)
        {
            writerWaiting = false;
            available.Release();
        }
    }

    public void FlushDeferredWake()
    {
        if (!Volatile.Read(ref writerWakeDeferred)) return;
        lock (gate)
        {
            if (writerWakeDeferred) WakeWriterLocked();
        }
    }

    internal bool IsWriterWaiting { get { lock (gate) return writerWaiting; } }

    private Task WaitForSignalAsync(CancellationToken token) => singleCancellationWake
        ? available.WaitAsync() : available.WaitAsync(token);

    private void WakeCancelledWriter()
    {
        lock (gate) WakeWriterLocked();
    }

    private void AbortLocked()
    {
        // Abort linearizes with completion consumption before clearing any credit.
        lock (completionGate)
        {
            state = QueueState.Aborted;
            pendingTailBuffer = null;
            pendingTailLength = pendingTailCount = 0;
            byte[]? returned = null;
            while (pending.TryDequeue(out OwnedSend request))
            {
                // All references to a producer block are adjacent in the pending FIFO.
                if (!ReferenceEquals(returned, request.Buffer))
                {
                    ReturnBuffer(request);
                    returned = request.Buffer;
                }
                ReleaseEntries(1);
            }
            ReleaseEntries(completions.Count);
            completions.Clear();
            if (skipEmptyCompletionGate) Volatile.Write(ref hasCompletions, false);
        }
    }

    private void ReturnBuffer(OwnedSend request) => ReturnBuffer(request.Buffer);

    private void ReturnBuffer(byte[] buffer)
    {
        pool.Return(buffer);
        retainedBytes -= buffer.Length;
        sharedBudget?.ReleaseBytes(buffer.Length);
    }

    private void ReleaseEntries(int count)
    {
        sharedBudget?.ReleaseEntries(count);
        Interlocked.Add(ref releasedEntries, count);
    }

    // Only read while holding the payload gate, so accepted sequence is stable.
    private int OutstandingEntries => checked((int)(sequence - Volatile.Read(ref releasedEntries)));

    // Signal outside the publish gate. CancelAsync avoids executing a slow I/O cancellation callback on the producer.
    private void SignalAbort() => _ = CancelWriterAsync();

    private async Task CancelWriterAsync()
    {
        try { await abort.CancelAsync().ConfigureAwait(false); }
        catch (Exception) { /* A misbehaving cancellation observer cannot reopen a retired connection. */ }
    }

    private readonly record struct OwnedSend(byte[] Buffer, NativeSendCompletion Completion);
}
