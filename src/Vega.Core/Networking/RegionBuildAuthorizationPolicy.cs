using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Authorization;
using Vega.Contracts.Accounts;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Regions;
using Vega.Contracts.Worlds;

namespace Vega.Core.Networking;

public sealed class RegionBuildAuthorizationPolicy(
    IRegionService regions,
    IPlayerDirectory players,
    IPlayerAuthorizationService authorization,
    IPlayerAccountSessionService? accountSessions = null) : IPacketProcessor<PacketView>
{
    public const string BypassPermission = "vega.regions.bypass";

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound ||
            context.PlayerId is not PlayerId playerId ||
            !TryGetMutationArea(context.Packet, out int startX, out int startY, out int endX, out int endY) ||
            authorization.HasPermission(playerId, BypassPermission))
        {
            return PacketProcessingDecision.Allow;
        }

        if (!players.TryGet(playerId, out PlayerSnapshot? player) || player is null)
        {
            return PacketProcessingDecision.Reject("packet.region.player_unavailable");
        }

        RegionSnapshot? start = regions.FindTopRegion(WorldId.Primary, startX, startY);
        RegionSnapshot? end = startX == endX && startY == endY
            ? start
            : regions.FindTopRegion(WorldId.Primary, endX, endY);
        return IsAllowed(start, player, playerId) && IsAllowed(end, player, playerId)
            ? PacketProcessingDecision.Allow
            : PacketProcessingDecision.Reject("packet.region.build_denied");
    }

    private bool IsAllowed(RegionSnapshot? region, PlayerSnapshot player, PlayerId playerId)
    {
        string subject = accountSessions is not null &&
            accountSessions.TryGetAccount(playerId, out AccountSnapshot? account) && account is not null
                ? account.Name
                : player.Name;
        if (region is null ||
            (region.Flags.TryGetValue("protected", out bool protectedFlag) && !protectedFlag) ||
            (region.Flags.TryGetValue("build", out bool publicBuild) && publicBuild) ||
            string.Equals(region.Owner, subject, StringComparison.OrdinalIgnoreCase) ||
            region.Members.Contains(subject))
        {
            return true;
        }

        return region.Groups.Any(group =>
            authorization.HasPermission(playerId, $"vega.group.{group}"));
    }

    private static bool TryGetMutationArea(
        PacketView packet,
        out int startX,
        out int startY,
        out int endX,
        out int endY)
    {
        (startX, startY, endX, endY) = packet.PacketType switch
        {
            PacketTypes.Tile => (
                packet.AsTileView().TileX,
                packet.AsTileView().TileY,
                packet.AsTileView().TileX,
                packet.AsTileView().TileY),
            PacketTypes.PlaceObject => (
                packet.AsPlaceObjectView().X,
                packet.AsPlaceObjectView().Y,
                packet.AsPlaceObjectView().X,
                packet.AsPlaceObjectView().Y),
            PacketTypes.PlaceChest => (
                packet.AsPlaceChestView().TileX,
                packet.AsPlaceChestView().TileY,
                packet.AsPlaceChestView().TileX,
                packet.AsPlaceChestView().TileY),
            PacketTypes.PlaceItemFrame => (
                packet.AsPlaceItemFrameView().X,
                packet.AsPlaceItemFrameView().Y,
                packet.AsPlaceItemFrameView().X,
                packet.AsPlaceItemFrameView().Y),
            PacketTypes.ChestUnlock => (
                packet.AsChestUnlockView().X,
                packet.AsChestUnlockView().Y,
                packet.AsChestUnlockView().X,
                packet.AsChestUnlockView().Y),
            PacketTypes.LiquidSet => (
                packet.AsLiquidSetView().X,
                packet.AsLiquidSetView().Y,
                packet.AsLiquidSetView().X,
                packet.AsLiquidSetView().Y),
            PacketTypes.PlaceTileEntity => (
                packet.AsPlaceTileEntityView().X,
                packet.AsPlaceTileEntityView().Y,
                packet.AsPlaceTileEntityView().X,
                packet.AsPlaceTileEntityView().Y),
            PacketTypes.GemLockToggle => (
                packet.AsGemLockToggleView().X,
                packet.AsGemLockToggleView().Y,
                packet.AsGemLockToggleView().X,
                packet.AsGemLockToggleView().Y),
            PacketTypes.MassWireOperation => (
                packet.AsMassWireOperationView().StartX,
                packet.AsMassWireOperationView().StartY,
                packet.AsMassWireOperationView().EndX,
                packet.AsMassWireOperationView().EndY),
            PacketTypes.FoodPlatterTryPlacing => (
                packet.AsFoodPlatterTryPlacingView().TileX,
                packet.AsFoodPlatterTryPlacingView().TileY,
                packet.AsFoodPlatterTryPlacingView().TileX,
                packet.AsFoodPlatterTryPlacingView().TileY),
            PacketTypes.TEDeadCellsDisplayJar => (
                packet.AsTEDeadCellsDisplayJarView().TileX,
                packet.AsTEDeadCellsDisplayJarView().TileY,
                packet.AsTEDeadCellsDisplayJarView().TileX,
                packet.AsTEDeadCellsDisplayJarView().TileY),
            PacketTypes.TELeashedEntityAnchorPlaceItem => (
                packet.AsTELeashedEntityAnchorPlaceItemView().TileX,
                packet.AsTELeashedEntityAnchorPlaceItemView().TileY,
                packet.AsTELeashedEntityAnchorPlaceItemView().TileX,
                packet.AsTELeashedEntityAnchorPlaceItemView().TileY),
            _ => (int.MinValue, int.MinValue, int.MinValue, int.MinValue)
        };
        return startX != int.MinValue;
    }
}
