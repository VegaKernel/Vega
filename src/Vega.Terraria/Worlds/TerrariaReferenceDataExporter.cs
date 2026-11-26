using System.Text.Json;
using ReLogic.Reflection;
using Terraria.ID;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Worlds;

/// <summary>Exports the native symbolic ID catalog, never world or character data.</summary>
public sealed class TerrariaReferenceDataExporter(string runtimeDataRoot, WorldId worldId) : IWorldReferenceDataExporter
{
    private readonly string root = Path.GetFullPath(runtimeDataRoot);
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async ValueTask<WorldReferenceExportResult> ExportAsync(WorldId requestedWorld, WorldReferenceKind kind, CancellationToken cancellationToken = default)
    {
        if (requestedWorld != worldId || !Enum.IsDefined(kind)) return new(false, ErrorCode: "reference.invalid_request");
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return new(false, ErrorCode: "reference.busy");
        try
        {
            return await Task.Run(() => Export(kind, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(false, ErrorCode: "reference.write_failed"); }
        finally { gate.Release(); }
    }

    private WorldReferenceExportResult Export(WorldReferenceKind kind, CancellationToken token)
    {
        var categories = new Dictionary<string, Entry[]>();
        foreach (WorldReferenceKind selected in kind == WorldReferenceKind.All ? Enum.GetValues<WorldReferenceKind>().Where(k => k != WorldReferenceKind.All) : [kind])
        {
            token.ThrowIfCancellationRequested();
            var (catalog, count) = selected switch
            {
                WorldReferenceKind.Item => (ItemID.Search, (int)ItemID.Count), WorldReferenceKind.Npc => (NPCID.Search, (int)NPCID.Count),
                WorldReferenceKind.Buff => (BuffID.Search, BuffID.Count), WorldReferenceKind.Projectile => (ProjectileID.Search, (int)ProjectileID.Count),
                WorldReferenceKind.Tile => (TileID.Search, (int)TileID.Count), _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            List<Entry> entries = [];
            for (int id = 0; id < count; id++)
                if (catalog.TryGetName(id, out string name)) entries.Add(new(id, name));
            categories.Add(selected.ToString().ToLowerInvariant(), [.. entries]);
        }
        string filename = kind.ToString().ToLowerInvariant() + ".json";
        string directory = Path.Combine(root, "reference");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, filename), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new { TerrariaVersion = typeof(global::Terraria.Main).Assembly.GetName().Version?.ToString(), Categories = categories }, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(true, $"reference/{filename}", categories.Values.Sum(entries => entries.Length));
    }

    private sealed record Entry(int Id, string Name);
}
