using Multiplicity.Packets;
using NUnit.Framework;
using Vega.Terraria.Hooking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class NativePlayerPacketSecurityHooksTests
{
    [TestCase(3, PacketTypes.PlayerHp, false)]
    [TestCase(9, PacketTypes.PlayerMana, false)]
    [TestCase(10, PacketTypes.PlayerHp, true)]
    [TestCase(10, PacketTypes.ItemDrop, true)]
    [TestCase(11, PacketTypes.ProjectileNew, true)]
    [TestCase(10, PacketTypes.PlayerSpawn, false)]
    public void GameplaySecurityStartsOnlyAfterVanillaConnectionCompletes(
        int connectionState,
        PacketTypes packetType,
        bool expected)
    {
        Assert.That(
            NativePlayerPacketSecurityHooks.ShouldApplySecurity(connectionState, (byte)packetType),
            Is.EqualTo(expected));
    }
}
