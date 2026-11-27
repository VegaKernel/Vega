using NUnit.Framework;
using Terraria.Net.Sockets;
using TerrariaRuntime;
using Vega.Contracts.Networking;
using Vega.Core.Commands;
using Vega.Core.Events;
using Vega.Core.Modules;
using Vega.Operations.Logging;
using Vega.Operations.Metrics;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[NonParallelizable]
public sealed class InboundPacketRateTests
{
    [Test]
    public async Task NativeIngressCountsEveryIdAndScopesDropsToTheCurrentConnectionAndPlugin()
    {
        var clock = new Clock();
        var events = new WorldGameplayEventHub();
        var metrics = new NetworkMetricsCounter();
        using var rates = new TerrariaInboundPacketRates(events, metrics, clock);
        await using var plugin = new HotModuleRegistrationScope(new CommandDispatcher([]), new StructuredOperationsLogger([]),
            "Fixture.PacketPolicy", Path.GetTempPath(), worldEvents: events, packetRates: rates);
        const byte movement = global::Terraria.ID.MessageID.PlayerControls;
        var client = new global::Terraria.RemoteClient { Id = 3 };
        var other = new global::Terraria.RemoteClient { Id = 4 };
        var oldSocket = new TcpSocket();
        var newSocket = new TcpSocket();
        var buffer = new global::Terraria.MessageBuffer();
        bool Receive(int slot, byte id)
        {
            buffer.whoAmI = slot;
            int start = 0, readOffset = 1, length = 1, type = id;
            return Hooks.MessageBuffer.InvokeGetData(buffer, ref id, ref readOffset, ref start, ref length, ref type,
                global::Terraria.ID.MessageID.Count);
        }

        bool wasDedicated = global::Terraria.Main.dedServ;
        try
        {
            global::Terraria.Main.dedServ = true;
            rates.Attach();
            Hooks.Netplay.NotifyServerConnectionActivated(client, oldSocket);
            Hooks.Netplay.NotifyServerConnectionActivated(other, new TcpSocket());
            for (int i = 0; i <= byte.MaxValue; i++) Receive(3, (byte)i);
            for (int i = 0; i <= byte.MaxValue; i++)
                Assert.That(plugin.Packets.Read(new(3), (byte)i), Is.EqualTo(new InboundPacketRate(new(3), (byte)i, 1, 256)));
            Assert.That(Receive(3, 255), Is.False, "Unknown IDs still follow the native rejection path.");
            plugin.Packets.AllowInbound(p => p.PacketId != movement || p.TypeCount <= 1);
            Assert.That(Receive(3, movement), Is.False, "Installing a policy must preserve earlier traffic.");
            Assert.That(Receive(3, movement), Is.False);
            Assert.That(plugin.Packets.Read(new(3), movement)!.Value.TypeCount, Is.EqualTo(3));
            Assert.That(Receive(4, movement), Is.True);
            Assert.That(metrics.Read().RejectedInboundPackets, Is.EqualTo(2));
            clock.Milliseconds = 999;
            Assert.That(Receive(3, movement), Is.False);
            clock.Milliseconds = 1000;
            Assert.That(plugin.Packets.Read(new(3), movement)!.Value.TotalCount, Is.Zero);
            Assert.That(Receive(3, movement), Is.True);
            Assert.That(Receive(3, movement), Is.False);

            Hooks.Netplay.NotifyServerConnectionActivated(client, newSocket);
            Hooks.Netplay.NotifyServerConnectionClosed(client, oldSocket);
            Assert.That(Receive(3, movement), Is.True);
            Assert.That(Receive(3, movement), Is.False);
            await plugin.DisposeAsync();
            Assert.That(Receive(3, movement), Is.True);
            Assert.That(rates.Read(new(3), movement)!.Value.TypeCount, Is.EqualTo(3));
            Hooks.Netplay.NotifyServerConnectionClosed(client, newSocket);
            Assert.That(rates.Read(new(3), movement), Is.Null);
        }
        finally { global::Terraria.Main.dedServ = wasDedicated; }
    }

    private sealed class Clock : TimeProvider
    {
        public long Milliseconds { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }
}
