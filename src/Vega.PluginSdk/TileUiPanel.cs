using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.PluginSdk;

/// <summary>Lease-scoped drag input with provider-local coordinates and the exact observing session.</summary>
public sealed record TileUiDragEvent(SceneGestureEvent Input, WorldTilePosition LocalOrigin, WorldTilePosition LocalPosition)
{
    public SceneSessionObserver Observer => Input.Observer;
    public SceneGesturePhase Phase => Input.Phase;
    public Guid GestureId => Input.GestureId;
    public int DeltaX => LocalPosition.X - LocalOrigin.X;
    public int DeltaY => LocalPosition.Y - LocalOrigin.Y;
}

/// <summary>
/// A bound widget layout. Pump Dispatch from an existing guarded game-thread SDK task; there is
/// no additional scheduler, native hook or provider authority. Creation transfers ownership to the plugin scope.
/// </summary>
public sealed class TileUiPanel : IDisposable
{
    private TileUiLayout? layout;
    private readonly SceneButtonTarget[] buttons;
    private readonly SceneGestureTarget[] gestures;
    private readonly Dictionary<string, Action<SceneButtonPress>> buttonHandlers;
    private readonly Dictionary<string, Action<TileUiDragEvent>> dragHandlers;
    private bool disposed;
    private bool dispatching;
    private bool preferDrag;

    internal TileUiPanel(TileUiLayout layout, IPluginTileProvider provider, SceneButtonTarget[] buttons, SceneGestureTarget[] gestures,
        Dictionary<string, Action<SceneButtonPress>> buttonHandlers, Dictionary<string, Action<TileUiDragEvent>> dragHandlers)
    {
        this.layout = layout;
        Provider = provider;
        this.buttons = buttons;
        this.gestures = gestures;
        this.buttonHandlers = buttonHandlers;
        this.dragHandlers = dragHandlers;
    }

    public IPluginTileProvider Provider { get; }

    /// <summary>Redraw the original mutable canvas without resetting control registrations or active gestures.</summary>
    public void Redraw()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!layout!.HasControlGeometry(buttons, gestures))
            throw new InvalidOperationException("Control geometry changed; create a new panel for a different control layout.");
        layout.Redraw(Provider);
    }

    /// <summary>
    /// Consume at most maxEvents current host-validated inputs. Button and drag lanes are alternated.
    /// Exceptions from widget callbacks propagate to the caller's existing plugin execution guard.
    /// </summary>
    public int Dispatch(int maxEvents = 32)
    {
        if (maxEvents is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maxEvents));
        if (disposed || dispatching) return 0;
        dispatching = true;
        int consumed = 0;
        try
        {
            while (!disposed && consumed < maxEvents)
            {
                if (preferDrag ? !(DispatchDrag() || DispatchButton()) : !(DispatchButton() || DispatchDrag())) break;
                consumed++;
            }
            return consumed;
        }
        finally { dispatching = false; }
    }

    private bool DispatchButton()
    {
        if (!Provider.TryTakeButtonPress(out var press) || press is null) return false;
        preferDrag = true;
        if (buttonHandlers.TryGetValue(press.Target.Key, out var onClick)) onClick(press);
        return true;
    }

    private bool DispatchDrag()
    {
        if (!Provider.TryTakeGesture(out var gesture) || gesture is null) return false;
        preferDrag = false;
        if (dragHandlers.TryGetValue(gesture.Target.Key, out var onDrag))
        {
            var bounds = Provider.Definition.Bounds;
            onDrag(new(gesture, new(gesture.Origin.X - bounds.X, gesture.Origin.Y - bounds.Y),
                new(gesture.Position.X - bounds.X, gesture.Position.Y - bounds.Y)));
        }
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        layout = null;
        buttonHandlers.Clear();
        dragHandlers.Clear();
        Provider.Dispose();
    }
}
