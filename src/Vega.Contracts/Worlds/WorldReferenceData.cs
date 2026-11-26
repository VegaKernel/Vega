namespace Vega.Contracts.Worlds;

public enum WorldReferenceKind { Item, Npc, Buff, Projectile, Tile, All }
public sealed record WorldReferenceExportResult(bool Succeeded, string? RelativePath = null, int EntryCount = 0, string? ErrorCode = null);

public interface IWorldReferenceDataExporter
{
    ValueTask<WorldReferenceExportResult> ExportAsync(WorldId worldId, WorldReferenceKind kind, CancellationToken cancellationToken = default);
}
