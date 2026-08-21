using Multiplicity.Packets;
using NUnit.Framework;
using Vega.Terraria.Hooking;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class NativePlayerPacketSecurityHooksTests
{
    [TestCase(TerrariaConnectionState.AwaitingHello, PacketTypes.PlayerHp, false)]
    [TestCase((TerrariaConnectionState)9, PacketTypes.PlayerMana, false)]
    [TestCase(TerrariaConnectionState.FullyConnected, PacketTypes.PlayerHp, true)]
    [TestCase(TerrariaConnectionState.FullyConnected, PacketTypes.ItemDrop, true)]
    [TestCase((TerrariaConnectionState)11, PacketTypes.ProjectileNew, true)]
    [TestCase(TerrariaConnectionState.FullyConnected, PacketTypes.PlayerSpawn, false)]
    public void GameplaySecurityStartsOnlyAfterVanillaConnectionCompletes(
        TerrariaConnectionState connectionState,
        PacketTypes packetType,
        bool expected)
    {
        Assert.That(
            NativePlayerPacketSecurityHooks.ShouldApplySecurity(connectionState, (byte)packetType),
            Is.EqualTo(expected));
    }
}
