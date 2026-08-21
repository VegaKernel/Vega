using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>
/// Rejects world-item synchronization values that are unsafe for Terraria's fixed item array
/// or floating-point world state.
/// </summary>
public sealed class WorldItemSyncSanityPolicy : IPacketProcessor<PacketView>
{
    public const short MaximumItemIndex = 400;
    public const short MaximumStack = 9_999;

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound ||
            !WorldItemSyncView.IsSupported(context.Packet.PacketType))
        {
            return new PacketProcessingDecision(PacketDecisionKind.Continue);
        }

        WorldItemSyncView item = context.Packet.AsWorldItemSyncView();
        if (item.ItemIndex is < 0 or > MaximumItemIndex)
        {
            return Violation("packet.world_item.index_out_of_range");
        }

        if (!float.IsFinite(item.PositionX) ||
            !float.IsFinite(item.PositionY) ||
            !float.IsFinite(item.VelocityX) ||
            !float.IsFinite(item.VelocityY))
        {
            return Violation("packet.world_item.non_finite_transform");
        }

        return item.Stack is < 0 or > MaximumStack
            ? Violation("packet.world_item.stack_out_of_range")
            : new PacketProcessingDecision(PacketDecisionKind.Continue);
    }

    private static PacketProcessingDecision Violation(string reason) =>
        new(PacketDecisionKind.Violation, reason);
}
