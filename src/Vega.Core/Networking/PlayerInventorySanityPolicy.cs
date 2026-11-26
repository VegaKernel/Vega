using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>Validates native inventory and loadout synchronization without replacing vanilla item behavior.</summary>
public sealed class PlayerInventorySanityPolicy(
    Func<int> slotCount,
    Func<int> loadoutCount,
    int itemTypeCount,
    int prefixCount,
    int maximumStack) : IPacketProcessor<PacketView>
{
    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound) return PacketProcessingDecision.Allow;
        switch (context.Packet.PacketType)
        {
            case PacketTypes.PlayerSlot:
            {
                var slot = context.Packet.AsPlayerSlotView();
                if (slot.PlayerId != context.ConnectionId.Slot)
                    return PacketProcessingDecision.Violation("packet.player_identity_mismatch");
                if (slot.SlotId < 0 || slot.SlotId >= slotCount())
                    return PacketProcessingDecision.Violation("packet.inventory.slot_out_of_range");
                if (slot.ItemNetId < 0 || slot.ItemNetId >= itemTypeCount)
                    return PacketProcessingDecision.Violation("packet.inventory.item_type_out_of_range");
                if (slot.Stack < 0 || slot.Stack > maximumStack)
                    return PacketProcessingDecision.Violation("packet.inventory.stack_out_of_range");
                if (slot.Prefix >= prefixCount)
                    return PacketProcessingDecision.Violation("packet.inventory.prefix_out_of_range");
                return PacketProcessingDecision.Allow;
            }
            case PacketTypes.SyncLoadout:
            {
                var loadout = new SyncLoadoutView(context.Packet);
                if (loadout.PlayerId != context.ConnectionId.Slot)
                    return PacketProcessingDecision.Violation("packet.player_identity_mismatch");
                return loadout.LoadoutIndex >= loadoutCount()
                    ? PacketProcessingDecision.Violation("packet.loadout.index_out_of_range")
                    : PacketProcessingDecision.Allow;
            }
            default:
                return PacketProcessingDecision.Allow;
        }
    }
}
