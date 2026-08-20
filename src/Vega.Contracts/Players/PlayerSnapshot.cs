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
}
