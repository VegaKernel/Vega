using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Terraria.Net;
using Terraria.Net.Sockets;

namespace Vega.Terraria.Networking;

/// <summary>
/// An accepted TCP socket. Native sends use one owned FIFO; native receives preserve their one-request
/// callback contract. No worker invokes a send callback or reads Terraria world state.
/// </summary>
internal sealed class NativeTcpConnection : ISocket
{
    private readonly object readGate = new();
    private readonly Socket socket;
    private readonly TcpAddress address;
    private readonly NativeOutboundQueue outbound;
    private readonly ArrayPool<byte> readPool;
    private readonly int maxReceiveBytes;
    private readonly TaskCompletionSource readsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ReadOperation? pendingRead;
    private int readOperations;
    private int closed;
    private bool closeCompleted;

    public NativeTcpConnection(Socket socket, long generation, NativeTcpTransportOptions options,
        NativeOutboundBudget budget, ArrayPool<byte>? readPool = null)
    {
        this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
        if (socket.RemoteEndPoint is not IPEndPoint remote) throw new ArgumentException("An accepted TCP socket is required.", nameof(socket));
        address = new TcpAddress(remote.Address, remote.Port);
        Generation = generation;
        socket.NoDelay = true;
        maxReceiveBytes = options.MaxReceiveBytes;
        this.readPool = readPool ?? ArrayPool<byte>.Shared;
        outbound = new(options.MaxOutstandingPerConnection, options.MaxRetainedBytesPerConnection,
            options.MaxSendBytes, sharedBudget: budget, maxBatchRequests: options.MaxBatchRequests,
            edgeTriggeredWake: options.EdgeTriggeredWake);
        // The queue always observes cancellation between writes. In the dispose-only experiment,
        // the owner interrupts an in-flight send via Close/Dispose rather than registering the
        // queue token with every OS operation. Overflow also closes this exact socket.
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<int>> writeSome = options.CancelPendingSocketSend
            ? (data, token) => socket.SendAsync(data, SocketFlags.None, token)
            : (data, _) => socket.SendAsync(data, SocketFlags.None);
        Completion = ObserveWriterAsync(outbound.RunWriterAsync(writeSome));
    }

    public long Generation { get; }
    public bool IsClosed => Volatile.Read(ref closed) != 0;
    public Task<NativeOutboundWriterResult> Completion { get; }
    public NativeOutboundQueueSnapshot OutboundSnapshot => outbound.Snapshot;

    public bool IsConnected()
    {
        if (IsClosed) return false;
        try { return socket.Connected && (!socket.Poll(0, SelectMode.SelectRead) || socket.Available > 0); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { return false; }
    }

    public bool IsDataAvailable()
    {
        if (IsClosed) return false;
        try { return socket.Connected && socket.Available > 0; }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { return false; }
    }

    public RemoteAddress GetRemoteAddress() => address;

    public void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object? state = null)
    {
        if (outbound.TryEnqueue(data, offset, size, Generation, callback, state) != NativeOutboundEnqueueResult.Accepted)
            Close();
    }

    public bool TryReadCompletion(out NativeSendCompletion completion) => outbound.TryReadCompletion(out completion);

    public void AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object? state = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        _ = data.AsMemory(offset, size);
        ReadOperation? request = null;
        lock (readGate)
        {
            if (!IsClosed && size <= maxReceiveBytes)
            {
                if (pendingRead is not null) throw new InvalidOperationException("Only one native read may be pending.");
                byte[] scratch = readPool.Rent(size);
                if (scratch.Length <= maxReceiveBytes)
                {
                    request = new ReadOperation(data, offset, size, scratch, callback, state);
                    pendingRead = request;
                    readOperations++;
                }
                else readPool.Return(scratch);
            }
        }

        if (request is not null)
            _ = ReadAsync(request);
        else
        {
            // Native RemoteClient.Update marks _isReading before calling AsyncReceive.
            // Even a read rejected by close must release that native pending-read flag.
            InvokeReceive(callback, state, 0);
            Close();
        }
    }

    public void Close()
    {
        ReadOperation? read;
        lock (readGate)
        {
            if (IsClosed) return;
            Volatile.Write(ref closed, 1);
            read = pendingRead;
            pendingRead = null; // Close claims this request; only the worker owns/returns the scratch.
        }
        outbound.Abort();
        try { socket.Dispose(); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        try
        {
            if (read is not null) InvokeReceive(read.Callback, read.State, 0);
        }
        finally
        {
            lock (readGate)
            {
                closeCompleted = true;
                SignalReadDrain();
            }
        }
    }

    public bool StartListening(SocketConnectionAccepted callback) => throw new NotSupportedException("An accepted socket cannot listen.");
    public void StopListening() => throw new NotSupportedException("An accepted socket cannot listen.");
    public void Connect(RemoteAddress remoteAddress) => throw new NotSupportedException("This socket is already accepted.");

    private async Task ReadAsync(ReadOperation request)
    {
        int count = 0;
        bool failed = false;
        try
        {
            count = await socket.ReceiveAsync(request.Scratch.AsMemory(0, request.Size), SocketFlags.None).ConfigureAwait(false);
            failed = count == 0;
        }
        catch (Exception) { failed = true; }

        try
        {
            bool claimed;
            lock (readGate)
            {
                claimed = ReferenceEquals(pendingRead, request);
                try
                {
                    if (claimed && count > 0)
                        request.Scratch.AsSpan(0, count).CopyTo(request.Destination.AsSpan(request.Offset, count));
                }
                finally
                {
                    // Return before the callback can allow the next native read. If Close won the claim,
                    // this operation never touched the native buffer after its zero callback.
                    readPool.Return(request.Scratch);
                    if (claimed) pendingRead = null;
                }
            }
            if (claimed) InvokeReceive(request.Callback, request.State, count);
        }
        finally
        {
            if (failed) Close();
            lock (readGate)
            {
                readOperations--;
                SignalReadDrain();
            }
        }
    }

    private void InvokeReceive(SocketReceiveCallback callback, object? state, int count)
    {
        try { callback(state, count); }
        catch (Exception)
        {
            // Vanilla TcpSocket also retries the native read callback with zero after a callback
            // exception, allowing its _isReading flag to clear. Do not retry arbitrary callbacks.
            if (count > 0 && callback.Target is global::Terraria.RemoteClient &&
                callback.Method.Name == nameof(global::Terraria.RemoteClient.ServerReadCallBack))
            {
                try { callback(state, 0); } catch (Exception) { }
            }
            Close();
        }
    }

    private async Task<NativeOutboundWriterResult> ObserveWriterAsync(Task<NativeOutboundWriterResult> writer)
    {
        NativeOutboundWriterResult result = await writer.ConfigureAwait(false);
        Close();
        await readsDrained.Task.ConfigureAwait(false);
        return result;
    }

    private void SignalReadDrain()
    {
        if (closeCompleted && readOperations == 0) readsDrained.TrySetResult();
    }

    private sealed record ReadOperation(byte[] Destination, int Offset, int Size, byte[] Scratch, SocketReceiveCallback Callback, object? State);
}
