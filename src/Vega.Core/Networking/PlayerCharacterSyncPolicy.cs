using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>Structural checks on the native, joined-client character metadata synchronization path.</summary>
public sealed class PlayerCharacterSyncPolicy(Func<int> nativeBuffSlots, int buffTypeCount) : IPacketProcessor<PacketView>
{
    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound) return PacketProcessingDecision.Allow;
        int slot = context.ConnectionId.Slot;
        switch (context.Packet.PacketType)
        {
            case PacketTypes.PlayerInfo:
                var appearance = context.Packet.AsPlayerInfoView();
                if (appearance.PlayerId != slot) return Ownership();
                return float.IsFinite(appearance.VoicePitchOffset) ? PacketProcessingDecision.Allow :
                    PacketProcessingDecision.Violation("packet.character.voice_non_finite");
            case PacketTypes.PlayerAnimation:
                var animation = context.Packet.AsPlayerAnimationView();
                if (animation.PlayerId != slot) return Ownership();
                return float.IsFinite(animation.ItemRotation) ? PacketProcessingDecision.Allow :
                    PacketProcessingDecision.Violation("packet.character.rotation_non_finite");
            case PacketTypes.Zones:
                return Match(context.Packet.AsZonesView().PlayerId, slot);
            case PacketTypes.Emoji:
                return Match(context.Packet.AsEmojiView().PlayerId, slot);
            case PacketTypes.NumberOfAnglerQuestsCompleted:
                return Match(context.Packet.AsNumberOfAnglerQuestsCompletedView().PlayerId, slot);
            case PacketTypes.PlayerBuff:
                // Read UInt16 buff IDs directly from the bounded packet; the ordinary path need not allocate an array.
                var reader = context.Packet.CreatePayloadReader();
                if (reader.ReadByte() != slot) return Ownership();
                int capacity = nativeBuffSlots();
                int bytes = reader.RemainingCount;
                // Protocol 329 emits only positive occupied slots followed by a UInt16 zero terminator.
                if (capacity <= 0 || bytes < sizeof(ushort) || bytes % sizeof(ushort) != 0 ||
                    bytes / sizeof(ushort) > capacity + 1)
                    return PacketProcessingDecision.Violation("packet.character.buff_layout");
                int occupied = bytes / sizeof(ushort) - 1;
                for (int index = 0; index < occupied; index++)
                {
                    ushort type = reader.ReadUInt16();
                    if (type == 0 || type >= buffTypeCount)
                        return PacketProcessingDecision.Violation("packet.character.buff_type_out_of_range");
                }
                return reader.ReadUInt16() == 0 ? PacketProcessingDecision.Allow :
                    PacketProcessingDecision.Violation("packet.character.buff_layout");
            default:
                return PacketProcessingDecision.Allow;
        }
    }

    private static PacketProcessingDecision Match(int claimed, int actual) => claimed == actual ? PacketProcessingDecision.Allow : Ownership();
    private static PacketProcessingDecision Ownership() => PacketProcessingDecision.Violation("packet.player_identity_mismatch");
}
