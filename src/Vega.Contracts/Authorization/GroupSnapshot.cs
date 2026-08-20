namespace Vega.Contracts.Authorization;

public sealed record GroupSnapshot(
    string Name,
    string? ParentName,
    IReadOnlySet<string> Permissions);
