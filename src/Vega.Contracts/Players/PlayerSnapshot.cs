using Vega.Contracts.Worlds;

namespace Vega.Contracts.Players;

/// <summary>
/// Immutable read model of the Terraria-owned player state that Vega exposes outside the game-thread boundary.
/// Account, authorization and other application state are projected separately by their owning services.
/// </summary>
public sealed record PlayerSnapshot(
    PlayerId Id,
    string Name,
    bool IsDead,
    int Life,
    int MaxLife,
    int Mana,
    int MaxMana,
    PlayerPosition Position,
    bool IsHostile,
    int Team)
{
    public PlayerSessionId? SessionId { get; init; }

    public DateTimeOffset? ConnectedAt { get; init; }

    public string? RemoteEndpoint { get; init; }

    public string? Platform { get; init; }

    /// <summary>
    /// World context currently owning the player. The legacy single-world runtime is always primary.
    /// </summary>
    public WorldId WorldId { get; init; } = WorldId.Primary;
}
