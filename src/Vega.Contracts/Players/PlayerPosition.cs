namespace TShockNG.Contracts.Players;

/// <summary>
/// Terraria world-space position in pixels (16 pixels per tile).
/// </summary>
public readonly record struct PlayerPosition(float X, float Y);
