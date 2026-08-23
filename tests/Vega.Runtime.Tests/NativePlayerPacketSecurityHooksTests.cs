using Multiplicity.Packets;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Terraria.Hooking;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class NativePlayerPacketSecurityHooksTests
{
    [TestCase((int)TerrariaConnectionState.AwaitingHello, PacketTypes.PlayerHp, false)]
    [TestCase((int)TerrariaConnectionState.FullyConnected - 1, PacketTypes.PlayerMana, false)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.PlayerHp, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.ItemDrop, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.SyncItemsWithShimmerDeprecated, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.SyncItemPosition, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.ProjectileDestroy, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.Tile, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.DoorUse, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.PaintTile, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.SignNew, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.SyncChestSize, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.TELeashedEntityAnchorPlaceItem, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.PlayerTeleportPortal, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.LiquidSet, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.PlaceTileEntity, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.MassWireOperation, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.FoodPlatterTryPlacing, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.TEDeadCellsDisplayJar, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected + 1, PacketTypes.ProjectileNew, true)]
    [TestCase((int)TerrariaConnectionState.FullyConnected, PacketTypes.PlayerSpawn, false)]
    public void GameplaySecurityStartsOnlyAfterVanillaConnectionCompletes(
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
