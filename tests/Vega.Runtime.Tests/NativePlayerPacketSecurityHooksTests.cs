using Multiplicity.Packets;
using NUnit.Framework;
using TerrariaRuntime;
using Vega.Contracts.Networking;
using Vega.Terraria.Hooking;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class NativePlayerPacketSecurityHooksTests
{
    [Test]
    public void RuntimeHookLengthIncludesPacketIdButVegaPayloadDoesNot()
    {
        const int start = 10;
        const int hookLength = 4;
        var buffer = new global::Terraria.MessageBuffer { whoAmI = 0 };
        global::Terraria.RemoteClient? previous = global::Terraria.Netplay.Clients[0];
        global::Terraria.Netplay.Clients[0] = new global::Terraria.RemoteClient();
        buffer.ResetReader();
        buffer.readBuffer[start] = (byte)PacketTypes.Ping;
        buffer.readBuffer[start + 1] = 0x11;
        buffer.readBuffer[start + 2] = 0x22;
        buffer.readBuffer[start + 3] = 0x33;
        buffer.readBuffer[start + 4] = 0xEE;
        int capturedReadOffset = -1;
        int capturedLength = -1;
        byte[]? payload = null;

        void Capture(object? sender, Hooks.MessageBuffer.GetDataEventArgs args)
        {
            capturedReadOffset = args.ReadOffset;
            capturedLength = args.Length;
            payload = NativePlayerPacketSecurityHooks.GetPayload(args).ToArray();
            args.Result = HookResult.Cancel;
        }

        Hooks.MessageBuffer.GetData += Capture;
        try
        {
            buffer.GetData(start, hookLength, out _);
        }
        finally
        {
            Hooks.MessageBuffer.GetData -= Capture;
            global::Terraria.Netplay.Clients[0] = previous!;
        }

        Assert.Multiple(() =>
        {
            Assert.That(capturedReadOffset, Is.EqualTo(start + 1));
            Assert.That(capturedLength, Is.EqualTo(hookLength));
            Assert.That(payload, Is.EqualTo(new byte[] { 0x11, 0x22, 0x33 }));
        });
    }

    [TestCase((int)TerrariaConnectionState.AwaitingHello, PacketTypes.PlayerHp, false)]
    [TestCase((int)TerrariaConnectionState.FullyConnected - 1, PacketTypes.PlayerMana, false)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.PlayerHp, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.ItemDrop, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.ProjectileDestroy, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.Tile, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.PlayerTeleportPortal, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.TELeashedEntityAnchorPlaceItem, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected + 1, PacketTypes.ProjectileNew, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.PlayerSpawn, false)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.PlayerSlot, false)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.NpcItemStrike, false)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.SyncChestSize, false)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.SyncItemsWithShimmerDeprecated, false)]
    public void CoreGameplaySecurityRunsOnlyForGameplayValidatedPackets(
        int connectionState,
        PacketTypes packetType,
        bool expected)
    {
        Assert.That(
            NativePlayerPacketSecurityHooks.ShouldApplySecurity(
                (TerrariaConnectionState)connectionState,
                (byte)packetType),
            Is.EqualTo(expected));
    }

    [TestCase((int)TerrariaConnectionState.PasswordChallenge, PacketDecisionKind.Reject, true)]
    [TestCase((int)TerrariaConnectionState.ProtocolAccepted, PacketDecisionKind.Reject, true)]
    [TestCase((int)TerrariaConnectionState.SendingWorld, PacketDecisionKind.Violation, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketDecisionKind.Reject, false)]
    [TestCase((int)TerrariaConnectionState.ProtocolAccepted, PacketDecisionKind.Allow, false)]
    public void RejectedHandshakePacketsTerminateOnlyPreLoginConnections(
        int connectionState,
        PacketDecisionKind decisionKind,
        bool expected)
    {
        Assert.That(
            NativePlayerPacketSecurityHooks.ShouldTerminateConnection(
                (TerrariaConnectionState)connectionState,
                decisionKind),
            Is.EqualTo(expected));
    }
}
