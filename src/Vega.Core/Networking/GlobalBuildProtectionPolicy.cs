using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Authorization;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Core.Networking;

public sealed class RuntimeWorldBuildProtectionService : IWorldBuildProtectionService
{
    private int enabled;
    private int spawnProtectionEnabled;
    private int spawnTileX;
    private int spawnTileY;
    private int spawnRadiusTiles = 10;

    public bool IsEnabled => Volatile.Read(ref enabled) != 0;

    public bool IsSpawnProtectionEnabled => Volatile.Read(ref spawnProtectionEnabled) != 0;

    public int SpawnProtectionRadiusTiles => Volatile.Read(ref spawnRadiusTiles);

    public bool Toggle()
    {
        while (true)
        {
            int current = Volatile.Read(ref enabled);
            int next = current == 0 ? 1 : 0;
            if (Interlocked.CompareExchange(ref enabled, next, current) == current)
            {
                return next != 0;
            }
        }
    }

    public bool ToggleSpawnProtection(int spawnTileX, int spawnTileY, int radiusTiles)
    {
        if (radiusTiles < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusTiles));
        }

        Volatile.Write(ref this.spawnTileX, spawnTileX);
        Volatile.Write(ref this.spawnTileY, spawnTileY);
        Volatile.Write(ref spawnRadiusTiles, radiusTiles);

        while (true)
        {
            int current = Volatile.Read(ref spawnProtectionEnabled);
            int next = current == 0 ? 1 : 0;
            if (Interlocked.CompareExchange(ref spawnProtectionEnabled, next, current) == current)
            {
                return next != 0;
            }
        }
    }

    public void UpdateSpawnPosition(int spawnTileX, int spawnTileY)
    {
        Volatile.Write(ref this.spawnTileX, spawnTileX);
        Volatile.Write(ref this.spawnTileY, spawnTileY);
    }

    public bool IsSpawnProtected(int tileX, int tileY)
    {
        if (!IsSpawnProtectionEnabled)
        {
            return false;
        }

        long deltaX = tileX - Volatile.Read(ref spawnTileX);
        long deltaY = tileY - Volatile.Read(ref spawnTileY);
        long radius = SpawnProtectionRadiusTiles;
        return (deltaX * deltaX) + (deltaY * deltaY) <= radius * radius;
    }
}

public sealed class GlobalBuildProtectionPolicy(
    IWorldBuildProtectionService protection,
    IPlayerAuthorizationService authorization) : IPacketProcessor<PacketView>
{
    public const string BypassPermission = "vega.world.antibuild";
    public const string SpawnBypassPermission = "vega.world.editspawn";

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound || !IsBuildMutation(context.Packet.PacketType))
        {
            return PacketProcessingDecision.Allow;
        }

        bool globalDenied = protection.IsEnabled;
        bool spawnDenied = protection.IsSpawnProtectionEnabled && IsSpawnMutation(context.Packet);
        if (!globalDenied && !spawnDenied)
        {
            return PacketProcessingDecision.Allow;
        }

        if (context.PlayerId is not PlayerId playerId)
        {
            return PacketProcessingDecision.Reject(
                spawnDenied ? "packet.spawn_protection.player_unavailable" : "packet.antibuild.player_unavailable");
        }

        if (globalDenied && authorization.HasPermission(playerId, BypassPermission))
        {
            globalDenied = false;
        }

        if (spawnDenied && authorization.HasPermission(playerId, SpawnBypassPermission))
        {
            spawnDenied = false;
        }

        if (spawnDenied)
        {
            return PacketProcessingDecision.Reject("packet.spawn_protection.build_denied");
        }

        return globalDenied
            ? PacketProcessingDecision.Reject("packet.antibuild.build_denied")
            : PacketProcessingDecision.Allow;
    }

    private bool IsSpawnMutation(PacketView packet)
    {
        if (!TryGetBuildArea(packet, out int startX, out int startY, out int endX, out int endY))
        {
            return false;
        }

        if (packet.PacketType != PacketTypes.MassWireOperation)
        {
            return protection.IsSpawnProtected(startX, startY) ||
                protection.IsSpawnProtected(endX, endY);
        }

        int minimumX = Math.Min(startX, endX);
        int maximumX = Math.Max(startX, endX);
        for (int x = minimumX; x <= maximumX; x++)
        {
            if (protection.IsSpawnProtected(x, startY) || protection.IsSpawnProtected(x, endY))
            {
                return true;
            }
        }

        int minimumY = Math.Min(startY, endY);
        int maximumY = Math.Max(startY, endY);
        for (int y = minimumY; y <= maximumY; y++)
        {
            if (protection.IsSpawnProtected(startX, y) || protection.IsSpawnProtected(endX, y))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetBuildArea(
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
            PacketTypes.PaintTile => (
                packet.AsPaintTileView().X,
                packet.AsPaintTileView().Y,
                packet.AsPaintTileView().X,
                packet.AsPaintTileView().Y),
            PacketTypes.PaintWall => (
                packet.AsPaintWallView().X,
                packet.AsPaintWallView().Y,
                packet.AsPaintWallView().X,
                packet.AsPaintWallView().Y),
            PacketTypes.SignNew => (
                packet.AsSignNewView().X,
                packet.AsSignNewView().Y,
                packet.AsSignNewView().X,
                packet.AsSignNewView().Y),
            PacketTypes.PlaceChest => (
                packet.AsPlaceChestView().TileX,
                packet.AsPlaceChestView().TileY,
                packet.AsPlaceChestView().TileX,
                packet.AsPlaceChestView().TileY),
            PacketTypes.PlaceTileEntity => (
                packet.AsPlaceTileEntityView().X,
                packet.AsPlaceTileEntityView().Y,
                packet.AsPlaceTileEntityView().X,
                packet.AsPlaceTileEntityView().Y),
            PacketTypes.PlaceItemFrame => (
                packet.AsPlaceItemFrameView().X,
                packet.AsPlaceItemFrameView().Y,
                packet.AsPlaceItemFrameView().X,
                packet.AsPlaceItemFrameView().Y),
            PacketTypes.LiquidSet => (
                packet.AsLiquidSetView().X,
                packet.AsLiquidSetView().Y,
                packet.AsLiquidSetView().X,
                packet.AsLiquidSetView().Y),
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

    private static bool IsBuildMutation(PacketTypes packetType) => packetType is
        PacketTypes.Tile or
        PacketTypes.TileSendSquare or
        PacketTypes.PaintTile or
        PacketTypes.PaintWall or
        PacketTypes.SignNew or
        PacketTypes.PlaceChest or
        PacketTypes.PlaceObject or
        PacketTypes.PlaceTileEntity or
        PacketTypes.PlaceItemFrame or
        PacketTypes.LiquidSet or
        PacketTypes.GemLockToggle or
        PacketTypes.MassWireOperation or
        PacketTypes.MassWireOperationPay or
        PacketTypes.TileEntityDisplayDollItemSync or
        PacketTypes.WeaponsRackTryPlacing or
        PacketTypes.TileEntityHatRackItemSync or
        PacketTypes.FoodPlatterTryPlacing or
        PacketTypes.TEDeadCellsDisplayJar or
        PacketTypes.TELeashedEntityAnchorPlaceItem;
}