using System.Text.Json;
using Vega.Contracts.Players;

namespace Vega.Modules.Navigation;

public sealed record WarpPoint(string Name, PlayerPosition Position, bool Hidden = false);

/// <summary>One bounded, atomic file per persistent world identity in the owning runtime's data directory.</summary>
public sealed class WarpStore(string directory)
{
    private const int MaximumWarps = 4096;
    private const long MaximumFileBytes = 2 * 1024 * 1024;
    private readonly string root = Path.GetFullPath(directory);
    private readonly SemaphoreSlim gate = new(1, 1); // One writer per runtime; sandbox data roots are independent.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask<IReadOnlyList<WarpPoint>> ReadAsync(Guid world, CancellationToken token = default)
    {
        if (!await gate.WaitAsync(0, token).ConfigureAwait(false)) throw new IOException("Warp storage is busy.");
        try { return await Task.Run(() => LoadAsync(world, token).AsTask(), token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public ValueTask<bool> AddAsync(Guid world, WarpPoint point, CancellationToken token = default)
    {
        if (!Valid(point)) return ValueTask.FromResult(false);
        return ChangeAsync(world, points =>
        {
            if (points.Count >= MaximumWarps || Find(points, point.Name) >= 0) return false;
            points.Add(point);
            return true;
        }, token);
    }

    public ValueTask<bool> RemoveAsync(Guid world, string name, CancellationToken token = default) => ChangeAsync(world, points =>
    {
        int index = Find(points, name);
        if (index < 0) return false;
        points.RemoveAt(index);
        return true;
    }, token);

    public ValueTask<bool> SetHiddenAsync(Guid world, string name, bool hidden, CancellationToken token = default) => ChangeAsync(world, points =>
    {
        int index = Find(points, name);
        if (index < 0) return false;
        points[index] = points[index] with { Hidden = hidden };
        return true;
    }, token);

    /// <summary>Publish an offline import into a new world store without overwriting existing warp data.</summary>
    public ValueTask<bool> ImportNewAsync(Guid world, IReadOnlyList<WarpPoint> imported, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(imported);
        if (imported.Count > MaximumWarps || imported.Any(point => !Valid(point)) ||
            imported.Select(point => point.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != imported.Count)
            throw new InvalidDataException("Imported warps contain invalid or duplicate points, or exceed the store limit.");
        return ChangeAsync(world, points => { points.AddRange(imported); return true; }, token, createOnly: true);
    }

    private async ValueTask<bool> ChangeAsync(Guid world, Func<List<WarpPoint>, bool> change, CancellationToken token, bool createOnly = false)
    {
        if (!await gate.WaitAsync(0, token).ConfigureAwait(false)) throw new IOException("Warp storage is busy.");
        try
        {
            return await Task.Run(async () =>
            {
            string path = PathFor(world);
            if (createOnly && File.Exists(path)) return false;
            List<WarpPoint> points = await LoadAsync(world, token).ConfigureAwait(false);
            if (!change(points)) return false;
            Directory.CreateDirectory(root);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(stream, points, JsonOptions, token).ConfigureAwait(false);
                    await stream.FlushAsync(token).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: !createOnly);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return true;
            }, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async ValueTask<List<WarpPoint>> LoadAsync(Guid world, CancellationToken token)
    {
        string path = PathFor(world);
        if (!File.Exists(path)) return [];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("Warp file exceeds its size limit.");
        List<WarpPoint> points = await JsonSerializer.DeserializeAsync<List<WarpPoint>>(stream, JsonOptions, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Warp file is empty.");
        if (points.Count > MaximumWarps || points.Any(p => !Valid(p)) ||
            points.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != points.Count)
            throw new InvalidDataException("Warp file contains invalid or duplicate points.");
        return points;
    }

    private string PathFor(Guid world) => world != Guid.Empty ? Path.Combine(root, world.ToString("N") + ".json")
        : throw new ArgumentException("A persistent world identity is required.", nameof(world));

    private static int Find(List<WarpPoint> points, string name) => points.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static readonly HashSet<string> ReservedNames = new(["add", "del", "hide", "list", "send"], StringComparer.OrdinalIgnoreCase);

    public static bool ValidName(string? name) => name is { Length: > 0 and <= 64 } && name == name.Trim() &&
        !name.Any(char.IsControl) && !ReservedNames.Contains(name);

    private static bool Valid(WarpPoint? point) => point is not null && ValidName(point.Name) &&
        float.IsFinite(point.Position.X) && float.IsFinite(point.Position.Y) && point.Position.X >= 0 && point.Position.Y >= 0;
}
