namespace Vega.Terraria.Worlds;

/// <summary>Installs a paged grid at construction, never replaces a live or already initialized world.</summary>
public sealed class NativeTileStorageScope : IDisposable
{
    private const string CanonicalSource = "System.Void Terraria.Main::.cctor()";
    private PagedNativeTileCollection? created;
    private bool attached = true;

    public NativeTileStorageScope() =>
        global::TerrariaRuntime.DefaultCollection<global::Terraria.ITile>.OnCreateCollection += OnCreate;

    public PagedNativeTileCollection Complete()
    {
        try
        {
            // The caller must set the native save path first. If Main was initialized by
            // another owner, do not replace its collection or silently report success.
            var canonical = global::Terraria.Main.tile;
            if (created is null || !ReferenceEquals(created, canonical))
                throw new InvalidOperationException("Paged tile storage could not be installed before native Main.tile initialization.");
            return created;
        }
        finally { Dispose(); }
    }

    public void Dispose()
    {
        if (!attached) return;
        attached = false;
        global::TerrariaRuntime.DefaultCollection<global::Terraria.ITile>.OnCreateCollection -= OnCreate;
    }

    private global::TerrariaRuntime.ICollection<global::Terraria.ITile> OnCreate(int width, int height, string source)
    {
        if (!string.Equals(source, CanonicalSource, StringComparison.Ordinal)) return null!;
        if (created is not null) throw new InvalidOperationException("Native canonical tile collection was requested twice.");
        created = new PagedNativeTileCollection(width, height);
        return created;
    }
}
