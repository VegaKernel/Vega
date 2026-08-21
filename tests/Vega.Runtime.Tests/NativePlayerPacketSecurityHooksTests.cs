using Multiplicity.Packets;
using NUnit.Framework;
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
}
