using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Core.Networking;

public sealed class SceneInteractionAuthorityPolicy(ISceneRuntime scenes, WorldId? ownedWorld,
    IPlayerDirectory? players) : IPacketProcessor<PacketView>
{
    public SceneInteractionAuthorityPolicy(ISceneRuntime scenes, WorldId? ownedWorld = null) : this(scenes, ownedWorld, null) { }

    public PacketProcessingDecision Process(in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound)
        {
            return PacketProcessingDecision.Allow;
        }

        WorldId world = ownedWorld ?? WorldId.Primary;
        if (context.Packet.PacketType == PacketTypes.SignNew) return ProcessSignEdit(in context, world);
        if (context.Packet.PacketType == PacketTypes.MassWireOperation) return ProcessMassWire(in context, world);
        if (context.PlayerId is not PlayerId playerId || !TryGetPosition(context.Packet, out int x, out int y)) return PacketProcessingDecision.Allow;
        if (!scenes.TryResolveTile(
            world,
            playerId,
            x,
            y,
            out _,
            out _))
        {
            if (context.Packet.PacketType == PacketTypes.SignRead && players?.TryGet(playerId, out var canonicalReader) == true && canonicalReader is not null && canonicalReader.WorldId == world)
                scenes.ObserveCanonicalSignRead(canonicalReader, x, y);
            return PacketProcessingDecision.Allow;
        }
        // A visible virtual object may produce a plugin-owned intent, but
        // the canonical interaction is still rejected. No native chest, sign
        // or item mutation is performed by this observation path.
        if (context.Packet.PacketType is PacketTypes.ChestGetContents or PacketTypes.SignRead && players is not null &&
            players.TryGet(playerId, out var player) && player is not null && player.WorldId == world)
        {
            if (context.Packet.PacketType == PacketTypes.ChestGetContents) scenes.TryRequestVirtualChestOpen(player, x, y);
            else scenes.TryRequestVirtualSignRead(player, x, y);
        }
        if (context.Packet.PacketType == PacketTypes.HitSwitch && scenes is ISceneButtonRuntime buttons && players is not null &&
            players.TryGet(playerId, out var buttonPlayer) && buttonPlayer is not null && buttonPlayer.WorldId == world)
            buttons.TryRequestButtonPress(buttonPlayer, x, y);
        return PacketProcessingDecision.Reject("scene.phantom_interaction");
    }

    private PacketProcessingDecision ProcessMassWire(in PacketProcessingContext<PacketView> context, WorldId world)
    {
        if (context.PlayerId is not { } id) return PacketProcessingDecision.Allow;
        var wire = context.Packet.AsMassWireOperationView();
        var origin = new WorldTilePosition(wire.StartX, wire.StartY);
        var position = new WorldTilePosition(wire.EndX, wire.EndY);
        if (scenes is ISceneGestureRuntime gestures && players is ISceneGestureToolState tool &&
            players.TryGet(id, out var player) && player is not null && player.WorldId == world &&
            gestures.TryCompleteGesture(player, origin, position, tool.IsHoldingGestureTool(player), context.Timestamp))
            return PacketProcessingDecision.Reject("scene.phantom_interaction");

        // The vanilla tool changes more than one cell. A virtual overlay may never route that
        // multi-cell operation into the canonical world, irrespective of plugin registrations.
        var bounds = new SceneBounds(Math.Min(origin.X, position.X), Math.Min(origin.Y, position.Y),
            Math.Abs(origin.X - position.X) + 1, Math.Abs(origin.Y - position.Y) + 1);
        return scenes.HasOverlayInRegion(world, bounds, id)
            ? PacketProcessingDecision.Reject("scene.phantom_interaction") : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ProcessSignEdit(in PacketProcessingContext<PacketView> context, WorldId world)
    {
        if (context.PlayerId is not PlayerId playerId) return PacketProcessingDecision.Allow;
        var edit = context.Packet.AsSignNewView();
        PlayerSnapshot? player = null;
        if (players?.TryGet(playerId, out var current) == true && current?.WorldId == world) player = current;
        // Session aliases remain intercepted even if the scene moved, was hidden, or was disposed.
        if (player is not null && scenes.IsVirtualSignIndex(player, edit.SignId))
        {
            if (edit.PlayerId == playerId.Slot) scenes.TryRequestPresentedVirtualSignEdit(player, edit.SignId, edit.X, edit.Y, edit.Text);
            return PacketProcessingDecision.Reject("scene.phantom_interaction");
        }
        if (scenes.TryResolveTile(world, playerId, edit.X, edit.Y, out _, out _))
        {
            if (player is not null && edit.PlayerId == playerId.Slot) scenes.TryRequestVirtualSignEdit(player, edit.X, edit.Y, edit.Text);
            return PacketProcessingDecision.Reject("scene.phantom_interaction");
        }
        return players is IWorldSignState signs && (!signs.TryGetSignPosition(edit.SignId, out var position) || position.X != edit.X || position.Y != edit.Y)
            ? PacketProcessingDecision.Reject("packet.sign.position_mismatch") : PacketProcessingDecision.Allow;
    }

    private bool TryGetPosition(PacketView packet, out int x, out int y)
    {
        (x, y) = packet.PacketType switch
        {
            PacketTypes.Tile => (packet.AsTileView().TileX, packet.AsTileView().TileY),
            PacketTypes.PlaceObject => (packet.AsPlaceObjectView().X, packet.AsPlaceObjectView().Y),
            PacketTypes.PaintTile => (packet.AsPaintTileView().X, packet.AsPaintTileView().Y),
            PacketTypes.PaintWall => (packet.AsPaintWallView().X, packet.AsPaintWallView().Y),
            PacketTypes.PlaceChest => (packet.AsPlaceChestView().TileX, packet.AsPlaceChestView().TileY),
            PacketTypes.ChestGetContents => (packet.AsChestGetContentsView().TileX, packet.AsChestGetContentsView().TileY),
            PacketTypes.ChestUnlock => (packet.AsChestUnlockView().X, packet.AsChestUnlockView().Y),
            PacketTypes.SignRead => (packet.AsSignReadView().X, packet.AsSignReadView().Y),
            PacketTypes.HitSwitch => (packet.AsHitSwitchView().X, packet.AsHitSwitchView().Y),
            PacketTypes.LiquidSet => (packet.AsLiquidSetView().X, packet.AsLiquidSetView().Y),
            PacketTypes.PlaceTileEntity => (packet.AsPlaceTileEntityView().X, packet.AsPlaceTileEntityView().Y),
            PacketTypes.PlaceItemFrame => (packet.AsPlaceItemFrameView().X, packet.AsPlaceItemFrameView().Y),
            PacketTypes.WeaponsRackTryPlacing => (packet.AsWeaponsRackTryPlacingView().TileX, packet.AsWeaponsRackTryPlacingView().TileY),
            PacketTypes.FoodPlatterTryPlacing => (packet.AsFoodPlatterTryPlacingView().TileX, packet.AsFoodPlatterTryPlacingView().TileY),
            PacketTypes.RequestTileEntityInteraction => EntityPosition(packet.AsRequestTileEntityInteractionView().TileEntityId),
            PacketTypes.TileEntityDisplayDollItemSync => EntityPosition(packet.AsTileEntityDisplayDollItemSyncView().TileEntityId),
            PacketTypes.TileEntityHatRackItemSync => EntityPosition(packet.AsTileEntityHatRackItemSyncView().TileEntityId),
            _ => (int.MinValue, int.MinValue)
        };
        return x != int.MinValue;
    }

    private (int X, int Y) EntityPosition(int id) => players is IWorldInteractionState world && world.TryGetTileEntityPosition(id, out var position)
        ? (position.X, position.Y) : (int.MinValue, int.MinValue);
}
