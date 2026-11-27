using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Authorization;
using Vega.Contracts.Accounts;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Regions;
using Vega.Contracts.Worlds;

namespace Vega.Regions;

public sealed class RegionBuildAuthorizationPolicy(
    IRegionService regions,
    IPlayerDirectory players,
    IPlayerAuthorizationService authorization,
    IPlayerAccountSessionService? accountSessions = null,
    IWorldInteractionState? worldInteractions = null,
    WorldBuildProtection? worldProtection = null) : IPacketProcessor<PacketView>
{
    public const string BypassPermission = "vega.regions.bypass";

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound ||
            context.PlayerId is not PlayerId playerId ||
            !TryGetInteractionArea(worldInteractions,
                context.Packet,
                out int startX,
                out int startY,
                out int endX,
                out int endY,
                out RegionInteraction interaction) ||
            (worldProtection is null && authorization.HasPermission(playerId, BypassPermission)))
        {
            return PacketProcessingDecision.Allow;
        }

        if (!players.TryGet(playerId, out PlayerSnapshot? player) || player is null)
        {
            return PacketProcessingDecision.Reject("packet.region.player_unavailable");
        }

        if (interaction == RegionInteraction.Build && worldProtection is not null)
        {
            var decision = worldProtection.Evaluate(player.WorldId, playerId, startX, startY, endX, endY);
            if (decision.Kind != PacketDecisionKind.Allow) return decision;
        }
        if (authorization.HasPermission(playerId, BypassPermission)) return PacketProcessingDecision.Allow;

        if (context.Packet.PacketType == PacketTypes.MassWireOperation)
        {
            return IsMassWireAllowed(player.WorldId, startX, startY, endX, endY, player, playerId)
                ? PacketProcessingDecision.Allow
                : PacketProcessingDecision.Reject("packet.region.build_denied");
        }

        RegionSnapshot? start = regions.FindTopRegion(player.WorldId, startX, startY);
        RegionSnapshot? end = startX == endX && startY == endY
            ? start
            : regions.FindTopRegion(player.WorldId, endX, endY);
        return IsAllowed(start, player, playerId, interaction) &&
            IsAllowed(end, player, playerId, interaction)
            ? PacketProcessingDecision.Allow
            : PacketProcessingDecision.Reject(interaction == RegionInteraction.Use
                ? "packet.region.use_denied"
                : "packet.region.build_denied");
    }

    private bool IsAllowed(
        RegionSnapshot? region,
        PlayerSnapshot player,
        PlayerId playerId,
        RegionInteraction interaction)
    {
        string? subject = accountSessions is null ? player.Name :
            accountSessions.TryGetAccount(playerId, out AccountSnapshot? account) ? account?.Name : null;
        if (region is null ||
            (region.Flags.TryGetValue("protected", out bool protectedFlag) && !protectedFlag) ||
            (region.Flags.TryGetValue(
                interaction == RegionInteraction.Use ? "use" : "build",
                out bool publicAccess) && publicAccess) ||
            (subject is not null && (string.Equals(region.Owner, subject, StringComparison.OrdinalIgnoreCase) ||
            region.Members.Contains(subject))))
        {
            return true;
        }

        return region.Groups.Any(group =>
            authorization.HasPermission(playerId, $"vega.group.{group}"));
    }

    private bool IsMassWireAllowed(
        WorldId worldId,
        int startX,
        int startY,
        int endX,
        int endY,
        PlayerSnapshot player,
        PlayerId playerId)
    {
        int minimumX = Math.Min(startX, endX);
        int maximumX = Math.Max(startX, endX);
        for (int x = minimumX; x <= maximumX; x++)
        {
            if (!IsAllowed(regions.FindTopRegion(worldId, x, startY), player, playerId, RegionInteraction.Build) ||
                !IsAllowed(regions.FindTopRegion(worldId, x, endY), player, playerId, RegionInteraction.Build))
            {
                return false;
            }
        }

        int minimumY = Math.Min(startY, endY);
        int maximumY = Math.Max(startY, endY);
        for (int y = minimumY; y <= maximumY; y++)
        {
            if (!IsAllowed(regions.FindTopRegion(worldId, startX, y), player, playerId, RegionInteraction.Build) ||
                !IsAllowed(regions.FindTopRegion(worldId, endX, y), player, playerId, RegionInteraction.Build))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool TryGetInteractionArea(
        IWorldInteractionState? worldInteractions,
        PacketView packet,
        out int startX,
        out int startY,
        out int endX,
        out int endY,
        out RegionInteraction interaction)
    {
        int? chestId = packet.PacketType switch
        {
            PacketTypes.ChestItem => packet.AsChestItemView().ChestId,
            PacketTypes.SyncChestSize => packet.AsSyncChestSizeView().ChestId,
            _ => null
        };
        if (chestId is int resolvedChestId)
        {
            if (worldInteractions is null ||
                !worldInteractions.TryGetChestPosition(resolvedChestId, out WorldTilePosition position))
            {
                (startX, startY, endX, endY, interaction) =
                    (int.MinValue, int.MinValue, int.MinValue, int.MinValue, RegionInteraction.Use);
                return false;
            }

            startX = endX = position.X;
            startY = endY = position.Y;
            interaction = RegionInteraction.Use;
            return true;
        }

        int? tileEntityId = packet.PacketType switch
        {
            PacketTypes.RequestTileEntityInteraction =>
                packet.AsRequestTileEntityInteractionView().TileEntityId,
            PacketTypes.TileEntityDisplayDollItemSync =>
                packet.AsTileEntityDisplayDollItemSyncView().TileEntityId,
            PacketTypes.TileEntityHatRackItemSync =>
                packet.AsTileEntityHatRackItemSyncView().TileEntityId,
            _ => null
        };
        if (tileEntityId is int resolvedTileEntityId)
        {
            if (worldInteractions is null ||
                !worldInteractions.TryGetTileEntityPosition(
                    resolvedTileEntityId,
                    out WorldTilePosition position))
            {
                (startX, startY, endX, endY, interaction) =
                    (int.MinValue, int.MinValue, int.MinValue, int.MinValue, RegionInteraction.Build);
                return false;
            }

            startX = endX = position.X;
            startY = endY = position.Y;
            interaction = RegionInteraction.Build;
            return true;
        }

        if (packet.PacketType == PacketTypes.ChestOpen)
        {
            ChestOpenView chest = packet.AsChestOpenView();
            startX = endX = chest.ChestX;
            startY = endY = chest.ChestY;
            interaction = RegionInteraction.Use;
            return true;
        }

        (startX, startY, endX, endY, interaction) = packet.PacketType switch
        {
            PacketTypes.Tile => (
                packet.AsTileView().TileX,
                packet.AsTileView().TileY,
                packet.AsTileView().TileX,
                packet.AsTileView().TileY,
                RegionInteraction.Build),
            PacketTypes.PlaceObject => (
                packet.AsPlaceObjectView().X,
                packet.AsPlaceObjectView().Y,
                packet.AsPlaceObjectView().X,
                packet.AsPlaceObjectView().Y,
                RegionInteraction.Build),
            PacketTypes.DoorUse => (
                packet.AsDoorUseView().TileX,
                packet.AsDoorUseView().TileY,
                packet.AsDoorUseView().TileX,
                packet.AsDoorUseView().TileY,
                RegionInteraction.Use),
            PacketTypes.PaintTile => (
                packet.AsPaintTileView().X,
                packet.AsPaintTileView().Y,
                packet.AsPaintTileView().X,
                packet.AsPaintTileView().Y,
                RegionInteraction.Build),
            PacketTypes.PaintWall => (
                packet.AsPaintWallView().X,
                packet.AsPaintWallView().Y,
                packet.AsPaintWallView().X,
                packet.AsPaintWallView().Y,
                RegionInteraction.Build),
            PacketTypes.SignRead => (
                packet.AsSignReadView().X,
                packet.AsSignReadView().Y,
                packet.AsSignReadView().X,
                packet.AsSignReadView().Y,
                RegionInteraction.Use),
            PacketTypes.SignNew => (
                packet.AsSignNewView().X,
                packet.AsSignNewView().Y,
                packet.AsSignNewView().X,
                packet.AsSignNewView().Y,
                RegionInteraction.Build),
            PacketTypes.PlaceChest => (
                packet.AsPlaceChestView().TileX,
                packet.AsPlaceChestView().TileY,
                packet.AsPlaceChestView().TileX,
                packet.AsPlaceChestView().TileY,
                RegionInteraction.Build),
            PacketTypes.ChestGetContents => (
                packet.AsChestGetContentsView().TileX,
                packet.AsChestGetContentsView().TileY,
                packet.AsChestGetContentsView().TileX,
                packet.AsChestGetContentsView().TileY,
                RegionInteraction.Use),
            PacketTypes.ChestName => (
                packet.AsChestNameView().ChestX,
                packet.AsChestNameView().ChestY,
                packet.AsChestNameView().ChestX,
                packet.AsChestNameView().ChestY,
                RegionInteraction.Use),
            PacketTypes.PlaceItemFrame => (
                packet.AsPlaceItemFrameView().X,
                packet.AsPlaceItemFrameView().Y,
                packet.AsPlaceItemFrameView().X,
                packet.AsPlaceItemFrameView().Y,
                RegionInteraction.Build),
            PacketTypes.ChestUnlock => (
                packet.AsChestUnlockView().X,
                packet.AsChestUnlockView().Y,
                packet.AsChestUnlockView().X,
                packet.AsChestUnlockView().Y,
                RegionInteraction.Use),
            PacketTypes.LiquidSet => (
                packet.AsLiquidSetView().X,
                packet.AsLiquidSetView().Y,
                packet.AsLiquidSetView().X,
                packet.AsLiquidSetView().Y,
                RegionInteraction.Build),
            PacketTypes.PlaceTileEntity => (
                packet.AsPlaceTileEntityView().X,
                packet.AsPlaceTileEntityView().Y,
                packet.AsPlaceTileEntityView().X,
                packet.AsPlaceTileEntityView().Y,
                RegionInteraction.Build),
            PacketTypes.GemLockToggle => (
                packet.AsGemLockToggleView().X,
                packet.AsGemLockToggleView().Y,
                packet.AsGemLockToggleView().X,
                packet.AsGemLockToggleView().Y,
                RegionInteraction.Build),
            PacketTypes.MassWireOperation => (
                packet.AsMassWireOperationView().StartX,
                packet.AsMassWireOperationView().StartY,
                packet.AsMassWireOperationView().EndX,
                packet.AsMassWireOperationView().EndY,
                RegionInteraction.Build),
            PacketTypes.FoodPlatterTryPlacing => (
                packet.AsFoodPlatterTryPlacingView().TileX,
                packet.AsFoodPlatterTryPlacingView().TileY,
                packet.AsFoodPlatterTryPlacingView().TileX,
                packet.AsFoodPlatterTryPlacingView().TileY,
                RegionInteraction.Build),
            PacketTypes.TEDeadCellsDisplayJar => (
                packet.AsTEDeadCellsDisplayJarView().TileX,
                packet.AsTEDeadCellsDisplayJarView().TileY,
                packet.AsTEDeadCellsDisplayJarView().TileX,
                packet.AsTEDeadCellsDisplayJarView().TileY,
                RegionInteraction.Build),
            PacketTypes.TELeashedEntityAnchorPlaceItem => (
                packet.AsTELeashedEntityAnchorPlaceItemView().TileX,
                packet.AsTELeashedEntityAnchorPlaceItemView().TileY,
                packet.AsTELeashedEntityAnchorPlaceItemView().TileX,
                packet.AsTELeashedEntityAnchorPlaceItemView().TileY,
                RegionInteraction.Build),
            _ => (int.MinValue, int.MinValue, int.MinValue, int.MinValue, RegionInteraction.Build)
        };
        return startX != int.MinValue;
    }

    internal enum RegionInteraction
    {
        Build,
        Use
    }
}
