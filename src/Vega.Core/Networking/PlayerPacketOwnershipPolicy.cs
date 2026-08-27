using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>
/// Rejects inbound packets whose player identity is required to match the sending connection.
/// Packets that legitimately target another player (for example heal/buff/damage requests) are intentionally
/// excluded and validated by their own semantic policies instead.
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
            // Protocol 326 carries two fields after Multiplicity 2.6.3's legacy Zones schema.
            // The sender byte remains the first byte and is safe to consume without materializing the stale view.
            PacketTypes.Zones => context.Packet.PayloadSpan[0],
            PacketTypes.NpcTalk => new NpcTalkView(context.Packet).PlayerId,
            PacketTypes.PlayerAnimation => new PlayerAnimationView(context.Packet).PlayerId,
            PacketTypes.PlayerMana => context.Packet.AsPlayerManaView().PlayerId,
            PacketTypes.PlayerTeam => context.Packet.AsPlayerTeamView().PlayerId,
            PacketTypes.TeamChangeFromUI => context.Packet.AsTeamChangeFromUIView().PlayerId,
            PacketTypes.TogglePvp => context.Packet.AsTogglePvpView().PlayerId,
            // The first byte is the sender player id; buff ids that follow are UInt16 in protocol 326.
            PacketTypes.PlayerBuff => context.Packet.PayloadSpan[0],
            PacketTypes.NpcSpecial => new NpcSpecialView(context.Packet).SubjectId,
            PacketTypes.SpawnBossorInvasion => new SpawnBossorInvasionView(context.Packet).PlayerId,
            PacketTypes.PlayHarp => new PlayHarpView(context.Packet).PlayerId,
            PacketTypes.PlayerDodge => new PlayerDodgeView(context.Packet).PlayerId,
            PacketTypes.PlayerStealth => new PlayerStealthView(context.Packet).Player,
            PacketTypes.WiredCannonShot => new WiredCannonShotView(context.Packet).PlayerId,
            PacketTypes.MassWireOperationPay => new MassWireOperationPayView(context.Packet).PlayerId,
            PacketTypes.UpdateMinionTarget => new UpdateMinionTargetView(context.Packet).PlayerId,
            PacketTypes.MinionAttackTargetUpdate => new MinionAttackTargetUpdateView(context.Packet).PlayerId,
            PacketTypes.Emoji => new EmojiView(context.Packet).PlayerId,
            PacketTypes.LandGolfBallInCup => new LandGolfBallInCupView(context.Packet).PlayerId,
            PacketTypes.UpdatePlayerLuckFactors => new UpdatePlayerLuckFactorsView(context.Packet).PlayerId,
            PacketTypes.SyncLoadout => new SyncLoadoutView(context.Packet).PlayerId,
            PacketTypes.SpectatePlayer => context.Packet.AsSpectatePlayerView().PlayerId,
            PacketTypes.PlayerTeleportPortal => context.Packet.AsPlayerTeleportPortalView().PlayerId,
            PacketTypes.PlayerDeathV2 => context.Packet.AsPlayerDeathV2View().PlayerId,
            PacketTypes.SyncPlayerChestIndex => context.Packet.AsSyncPlayerChestIndexView().Player,
            PacketTypes.TileEntityDisplayDollItemSync => context.Packet.AsTileEntityDisplayDollItemSyncView().PlayerId,
            PacketTypes.RequestTileEntityInteraction => context.Packet.AsRequestTileEntityInteractionView().PlayerId,
            PacketTypes.TileEntityHatRackItemSync => context.Packet.AsTileEntityHatRackItemSyncView().PlayerId,
            PacketTypes.SyncTilePicking => context.Packet.AsSyncTilePickingView().PlayerId,
            PacketTypes.SignNew => context.Packet.AsSignNewView().PlayerId,
            PacketTypes.SyncProjectileTrackers => context.Packet.AsSyncProjectileTrackersView().PlayerId,
            _ => null
        };

        return claimedSlot is not null && claimedSlot != context.ConnectionId.Slot
            ? PacketProcessingDecision.Violation("packet.player_identity_mismatch")
            : PacketProcessingDecision.Allow;
    }
}
