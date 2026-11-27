using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using NUnit.Framework;
using Terraria.Net.Sockets;
using TerrariaRuntime;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class NativeTcpTransportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task CompletionBurstPreservesPeerFifoFairnessCountAndRetirement()
    {
        await using var fixture = new Fixture(new NativeTcpTransportOptions
        {
            CompletionDrainBatch = 4, MaxCallbacksPerPass = 5, CallbackBudget = TimeSpan.FromSeconds(1)
        });
        var (firstPeer, first) = await fixture.ConnectAsync();
        var (secondPeer, second) = await fixture.ConnectAsync();
        using (firstPeer)
        using (secondPeer)
        {
            fixture.Bind(1, first);
            fixture.Bind(2, second);
            List<int> delivered = [];
            for (int i = 0; i < 6; i++)
                first.AsyncSend([1], 0, 1, state => delivered.Add((int)state!), i);
            second.AsyncSend([2], 0, 1, _ => { delivered.Add(10); second.Close(); });
            second.AsyncSend([2], 0, 1, _ => delivered.Add(11));
            await UntilAsync(() => first.OutboundSnapshot.PendingCompletions == 6 && second.OutboundSnapshot.PendingCompletions == 2);

            Assert.That(fixture.Host.DrainCompletions(), Is.EqualTo(5));
            Assert.That(delivered, Is.EqualTo(new[] { 0, 1, 2, 3, 10 }));
            Assert.That(second.IsClosed, Is.True);
            Assert.That(fixture.Host.DrainCompletions(), Is.EqualTo(2));
            Assert.That(delivered, Is.EqualTo(new[] { 0, 1, 2, 3, 10, 4, 5 }));
            Assert.That(fixture.Host.Retained, Is.EqualTo((0, 0L)));
        }
    }

    [Test]
    public async Task CompletionBurstSkipsAlreadyDequeuedCallbacksAfterPeerClose()
    {
        await using var fixture = new Fixture(new NativeTcpTransportOptions
        {
            CompletionDrainBatch = 4, CallbackBudget = TimeSpan.FromSeconds(1)
        });
        var (peer, connection) = await fixture.ConnectAsync();
        using (peer)
        {
            fixture.Bind(1, connection);
            int callbacks = 0;
            connection.AsyncSend([1], 0, 1, _ => { callbacks++; connection.Close(); });
            for (int i = 0; i < 3; i++) connection.AsyncSend([1], 0, 1, _ => callbacks++);
            await UntilAsync(() => connection.OutboundSnapshot.PendingCompletions == 4);
            Assert.That(fixture.Host.DrainCompletions(), Is.EqualTo(4));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(fixture.Host.Retained, Is.EqualTo((0, 0L)));
        }
    }

    [Test]
    [NonParallelizable]
    public void ExplicitAttachSelectsCandidateFactoryAndDisposeRestoresPreviousFactory()
    {
        var previous = Hooks.Netplay.ServerTcpListenerFactory;
        using var host = new NativeTcpTransportHost(new NativeTcpTransportOptions());
        host.Attach();
        try { Assert.That(Hooks.Netplay.InvokeCreateServerTcpListener(), Is.SameAs(host.Listener)); }
        finally { host.Dispose(); }
        Assert.That(ReferenceEquals(Hooks.Netplay.ServerTcpListenerFactory, previous), Is.True);
    }

    [Test]
    public async Task LoopbackSendsKeepOrderOwnedSliceAndWorldThreadCompletion()
    {
        await using var fixture = new Fixture();
        var (peer, connection) = await fixture.ConnectAsync();
        using (peer)
        {
            fixture.Bind(4, connection);
            ISocket native = connection;
            byte[] buffer = [99, 1, 2, 3, 99];
            var callbacks = new List<object?>();
            object first = new(), second = new();
            native.AsyncSend(buffer, 1, 3, state => callbacks.Add(state), first);
            Array.Fill(buffer, (byte)88);
            native.AsyncSend([4, 5], 0, 2, state => callbacks.Add(state), second);
            byte[] received = new byte[5];
            await peer.GetStream().ReadExactlyAsync(received).AsTask().WaitAsync(Timeout);
            await UntilAsync(() => connection.OutboundSnapshot.PendingCompletions == 2);
            Assert.Multiple(() =>
            {
                Assert.That(received, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
                Assert.That(callbacks, Is.Empty);
                Assert.That(native.GetRemoteAddress().GetIdentifier(), Is.EqualTo("127.0.0.1"));
                Assert.That(fixture.Host.Retained, Is.EqualTo((2, 0L)), "Completion credits remain charged until delivery.");
            });
            Assert.That(fixture.Host.DrainCompletions(), Is.EqualTo(2));
            Assert.That(callbacks, Is.EqualTo(new[] { first, second }));
            Assert.That(fixture.Host.Retained, Is.EqualTo((0, 0L)));
        }
    }

    [Test]
    public async Task ReadUsesExactReceiveWindowAndCloseClaimsPendingCallbackOnce()
    {
        await using var fixture = new Fixture();
        var (peer, connection) = await fixture.ConnectAsync();
        using (peer)
        {
            ISocket native = connection;
            byte[] destination = [9, 9, 9, 9];
            object state = new();
            var read = new TaskCompletionSource<(object?, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
            native.AsyncReceive(destination, 2, 1, (current, count) => read.TrySetResult((current, count)), state);
            await peer.GetStream().WriteAsync(new byte[] { 42 });
            Assert.That(await read.Task.WaitAsync(Timeout), Is.EqualTo((state, 1)));
            Assert.That(destination, Is.EqualTo(new byte[] { 9, 9, 42, 9 }));

            int closeCallbacks = 0;
            var closed = new TaskCompletionSource<(object?, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
            native.AsyncReceive(destination, 0, 1, (current, count) =>
            {
                Interlocked.Increment(ref closeCallbacks);
                closed.TrySetResult((current, count));
            }, state);
            native.Close();
            native.Close();
            Assert.That(await closed.Task.WaitAsync(Timeout), Is.EqualTo((state, 0)));
            await connection.Completion.WaitAsync(Timeout);
            Assert.Multiple(() =>
            {
                Assert.That(closeCallbacks, Is.EqualTo(1));
                Assert.That(destination, Is.EqualTo(new byte[] { 9, 9, 42, 9 }));
                Assert.That(native.IsConnected(), Is.False);
            });
        }
    }

    [Test]
    public async Task ReceiveReadinessSignalsPeerFinAfterBufferedBytes()
    {
        await using var fixture = new Fixture();
        var (peer, connection) = await fixture.ConnectAsync();
        using (peer)
        {
            Assert.That(connection.IsDataAvailable(), Is.False);
            await peer.GetStream().WriteAsync(new byte[] { 42 });
            peer.Client.Shutdown(SocketShutdown.Send);
            await UntilAsync(connection.IsDataAvailable);

            byte[] destination = new byte[1];
            var payload = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.AsyncReceive(destination, 0, 1, (_, count) => payload.TrySetResult(count));
            Assert.That(await payload.Task.WaitAsync(Timeout), Is.EqualTo(1));
            Assert.That(destination[0], Is.EqualTo(42));

            await UntilAsync(connection.IsDataAvailable);
            var eof = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.AsyncReceive(destination, 0, 1, (_, count) => eof.TrySetResult(count));
            Assert.That(await eof.Task.WaitAsync(Timeout), Is.Zero);
            await connection.Completion.WaitAsync(Timeout);
            Assert.That(connection.IsConnected(), Is.False);
        }
    }

    [Test]
    public async Task ReplacementSocketCannotReceiveAnOldGenerationCompletion()
    {
        await using var fixture = new Fixture();
        var (oldPeer, previous) = await fixture.ConnectAsync();
        var (freshPeer, replacement) = await fixture.ConnectAsync();
        using (oldPeer)
        using (freshPeer)
        {
            fixture.Bind(3, previous);
            int oldCallbacks = 0, freshCallbacks = 0;
            previous.AsyncSend([1], 0, 1, _ => oldCallbacks++);
            await UntilAsync(() => previous.OutboundSnapshot.PendingCompletions == 1);
            // Simulate native slot identity changing before a stale callback is drained.
            fixture.NativeSlots[3] = replacement;
            Assert.That(fixture.Host.DrainCompletions(), Is.EqualTo(1));
            Assert.That(oldCallbacks, Is.Zero);
            fixture.Bind(3, replacement);
            fixture.Host.UnbindClosed(3, previous); // A late closed notification must not detach the replacement.
            replacement.AsyncSend([2], 0, 1, _ => freshCallbacks++);
            await UntilAsync(() => replacement.OutboundSnapshot.PendingCompletions == 1);
            Assert.That(fixture.Host.DrainCompletions(), Is.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(oldCallbacks, Is.Zero);
                Assert.That(freshCallbacks, Is.EqualTo(1));
                Assert.That(replacement.Generation, Is.GreaterThan(previous.Generation));
            });
        }
    }

    [Test]
    public async Task PerConnectionLimitRetiresOnlyOriginatingSocket()
    {
        await using var fixture = new Fixture(new NativeTcpTransportOptions
        {
            MaxOutstandingPerConnection = 1,
            CallbackBudget = TimeSpan.FromSeconds(1)
        });
        var (firstPeer, first) = await fixture.ConnectAsync();
        var (secondPeer, second) = await fixture.ConnectAsync();
        using (firstPeer)
        using (secondPeer)
        {
            fixture.Bind(1, first);
            fixture.Bind(2, second);
            int firstCallbacks = 0, secondCallbacks = 0;
            first.AsyncSend([1], 0, 1, _ => firstCallbacks++);
            await UntilAsync(() => first.OutboundSnapshot.PendingCompletions == 1);
            first.AsyncSend([2], 0, 1, _ => firstCallbacks++); // Only one unconsumed completion fits.
            second.AsyncSend([3], 0, 1, _ => secondCallbacks++);
            await first.Completion.WaitAsync(Timeout);
            await UntilAsync(() => second.OutboundSnapshot.PendingCompletions == 1);
            Assert.That(fixture.Host.DrainCompletions(), Is.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(first.IsClosed, Is.True);
                Assert.That(firstCallbacks, Is.Zero);
                Assert.That(second.IsConnected(), Is.True);
                Assert.That(secondCallbacks, Is.EqualTo(1));
                Assert.That(fixture.Host.Retained, Is.EqualTo((0, 0L)));
            });
        }
    }

    [Test]
    public async Task WorldThreadDrainsCompletedSendsDuringBurstBeforeWorldQueueFills()
    {
        await using var fixture = new Fixture(new NativeTcpTransportOptions
        {
            MaxOutstandingPerConnection = 16,
            MaxOutstandingPerWorld = 16,
            MaxCallbacksPerPass = 1,
            CallbackBudget = TimeSpan.FromSeconds(1)
        });
        var (peer, connection) = await fixture.ConnectAsync();
        using (peer)
        {
            fixture.Bind(1, connection);
            int callbacks = 0;
            int worldThread = 0;
            int callbackThread = 0;
            SocketSendCallback callback = _ =>
            {
                callbackThread = Environment.CurrentManagedThreadId;
                callbacks++;
            };
            for (int index = 0; index < 16; index++) connection.AsyncSend([1], 0, 1, callback);
            await UntilAsync(() => connection.OutboundSnapshot.PendingCompletions == 16);

            worldThread = Environment.CurrentManagedThreadId;
            Assert.That(fixture.Host.DrainCompletions(), Is.EqualTo(1));
            for (int index = 0; index < 4; index++) connection.AsyncSend([2], 0, 1, callback);

            Assert.Multiple(() =>
            {
                Assert.That(connection.IsClosed, Is.False);
                Assert.That(callbacks, Is.EqualTo(5));
                Assert.That(callbackThread, Is.EqualTo(worldThread));
            });
        }
    }

    [Test]
    public async Task SharedWorldBudgetCountsCompletedRequestsAcrossConnections()
    {
        await using var fixture = new Fixture(new NativeTcpTransportOptions
        {
            MaxOutstandingPerConnection = 2,
            MaxOutstandingPerWorld = 2,
            CallbackBudget = TimeSpan.FromSeconds(1)
        });
        var (firstPeer, first) = await fixture.ConnectAsync();
        var (secondPeer, second) = await fixture.ConnectAsync();
        using (firstPeer)
        using (secondPeer)
        {
            fixture.Bind(1, first);
            fixture.Bind(2, second);
            int acceptedCallbacks = 0, rejectedCallbacks = 0;
            first.AsyncSend([1], 0, 1, _ => acceptedCallbacks++);
            first.AsyncSend([2], 0, 1, _ => acceptedCallbacks++);
            await UntilAsync(() => first.OutboundSnapshot.PendingCompletions == 2);
            second.AsyncSend([3], 0, 1, _ => rejectedCallbacks++);
            await second.Completion.WaitAsync(Timeout);
            Assert.That(fixture.Host.Retained, Is.EqualTo((2, 0L)));
            Assert.That(fixture.Host.DrainCompletions(), Is.EqualTo(2));
            Assert.Multiple(() =>
            {
                Assert.That(first.IsConnected(), Is.True);
                Assert.That(second.IsClosed, Is.True);
                Assert.That(acceptedCallbacks, Is.EqualTo(2));
                Assert.That(rejectedCallbacks, Is.Zero);
                Assert.That(fixture.Host.Retained, Is.EqualTo((0, 0L)));
            });
        }
    }

    [Test]
    public async Task StoppingListenerDoesNotCloseAcceptedSocketButStoppingHostDoes()
    {
        await using var fixture = new Fixture();
        var (peer, connection) = await fixture.ConnectAsync();
        using (peer)
        {
            fixture.Bind(1, connection);
            fixture.Host.Listener.StopListening();
            fixture.Host.Listener.StopListening();
            await fixture.Host.Listener.Completion.WaitAsync(Timeout);
            Assert.That(connection.IsConnected(), Is.True);
            connection.AsyncSend([9], 0, 1, _ => { });
            byte[] received = new byte[1];
            await peer.GetStream().ReadExactlyAsync(received).AsTask().WaitAsync(Timeout);
            Assert.That(received[0], Is.EqualTo(9));
            using var deadline = new CancellationTokenSource(Timeout);
            await fixture.Host.StopAsync(deadline.Token);
            Assert.That(connection.IsClosed, Is.True);
            Assert.That(fixture.Host.Retained, Is.EqualTo((0, 0L)));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CompletionBudgetMaintainsRoundRobinFairnessAcrossSparseSlots(bool activeSlotSnapshot)
    {
        await using var fixture = new Fixture(new NativeTcpTransportOptions
        {
            MaxCallbacksPerPass = 1,
            CallbackBudget = TimeSpan.FromSeconds(1)
        }, activeSlotSnapshot);
        var (firstPeer, first) = await fixture.ConnectAsync();
        var (secondPeer, second) = await fixture.ConnectAsync();
        using (firstPeer)
        using (secondPeer)
        {
            fixture.Bind(3, first);
            fixture.Bind(219, second);
            var callbacks = new List<int>();
            first.AsyncSend([1], 0, 1, _ => callbacks.Add(1));
            first.AsyncSend([2], 0, 1, _ => callbacks.Add(3));
            second.AsyncSend([4], 0, 1, _ => callbacks.Add(2));
            await UntilAsync(() => first.OutboundSnapshot.PendingCompletions == 2 && second.OutboundSnapshot.PendingCompletions == 1);
            for (int i = 0; i < 3; i++) Assert.That(fixture.Host.DrainCompletions(), Is.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(callbacks, Is.EqualTo(new[] { 1, 2, 3 }));
                Assert.That(fixture.Host.Retained, Is.EqualTo((0, 0L)));
                Assert.That(fixture.Host.DrainCompletions(), Is.Zero);
            });
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task CloseReleasesOutstandingSocketWritesWithEitherCancellationMode(bool cancelPendingSocketSend)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var peer = new TcpClient { ReceiveBufferSize = 4096 };
        using var deadline = new CancellationTokenSource(Timeout);
        await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
        using Socket socket = await listener.AcceptSocketAsync(deadline.Token);
        socket.SendBufferSize = 4096;
        var options = new NativeTcpTransportOptions { CancelPendingSocketSend = cancelPendingSocketSend };
        var budget = new NativeOutboundBudget(options.MaxOutstandingPerWorld, options.MaxRetainedBytesPerWorld);
        var connection = new NativeTcpConnection(socket, 1, options, budget);
        byte[] payload = new byte[options.MaxSendBytes];
        int callbacks = 0;
        try
        {
            // Ordinary bounded local data with a temporarily paused receiver. The OS may buffer
            // a write completely; in that case its pending callback credit must still be retired.
            for (int i = 0; i < 4; i++)
                connection.AsyncSend(payload, 0, payload.Length, _ => Interlocked.Increment(ref callbacks));
            await UntilAsync(() => connection.OutboundSnapshot is { PendingRequests: < 4, OutstandingRequests: 4 });
        }
        finally { connection.Close(); }
        await connection.Completion.WaitAsync(Timeout);
        Assert.Multiple(() =>
        {
            Assert.That(connection.IsClosed, Is.True);
            Assert.That(connection.OutboundSnapshot.OutstandingRequests, Is.Zero);
            Assert.That(connection.OutboundSnapshot.RetainedBytes, Is.Zero);
            Assert.That(budget.Snapshot, Is.EqualTo((0, 0L)));
            Assert.That(connection.TryReadCompletion(out _), Is.False);
            Assert.That(callbacks, Is.Zero);
        });
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Channel<NativeTcpConnection> accepted = Channel.CreateUnbounded<NativeTcpConnection>();
        public ConcurrentDictionary<int, ISocket> NativeSlots { get; } = new();
        public NativeTcpTransportHost Host { get; }

        public Fixture(NativeTcpTransportOptions? options = null, bool activeSlotSnapshot = true)
        {
            Host = new NativeTcpTransportHost(options ?? new NativeTcpTransportOptions { CallbackBudget = TimeSpan.FromSeconds(1) },
                () => new IPEndPoint(IPAddress.Loopback, 0), slot => NativeSlots.TryGetValue(slot, out ISocket? socket) ? socket : null,
                activeSlotSnapshot);
            Assert.That(Host.Listener.StartListening(socket => accepted.Writer.TryWrite((NativeTcpConnection)socket)), Is.True);
        }

        public async Task<(TcpClient, NativeTcpConnection)> ConnectAsync()
        {
            var peer = new TcpClient();
            try
            {
                await peer.ConnectAsync(IPAddress.Loopback, Host.Listener.LocalEndPoint!.Port).WaitAsync(Timeout);
                NativeTcpConnection connection = await accepted.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
                return (peer, connection);
            }
            catch { peer.Dispose(); throw; }
        }

        public void Bind(int slot, NativeTcpConnection connection)
        {
            NativeSlots[slot] = connection;
            Host.BindActivated(slot, connection);
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(Timeout);
            await Host.StopAsync(timeout.Token);
        }
    }
}
