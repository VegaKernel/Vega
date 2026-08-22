namespace Vega.Contracts.Regions;

using Vega.Contracts.Worlds;

public sealed record RegionSnapshot(
    int Id,
    string Name,
    int X,
    int Y,
    int Width,
    int Height,
    string? Owner)
{
    public WorldId WorldId { get; init; } = WorldId.Primary;

    public int Priority { get; init; }

    public IReadOnlySet<string> Members { get; init; } = new HashSet<string>();

    public IReadOnlySet<string> Groups { get; init; } = new HashSet<string>();

    public IReadOnlyDictionary<string, bool> Flags { get; init; } = new Dictionary<string, bool>();

    public long Revision { get; init; }

    public bool Contains(int tileX, int tileY) =>
        tileX >= X && tileY >= Y && tileX < X + Width && tileY < Y + Height;
}
