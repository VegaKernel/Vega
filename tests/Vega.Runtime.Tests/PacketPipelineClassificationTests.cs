using Multiplicity.Packets;
using NUnit.Framework;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class PacketPipelineClassificationTests
{
    [TestCase(PacketTypes.PlayerUpdate, "PlayerState", true)]
    [TestCase(PacketTypes.PlayerInfo, "PlayerState", true)]
    [TestCase(PacketTypes.PlayerBuff, "PlayerState", true)]
    [TestCase(PacketTypes.ProjectileNew, "Projectile", true)]
    [TestCase(PacketTypes.Tile, "WorldInteraction", false)]
    [TestCase(PacketTypes.ChestItem, "Chest", false)]
    [TestCase(PacketTypes.RequestTileEntityInteraction, "TileEntity", false)]
    public void CurrentSecurityFamiliesAreOnlyPartiallyOffloadable(
        PacketTypes packetType,
        string family,
        bool latencySensitive)
    {
        PacketPipelineProfile profile = PacketPipelineClassification.Get(packetType);

        Assert.Multiple(() =>
        {
            Assert.That(profile.Family.ToString(), Is.EqualTo(family));
            Assert.That(profile.Preparation, Is.EqualTo(PacketPreparationClass.PartiallyOffloadable));
            Assert.That(profile.SecurityProcessed, Is.True);
            Assert.That(profile.OrderSensitive, Is.True);
            Assert.That(profile.WorldStateSensitive, Is.True);
            Assert.That(profile.LatencySensitive, Is.EqualTo(latencySensitive));
            Assert.That(profile.SafelyCoalescable, Is.False);
        });
    }

    [Test]
    public void UnclassifiedPacketsRemainOnTheGameThread()
    {
        PacketPipelineProfile profile = PacketPipelineClassification.Get(PacketTypes.ConnectRequest);

        Assert.Multiple(() =>
        {
            Assert.That(profile.Family, Is.EqualTo(PacketFamily.Other));
            Assert.That(profile.Preparation, Is.EqualTo(PacketPreparationClass.GameThreadOnly));
            Assert.That(profile.SecurityProcessed, Is.False);
            Assert.That(profile.SafelyCoalescable, Is.False);
        });
    }

    [Test]
    public void EveryTerrariaPacketTypeHasAConservativeProfile()
    {
        PacketPipelineProfile[] profiles = Enum
            .GetValues<PacketTypes>()
            .Select(PacketPipelineClassification.Get)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(profiles, Is.Not.Empty);
            Assert.That(profiles.All(static profile => profile.OrderSensitive), Is.True);
            Assert.That(profiles.All(static profile => !profile.SafelyCoalescable), Is.True);
        });
    }
}
