using Vega.Contracts.Scenes;

namespace Vega.PluginSdk;

public enum TileUiAlignment { Left, Center, Right }

public sealed partial class TileUiLayout
{
    private readonly Dictionary<string, Action<SceneButtonPress>> buttonHandlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action<TileUiDragEvent>> dragHandlers = new(StringComparer.Ordinal);

    /// <summary>Paint a label using native alphabet statue frames (Latin letters, digits, spaces and newlines).</summary>
    public TileUiLayout Label(SceneBounds rectangle, string text, byte color = 0, TileUiAlignment alignment = TileUiAlignment.Left,
        SceneTile background = default)
    {
        ValidateBounds(rectangle);
        ArgumentNullException.ThrowIfNull(text);
        if (color > 31) throw new ArgumentOutOfRangeException(nameof(color));
        if (!Enum.IsDefined(alignment)) throw new ArgumentOutOfRangeException(nameof(alignment));
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length > rectangle.Height / 2) throw new ArgumentException("Text does not fit the label height.", nameof(text));
        // Validate all lines before painting, including character support and horizontal fit.
        var widths = new int[lines.Length];
        for (int row = 0; row < lines.Length; row++)
        {
            long width = 0;
            foreach (char c in lines[row]) width += c == ' ' ? 1 : Glyph(c) >= 0 ? 2 : throw new ArgumentException("Labels support only Latin letters, digits, spaces and line breaks.", nameof(text));
            if (width > rectangle.Width) throw new ArgumentException("Text does not fit the label width.", nameof(text));
            widths[row] = (int)width;
        }
        Paint(rectangle, background);
        for (int row = 0; row < lines.Length; row++)
        {
            int x = rectangle.X + (alignment switch
            {
                TileUiAlignment.Center => (rectangle.Width - widths[row]) / 2,
                TileUiAlignment.Right => rectangle.Width - widths[row],
                _ => 0
            });
            int y = rectangle.Y + row * 2;
            foreach (char c in lines[row])
            {
                if (c == ' ') { x++; continue; }
                int glyph = Glyph(c);
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                        tiles[(y + dy) * Width + x + dx] = new(337, background.WallType,
                            (short)(glyph * 36 + dx * 18), (short)(dy * 18), TileColor: color, WallColor: background.WallColor,
                            Flags: SceneTileFlags.Active | (background.Flags & (SceneTileFlags.FullbrightWall | SceneTileFlags.InvisibleWall)));
                x += 2;
            }
        }
        return this;
    }

    // Terraria's AlphabetStatues tile (337): ten digits, then 26 Latin letters, 36 frame pixels each.
    private static int Glyph(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'Z' => 10 + c - 'A',
        >= 'a' and <= 'z' => 10 + c - 'a',
        _ => -1
    };

    public TileUiLayout Button(string key, int x, int y, SceneTile switchTile, Action<SceneButtonPress> onClick)
    {
        ArgumentNullException.ThrowIfNull(onClick);
        Button(key, x, y, switchTile);
        buttonHandlers.Add(key, onClick);
        return this;
    }

    /// <summary>Register all drag phases. Commit only End; Cancel terminates a preview.</summary>
    public TileUiLayout Drag(string key, SceneBounds rectangle, Action<TileUiDragEvent> onDrag)
    {
        ArgumentNullException.ThrowIfNull(onDrag);
        Gesture(key, rectangle);
        dragHandlers.Add(key, onDrag);
        return this;
    }

    /// <summary>Compose a reusable control group in bounded container-local coordinates.</summary>
    public TileUiLayout Container(SceneBounds rectangle, Action<TileUiContainer> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        ValidateBounds(rectangle);
        build(new(this, rectangle));
        return this;
    }

    public TileUiPanel CreatePersonalPanel(IVegaPluginContext plugin, string name, int x, int y,
        IEnumerable<SceneSessionObserver> observers, int layer = 0)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        return BindPanel(plugin, CreatePersonal(plugin.World.FakeProvider, name, x, y, observers, layer));
    }

    public TileUiPanel CreateGlobalPanel(IVegaPluginContext plugin, string name, int x, int y, int layer = 0)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        return BindPanel(plugin, CreateGlobal(plugin.World.FakeProvider, name, x, y, layer));
    }

    private TileUiPanel BindPanel(IVegaPluginContext plugin, IPluginTileProvider provider)
    {
        var panel = new TileUiPanel(this, provider, buttons.ToArray(), gestures.ToArray(), new(buttonHandlers), new(dragHandlers));
        try { return plugin.Resources.Own(panel); }
        catch { panel.Dispose(); throw; }
    }

    internal bool HasControlGeometry(IReadOnlyList<SceneButtonTarget> expectedButtons, IReadOnlyList<SceneGestureTarget> expectedGestures) =>
        buttons.SequenceEqual(expectedButtons) && gestures.SequenceEqual(expectedGestures);
}

/// <summary>A bounded view onto the same underlying canvas, not a separate provider or child scene.</summary>
public sealed class TileUiContainer
{
    private readonly TileUiLayout layout;
    private readonly SceneBounds bounds;
    internal TileUiContainer(TileUiLayout layout, SceneBounds bounds) { this.layout = layout; this.bounds = bounds; }

    public int Width => bounds.Width;
    public int Height => bounds.Height;

    public TileUiContainer Paint(SceneBounds rectangle, SceneTile tile) { layout.Paint(Translate(rectangle), tile); return this; }
    public TileUiContainer Label(SceneBounds rectangle, string text, byte color = 0, TileUiAlignment alignment = TileUiAlignment.Left,
        SceneTile background = default) { layout.Label(Translate(rectangle), text, color, alignment, background); return this; }

    public TileUiContainer Button(string key, int x, int y, SceneTile switchTile, Action<SceneButtonPress> onClick)
    {
        var position = Translate(new(x, y, 1, 1));
        layout.Button(key, position.X, position.Y, switchTile, onClick);
        return this;
    }

    public TileUiContainer Drag(string key, SceneBounds rectangle, Action<TileUiDragEvent> onDrag) { layout.Drag(key, Translate(rectangle), onDrag); return this; }

    public TileUiContainer Container(SceneBounds rectangle, Action<TileUiContainer> build)
    { layout.Container(Translate(rectangle), build); return this; }

    private SceneBounds Translate(SceneBounds local)
    {
        if (local.X < 0 || local.Y < 0 || local.Width <= 0 || local.Height <= 0 ||
            (long)local.X + local.Width > Width || (long)local.Y + local.Height > Height)
            throw new ArgumentOutOfRangeException(nameof(local), "Control must fit its container.");
        return local with { X = bounds.X + local.X, Y = bounds.Y + local.Y };
    }
}
