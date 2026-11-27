using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Core.Networking;

/// <summary>Managed virtual GUI changes are observations, never native inventory/chest mutations.</summary>
public sealed class SceneManagedChestGuiPolicy(ISceneRuntime scenes, WorldId world, IPlayerDirectory players) : IPacketProcessor<PacketView>
{
    private static readonly PacketProcessingDecision Managed = PacketProcessingDecision.Reject("scene.virtual_chest_managed");

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound || scenes is not IVirtualChestGuiAuthority { HasManagedChestPlayers: true } authority ||
            !IsManagedInventoryPacket(context.Packet.PacketType) || context.PlayerId is not PlayerId playerId || !authority.IsManagedChestSlot(world, playerId))
            return PacketProcessingDecision.Allow;
        // A temporarily unavailable directory snapshot is not permission to mutate a managed slot.
        if (!players.TryGet(playerId, out var player) || player is null || player.WorldId != world || !authority.IsManagedChestPlayer(player)) return Managed;
        try
        {
            switch (context.Packet.PacketType)
            {
                case PacketTypes.PlayerSlot:
                    var slot = context.Packet.AsPlayerSlotView();
                    if (slot.PlayerId == playerId.Slot)
                        authority.ObserveManagedInventory(player, new(slot.SlotId, slot.ItemNetId, slot.Stack, slot.Prefix, (slot.ItemFlags & 1) != 0), context.Timestamp);
                    break;
                case PacketTypes.ChestItem:
                    var item = context.Packet.AsChestItemView();
                    authority.ObserveManagedChest(player, item.ChestId, new(item.ItemSlot, item.ItemNetId, item.Stack, item.Prefix), context.Timestamp);
                    break;
                case PacketTypes.ChestOpen:
                    var open = context.Packet.AsChestOpenView();
                    authority.ObserveManagedChestOpen(player, open.ChestId, open.ChestX, open.ChestY);
                    break;
                case PacketTypes.ChestGetContents:
                    var click = context.Packet.AsChestGetContentsView();
                    authority.RequestManagedGuiClose(player);
                    // A new virtual open can proceed to the ordinary scene-intent policy;
                    // canonical chest interactions are out of scope until this exact session ends.
                    if (scenes.TryResolveTile(world, playerId, click.TileX, click.TileY, out _, out _)) return PacketProcessingDecision.Allow;
                    break;
                case PacketTypes.PlaceItemFrame:
                    var frame = context.Packet.AsPlaceItemFrameView();
                    if (scenes is IManagedDisplayAuthority frameAuthority)
                        frameAuthority.ObserveManagedDisplayPlacement(player,ManagedDisplayKind.ItemFrame,frame.X,frame.Y,new(frame.ItemId,frame.Stack,frame.Prefix),context.Timestamp);
                    break;
                case PacketTypes.WeaponsRackTryPlacing:
                    var rack = context.Packet.AsWeaponsRackTryPlacingView();
                    if (scenes is IManagedDisplayAuthority rackAuthority)
                        rackAuthority.ObserveManagedDisplayPlacement(player,ManagedDisplayKind.WeaponsRack,rack.TileX,rack.TileY,new(rack.ItemType,rack.Stack,rack.Prefix),context.Timestamp);
                    break;
                default:
                    authority.RequestManagedGuiClose(player);
                    break;
            }
        }
        catch (Exception)
        {
            // Never let an error in a managed transaction fall through into vanilla mutation.
            authority.RequestManagedGuiClose(player);
        }
        return Managed;
    }

    private static bool IsManagedInventoryPacket(PacketTypes type) => type is PacketTypes.PlayerSlot or PacketTypes.SyncLoadout or
        PacketTypes.ChestItem or PacketTypes.ChestOpen or PacketTypes.ChestGetContents or PacketTypes.ChestName or PacketTypes.SyncChestSize or
        PacketTypes.SyncPlayerChestIndex or PacketTypes.ForceItemIntoNearestChest or PacketTypes.ItemDrop or PacketTypes.UpdateItemDrop or
        PacketTypes.SyncItemDespawn or PacketTypes.SyncItemPosition or PacketTypes.SyncItemsWithShimmerDeprecated or PacketTypes.SyncItemCannotBeTakenByEnemiesDeprecated or
        PacketTypes.PlaceItemFrame or PacketTypes.WeaponsRackTryPlacing or
        PacketTypes.FoodPlatterTryPlacing or PacketTypes.TileEntityDisplayDollItemSync or PacketTypes.TileEntityHatRackItemSync or
        PacketTypes.RequestTileEntityInteraction or PacketTypes.TEDeadCellsDisplayJar or PacketTypes.TELeashedEntityAnchorPlaceItem;
}
