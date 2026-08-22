using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Authorization;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;

namespace Vega.Core.Networking;

public sealed class GameplayEnforcementPolicy : IPacketProcessor<PacketView>, IDisposable
{
    private readonly object sync = new();
    private readonly GameplayEnforcementOptions options;
    private readonly IPlayerAuthorizationService authorization;
    private readonly IPlayerDirectory players;
    private readonly IPlayerSessionLifecycle sessions;
    private readonly Dictionary<PlayerId, DateTimeOffset> lastDeaths = [];
    private bool disposed;

    public GameplayEnforcementPolicy(
        GameplayEnforcementOptions options,
        IPlayerAuthorizationService authorization,
        IPlayerDirectory players,
        IPlayerSessionLifecycle sessions)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        this.players = players ?? throw new ArgumentNullException(nameof(players));
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        sessions.Closed += OnSessionClosed;
    }

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (!options.Enabled ||
            context.Direction != PacketDirection.Inbound ||
            context.PlayerId is not PlayerId playerId ||
            authorization.HasPermission(playerId, options.BypassPermission))
        {
            return PacketProcessingDecision.Allow;
        }

        players.TryGet(playerId, out PlayerSnapshot? player);
        if (context.Packet.PacketType == PacketTypes.PlayerUpdate &&
            options.RejectLivingGhost &&
            player is { IsDead: false } &&
            (context.Packet.AsPlayerUpdateView().MovementFlags & UpdatePlayerMovementFlags.IsGhost) != 0)
        {
            return PacketProcessingDecision.Reject("packet.gameplay.living_ghost");
        }

        if (context.Packet.PacketType == PacketTypes.SpectatePlayer &&
            options.RestrictSpectateToDeadPlayers &&
            player is { IsDead: false } &&
            context.Packet.AsSpectatePlayerView().TargetPlayerId >= 0)
        {
            return PacketProcessingDecision.Reject("packet.gameplay.spectate_while_alive");
        }

        if (context.Packet.PacketType == PacketTypes.PlayerDeathV2 &&
            options.MinimumDeathIntervalMilliseconds > 0 &&
            IsRepeatedDeath(playerId, context.Timestamp))
        {
            return PacketProcessingDecision.Reject("packet.gameplay.repeated_death");
        }

        return player is not null &&
            options.MaximumInteractionDistanceTiles > 0 &&
            TryGetInteractionTile(context.Packet, out int tileX, out int tileY) &&
            !IsWithinDistance(player.Position, tileX, tileY, options.MaximumInteractionDistanceTiles)
                ? PacketProcessingDecision.Reject("packet.gameplay.interaction_too_far")
                : PacketProcessingDecision.Allow;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        sessions.Closed -= OnSessionClosed;
        lock (sync)
        {
            lastDeaths.Clear();
        }

        disposed = true;
    }

    private bool IsRepeatedDeath(PlayerId playerId, DateTimeOffset timestamp)
    {
        lock (sync)
        {
            bool repeated = lastDeaths.TryGetValue(playerId, out DateTimeOffset previous) &&
                timestamp >= previous &&
                timestamp - previous < TimeSpan.FromMilliseconds(options.MinimumDeathIntervalMilliseconds);
            if (!repeated)
            {
                lastDeaths[playerId] = timestamp;
            }

            return repeated;
        }
    }

    private static bool TryGetInteractionTile(PacketView packet, out int tileX, out int tileY)
    {
        (tileX, tileY) = packet.PacketType switch
        {
            PacketTypes.Tile => (packet.AsTileView().TileX, packet.AsTileView().TileY),
            PacketTypes.PlaceObject => (packet.AsPlaceObjectView().X, packet.AsPlaceObjectView().Y),
            PacketTypes.DoorUse => (packet.AsDoorUseView().TileX, packet.AsDoorUseView().TileY),
            PacketTypes.PaintTile => (packet.AsPaintTileView().X, packet.AsPaintTileView().Y),
            PacketTypes.PaintWall => (packet.AsPaintWallView().X, packet.AsPaintWallView().Y),
            PacketTypes.SignRead => (packet.AsSignReadView().X, packet.AsSignReadView().Y),
            PacketTypes.SignNew => (packet.AsSignNewView().X, packet.AsSignNewView().Y),
            PacketTypes.PlaceChest => (packet.AsPlaceChestView().TileX, packet.AsPlaceChestView().TileY),
            PacketTypes.LiquidSet => (packet.AsLiquidSetView().X, packet.AsLiquidSetView().Y),
            PacketTypes.MassWireOperation =>
                (packet.AsMassWireOperationView().EndX, packet.AsMassWireOperationView().EndY),
            _ => (int.MinValue, int.MinValue)
        };
        return tileX != int.MinValue;
    }

    private static bool IsWithinDistance(
        PlayerPosition position,
        int tileX,
        int tileY,
        int maximumTiles)
    {
        float playerTileX = position.X / 16f;
        float playerTileY = position.Y / 16f;
        return MathF.Abs(playerTileX - tileX) <= maximumTiles &&
            MathF.Abs(playerTileY - tileY) <= maximumTiles;
    }

    private void OnSessionClosed(PlayerSessionClosed closed)
    {
        lock (sync)
        {
            lastDeaths.Remove(closed.PlayerId);
        }
    }
}
