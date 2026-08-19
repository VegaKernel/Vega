namespace TShockNG.Contracts.Authorization;

public sealed record GroupSnapshot(
    string Name,
    string? ParentName,
    IReadOnlySet<string> Permissions);
