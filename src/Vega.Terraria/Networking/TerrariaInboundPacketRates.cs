using Terraria.Net.Sockets;
using TerrariaRuntime;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Networking;

public sealed class TerrariaInboundPacketRates(IWorldGameplayEvents events, INetworkMetricsRecorder metrics,
    TimeProvider? timeProvider = null) : IInboundPacketRateReader, IDisposable
{
    private static readonly PacketProcessingDecision Dropped = PacketProcessingDecision.Reject("packet.plugin_rate_policy");
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Window?[] connections = new Window[256];
    private bool attached;

    internal void Attach()
    {
        if (attached) return;
        Hooks.Netplay.ServerConnectionActivated += OnActivated;
        Hooks.Netplay.ServerConnectionClosed += OnClosed;
        Hooks.MessageBuffer.GetData += OnReceive;
        attached = true;
    }

    public void Dispose()
    {
        if (!attached) return;
        Hooks.MessageBuffer.GetData -= OnReceive;
        Hooks.Netplay.ServerConnectionActivated -= OnActivated;
        Hooks.Netplay.ServerConnectionClosed -= OnClosed;
        Array.Clear(connections);
        attached = false;
    }

    public InboundPacketRate? Read(ConnectionId connection, byte packetId) =>
        Find(connection.Slot)?.Sample(connection, packetId, clock, receive: false);

    private Window? Find(int slot) => (uint)slot < connections.Length ? Volatile.Read(ref connections[slot]) : null;

    private void OnActivated(int slot, ISocket socket)
    {
        if ((uint)slot < connections.Length)
            Volatile.Write(ref connections[slot], new Window(socket, clock.GetTimestamp()));
    }

    private void OnClosed(int slot, ISocket socket)
    {
        if (Find(slot) is { } window && ReferenceEquals(window.Socket, socket))
            Interlocked.CompareExchange(ref connections[slot], null, window);
    }

    private void OnReceive(object? sender, Hooks.MessageBuffer.GetDataEventArgs args)
    {
        int slot = args.Instance.whoAmI;
        if (Find(slot) is not { } window) return;
        var sample = window.Sample(new(slot), args.PacketId, clock, receive: true);
        if (args.Result == HookResult.Cancel || events.Allows(sample)) return;
        args.Result = HookResult.Cancel;
        metrics.RecordSecurityDecision(Dropped);
    }

    private sealed class Window(ISocket socket, long start)
    {
        public ISocket Socket { get; } = socket;
        private readonly int[] counts = new int[byte.MaxValue + 1];
        private int total;

        public InboundPacketRate Sample(ConnectionId connection, byte id, TimeProvider clock, bool receive)
        {
            lock (counts)
            {
                long now = clock.GetTimestamp();
                if (now - start >= clock.TimestampFrequency)
                {
                    if (!receive) return new(connection, id, 0, 0);
                    Array.Clear(counts);
                    start = now;
                    total = 0;
                }
                if (receive)
                {
                    if (counts[id] < int.MaxValue) counts[id]++;
                    if (total < int.MaxValue) total++;
                }
                return new(connection, id, counts[id], total);
            }
        }
    }
}
