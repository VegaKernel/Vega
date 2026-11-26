using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>
/// Rejects tile, chest and anchor mutations that can index outside Terraria's fixed world state.
/// </summary>
public sealed class WorldMutationSanityPolicy(
    Func<int> worldWidth,
    Func<int> worldHeight,
    Func<int> chestCount,
    Func<int> inventorySlotCount,
    int tileTypeCount,
    int wallTypeCount,
    int itemTypeCount,
    int prefixCount,
    int paintColorCount = 32) : IPacketProcessor<PacketView>
{
    private const int MaximumChestSlots = 40;
    private const int MaximumStack = 9_999;

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound)
        {
            return PacketProcessingDecision.Allow;
        }

        return context.Packet.PacketType switch
        {
            PacketTypes.Tile => ValidateTile(context.Packet.AsTileView()),
            PacketTypes.PlaceObject => ValidateObject(context.Packet.AsPlaceObjectView()),
            PacketTypes.DoorUse => ValidateDoor(context.Packet.AsDoorUseView()),
            PacketTypes.PaintTile => ValidatePaint(context.Packet.AsPaintTileView().X,
                context.Packet.AsPaintTileView().Y, context.Packet.AsPaintTileView().Color,
                context.Packet.AsPaintTileView().Coat),
            PacketTypes.PaintWall => ValidatePaint(context.Packet.AsPaintWallView().X,
                context.Packet.AsPaintWallView().Y, context.Packet.AsPaintWallView().Color,
                context.Packet.AsPaintWallView().Coat),
            PacketTypes.SignRead => ValidateCoordinates(
                context.Packet.AsSignReadView().X,
                context.Packet.AsSignReadView().Y,
                "packet.sign.position_out_of_world"),
            PacketTypes.SignNew => ValidateCoordinates(
                context.Packet.AsSignNewView().X,
                context.Packet.AsSignNewView().Y,
                "packet.sign.position_out_of_world"),
            PacketTypes.PlaceItemFrame => ValidateItemFrame(context.Packet.AsPlaceItemFrameView()),
            PacketTypes.ChestGetContents => ValidateCoordinates(
                context.Packet.AsChestGetContentsView().TileX,
                context.Packet.AsChestGetContentsView().TileY,
                "packet.chest.position_out_of_world"),
            PacketTypes.ChestItem => ValidateChestItem(context.Packet.AsChestItemView()),
            PacketTypes.ChestOpen => ValidateChestOpen(context.Packet.AsChestOpenView()),
            PacketTypes.PlaceChest => ValidatePlaceChest(context.Packet.AsPlaceChestView()),
            PacketTypes.SyncChestSize => ValidateChestSize(context.Packet.AsSyncChestSizeView()),
            PacketTypes.ChestName => ValidateChestName(context.Packet.AsChestNameView()),
            PacketTypes.SyncPlayerChestIndex => ValidatePlayerChestIndex(context.Packet.AsSyncPlayerChestIndexView().Chest),
            PacketTypes.ForceItemIntoNearestChest => ValidateQuickStack(context.Packet.AsForceItemIntoNearestChestView()),
            PacketTypes.ChestUnlock => ValidateCoordinates(
                context.Packet.AsChestUnlockView().X,
                context.Packet.AsChestUnlockView().Y,
                "packet.chest.position_out_of_world"),
            PacketTypes.TELeashedEntityAnchorPlaceItem => ValidateLeashedAnchor(
                context.Packet.AsTELeashedEntityAnchorPlaceItemView()),
            PacketTypes.SyncTilePicking => ValidateTilePicking(context.Packet.AsSyncTilePickingView()),
            PacketTypes.LiquidSet => ValidateCoordinates(
                context.Packet.AsLiquidSetView().X,
                context.Packet.AsLiquidSetView().Y,
                "packet.liquid.position_out_of_world"),
            PacketTypes.PlaceTileEntity => ValidateCoordinates(
                context.Packet.AsPlaceTileEntityView().X,
                context.Packet.AsPlaceTileEntityView().Y,
                "packet.tile_entity.position_out_of_world"),
            PacketTypes.GemLockToggle => ValidateCoordinates(
                context.Packet.AsGemLockToggleView().X,
                context.Packet.AsGemLockToggleView().Y,
                "packet.gem_lock.position_out_of_world"),
            PacketTypes.MassWireOperation => ValidateMassWire(context.Packet.AsMassWireOperationView()),
            PacketTypes.FoodPlatterTryPlacing => ValidatePlacedItem(
                context.Packet.AsFoodPlatterTryPlacingView().TileX,
                context.Packet.AsFoodPlatterTryPlacingView().TileY,
                context.Packet.AsFoodPlatterTryPlacingView().ItemType,
                context.Packet.AsFoodPlatterTryPlacingView().Prefix,
                context.Packet.AsFoodPlatterTryPlacingView().Stack,
                "packet.food_platter"),
            PacketTypes.TEDeadCellsDisplayJar => ValidatePlacedItem(
                context.Packet.AsTEDeadCellsDisplayJarView().TileX,
                context.Packet.AsTEDeadCellsDisplayJarView().TileY,
                context.Packet.AsTEDeadCellsDisplayJarView().ItemType,
                context.Packet.AsTEDeadCellsDisplayJarView().Prefix,
                context.Packet.AsTEDeadCellsDisplayJarView().Stack,
                "packet.display_jar"),
            PacketTypes.TileEntityDisplayDollItemSync => ValidateDisplayDollItem(
                context.Packet.AsTileEntityDisplayDollItemSyncView()),
            PacketTypes.TileEntityHatRackItemSync => ValidateHatRackItem(
                context.Packet.AsTileEntityHatRackItemSyncView()),
            _ => PacketProcessingDecision.Allow
        };
    }

    private PacketProcessingDecision ValidateTile(TileView tile)
    {
        PacketProcessingDecision position = ValidateCoordinates(
            tile.TileX,
            tile.TileY,
            "packet.tile.position_out_of_world");
        if (position.Kind != PacketDecisionKind.Allow)
        {
            return position;
        }

        if (tile.Value < 0)
        {
            return PacketProcessingDecision.Violation("packet.tile.value_out_of_range");
        }

        if (tile.Action is TileAction.PlaceTile or TileAction.ReplaceTile && tile.Value >= tileTypeCount)
        {
            return PacketProcessingDecision.Violation("packet.tile.type_out_of_range");
        }

        return tile.Action is TileAction.PlaceWall or TileAction.ReplaceWall && tile.Value >= wallTypeCount
            ? PacketProcessingDecision.Violation("packet.wall.type_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateObject(PlaceObjectView value)
    {
        PacketProcessingDecision position = ValidateCoordinates(value.X, value.Y, "packet.object.position_out_of_world");
        if (position.Kind != PacketDecisionKind.Allow)
        {
            return position;
        }

        return value.Type < 0 || value.Type >= tileTypeCount
            ? PacketProcessingDecision.Violation("packet.object.type_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateDoor(DoorUseView value)
    {
        PacketProcessingDecision position = ValidateCoordinates(
            value.TileX,
            value.TileY,
            "packet.door.position_out_of_world");
        return position.Kind != PacketDecisionKind.Allow
            ? position
            : !Enum.IsDefined(value.Action)
                ? PacketProcessingDecision.Violation("packet.door.action_out_of_range")
                : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidatePaint(int x, int y, byte color, byte coat)
    {
        PacketProcessingDecision position = ValidateCoordinates(x, y, "packet.paint.position_out_of_world");
        if (position.Kind != PacketDecisionKind.Allow)
        {
            return position;
        }

        return color >= paintColorCount || coat > 1
            ? PacketProcessingDecision.Violation("packet.paint.value_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateItemFrame(PlaceItemFrameView value)
    {
        PacketProcessingDecision position = ValidateCoordinates(value.X, value.Y, "packet.item_frame.position_out_of_world");
        if (position.Kind != PacketDecisionKind.Allow)
        {
            return position;
        }

        if (value.ItemId < 0 || value.ItemId >= itemTypeCount)
        {
            return PacketProcessingDecision.Violation("packet.item_frame.type_out_of_range");
        }

        if (value.Prefix >= prefixCount)
        {
            return PacketProcessingDecision.Violation("packet.item_frame.prefix_out_of_range");
        }

        return value.Stack is < 0 or > MaximumStack
            ? PacketProcessingDecision.Violation("packet.item_frame.stack_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateChestItem(ChestItemView value)
    {
        PacketProcessingDecision id = ValidateChestId(value.ChestId);
        if (id.Kind != PacketDecisionKind.Allow)
        {
            return id;
        }

        if (value.ItemSlot >= MaximumChestSlots)
        {
            return PacketProcessingDecision.Violation("packet.chest.slot_out_of_range");
        }

        if (value.Stack is < 0 or > MaximumStack)
        {
            return PacketProcessingDecision.Violation("packet.chest.stack_out_of_range");
        }

        if (value.ItemNetId < WorldItemSyncSanityPolicy.MinimumItemNetId || value.ItemNetId >= itemTypeCount)
        {
            return PacketProcessingDecision.Violation("packet.chest.item_type_out_of_range");
        }

        return value.Prefix >= prefixCount
            ? PacketProcessingDecision.Violation("packet.chest.prefix_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateChestOpen(ChestOpenView value)
    {
        PacketProcessingDecision position = ValidateCoordinates(
            value.ChestX,
            value.ChestY,
            "packet.chest.position_out_of_world");
        if (position.Kind != PacketDecisionKind.Allow)
        {
            return position;
        }

        return value.ChestId < -1
            ? PacketProcessingDecision.Violation("packet.chest.id_out_of_range")
            : value.ChestId >= 0 ? ValidateChestId(value.ChestId) : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidatePlaceChest(PlaceChestView value)
    {
        PacketProcessingDecision position = ValidateCoordinates(value.TileX, value.TileY, "packet.chest.position_out_of_world");
        if (position.Kind != PacketDecisionKind.Allow)
        {
            return position;
        }

        return value.ChestIdToDestroy >= 0
            ? ValidateChestId(value.ChestIdToDestroy)
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateChestSize(SyncChestSizeView value)
    {
        PacketProcessingDecision id = ValidateChestId(value.ChestId);
        if (id.Kind != PacketDecisionKind.Allow)
        {
            return id;
        }

        return value.NewSize is < 0 or > MaximumChestSlots
            ? PacketProcessingDecision.Violation("packet.chest.size_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateChestName(ChestNameView value)
    {
        PacketProcessingDecision id = ValidateChestId(value.ChestId);
        if (id.Kind != PacketDecisionKind.Allow)
        {
            return id;
        }

        return ValidateCoordinates(value.ChestX, value.ChestY, "packet.chest.position_out_of_world");
    }

    private PacketProcessingDecision ValidateLeashedAnchor(TELeashedEntityAnchorPlaceItemView value)
    {
        PacketProcessingDecision position = ValidateCoordinates(
            value.TileX,
            value.TileY,
            "packet.leashed_entity.anchor_out_of_world");
        if (position.Kind != PacketDecisionKind.Allow)
        {
            return position;
        }

        return value.ItemType < 0 || value.ItemType >= itemTypeCount
            ? PacketProcessingDecision.Violation("packet.leashed_entity.item_type_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateTilePicking(SyncTilePickingView value) =>
        ValidateCoordinates(value.TileX, value.TileY, "packet.tile.position_out_of_world");


    private PacketProcessingDecision ValidatePlayerChestIndex(int chest) =>
        chest is >= -5 and <= -1 ? PacketProcessingDecision.Allow : ValidateChestId(chest);

    private PacketProcessingDecision ValidateQuickStack(ForceItemIntoNearestChestView value)
    {
        if (value.PayloadKind != QuickStackChestsPayloadKind.InventoryRequest)
            return PacketProcessingDecision.Reject("packet.quick_stack.direction");
        int count = inventorySlotCount();
        foreach (short slot in value.InventorySlots)
            if (count <= 0 || slot < 0 || slot >= count)
                return PacketProcessingDecision.Violation("packet.inventory.slot_out_of_range");
        return PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateMassWire(MassWireOperationView value)
    {
        PacketProcessingDecision start = ValidateCoordinates(
            value.StartX,
            value.StartY,
            "packet.mass_wire.position_out_of_world");
        return start.Kind != PacketDecisionKind.Allow
            ? start
            : ValidateCoordinates(value.EndX, value.EndY, "packet.mass_wire.position_out_of_world");
    }

    private PacketProcessingDecision ValidatePlacedItem(
        int x,
        int y,
        int itemType,
        byte prefix,
        int stack,
        string reasonPrefix)
    {
        PacketProcessingDecision position = ValidateCoordinates(x, y, $"{reasonPrefix}.position_out_of_world");
        if (position.Kind != PacketDecisionKind.Allow)
        {
            return position;
        }

        if (itemType < 0 || itemType >= itemTypeCount)
        {
            return PacketProcessingDecision.Violation($"{reasonPrefix}.item_type_out_of_range");
        }

        if (prefix >= prefixCount)
        {
            return PacketProcessingDecision.Violation($"{reasonPrefix}.prefix_out_of_range");
        }

        return stack is < 0 or > MaximumStack
            ? PacketProcessingDecision.Violation($"{reasonPrefix}.stack_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateDisplayDollItem(TileEntityDisplayDollItemSyncView value) =>
        !value.HasItemData
            ? PacketProcessingDecision.Allow
            : ValidateTileEntityItem(value.ItemType, value.Stack, value.Prefix);

    private PacketProcessingDecision ValidateHatRackItem(TileEntityHatRackItemSyncView value) =>
        ValidateTileEntityItem(value.ItemType, value.Stack, value.Prefix);

    private PacketProcessingDecision ValidateTileEntityItem(int itemType, int stack, byte prefix)
    {
        if (itemType >= itemTypeCount)
        {
            return PacketProcessingDecision.Violation("packet.tile_entity.item_type_out_of_range");
        }

        if (stack > MaximumStack)
        {
            return PacketProcessingDecision.Violation("packet.tile_entity.stack_out_of_range");
        }

        return prefix >= prefixCount
            ? PacketProcessingDecision.Violation("packet.tile_entity.prefix_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateChestId(int id)
    {
        int count = chestCount();
        return id < 0 || count <= 0 || id >= count
            ? PacketProcessingDecision.Violation("packet.chest.id_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateCoordinates(int x, int y, string reason)
    {
        int width = worldWidth();
        int height = worldHeight();
        return width <= 0 || height <= 0 || x < 0 || y < 0 || x >= width || y >= height
            ? PacketProcessingDecision.Violation(reason)
            : PacketProcessingDecision.Allow;
    }
}
