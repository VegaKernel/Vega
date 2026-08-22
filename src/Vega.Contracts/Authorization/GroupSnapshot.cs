namespace Vega.Contracts.Authorization;

public sealed record GroupSnapshot(
    string Name,
    string? ParentName,
    IReadOnlySet<string> Permissions)
{
    public IReadOnlySet<string> DeniedPermissions { get; init; } = new HashSet<string>();

    public long Revision { get; init; }
}
