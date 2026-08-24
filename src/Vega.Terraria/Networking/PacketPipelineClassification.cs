using Multiplicity.Packets;

namespace Vega.Terraria.Networking;

internal enum PacketPreparationClass
{
    GameThreadOnly,
    PartiallyOffloadable,
    PureBeforeApply
}

internal enum PacketFamily
{
    Other,
    PlayerState,
    Item,
    Projectile,
    WorldInteraction,
    Chest,
    TileEntity
}

internal readonly record struct PacketPipelineProfile(
    PacketFamily Family,
    PacketPreparationClass Preparation,
    bool SecurityProcessed,
    bool OrderSensitive,
    bool WorldStateSensitive,
    bool LatencySensitive,
    bool SafelyCoalescable);

internal static class PacketPipelineClassification
{
    public static PacketPipelineProfile Get(byte packetId) => Get((PacketTypes)packetId);

    public static PacketPipelineProfile Get(PacketTypes packetType)
    {
        PacketFamily family = packetType switch
        {
            PacketTypes.PlayerUpdate or
            PacketTypes.PlayerHp or
            PacketTypes.PlayerMana or
            PacketTypes.PlayerTeam or
            PacketTypes.TeamChangeFromUI or
            PacketTypes.TogglePvp or
            PacketTypes.SpectatePlayer or
            PacketTypes.PlayerTeleportPortal or
            PacketTypes.PlayerHurtV2 or
            PacketTypes.PlayerDeathV2 => PacketFamily.PlayerState,

            PacketTypes.ItemDrop or
            PacketTypes.UpdateItemDrop or
            PacketTypes.SyncItemsWithShimmerDeprecated or
            PacketTypes.SyncItemCannotBeTakenByEnemiesDeprecated or
            PacketTypes.SyncItemDespawn or
            PacketTypes.SyncItemPosition or
            PacketTypes.ForceItemIntoNearestChest or
            PacketTypes.FoodPlatterTryPlacing => PacketFamily.Item,

            PacketTypes.ProjectileNew or
            PacketTypes.ProjectileDestroy or
            PacketTypes.SyncProjectileTrackers => PacketFamily.Projectile,

            PacketTypes.Tile or
            PacketTypes.DoorUse or
            PacketTypes.PaintTile or
            PacketTypes.PaintWall or
            PacketTypes.SignRead or
            PacketTypes.SignNew or
            PacketTypes.PlaceObject or
            PacketTypes.PlaceItemFrame or
            PacketTypes.LiquidSet or
            PacketTypes.GemLockToggle or
            PacketTypes.MassWireOperation => PacketFamily.WorldInteraction,

            PacketTypes.ChestGetContents or
            PacketTypes.ChestItem or
            PacketTypes.ChestOpen or
            PacketTypes.PlaceChest or
            PacketTypes.SyncChestSize or
            PacketTypes.ChestName or
            PacketTypes.ChestUnlock or
            PacketTypes.SyncPlayerChestIndex => PacketFamily.Chest,

            PacketTypes.PlaceTileEntity or
            PacketTypes.TEDeadCellsDisplayJar or
            PacketTypes.TileEntityDisplayDollItemSync or
            PacketTypes.RequestTileEntityInteraction or
            PacketTypes.TileEntityHatRackItemSync or
            PacketTypes.SyncTilePicking or
            PacketTypes.TELeashedEntityAnchorPlaceItem => PacketFamily.TileEntity,

            _ => PacketFamily.Other
        };

        bool processed = family != PacketFamily.Other || packetType == PacketTypes.SyncExtraValue;
        if (!processed)
        {
            return new PacketPipelineProfile(
                PacketFamily.Other,
                PacketPreparationClass.GameThreadOnly,
                false,
                true,
                true,
                false,
                false);
        }

        bool latencySensitive = family is PacketFamily.PlayerState or PacketFamily.Projectile;
        return new PacketPipelineProfile(
            family,
            PacketPreparationClass.PartiallyOffloadable,
            true,
            true,
            true,
            latencySensitive,
            false);
    }
}
