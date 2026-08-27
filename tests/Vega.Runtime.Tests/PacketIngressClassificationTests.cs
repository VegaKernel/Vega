using Multiplicity.Packets;
using NUnit.Framework;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class PacketIngressClassificationTests
{
    [Test]
    public void EveryKnownProtocolPacketHasExplicitIngressDisposition()
    {
        PacketTypes[] known = Enum.GetValues<PacketTypes>();
        PacketTypes[] unknown = known
            .Where(packet => PacketPipelineClassification.GetIngressDisposition(packet) == PacketIngressDisposition.Unknown)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(unknown, Is.Empty);
            Assert.That(known.Max(static packet => (byte)packet), Is.EqualTo(PacketPipelineClassification.HighestAuditedPacketId));
            Assert.That(PacketPipelineClassification.HighestAuditedPacketId, Is.EqualTo((byte)164));
        });
    }

    [TestCase(PacketTypes.NpcItemStrike)]
    [TestCase(PacketTypes.NumberOfAnglerQuestsCompleted)]
    [TestCase(PacketTypes.NpcTeleportPortal)]
    [TestCase(PacketTypes.SyncCavernMonsterType)]
    [TestCase(PacketTypes.DebugCommands)]
    public void KnownServerOriginOrExploitPacketsAreNeverClientGameplay(PacketTypes packetType)
    {
        Assert.That(
            PacketPipelineClassification.GetIngressDisposition(packetType),
            Is.EqualTo(PacketIngressDisposition.ServerOnly));
    }

    [Test]
    public void AnglerCompletionRequestIsClientGameplayButReportedCountIsServerOnly()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                PacketPipelineClassification.GetIngressDisposition(PacketTypes.CompleteAnglerQuest),
                Is.EqualTo(PacketIngressDisposition.GameplayValidated));
            Assert.That(
                PacketPipelineClassification.GetIngressDisposition(PacketTypes.NumberOfAnglerQuestsCompleted),
                Is.EqualTo(PacketIngressDisposition.ServerOnly));
        });
    }

    [TestCase(PacketTypes.PlayerInfo)]
    [TestCase(PacketTypes.PlayerSlot)]
    [TestCase(PacketTypes.TileGetSection)]
    [TestCase(PacketTypes.PlayerSpawn)]
    [TestCase(PacketTypes.RequestSection)]
    public void EarlyPacketsAreValidBothBeforeAndAfterLogin(PacketTypes packetType)
    {
        Assert.That(
            PacketPipelineClassification.GetIngressDisposition(packetType),
            Is.EqualTo(PacketIngressDisposition.EarlyValidated));
    }
}
