using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria.ID;
using TerrariaRuntime;
using Vega.Terraria.Hooking;

namespace Vega.Runtime.Tests;

public sealed class NativeMovementInterestHooksTests
{
    [Test, NonParallelizable]
    public void OnlyValidNearbyRelaysResetDistantHistory()
    {
        const int source = 1, target = 2;
        var players = global::Terraria.Main.player;
        var clients = global::Terraria.Netplay.Clients;
        var oldSource = players[source];
        var oldTarget = players[target];
        var oldSender = clients[source];
        var oldRemote = clients[target];
        var oldBuffer = global::Terraria.NetMessage.buffer[target];
        using var filter = new NativeMovementInterestHooks(1, () => true);
        bool suppressed = false;
        void Observe(ref Hooks.NetMessage.ReadOnlySendBytesContext bytes)
        {
            suppressed = bytes.Cancelled;
            bytes.Cancel();
        }
        try
        {
            players[source] = new() { active = true };
            players[target] = new() { active = true };
            clients[source] = new() { State = 10 };
            var remote = clients[target] = new() { Id = target, State = 10 };
            var buffer = global::Terraria.NetMessage.buffer[target] = new();
            filter.Attach();
            Hooks.NetMessage.SendBytesReadOnly += Observe;
            Hooks.NetMessage.InvokeSendData(new() { Event = HookEvent.Before, MsgType = MessageID.PlayerControls,
                RemoteClient = -1, IgnoreClient = source, Number = source });

            bool Suppressed(int distance, byte packet = MessageID.PlayerControls)
            {
                players[target].position.X = distance;
                byte[] bytes = new byte[17];
                bytes[2] = packet;
                bytes[3] = source;
                Hooks.NetMessage.InvokeSendBytes(remote.Socket, bytes, 0, bytes.Length, remote.ServerWriteCallBack, null, target);
                return suppressed;
            }
            Assert.That(Suppressed(0), Is.False);
            Assert.That(Suppressed(4000), Is.False);
            Assert.That(Suppressed(4000), Is.True);
            Assert.That(buffer.spamCount, Is.EqualTo(-1));
            Assert.That(Suppressed(0, MessageID.SyncPlayer), Is.False);
            Assert.That(Suppressed(3200), Is.True);
            Assert.That(Suppressed(0), Is.False);
            Assert.That(Suppressed(3200), Is.False);
            Assert.That(Suppressed(4000), Is.False);
            Assert.That(Suppressed(4000), Is.True);
        }
        finally
        {
            Hooks.NetMessage.SendBytesReadOnly -= Observe;
            players[source] = oldSource;
            players[target] = oldTarget;
            clients[source] = oldSender;
            clients[target] = oldRemote;
            global::Terraria.NetMessage.buffer[target] = oldBuffer;
        }
    }

    [Test]
    public void DistantSamplesPreserveTransitionsAndResetWhenSlotsAreReused()
    {
        var sample = new NativeMovementInterestHooks.MovementSample();
        var distant = new Vector2(4000, 0);
        var boundary = new Vector2(3200, 0);
        Assert.That(sample.ShouldSend(default, distant, 0, false, 1, 1, 1, 20), Is.True);
        Assert.That(sample.ShouldSend(default, boundary, 0, false, 1, 1, 2, 20), Is.False);
        Assert.That(sample.ShouldSend(default, distant, 0, false, 1, 1, 21, 20), Is.True);
        Assert.That(sample.ShouldSend(default, distant, 1, false, 1, 1, 22, 20), Is.True);
        Assert.That(sample.ShouldSend(default, distant, 1, true, 1, 1, 23, 20), Is.True);
        Assert.That(sample.ShouldSend(new Vector2(0, 700), distant, 1, false, 1, 1, 24, 20), Is.True);
        Assert.That(sample.ShouldSend(default, distant, 1, false, 1, 2, 25, 20), Is.True);
        Assert.That(sample.ShouldSend(default, distant, 1, false, 2, 2, 26, 20), Is.True);
        Assert.That(sample.ShouldSend(default, default, 1, false, 2, 2, 27, 20), Is.True);
        Assert.That(sample.ShouldSend(default, boundary, 1, false, 2, 2, 28, 20), Is.True);
    }
}
