namespace Vega.Contracts.Worlds;

public sealed record NpcDefinition(int NetId, string Name);

public enum NpcResolutionStatus
{
    Found,
    NotFound,
    Ambiguous
}

public sealed record NpcResolutionResult(
    NpcResolutionStatus Status,
    NpcDefinition? Npc = null,
    IReadOnlyList<NpcDefinition>? Matches = null)
{
    public IReadOnlyList<NpcDefinition> Candidates => Matches ?? [];
}

public interface INpcCatalog
{
    NpcResolutionResult Resolve(string query);
}
