using Vega.Contracts.Scenes;

namespace Vega.PluginSdk;

/// <summary>
/// Small tile-canvas layout for a FakeProvider panel. Coordinates are provider-local. A layout is
/// plugin-owned and mutable; the published provider leases and tile snapshots stay host-owned.
/// </summary>
public sealed partial class TileUiLayout
{
    private readonly SceneTile[] tiles;
    private readonly List<SceneButtonTarget> buttons = [];
    private readonly List<SceneGestureTarget> gestures = [];

    public TileUiLayout(int width, int height, SceneTile background = default)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Canvas dimensions must be positive.");
        Width = width;
        Height = height;
        tiles = new SceneTile[checked(width * height)];
        Array.Fill(tiles, background);
    }

    public int Width { get; }
    public int Height { get; }

    public TileUiLayout Paint(SceneBounds rectangle, SceneTile tile)
    {
        ValidateBounds(rectangle);
        for (int y = rectangle.Y; y < rectangle.Bottom; y++)
            tiles.AsSpan(y * Width + rectangle.X, rectangle.Width).Fill(tile);
        return this;
    }

    /// <summary>Paint and register a stock switch supplied by the caller.</summary>
    public TileUiLayout Button(string key, int x, int y, SceneTile switchTile)
    {
        ValidateControlKey(key);
        if ((switchTile.Flags & SceneTileFlags.Active) == 0) throw new ArgumentException("A button requires an active native switch tile.", nameof(switchTile));
        if (buttons.Count == 256 || buttons.Any(target => target.Key == key || target.LocalX == x && target.LocalY == y))
            throw new ArgumentException("Button keys and positions must be unique; a panel accepts at most 256 buttons.", nameof(key));
        Paint(new(x, y, 1, 1), switchTile);
        buttons.Add(new(key, x, y));
        return this;
    }

    /// <summary>Declare a drag handle/canvas region without changing its visual appearance.</summary>
    public TileUiLayout Gesture(string key, SceneBounds rectangle)
    {
        ValidateControlKey(key);
        ValidateBounds(rectangle);
        if (gestures.Count == 256 || gestures.Any(target => target.Key == key || target.LocalBounds.Intersects(rectangle)))
            throw new ArgumentException("Gesture keys must be unique and rectangles nonoverlapping; a panel accepts at most 256 gestures.", nameof(key));
        gestures.Add(new(key, rectangle));
        return this;
    }

    public SceneTileArea Snapshot() => new(Width, Height, tiles);

    public IPluginTileProvider CreatePersonal(IPluginFakeProvider fakeProvider, string name, int x, int y,
        IEnumerable<SceneSessionObserver> observers, int layer = 0) => Create(fakeProvider,
        () => fakeProvider.CreatePersonal(name, new(x, y, Width, Height), Snapshot(), observers, layer));

    public IPluginTileProvider CreateGlobal(IPluginFakeProvider fakeProvider, string name, int x, int y, int layer = 0) => Create(fakeProvider,
        () => fakeProvider.CreateGlobal(name, new(x, y, Width, Height), Snapshot(), layer));

    /// <summary>Update visuals only. Control geometry and active gesture captures are retained.</summary>
    public void Redraw(IPluginTileProvider panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        var bounds = panel.Definition.Bounds;
        if (bounds.Width != Width || bounds.Height != Height) throw new ArgumentException("Panel dimensions do not match this layout.", nameof(panel));
        panel.Replace(bounds, Snapshot());
    }

    private IPluginTileProvider Create(IPluginFakeProvider fakeProvider, Func<IPluginTileProvider> create)
    {
        ArgumentNullException.ThrowIfNull(fakeProvider);
        if (buttons.Count != 0 && !fakeProvider.SupportsVirtualButtons) throw new NotSupportedException("Virtual buttons are unavailable.");
        if (gestures.Count != 0 && !fakeProvider.SupportsVirtualGestures) throw new NotSupportedException("Virtual gestures are unavailable.");
        var panel = create();
        try
        {
            if (buttons.Count != 0) panel.SetButtonTargets(buttons);
            if (gestures.Count != 0) panel.SetGestureTargets(gestures);
            return panel;
        }
        catch
        {
            panel.Dispose();
            throw;
        }
    }

    private void ValidateBounds(SceneBounds rectangle)
    {
        if (rectangle.X < 0 || rectangle.Y < 0 || rectangle.Width <= 0 || rectangle.Height <= 0 ||
            (long)rectangle.X + rectangle.Width > Width || (long)rectangle.Y + rectangle.Height > Height)
            throw new ArgumentOutOfRangeException(nameof(rectangle), "Rectangle must fit the canvas.");
    }

    private static void ValidateControlKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 64 || key.Any(char.IsControl)) throw new ArgumentException("Control keys must be at most 64 characters without control characters.", nameof(key));
    }
}
