namespace Vega.Contracts.Worlds;

/// <summary>
/// Read-only identity surface for a managed world. Mutable Terraria state is intentionally not exposed here.
/// </summary>
public interface IWorldContext
{
    WorldId Id { get; }
    string Name { get; }
}

/// <summary>
/// Resolves managed worlds without assuming that exactly one world exists.
/// </summary>
public interface IWorldContextAccessor
{
    IWorldContext Primary { get; }

    bool TryGet(WorldId id, out IWorldContext? world);
}
