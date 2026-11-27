using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Regions;

/// <summary>Host-only current native state, captured after revalidating an exact player snapshot.</summary>
public readonly record struct CurrentPlayerIntentState(PlayerPosition Position, bool IsHostile, int Team);

/// <summary>Supplementary typed plugin restrictions. Register after built-in checks; a plugin may only deny.</summary>
public sealed class PlayerGameplayIntentPolicy(IPlayerDirectory players, IWorldInteractionState interactions,
    IWorldGameplayEvents events, IGameThreadDispatcher dispatcher, WorldId world, Func<PlayerSnapshot, CurrentPlayerIntentState?> currentState,
    IOperationsLogger logger) : IPacketProcessor<PacketView>
{
    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound || context.PlayerId is not { } id) return PacketProcessingDecision.Allow;
        if (context.Packet.PacketType is PacketTypes.TogglePvp or PacketTypes.PlayerTeam or PacketTypes.TeamChangeFromUI)
            return ProcessPvp(in context, id);
        bool restricted = events.HasPolicies<PlayerTileInteractionEvent>();
        bool observed = events.HasSubscribers<PlayerTileInteractionEvent>();
        bool committed = events.HasSubscribers<PlayerTileChangesCommittedEvent>();
        if ((!restricted && !observed && !committed) || !RegionBuildAuthorizationPolicy.TryGetInteractionArea(interactions, context.Packet,
                out int x1, out int y1, out int x2, out int y2, out var access)) return PacketProcessingDecision.Allow;

        PlayerSnapshot? player = Resolve(id, context.ConnectionId, out _);
        if (player?.SessionId is not { } session)
            return restricted ? PacketProcessingDecision.Reject("packet.tile_policy.session_unavailable") : PacketProcessingDecision.Allow;

        var (operation, contentType) = DescribeEdit(context.Packet);
        var intent = new PlayerTileInteractionEvent(world, id, session, Kind(context.Packet.PacketType),
            access == RegionBuildAuthorizationPolicy.RegionInteraction.Build ? TileInteractionAccess.Build : TileInteractionAccess.Use,
            new(x1, y1), new(x2, y2), operation, contentType);
        var decision = ObserveAndDecide(intent, player, observed, restricted, "packet.tile_policy");
        if (committed && decision.Kind == PacketDecisionKind.Allow)
        {
            try { events.Publish(new PlayerTileInteractionAcceptedEvent(intent)); }
            catch (Exception ex) { logger.Log(OperationsLogLevel.Warning, "gameplay-intent", "Unable to observe accepted tile interaction.", ex); }
        }
        return decision;
    }

    private PacketProcessingDecision ProcessPvp(scoped in PacketProcessingContext<PacketView> context, PlayerId id)
    {
        bool restricted = events.HasPolicies<PlayerPvpChangeRequestedEvent>();
        bool observed = events.HasSubscribers<PlayerPvpChangeRequestedEvent>();
        if (!restricted && !observed) return PacketProcessingDecision.Allow;
        PlayerSnapshot? player = Resolve(id, context.ConnectionId, out var live);
        if (player?.SessionId is not { } session)
            return restricted ? PacketProcessingDecision.Reject("packet.pvp_policy.session_unavailable") : PacketProcessingDecision.Allow;

        bool? hostile = context.Packet.PacketType == PacketTypes.TogglePvp ? context.Packet.AsTogglePvpView().PvpEnabled : null;
        int? team = context.Packet.PacketType switch
        {
            PacketTypes.PlayerTeam => context.Packet.AsPlayerTeamView().Team,
            PacketTypes.TeamChangeFromUI => context.Packet.AsTeamChangeFromUIView().Team,
            _ => null
        };
        return ObserveAndDecide(new PlayerPvpChangeRequestedEvent(world, id, session, live.Position,
            hostile.HasValue ? PlayerPvpChangeKind.Hostility : PlayerPvpChangeKind.Team, live.IsHostile, live.Team,
            hostile, team), player, observed, restricted, "packet.pvp_policy");
    }

    private PlayerSnapshot? Resolve(PlayerId id, ConnectionId connectionId, out CurrentPlayerIntentState live)
    {
        live = default;
        if (!dispatcher.IsGameThread || !players.TryGet(id, out var player) || player is not { SessionId: not null } ||
            player.WorldId != world || player.Connection is not { LoginPhase: ConnectionLoginPhase.FullyConnected } connection ||
            connection.ConnectionId != connectionId || Capture(player) is not { } state) return null;
        live = state;
        return player;
    }

    private PacketProcessingDecision ObserveAndDecide<T>(T intent, PlayerSnapshot player, bool observed, bool restricted, string reason)
        where T : struct
    {
        // Host-internal subscribers may also exist; an observation failure must never break native processing.
        try { if (observed) events.Publish(intent); }
        catch (Exception ex) { logger.Log(OperationsLogLevel.Error, "gameplay-intent", "Gameplay intent observation failed.", ex); }
        try
        {
            if (restricted && !events.Allows(intent)) return PacketProcessingDecision.Reject(reason + ".denied");
        }
        catch (Exception ex)
        {
            logger.Log(OperationsLogLevel.Error, "gameplay-intent", "Gameplay intent decision failed.", ex);
            return PacketProcessingDecision.Reject(reason + ".failed");
        }

        return Capture(player).HasValue ? PacketProcessingDecision.Allow : PacketProcessingDecision.Reject(reason + ".session_unavailable");
    }

    private CurrentPlayerIntentState? Capture(PlayerSnapshot player)
    {
        try { return currentState(player); }
        catch (Exception ex)
        {
            logger.Log(OperationsLogLevel.Warning, "gameplay-intent", "Live gameplay intent session unavailable.", ex);
            return null;
        }
    }

    private static TileInteractionKind Kind(PacketTypes kind) => kind switch
    {
        PacketTypes.Tile => TileInteractionKind.Tile,
        PacketTypes.PlaceObject => TileInteractionKind.ObjectPlacement,
        PacketTypes.DoorUse => TileInteractionKind.Door,
        PacketTypes.PaintTile or PacketTypes.PaintWall => TileInteractionKind.Paint,
        PacketTypes.SignRead or PacketTypes.SignNew => TileInteractionKind.Sign,
        PacketTypes.ChestItem or PacketTypes.SyncChestSize or PacketTypes.ChestOpen or PacketTypes.PlaceChest or
            PacketTypes.ChestGetContents or PacketTypes.ChestName or PacketTypes.ChestUnlock => TileInteractionKind.Container,
        PacketTypes.LiquidSet => TileInteractionKind.Liquid,
        PacketTypes.PlaceTileEntity or PacketTypes.PlaceItemFrame => TileInteractionKind.TileEntity,
        PacketTypes.MassWireOperation => TileInteractionKind.Wiring,
        _ => TileInteractionKind.Other
    };

    private static (TileEditOperation?, int?) DescribeEdit(PacketView packet)
    {
        if (packet.PacketType == PacketTypes.PlaceObject) return (TileEditOperation.PlaceTile, packet.AsPlaceObjectView().Type);
        if (packet.PacketType != PacketTypes.Tile) return (null, null);
        var tile = packet.AsTileView();
        return tile.Action switch
        {
            TileAction.KillTile or TileAction.KillTileNoItem or TileAction.KillTileKeepTileType => (TileEditOperation.BreakTile, null),
            TileAction.PlaceTile => (TileEditOperation.PlaceTile, tile.Value),
            TileAction.ReplaceTile => (TileEditOperation.ReplaceTile, tile.Value),
            TileAction.KillWall => (TileEditOperation.BreakWall, null),
            TileAction.PlaceWall => (TileEditOperation.PlaceWall, tile.Value),
            TileAction.ReplaceWall => (TileEditOperation.ReplaceWall, tile.Value),
            TileAction.PlaceWire or TileAction.PlaceWire2 or TileAction.PlaceWire3 or TileAction.PlaceWire4 => (TileEditOperation.AddWire, null),
            TileAction.KillWire or TileAction.KillWire2 or TileAction.KillWire3 or TileAction.KillWire4 => (TileEditOperation.RemoveWire, null),
            TileAction.PlaceActuator => (TileEditOperation.AddActuator, null),
            TileAction.KillActuator => (TileEditOperation.RemoveActuator, null),
            TileAction.PoundTile or TileAction.SlopeTile or TileAction.PoundTileWithSlope => (TileEditOperation.ChangeShape, null),
            TileAction.FrameTrack => (TileEditOperation.FrameTrack, null),
            TileAction.Actuate => (TileEditOperation.Activate, null),
            TileAction.PokeLogicGate => (TileEditOperation.LogicGate, null),
            _ => (TileEditOperation.Other, null)
        };
    }
}
