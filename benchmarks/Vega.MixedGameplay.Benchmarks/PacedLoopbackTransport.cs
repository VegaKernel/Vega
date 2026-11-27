using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Terraria.Net.Sockets;
#if !TSHOCK_COMPARISON
using Vega.Terraria.Networking;
using TerrariaRuntime;
#endif

namespace Vega.MixedGameplay.Benchmarks;

/// <summary>
/// Disposable fixture transport. Each accepted server socket is paired with a passive
/// localhost reader; none connects to the public Terraria listener or sends client input.
/// </summary>
internal sealed class PacedLoopbackTransport : IDisposable
{
    private readonly Peer?[] peers;
#if !TSHOCK_COMPARISON
    private readonly NativeTcpTransportHost? ordered;
#endif
    private readonly CancellationTokenSource lifetime = new();
    private long nextTick;
    private int overruns;
    private bool paced;
    private readonly bool measuresQueueAge = false;
    private int peakEntries;
    private long peakBytes;
    private double peakSendAgeMs, peakCompletionAgeMs;
    private long initialSocketWrites;

    public PacedLoopbackTransport(string mode, int count)
    {
        peers = new Peer[count];
#if !TSHOCK_COMPARISON
        if (mode == "ordered")
        {
            var options = Environment.GetEnvironmentVariable("VEGA_BENCH_SERVER_DEFAULTS") == "1"
                ? new ServerNetworkOptions().ToTransportOptions()
                : new NativeTcpTransportOptions { MaxBatchRequests = 16, MeasureQueueAge = true,
                CompletionDrainBatch = int.TryParse(Environment.GetEnvironmentVariable("VEGA_BENCH_COMPLETION_BATCH"), out int completionBatch) ? completionBatch : 1,
                DeferWriterWake = Environment.GetEnvironmentVariable("VEGA_BENCH_DEFER_WAKE") == "1",
                ProducerBatching = Environment.GetEnvironmentVariable("VEGA_BENCH_DEFER_WAKE") == "1" &&
                    Environment.GetEnvironmentVariable("VEGA_BENCH_PRODUCER_BATCHING") == "1",
                DeferredWakeFlushInterval = TimeSpan.FromMilliseconds(double.TryParse(Environment.GetEnvironmentVariable("VEGA_BENCH_DEFER_MS"),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double interval) ? interval : 1) };
            measuresQueueAge = options.MeasureQueueAge;
            ordered = new NativeTcpTransportHost(options,
                currentSocket: slot => (uint)slot < peers.Length ? peers[slot]?.Socket : null);
        }
        else if (mode != "native") throw new ArgumentOutOfRangeException(nameof(mode));
#else
        if (mode != "native") throw new ArgumentOutOfRangeException(nameof(mode));
#endif

        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var setup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            for (int i = 0; i < count; i++)
            {
                var receiver = new TcpClient(AddressFamily.InterNetwork);
                TcpClient? accepted = null;
                ISocket? socket = null;
                try
                {
                    receiver.ConnectAsync(IPAddress.Loopback, port, setup.Token).AsTask().GetAwaiter().GetResult();
                    accepted = listener.AcceptTcpClientAsync(setup.Token).AsTask().GetAwaiter().GetResult();
                    accepted.NoDelay = true;
#if !TSHOCK_COMPARISON
                    if (ordered is not null)
                        socket = ordered.CreateAccepted(accepted.Client) ?? throw new InvalidOperationException("Fixture socket was rejected.");
                    else
#endif
                        socket = (ISocket)Activator.CreateInstance(typeof(TcpSocket), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                            null, [accepted], null)!;
                    // The native socket owns the accepted TCP connection from here.
                    accepted = null;
                    peers[i] = new Peer(socket, receiver, lifetime.Token);
#if !TSHOCK_COMPARISON
                    ordered?.BindActivated(i, socket);
#endif
                }
                catch
                {
                    if (peers[i] is null) { socket?.Close(); accepted?.Dispose(); receiver.Dispose(); }
                    throw;
                }
            }
        }
        catch { Dispose(); throw; }
        nextTick = Stopwatch.GetTimestamp();
    }

    public Peer GetPeer(int slot) => peers[slot]!;

    public void ResetMeasurement()
    {
        Drain();
        foreach (Peer? peer in peers) peer!.ResetMeasurement();
        paced = false;
        overruns = peakEntries = 0;
        peakBytes = 0;
        peakSendAgeMs = peakCompletionAgeMs = 0;
        initialSocketWrites = ReadSocketWrites();
        nextTick = Stopwatch.GetTimestamp();
    }

    public int Pump()
    {
        ValidateConnected();
        Sample();
#if !TSHOCK_COMPARISON
        int processed = ordered?.DrainCompletions() ?? 0;
#else
        int processed = 0;
#endif
        Sample();
        return processed;
    }

    public void Pace()
    {
        paced = true;
        Sample();
        // Maintain ordinary 60 Hz offered load; do not catch up missed periods with bursts.
        long target = nextTick + Stopwatch.Frequency / 60;
        if (Stopwatch.GetTimestamp() > target) overruns++;
        while (true)
        {
            double remaining = (target - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency;
            if (remaining <= 0) break;
            // The Windows scheduler can round a nominal 1 ms sleep to an entire
            // timer period. Yield for the short 60-Hz remainder instead; pacing
            // CPU is included in process CPU, busy tick timing excludes the wait.
            if (remaining > 20) Thread.Sleep((int)remaining - 16);
            else if (remaining > 0.2) Thread.Sleep(0);
            else Thread.SpinWait(32);
        }
        nextTick = Stopwatch.GetTimestamp();
    }

    public void FlushTick()
    {
#if !TSHOCK_COMPARISON
        ordered?.FlushDeferredWriters();
#endif
    }

    public Result Finish()
    {
        long started = Stopwatch.GetTimestamp();
        Drain();
        return new(peers.Sum(p => p!.MeasuredSends), peers.Sum(p => p!.MeasuredBytes),
            peers.Sum(p => p!.MeasuredCallbacks), peers.Sum(p => p!.MeasuredReceivedBytes), peers.Length,
            true, paced ? overruns : null, peakEntries, peakBytes,
            measuresQueueAge ? peakSendAgeMs : null, measuresQueueAge ? peakCompletionAgeMs : null,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, measuresQueueAge ? ReadSocketWrites() - initialSocketWrites : null);
    }

    private long ReadSocketWrites()
    {
#if !TSHOCK_COMPARISON
        if (ordered is not null) return peers.Sum(p => ((NativeTcpConnection)p!.Socket).SocketWriteCalls);
#endif
        return 0;
    }

    private void Drain()
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            Pump();
            if (peers.All(p => p!.IsDrained)) break;
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(15))
                throw new TimeoutException($"Mixed loopback drain timed out: callbacks={peers.Sum(p => p!.Callbacks)}/{peers.Sum(p => p!.Sends)} " +
                    $"received={peers.Sum(p => p!.ReceivedBytes)}/{peers.Sum(p => p!.Bytes)}.");
            Thread.Sleep(1);
        }
        foreach (Peer? peer in peers) peer!.ValidateHash();
    }

    private void ValidateConnected()
    {
        for (int i = 0; i < peers.Length; i++) peers[i]!.ValidateConnected();
    }

    private void Sample()
    {
#if !TSHOCK_COMPARISON
        if (ordered is null) return;
        var retained = ordered.Retained;
        peakEntries = Math.Max(peakEntries, retained.Entries);
        peakBytes = Math.Max(peakBytes, retained.Bytes);
        if (!measuresQueueAge) return;
        var ages = ordered.ReadQueueAges();
        peakSendAgeMs = Math.Max(peakSendAgeMs, ages.Send.TotalMilliseconds);
        peakCompletionAgeMs = Math.Max(peakCompletionAgeMs, ages.Completion.TotalMilliseconds);
#endif
    }

    public void Dispose()
    {
        lifetime.Cancel();
        foreach (Peer? peer in peers)
        {
            peer?.Socket.Close();
            peer?.Receiver.Dispose();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task.WhenAll(peers.Where(p => p is not null).Select(p => p!.Reader)).WaitAsync(timeout.Token).GetAwaiter().GetResult();
#if !TSHOCK_COMPARISON
        ordered?.StopAsync(timeout.Token).GetAwaiter().GetResult();
#endif
        lifetime.Dispose();
    }

    public sealed record Result(long ScheduledCallbacks, long SentBytes, long CompletedCallbacks, long ReceivedBytes,
        int ValidatedPeers, bool PayloadHashVerified, int? TickOverruns, int PeakRetainedEntries, long PeakRetainedBytes,
        double? PeakSendQueueAgeMs, double? PeakCompletionQueueAgeMs, double FinalDrainMs, long? SocketWriteCalls);

    internal sealed class Peer
    {
        // Per-peer stream hashes include both warmup and measure. Verify at each phase
        // boundary; this catches corruption/reordering as well as dropped bytes.
        private const ulong HashSeed = 14695981039346656037UL;
        private const ulong HashPrime = 1099511628211UL;
        private ulong expectedHash = HashSeed, receivedHash = HashSeed;
        private ulong runningReceiveHash = HashSeed;
        private long callbacks, receivedBytes;
        private long initialSends, initialBytes, initialCallbacks, initialReceived;
        private SocketSendCallback? lastNativeCallback, lastWrapper;
        private readonly Dictionary<SocketSendCallback, SocketSendCallback> wrappers = [];
        private Exception? callbackError, readError;

        public Peer(ISocket socket, TcpClient receiver, CancellationToken token)
        {
            Socket = socket;
            Receiver = receiver;
            Reader = ReadAsync(token);
        }

        public ISocket Socket { get; }
        public TcpClient Receiver { get; }
        public Task Reader { get; }
        public long Sends { get; private set; }
        public long Bytes { get; private set; }
        public long Callbacks => Interlocked.Read(ref callbacks);
        public long ReceivedBytes => Interlocked.Read(ref receivedBytes);
        public long MeasuredSends => Sends - initialSends;
        public long MeasuredBytes => Bytes - initialBytes;
        public long MeasuredCallbacks => Callbacks - initialCallbacks;
        public long MeasuredReceivedBytes => ReceivedBytes - initialReceived;
        public bool IsDrained => Callbacks == Sends && ReceivedBytes == Bytes;

        public void Send(byte[] data, int offset, int size, SocketSendCallback callback, object? state)
        {
            Socket.AsyncSend(data, offset, size, PrepareSend(data.AsSpan(offset, size), callback), state);
        }

#if !TSHOCK_COMPARISON
        public void SendBorrowed(ReadOnlySpan<byte> packet, SocketSendCallback callback, object? state)
        {
            var wrapper = PrepareSend(packet, callback);
            if (Socket is IBorrowedPacketSocket borrowed) borrowed.SendBorrowed(packet, wrapper, state);
            else Socket.AsyncSend(packet.ToArray(), 0, packet.Length, wrapper, state);
        }
#endif

        private SocketSendCallback PrepareSend(ReadOnlySpan<byte> packet, SocketSendCallback callback)
        {
            Sends++;
            Bytes += packet.Length;
            expectedHash = Append(expectedHash, packet);
            // Use value identity to avoid allocating a per-send closure even on a
            // legacy native build creating an equivalent callback delegate every send.
            if (!Equals(lastNativeCallback, callback))
            {
                if (!wrappers.TryGetValue(callback, out lastWrapper))
                {
                    lastWrapper = CreateWrapper(callback);
                    wrappers.Add(callback, lastWrapper);
                }
                lastNativeCallback = callback;
            }
            return lastWrapper!;
        }

        private SocketSendCallback CreateWrapper(SocketSendCallback callback) => value =>
        {
            try { callback(value); Interlocked.Increment(ref callbacks); }
            catch (Exception ex) { Volatile.Write(ref callbackError, ex); }
        };

        public void ResetMeasurement()
        {
            initialSends = Sends;
            initialBytes = Bytes;
            initialCallbacks = Callbacks;
            initialReceived = ReceivedBytes;
        }

        public void ValidateConnected()
        {
            if (Volatile.Read(ref callbackError) is { } callbackFailure)
                throw new InvalidOperationException("A native send callback failed.", callbackFailure);
            if (Volatile.Read(ref readError) is { } readFailure)
                throw new InvalidOperationException("A passive fixture reader failed.", readFailure);
            if (!Socket.IsConnected() || Callbacks > Sends || ReceivedBytes > Bytes)
                throw new InvalidOperationException("Loopback socket closed or delivery counters exceeded offered load.");
        }

        public void ValidateHash()
        {
            if (!IsDrained || Volatile.Read(ref receivedHash) != expectedHash)
                throw new InvalidOperationException("Loopback stream hash or delivery count differs from serialized output.");
        }

        private async Task ReadAsync(CancellationToken token)
        {
            try
            {
                byte[] buffer = new byte[32 * 1024];
                NetworkStream stream = Receiver.GetStream();
                while (true)
                {
                    int count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException("Passive fixture peer closed before benchmark completion.");
                    runningReceiveHash = Append(runningReceiveHash, buffer.AsSpan(0, count));
                    Volatile.Write(ref receivedHash, runningReceiveHash);
                    Interlocked.Add(ref receivedBytes, count);
                }
            }
            catch (Exception) when (token.IsCancellationRequested) { }
            catch (Exception ex) { Volatile.Write(ref readError, ex); }
        }

        private static ulong Append(ulong hash, ReadOnlySpan<byte> bytes)
        {
            foreach (byte value in bytes) hash = unchecked((hash ^ value) * HashPrime);
            return hash;
        }
    }
}
