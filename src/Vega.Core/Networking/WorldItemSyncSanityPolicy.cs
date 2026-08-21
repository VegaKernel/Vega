using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>
/// Rejects client-authored world item state that cannot safely index Terraria state.
/// </summary>
public sealed class WorldItemSyncSanityPolicy(int itemTypeCount, int prefixCount) : IPacketProcessor<PacketView>
{
    public const short MaximumItemIndex = 400;
    public const short MaximumStack = 9_999;
    public const short MinimumItemNetId = -48;

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound)
        {
            return PacketProcessingDecision.Allow;
        }

        return context.Packet.PacketType switch
        {
            PacketTypes.ItemDrop => Validate(context.Packet.AsItemDropView()),
            PacketTypes.UpdateItemDrop => Validate(context.Packet.AsUpdateItemDropView()),
            PacketTypes.SyncItemsWithShimmerDeprecated or
            PacketTypes.SyncItemCannotBeTakenByEnemiesDeprecated =>
                PacketProcessingDecision.Violation("packet.world_item.deprecated_packet"),
            PacketTypes.SyncItemDespawn => ValidateIndex(context.Packet.AsSyncItemDespawnView().ItemId),
            PacketTypes.SyncItemPosition => ValidatePosition(context.Packet.AsSyncItemPositionView()),
            _ => PacketProcessingDecision.Allow
        };
    }

    private PacketProcessingDecision Validate(ItemDropView item) =>
        Validate(item.ItemIndex, item.PositionX, item.PositionY, item.VelocityX, item.VelocityY,
            item.Stack, item.Prefix, item.ItemNetId);

    private PacketProcessingDecision Validate(UpdateItemDropView item) =>
        Validate(item.ItemIndex, item.PositionX, item.PositionY, item.VelocityX, item.VelocityY,
            item.Stack, item.Prefix, item.ItemNetId);

    private PacketProcessingDecision Validate(
        short itemIndex,
        float positionX,
        float positionY,
        float velocityX,
        float velocityY,
        short stack,
        byte prefix,
        short itemNetId)
    {
        PacketProcessingDecision index = ValidateIndex(itemIndex);
        if (index.Kind != PacketDecisionKind.Allow)
        {
            return index;
        }

        if (!float.IsFinite(positionX) || !float.IsFinite(positionY) ||
            !float.IsFinite(velocityX) || !float.IsFinite(velocityY))
        {
            return PacketProcessingDecision.Violation("packet.world_item.non_finite_transform");
        }

        if (stack is < 0 or > MaximumStack)
        {
            return PacketProcessingDecision.Violation("packet.world_item.stack_out_of_range");
        }

        if (itemNetId < MinimumItemNetId || itemNetId >= itemTypeCount)
        {
            return PacketProcessingDecision.Violation("packet.world_item.type_out_of_range");
        }

        return prefix >= prefixCount
            ? PacketProcessingDecision.Violation("packet.world_item.prefix_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private static PacketProcessingDecision ValidateIndex(short itemIndex) =>
        itemIndex is < 0 or > MaximumItemIndex
            ? PacketProcessingDecision.Violation("packet.world_item.index_out_of_range")
            : PacketProcessingDecision.Allow;

    private static PacketProcessingDecision ValidatePosition(SyncItemPositionView item)
    {
        PacketProcessingDecision index = ValidateIndex(item.ItemId);
        if (index.Kind != PacketDecisionKind.Allow)
        {
            return index;
        }

        return !float.IsFinite(item.PositionX) || !float.IsFinite(item.PositionY)
            ? PacketProcessingDecision.Violation("packet.world_item.non_finite_position")
            : PacketProcessingDecision.Allow;
    }
}
