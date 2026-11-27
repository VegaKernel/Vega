namespace Vega.Modules.Administration;

/// <summary>Optional additional restriction on delegated account and temporary-group assignments.</summary>
public sealed class GroupAssignmentOptions
{
    public Dictionary<string, HashSet<string>> Groups { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
