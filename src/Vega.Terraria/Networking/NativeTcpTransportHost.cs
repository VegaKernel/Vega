using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Terraria.Net.Sockets;
using TerrariaRuntime;

namespace Vega.Terraria.Networking;

/// <summary>
/// Explicitly attached candidate transport. The caller must attach before the dedicated listener starts;
/// vanilla remains the default when this host is not attached. StopAsync must be awaited off the world thread.
/// </summary>
internal sealed class NativeTcpTransportHost : IDisposable
{
    private readonly object gate = new();
    private readonly NativeTcpTransportOptions options;
    private readonly NativeOutboundBudget budget;
    private readonly Func<int, ISocket?> currentSocket;
    private readonly HashSet<NativeTcpConnection> connections = [];
    private readonly NativeTcpConnection?[] slots = new NativeTcpConnection?[256];
    private readonly NativeTcpListener listener;
    private readonly bool activeSlotSnapshot;
    private readonly Func<ISocket> listenerFactory;
    private readonly Action afterEnqueue;
    private readonly Func<bool> shouldDeferWake;
    private readonly Action<long, NativeOutboundEnqueueResult>? onSendRejected;
    private readonly int pressureThreshold;
    private readonly int sendsBetweenPressureChecks;
    private Func<ISocket>? previousFactory;
    private long generation;
    private int nextSlot;
    private int worldThreadId;
    private int sendsSincePressureCheck;
    private int sendsSinceWakeCheck;
    private long lastWakeFlush;
    private bool pumping;
    private bool attached;
    private bool stopped;

    public NativeTcpTransportHost(NativeTcpTransportOptions options, Func<IPEndPoint>? bindEndpoint = null,
        Func<int, ISocket?>? currentSocket = null, bool activeSlotSnapshot = true,
        Action<long, NativeOutboundEnqueueResult>? onSendRejected = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        this.currentSocket = currentSocket ?? ResolveNativeSocket;
        this.activeSlotSnapshot = activeSlotSnapshot;
        this.onSendRejected = onSendRejected;
        afterEnqueue = DrainDuringSendBurst;
        shouldDeferWake = () => options.DeferWriterWake && !pumping && Environment.CurrentManagedThreadId == Volatile.Read(ref worldThreadId);
        pressureThreshold = Math.Max(1, options.MaxOutstandingPerConnection / 2);
        sendsBetweenPressureChecks = Math.Clamp(pressureThreshold / 8, 1, 512);
        budget = new NativeOutboundBudget(options.MaxOutstandingPerWorld, options.MaxRetainedBytesPerWorld);
        listener = new NativeTcpListener(bindEndpoint ?? ResolveNativeEndpoint, CreateAccepted);
        listenerFactory = () => listener;
    }

    public NativeTcpListener Listener => listener;
    public (int Entries, long Bytes) Retained => budget.Snapshot;

    internal (int Slot, TimeSpan Send, TimeSpan Completion) ReadQueueAges()
    {
        int slowestSlot = -1;
        TimeSpan send = TimeSpan.Zero, completion = TimeSpan.Zero;
        for (int slot = 0; slot < slots.Length; slot++)
        {
            NativeTcpConnection? connection = Volatile.Read(ref slots[slot]);
            if (connection is null || connection.IsClosed) continue;
            var age = connection.ReadQueueAge();
            if (age.Send > send) { send = age.Send; slowestSlot = slot; }
            if (age.Completion > completion) completion = age.Completion;
        }
        return (slowestSlot, send, completion);
    }

    public void Attach()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            if (attached) return;
            previousFactory = Hooks.Netplay.ServerTcpListenerFactory;
            Hooks.Netplay.ServerTcpListenerFactory = listenerFactory;
            Hooks.Netplay.ServerConnectionActivated += BindActivated;
            Hooks.Netplay.ServerConnectionClosing += UnbindClosing;
            Hooks.Netplay.ServerConnectionClosed += UnbindClosed;
            HookEvents.Terraria.Netplay.UpdateServerInMainThread += OnNativePacketPass;
            if (options.DeferWriterWake) Hooks.Main.DedicatedTickCompleted += OnTickCompleted;
            attached = true;
        }
    }

    internal NativeTcpConnection? CreateAccepted(Socket socket)
    {
        NativeTcpConnection connection;
        lock (gate)
        {
            if (stopped || connections.Count >= options.MaxConnections) return null;
            connection = new NativeTcpConnection(socket, ++generation, options, budget,
                afterEnqueue: afterEnqueue, onSendRejected: onSendRejected, shouldDeferWake: shouldDeferWake);
            connections.Add(connection);
        }
        _ = RetireWhenFinishedAsync(connection);
        return connection;
    }

    internal void BindActivated(int slot, ISocket socket)
    {
        if ((uint)slot >= (uint)slots.Length || socket is not NativeTcpConnection connection) return;
        NativeTcpConnection? previous;
        lock (gate)
        {
            if (stopped || !connections.Contains(connection) || connection.IsClosed) return;
            previous = Interlocked.Exchange(ref slots[slot], connection);
            connection.SetDeferredWakeBound(options.DeferWriterWake);
        }
        if (previous is not null && !ReferenceEquals(previous, connection)) previous.Close();
    }

    internal void UnbindClosing(int slot, ISocket socket)
    {
        if ((uint)slot < (uint)slots.Length && socket is NativeTcpConnection connection)
        {
            Interlocked.CompareExchange(ref slots[slot], null, connection);
            connection.SetDeferredWakeBound(false);
        }
        // This notification is before native reset; do not close its socket before other pre-reset observers.
    }

    internal void UnbindClosed(int slot, ISocket socket)
    {
        UnbindClosing(slot, socket);
        if (socket is NativeTcpConnection connection) connection.Close();
    }

    private void OnNativePacketPass(object? sender, HookEvents.Terraria.Netplay.UpdateServerInMainThreadEventArgs args) => DrainCompletions();
    private void OnTickCompleted(TimeSpan _) => FlushDeferredWriters();

    // Dense sequential native broadcasts can fill each idle per-client FIFO before
    // scheduling its writer. Flush during a burst and at every tick boundary; no
    // socket worker or non-bound connection may defer its own wake.
    internal void FlushDeferredWriters()
    {
        if (!options.DeferWriterWake) return;
        for (int slot = 0; slot < slots.Length; slot++) Volatile.Read(ref slots[slot])?.FlushDeferredWake();
        lastWakeFlush = Stopwatch.GetTimestamp();
        sendsSinceWakeCheck = 0;
    }

    private void DrainDuringSendBurst()
    {
        if (Environment.CurrentManagedThreadId != Volatile.Read(ref worldThreadId) || pumping) return;
        if (options.DeferWriterWake && ++sendsSinceWakeCheck >= 256)
        {
            sendsSinceWakeCheck = 0;
            if (Stopwatch.GetElapsedTime(lastWakeFlush) >= options.DeferredWakeFlushInterval) FlushDeferredWriters();
        }
        if (++sendsSincePressureCheck < sendsBetweenPressureChecks) return;
        sendsSincePressureCheck = 0;
        if (budget.Snapshot.Entries >= pressureThreshold)
            DrainCompletions();
    }

    /// <summary>World-thread-only completion pump. Do not call from socket workers.</summary>
    internal int DrainCompletions()
    {
        if (pumping) return 0;
        Volatile.Write(ref worldThreadId, Environment.CurrentManagedThreadId);
        FlushDeferredWriters();
        pumping = true;
        try { return DrainCompletionsCore(); }
        finally { pumping = false; }
    }

    private int DrainCompletionsCore()
    {
        if (!activeSlotSnapshot) return DrainAllSlots();
        long started = Stopwatch.GetTimestamp();
        // Snapshot registry indices, not connection references. Every callback still acquires the
        // current connection and revalidates its generation and native socket identity at delivery.
        // Joining slots after the snapshot become eligible on the next native packet pass.
        Span<int> candidates = stackalloc int[slots.Length];
        int candidateCount = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            int slot = (nextSlot + i) % slots.Length;
            if (Volatile.Read(ref slots[slot]) is not null) candidates[candidateCount++] = slot;
        }
        if (candidateCount == 0) return 0;

        int processed = 0, missed = 0, index = 0;
        while (processed < options.MaxCallbacksPerPass && missed < candidateCount &&
               Stopwatch.GetElapsedTime(started) < options.CallbackBudget)
        {
            int slot = candidates[index];
            if (++index == candidateCount) index = 0;
            nextSlot = (slot + 1) % slots.Length;
            NativeTcpConnection? connection = Volatile.Read(ref slots[slot]);
            if (connection is null || !connection.TryReadCompletion(out NativeSendCompletion completion))
            {
                missed++;
                continue;
            }
            missed = 0;
            processed++;
            DeliverCompletion(slot, connection, completion);
        }
        return processed;
    }

    private int DrainAllSlots()
    {
        int processed = 0, missed = 0;
        long started = Stopwatch.GetTimestamp();
        while (processed < options.MaxCallbacksPerPass && missed < slots.Length &&
               Stopwatch.GetElapsedTime(started) < options.CallbackBudget)
        {
            int slot = nextSlot;
            nextSlot = (nextSlot + 1) % slots.Length;
            NativeTcpConnection? connection = Volatile.Read(ref slots[slot]);
            if (connection is null || !connection.TryReadCompletion(out NativeSendCompletion completion))
            {
                missed++;
                continue;
            }
            missed = 0;
            processed++;
            DeliverCompletion(slot, connection, completion);
        }
        return processed;
    }

    private void DeliverCompletion(int slot, NativeTcpConnection connection, NativeSendCompletion completion)
    {
        if (connection.IsClosed || completion.Generation != connection.Generation ||
            !ReferenceEquals(Volatile.Read(ref slots[slot]), connection) || !ReferenceEquals(currentSocket(slot), connection)) return;
        try { completion.Callback?.Invoke(completion.State); }
        catch (Exception) { connection.Close(); }
    }

    public void Dispose()
    {
        NativeTcpConnection[] snapshot;
        lock (gate)
        {
            if (stopped) return;
            stopped = true;
            if (attached)
            {
                if (ReferenceEquals(Hooks.Netplay.ServerTcpListenerFactory, listenerFactory))
                    Hooks.Netplay.ServerTcpListenerFactory = previousFactory;
                Hooks.Netplay.ServerConnectionActivated -= BindActivated;
                Hooks.Netplay.ServerConnectionClosing -= UnbindClosing;
                Hooks.Netplay.ServerConnectionClosed -= UnbindClosed;
                HookEvents.Terraria.Netplay.UpdateServerInMainThread -= OnNativePacketPass;
                if (options.DeferWriterWake) Hooks.Main.DedicatedTickCompleted -= OnTickCompleted;
                attached = false;
            }
            snapshot = connections.ToArray();
            Array.Clear(slots);
        }
        listener.StopListening();
        foreach (NativeTcpConnection connection in snapshot) connection.Close();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Dispose();
        Task listenerTask = listener.Completion;
        Task[] tasks;
        lock (gate) tasks = connections.Select(connection => (Task)connection.Completion).Append(listenerTask).ToArray();
        await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RetireWhenFinishedAsync(NativeTcpConnection connection)
    {
        try { await connection.Completion.ConfigureAwait(false); }
        finally
        {
            lock (gate)
            {
                connections.Remove(connection);
                for (int slot = 0; slot < slots.Length; slot++) Interlocked.CompareExchange(ref slots[slot], null, connection);
            }
        }
    }

    private static IPEndPoint ResolveNativeEndpoint()
    {
        IPAddress address = IPAddress.Any;
        if (global::Terraria.Program.LaunchParameters.TryGetValue("-ip", out string? value) && IPAddress.TryParse(value, out IPAddress? configured))
            address = configured;
        return new IPEndPoint(address, global::Terraria.Netplay.ListenPort);
    }

    private static ISocket? ResolveNativeSocket(int slot) => (uint)slot < (uint)global::Terraria.Netplay.Clients.Length
        ? global::Terraria.Netplay.Clients[slot]?.Socket : null;
}
