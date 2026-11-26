using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Core.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class PlayerCharacterSyncPolicyTests
{
    private static IEnumerable<TerrariaPacket> OrdinaryPackets()
    {
        yield return new PlayerInfo { PlayerId = 7, Name = "Player", VoicePitchOffset = 0.25f };
        yield return new Zones { PlayerId = 7, Zone1 = 1 };
        yield return new PlayerAnimation { PlayerId = 7, ItemRotation = 0.5f, ItemAnimation = 10, ItemAnimationMax = 20 };
        yield return new Emoji { PlayerId = 7, EmoteId = 1 };
        yield return new NumberOfAnglerQuestsCompleted { PlayerId = 7, AnglerQuestsCompleted = 10, GolferScoreAccumulated = 4 };
        yield return new PlayerBuff { PlayerId = 7, BuffTypes = [1, 2, 0] };
    }

    [TestCaseSource(nameof(OrdinaryPackets))]
    public void OrdinarySelfSynchronizationRemainsSupported(TerrariaPacket packet)
    {
        Assert.That(Process(packet, 7, PacketDirection.Inbound).Kind, Is.EqualTo(PacketDecisionKind.Allow));
        Assert.That(Process(packet, 7, PacketDirection.Outbound).Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    [Test]
    public void NativeBuffSlotCountAndTypeBoundsAreEnforced()
    {
        Assert.That(Process(new PlayerBuff { PlayerId = 7, BuffTypes = [0] }, 7, PacketDirection.Inbound).Kind, Is.EqualTo(PacketDecisionKind.Allow));
        Assert.That(Process(new PlayerBuff { PlayerId = 7, BuffTypes = [1, 2, 3, 4, 0] }, 7, PacketDirection.Inbound).Kind, Is.EqualTo(PacketDecisionKind.Allow));
        Assert.That(Process(new PlayerBuff { PlayerId = 7, BuffTypes = [1, 2, 3, 4, 5, 0] }, 7, PacketDirection.Inbound).Reason, Is.EqualTo("packet.character.buff_layout"));
        Assert.That(Process(new PlayerBuff { PlayerId = 7, BuffTypes = [1, 2, 500, 0] }, 7, PacketDirection.Inbound).Reason, Is.EqualTo("packet.character.buff_type_out_of_range"));
    }

    private static PacketProcessingDecision Process(TerrariaPacket packet, int slot, PacketDirection direction)
    {
        using var buffer = new MemoryStream();
        packet.ToStream(buffer, includeHeader: false);
        var view = PacketView.FromPayload(packet.PacketType, buffer.ToArray());
        var context = new PacketProcessingContext<PacketView>(view, direction, new ConnectionId(slot), null, DateTimeOffset.UtcNow);
        return new PlayerCharacterSyncPolicy(() => 4, 500).Process(in context);
    }
}
