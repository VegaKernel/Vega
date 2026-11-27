# Virtual TUI gestures

Vega FakeProvider exposes provider-local rectangles for stock WireKite inputs.
The host observes accepted native projectile updates from the exact session
while that player is holding the native tool. No plugin receives packet access.

`TileUiLayout` is a small SDK-side canvas for rectangular widget composition.
It handles tile buffer creation, `Paint`, `Button`, `Gesture` and provider
creation, so a basic panel does not need manual tile indexing or separate
registration:

```csharp
var layout = new TileUiLayout(12, 6, background)
    .Paint(new(0, 0, 12, 1), headerTile)
    .Button("confirm", 10, 4, stockSwitchTile)
    .Gesture("drag-handle", new(0, 0, 9, 1));
using var panel = layout.CreatePersonal(plugin.World.FakeProvider, "dialog", x, y, observers);
// A fixed-size redraw updates tiles but does not re-register controls.
layout.Paint(new(2, 2, 6, 1), selectedTile).Redraw(panel);
```

Controls are bound when creating the provider; change the control geometry
explicitly through `SetGestureTargets`/`SetButtonTargets`, or publish a new
panel layout. With these basic creation methods the canvas helper does not
dispatch callbacks or hold native state.

## Reusable controls

The same layout can bind managed callbacks through `Button`, `Drag`, nested
`Container` builders and `CreatePersonalPanel`/`CreateGlobalPanel`. A container
uses local coordinates and enforces its own rectangle; all controls still live
in one provider, and their keys are provider-wide. For example:

```csharp
var layout = new TileUiLayout(16, 8, background);
TileUiPanel? panel = null;
layout.Container(new(1, 1, 14, 6), ui => ui
    .Label(new(0, 0, 10, 2), "READY", color: 0, background: background)
    .Button("confirm", 12, 1, stockSwitchTile, click =>
    {
        plugin.Log.Info($"Confirm from session {click.Observer.Session}");
    })
    .Drag("slider", new(0, 3, 12, 2), drag =>
    {
        if (drag.Phase == SceneGesturePhase.Move)
        {
            // Repaint a preview against the fixed drag area, not a moved target.
            int x = Math.Clamp(drag.DeltaX + 5, 0, 11);
            layout.Paint(new(1, 5, 12, 1), background)
                .Paint(new(1 + x, 5, 1, 1), selectedTile);
            panel!.Redraw();
        }
        if (drag.Phase == SceneGesturePhase.End)
            plugin.Log.Info($"Commit delta {drag.DeltaX} for {drag.Observer.Session}");
        // Cancel should discard that gesture's transient preview.
    }));
panel = layout.CreatePersonalPanel(plugin, "dialog", x, y, observers);
plugin.Tasks.Every("dialog-input", TimeSpan.FromMilliseconds(50), () => panel.Dispatch());
```

`Label` paints 2×2 native alphabet statue frames (`A–Z`, `0–9`, spaces and line
breaks, case-insensitive) with left/center/right alignment, paint color and a
caller-chosen background. It rejects unsupported characters and non-fitting text
before changing tiles. It is not a Unicode font renderer. Redraw replaces the
label rectangle so shorter texts remove previous characters.

The panel is automatically owned by the plugin scope. Its `Dispatch` method
consumes a maximum of 32 inputs by default (configurable 1–256), alternates the
button and drag queues, and passes validated exact-session inputs to callbacks.
There is no additional timer or native hook: dispatch must run on the existing
guarded game-thread task. Explicit disposal clears callbacks and retires the
provider; the scheduled dispatch becomes a no-op. Callback errors propagate to
the caller's existing plugin error boundary. Do not retain inputs as authority
to act on a player slot later; use the session identity and revalidate any game
operation. A managed panel consumes all registered inputs, so bind all controls
through its callback overloads. For manual polling use the original
`CreatePersonal`/`CreateGlobal` methods instead.

`Redraw()` checks that input geometry still matches the initial layout; it can
change pixels and label text without resetting active gestures. Create a new
panel to change control geometry. Do not mix direct `SetButtonTargets` or
`SetGestureTargets` calls with a managed panel. Keep mutable per-player preview
state keyed by the exact `SceneSessionObserver` (or use one personal panel per
session); a provider may be observed by multiple sessions. This is a reusable
rectangular widget layer, not an implementation of every legacy TUI widget
(scrolling, item tags, animations, localization and full font support remain
application-level work).

```csharp
if (plugin.World.FakeProvider.SupportsVirtualGestures)
    panel.SetGestureTargets([new SceneGestureTarget("drag-handle", new(0, 0, 4, 3))]);

// Poll from a plugin-owned game-thread task, like the existing virtual buttons.
while (panel.TryTakeGesture(out var gesture))
{
    if (gesture!.Phase == SceneGesturePhase.Move)
    {
        int dx = gesture.Position.X - gesture.Origin.X;
        int dy = gesture.Position.Y - gesture.Origin.Y;
        // Update the widget preview within a fixed provider canvas.
    }
    // Commit only End; Cancel is terminal and must clear any preview.
}
```

Each provider registers up to 256 nonoverlapping, uniquely keyed rectangles.
The input must remain within the same registered target, in reach and visible
to the exact session. Begin, latest Move and terminal End/Cancel are bounded per
session; intermediate movement is coalesced, not a guaranteed input history.
Idle input expires after five seconds; a capture lasts at most twenty seconds.
Release is consumed only by the host and does not execute canonical wiring.
Ordinary native wiring outside visible overlays remains unchanged. Multi-cell
wiring intersecting an overlay is conservatively rejected, even without a
gesture target, to avoid changing canonical tiles behind virtual UI.

Moving, hiding, replacing the input geometry, removing a provider or ending a
session invalidates its old input. A redraw with unchanged provider geometry can
update the widget preview while a gesture is active. Disposed/covered targets
do not deliver stale terminal actions. Plugins should clear preview state when
the provider is replaced or disabled.

This is a stock-client input layer, not a complete port of the old TUI widget
library or its control-permission/wire-color semantics. The native tool remains
the normal vanilla tool; no modified client is required. Real client timing is
not verified by the unit tests (they exercise the typed host capture and release
boundaries). The shared SDK virtual buttons, managed chests and signs remain
available for widgets that do not use drag interactions.
