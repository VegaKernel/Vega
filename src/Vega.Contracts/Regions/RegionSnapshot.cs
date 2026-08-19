namespace TShockNG.Contracts.Regions;

public sealed record RegionSnapshot(
    int Id,
    string Name,
    int X,
    int Y,
    int Width,
    int Height,
    string? Owner);
