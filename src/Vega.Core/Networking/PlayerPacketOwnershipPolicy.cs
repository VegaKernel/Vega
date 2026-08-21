using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>
/// Rejects inbound player-state packets that try to mutate another connection's player slot.
/// </summary>
public sealed class PlayerPacketOwnershipPolicy : IPacketProcessor<PacketView>
{
    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound)
        {
            return PacketProcessingDecision.Allow;
        }

        int? claimedSlot = context.Packet.PacketType switch
        {
            PacketTypes.PlayerUpdate => context.Packet.AsPlayerUpdateView().PlayerId,
            PacketTypes.PlayerHp => context.Packet.AsPlayerHpView().PlayerId,
            PacketTypes.PlayerMana => context.Packet.AsPlayerManaView().PlayerId,
            PacketTypes.PlayerTeam => context.Packet.AsPlayerTeamView().PlayerId,
            PacketTypes.TeamChangeFromUI => context.Packet.AsTeamChangeFromUIView().PlayerId,
            PacketTypes.TogglePvp => context.Packet.AsTogglePvpView().PlayerId,
            PacketTypes.SpectatePlayer => context.Packet.AsSpectatePlayerView().PlayerId,
            PacketTypes.PlayerTeleportPortal => context.Packet.AsPlayerTeleportPortalView().PlayerId,
            PacketTypes.PlayerDeathV2 => context.Packet.AsPlayerDeathV2View().PlayerId,
            PacketTypes.SyncPlayerChestIndex => context.Packet.AsSyncPlayerChestIndexView().Player,
            PacketTypes.TileEntityDisplayDollItemSync => context.Packet.AsTileEntityDisplayDollItemSyncView().PlayerId,
            PacketTypes.RequestTileEntityInteraction => context.Packet.AsRequestTileEntityInteractionView().PlayerId,
            PacketTypes.TileEntityHatRackItemSync => context.Packet.AsTileEntityHatRackItemSyncView().PlayerId,
            PacketTypes.SyncTilePicking => context.Packet.AsSyncTilePickingView().PlayerId,
            PacketTypes.SyncProjectileTrackers => context.Packet.AsSyncProjectileTrackersView().PlayerId,
            _ => null
        };

        return claimedSlot is not null && claimedSlot != context.ConnectionId.Slot
            ? PacketProcessingDecision.Violation("packet.player_identity_mismatch")
            : PacketProcessingDecision.Allow;
    }
}
