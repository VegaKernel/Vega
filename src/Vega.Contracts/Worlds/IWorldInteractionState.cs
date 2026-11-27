using Vega.Contracts.Players;

namespace Vega.Contracts.Worlds;

public readonly record struct WorldTilePosition(int X, int Y);

/// <summary>Protocol bounds for canonical native chest storage, not virtual scene storage.</summary>
public static class NativeChestCapacity
{
    public const int Minimum = 40;
    public const int Maximum = 200;
    public static bool IsSupported(int capacity) => capacity is >= Minimum and <= Maximum;
}

public interface IWorldInteractionState
{
    bool TryGetActiveChest(PlayerId playerId, out int chestId);

    bool TryGetChestPosition(int chestId, out WorldTilePosition position);
    /// <summary>Reads the current registered native chest capacity. Missing or inconsistent storage fails closed.</summary>
    bool TryGetChestCapacity(int chestId, out int capacity) { capacity = 0; return false; }

    bool TryGetTileEntityPosition(int tileEntityId, out WorldTilePosition position);
}

public interface IWorldSignState
{
    bool TryGetSignPosition(int signId, out WorldTilePosition position);
}
