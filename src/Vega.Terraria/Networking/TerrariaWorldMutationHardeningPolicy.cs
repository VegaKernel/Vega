using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Terraria.Networking;

/// <summary>
/// Retains Terraria-version-specific world mutation checks that require live native registries.
/// </summary>
public sealed class TerrariaWorldMutationHardeningPolicy : IPacketProcessor<PacketView>
{
    private readonly Func<int> worldWidth;
    private readonly Func<int> worldHeight;
    private readonly Func<int, int, ushort> tileType;
    private readonly Func<int, int> itemMaxStack;
    private readonly Func<int, int, bool> itemFrameExists;

    public TerrariaWorldMutationHardeningPolicy() : this(
        static () => global::Terraria.Main.maxTilesX,
        static () => global::Terraria.Main.maxTilesY,
        static (x, y) => global::Terraria.Main.tile[x, y].type,
        NativeItemMaxStack,
        NativeItemFrameExists)
    {
    }

    public TerrariaWorldMutationHardeningPolicy(
        Func<int> worldWidth,
        Func<int> worldHeight,
        Func<int, int, ushort> tileType,
        Func<int, int> itemMaxStack,
        Func<int, int, bool>? itemFrameExists = null)
    {
        this.worldWidth = worldWidth ?? throw new ArgumentNullException(nameof(worldWidth));
        this.worldHeight = worldHeight ?? throw new ArgumentNullException(nameof(worldHeight));
        this.tileType = tileType ?? throw new ArgumentNullException(nameof(tileType));
        this.itemMaxStack = itemMaxStack ?? throw new ArgumentNullException(nameof(itemMaxStack));
        this.itemFrameExists = itemFrameExists ?? NativeItemFrameExists;
    }

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound)
        {
            return PacketProcessingDecision.Allow;
        }

        return context.Packet.PacketType switch
        {
            PacketTypes.DoorUse => ValidateDoor(context.Packet.AsDoorUseView()),
            PacketTypes.ChestItem => ValidateChestStack(context.Packet.AsChestItemView()),
            PacketTypes.ItemDrop => ValidateWorldItemStack(context.Packet.AsItemDropView()),
            PacketTypes.UpdateItemDrop => ValidateWorldItemStack(context.Packet.AsUpdateItemDropView()),
            PacketTypes.PlaceItemFrame => ValidateItemFrame(context.Packet.AsPlaceItemFrameView()),
            _ => PacketProcessingDecision.Allow
        };
    }

    private PacketProcessingDecision ValidateDoor(DoorUseView door)
    {
        if (door.TileX < 0 || door.TileY < 0 ||
            door.TileX >= worldWidth() || door.TileY >= worldHeight())
        {
            return PacketProcessingDecision.Violation("packet.door.position_out_of_world");
        }

        ushort currentTileType = tileType(door.TileX, door.TileY);
        return currentTileType is global::Terraria.ID.TileID.ClosedDoor or
            global::Terraria.ID.TileID.OpenDoor or
            global::Terraria.ID.TileID.TallGateClosed or
            global::Terraria.ID.TileID.TallGateOpen or
            global::Terraria.ID.TileID.TrapdoorClosed or
            global::Terraria.ID.TileID.TrapdoorOpen
                ? PacketProcessingDecision.Allow
                : PacketProcessingDecision.Reject("packet.door.tile_type_mismatch");
    }

    private PacketProcessingDecision ValidateChestStack(ChestItemView chestItem) =>
        chestItem.Stack > itemMaxStack(chestItem.ItemNetId)
            ? PacketProcessingDecision.Violation("packet.chest.stack_exceeds_item_limit")
            : PacketProcessingDecision.Allow;

    private PacketProcessingDecision ValidateWorldItemStack(ItemDropView item) =>
        ValidateWorldItemStack(item.Stack, item.ItemNetId);

    private PacketProcessingDecision ValidateWorldItemStack(UpdateItemDropView item) =>
        ValidateWorldItemStack(item.Stack, item.ItemNetId);

    private PacketProcessingDecision ValidateWorldItemStack(short stack, short itemNetId)
    {
        if (stack == 0 || itemNetId == 0)
        {
            return PacketProcessingDecision.Allow;
        }

        return stack > itemMaxStack(itemNetId)
            ? PacketProcessingDecision.Violation("packet.world_item.stack_exceeds_item_limit")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateItemFrame(PlaceItemFrameView itemFrame) =>
        itemFrameExists(itemFrame.X, itemFrame.Y)
            ? PacketProcessingDecision.Allow
            : PacketProcessingDecision.Reject("packet.item_frame.not_found");

    private static int NativeItemMaxStack(int itemNetId)
    {
        var item = new global::Terraria.Item();
        item.netDefaults(itemNetId);
        return item.maxStack;
    }

    private static bool NativeItemFrameExists(int x, int y)
    {
        int id = global::Terraria.GameContent.Tile_Entities.TEItemFrame.Find(x, y);
        return id >= 0 &&
            global::Terraria.DataStructures.TileEntity.ByID.TryGetValue(id, out global::Terraria.DataStructures.TileEntity? entity) &&
            entity is global::Terraria.GameContent.Tile_Entities.TEItemFrame;
    }
}
