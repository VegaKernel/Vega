using System.Buffers;
using Terraria.Net.Sockets;

namespace Vega.Terraria.Networking;

internal enum NativeOutboundEnqueueResult { Accepted, Closed, EntryLimit, ByteLimit, RequestTooLarge }
internal enum NativeOutboundWriterStatus { Completed, Aborted, Cancelled, Faulted }

internal readonly record struct NativeOutboundWriterResult(NativeOutboundWriterStatus Status, long SentRequests, long WrittenBytes, Exception? Error = null);

/// <summary>Only the native lifecycle-bound completion pump may invoke Callback, after checking Generation.</summary>
internal readonly record struct NativeSendCompletion(long Sequence, long Generation, int Length, SocketSendCallback? Callback, object? State);

internal readonly record struct NativeOutboundQueueSnapshot(int PendingRequests, int OutstandingRequests,
    long RetainedBytes, int PendingCompletions, bool IsAccepting, bool IsAborted);

/// <summary>
/// Candidate per-connection native transport queue. Counts owned buffer capacity, including the active write,
/// and keeps an entry credit until its completion metadata is consumed or retired. No Terraria state or native
/// callbacks are accessed on the writer. The socket owner must dispose/cancel its I/O on abort.
/// </summary>
internal sealed class NativeOutboundQueue
{
    internal const int BatchRequestLimit = 16;
    internal const int BatchByteLimit = 32 * 1024;
    private enum QueueState { Open, Completing, Aborted }

    private readonly object gate = new();
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
    private QueueState state;
    private int outstanding;
    private long retainedBytes;
    private long sequence;
    private bool writerStarted;
    private bool writerWaiting;

    public NativeOutboundQueue(int maxEntries, long maxRetainedBytes, int maxRequestBytes, ArrayPool<byte>? pool = null,
        NativeOutboundBudget? sharedBudget = null, int maxBatchRequests = 1, bool edgeTriggeredWake = true)
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
        available = new(0, edgeTriggeredWake ? 1 : int.MaxValue);
    }

    public NativeOutboundQueueSnapshot Snapshot
    {
        get
        {
            lock (gate) return new(pending.Count, outstanding, retainedBytes, completions.Count,
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
        ReadOnlySpan<byte> bytes = source.AsSpan(offset, length);
        NativeOutboundEnqueueResult result;
        lock (gate)
        {
            if (state != QueueState.Open) return NativeOutboundEnqueueResult.Closed;
            if (length > maxRequestBytes) result = NativeOutboundEnqueueResult.RequestTooLarge;
            else if (outstanding >= maxEntries) result = NativeOutboundEnqueueResult.EntryLimit;
            else if (length > maxRetainedBytes - retainedBytes) result = NativeOutboundEnqueueResult.ByteLimit;
            else
            {
                byte[] owned = pool.Rent(length);
                if (owned.Length > maxRetainedBytes - retainedBytes)
                {
                    pool.Return(owned);
                    result = NativeOutboundEnqueueResult.ByteLimit;
                }
                else if (sharedBudget is not null && (result = sharedBudget.TryReserve(owned.Length)) != NativeOutboundEnqueueResult.Accepted)
                {
                    pool.Return(owned);
                }
                else
                {
                    bytes.CopyTo(owned);
                    pending.Enqueue(new(owned, new(++sequence, generation, length, callback, callbackState)));
                    retainedBytes += owned.Length;
                    outstanding++;
                    WakeWriterLocked();
                    return NativeOutboundEnqueueResult.Accepted;
                }
            }

            AbortLocked();
        }
        SignalAbort();
        return result;
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
        lock (gate)
        {
            if (!completions.TryDequeue(out completion)) return false;
            ReleaseEntries(1);
            return true;
        }
    }

    private async Task<NativeOutboundWriterResult> WriteLoopAsync(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<int>> writeSome, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(abort.Token, cancellationToken);
        CancellationToken token = linked.Token;
        long sent = 0, written = 0;
        // One small metadata array per coalescing writer, not per batch; disabled mode remains allocation-free here.
        NativeSendCompletion[]? batch = maxBatchRequests > 1 ? new NativeSendCompletion[maxBatchRequests] : null;
        try
        {
            while (true)
            {
                if (!edgeTriggeredWake) await available.WaitAsync(token).ConfigureAwait(false);
                OwnedSend request;
                byte[]? buffer = null;
                int length = 0, count = 1;
                lock (gate)
                {
                    if (state == QueueState.Aborted) return new(NativeOutboundWriterStatus.Aborted, sent, written);
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
                        length = request.Completion.Length;
                        if (batch is not null && TryCoalesceLocked(request, batch, out byte[]? merged, out int mergedLength, out int mergedCount))
                        {
                            buffer = merged!;
                            length = mergedLength;
                            count = mergedCount;
                        }
                    }
                }
                if (buffer is null)
                {
                    if (edgeTriggeredWake) await available.WaitAsync(token).ConfigureAwait(false);
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
                        if (succeeded && state != QueueState.Aborted && !token.IsCancellationRequested)
                        {
                            if (count == 1) completions.Enqueue(request.Completion);
                            else for (int i = 0; i < count; i++) completions.Enqueue(batch![i]);
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
            ReturnBuffer(current);
        }
        buffer = merged;
        return true;
    }

    private void WakeWriterLocked()
    {
        if (!edgeTriggeredWake) available.Release();
        else if (writerWaiting)
        {
            writerWaiting = false;
            available.Release();
        }
    }

    private void AbortLocked()
    {
        state = QueueState.Aborted;
        while (pending.TryDequeue(out OwnedSend request))
        {
            ReturnBuffer(request);
            ReleaseEntries(1);
        }
        ReleaseEntries(completions.Count);
        completions.Clear();
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
        outstanding -= count;
        sharedBudget?.ReleaseEntries(count);
    }

    // Signal outside the publish gate. CancelAsync avoids executing a slow I/O cancellation callback on the producer.
    private void SignalAbort() => _ = CancelWriterAsync();

    private async Task CancelWriterAsync()
    {
        try { await abort.CancelAsync().ConfigureAwait(false); }
        catch (Exception) { /* A misbehaving cancellation observer cannot reopen a retired connection. */ }
    }

    private readonly record struct OwnedSend(byte[] Buffer, NativeSendCompletion Completion);
}
