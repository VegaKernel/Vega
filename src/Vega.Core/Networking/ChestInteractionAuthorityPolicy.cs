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
        if (context.Direction != PacketDirection.Inbound || context.PlayerId is not PlayerId playerId)
        {
            return PacketProcessingDecision.Allow;
        }

        return context.Packet.PacketType switch
        {
            PacketTypes.ChestItem => ValidateActiveChest(
                playerId,
                context.Packet.AsChestItemView().ChestId),
            PacketTypes.SyncChestSize => ValidateActiveChest(
                playerId,
                context.Packet.AsSyncChestSizeView().ChestId),
            PacketTypes.ChestOpen => ValidateChestOpen(context.Packet.AsChestOpenView()),
            PacketTypes.ChestName => ValidateChestName(context.Packet.AsChestNameView()),
            _ => PacketProcessingDecision.Allow
        };
    }

    private PacketProcessingDecision ValidateActiveChest(PlayerId playerId, int chestId) =>
        world.TryGetActiveChest(playerId, out int activeChest) &&
        activeChest == chestId &&
        world.TryGetChestPosition(chestId, out _)
            ? PacketProcessingDecision.Allow
            : PacketProcessingDecision.Reject("packet.chest.session_mismatch");

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
