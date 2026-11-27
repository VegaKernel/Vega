using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Core.Networking;

public sealed class ChestInteractionAuthorityPolicy(IWorldInteractionState world) : IPacketProcessor<PacketView>
{
    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound)
        {
            return PacketProcessingDecision.Allow;
        }

        return context.Packet.PacketType switch
        {
            PacketTypes.ChestItem => ValidateChestItem(context.PlayerId, context.Packet.AsChestItemView()),
            PacketTypes.SyncChestSize => ValidateChestSize(context.PlayerId, context.Packet.AsSyncChestSizeView()),
            PacketTypes.ChestOpen when context.PlayerId.HasValue => ValidateChestOpen(context.Packet.AsChestOpenView()),
            PacketTypes.ChestName when context.PlayerId.HasValue => ValidateChestName(context.Packet.AsChestNameView()),
            _ => PacketProcessingDecision.Allow
        };
    }

    private PacketProcessingDecision ValidateChestItem(PlayerId? playerId, ChestItemView value) =>
        ValidateChestItem(playerId, value.ChestId, value.ItemSlot);

    private PacketProcessingDecision ValidateChestSize(PlayerId? playerId, SyncChestSizeView value) =>
        ValidateChestSize(playerId, value.ChestId, value.NewSize);

    private PacketProcessingDecision ValidateActiveChest(PlayerId? playerId, int chestId, out int capacity)
    {
        capacity = 0;
        if (playerId is not PlayerId player || !world.TryGetActiveChest(player, out int activeChest) || activeChest != chestId ||
            !world.TryGetChestPosition(chestId, out _))
            return PacketProcessingDecision.Reject("packet.chest.session_mismatch");
        return world.TryGetChestCapacity(chestId, out capacity) && NativeChestCapacity.IsSupported(capacity)
            ? PacketProcessingDecision.Allow : PacketProcessingDecision.Reject("packet.chest.capacity_unavailable");
    }

    internal PacketProcessingDecision ValidateChestItem(PlayerId? playerId, int chestId, int itemSlot)
    {
        var active = ValidateActiveChest(playerId, chestId, out int capacity);
        return active.Kind != PacketDecisionKind.Allow ? active : (uint)itemSlot < (uint)capacity
            ? PacketProcessingDecision.Allow : PacketProcessingDecision.Reject("packet.chest.slot_out_of_range");
    }

    internal PacketProcessingDecision ValidateChestSize(PlayerId? playerId, int chestId, int size)
    {
        var active = ValidateActiveChest(playerId, chestId, out int capacity);
        // A client may acknowledge the current native size but cannot create,
        // shrink or expand storage. Capacity changes remain server-owned.
        return active.Kind != PacketDecisionKind.Allow ? active : size == capacity
            ? PacketProcessingDecision.Allow : PacketProcessingDecision.Reject("packet.chest.size_not_authoritative");
    }

    private PacketProcessingDecision ValidateChestOpen(ChestOpenView packet)
    {
        if (packet.ChestId == -1)
        {
            return PacketProcessingDecision.Allow;
        }

        return world.TryGetChestPosition(packet.ChestId, out WorldTilePosition actual) &&
            actual.X == packet.ChestX && actual.Y == packet.ChestY
                ? PacketProcessingDecision.Allow
                : PacketProcessingDecision.Reject("packet.chest.position_mismatch");
    }

    private PacketProcessingDecision ValidateChestName(ChestNameView packet) =>
        world.TryGetChestPosition(packet.ChestId, out WorldTilePosition actual) &&
        actual.X == packet.ChestX && actual.Y == packet.ChestY
            ? PacketProcessingDecision.Allow
            : PacketProcessingDecision.Reject("packet.chest.position_mismatch");
}
