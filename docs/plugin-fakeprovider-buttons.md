# FakeProvider virtual buttons

The tile-provider SDK can now receive ordinary stock-client switch clicks as typed, provider-owned input intents. This is a small building block for custom menus, panels, interactive previews and TUI applications. It is not a raw network callback or native wiring operation.

```csharp
using Vega.Contracts.Scenes;

var fake = context.World.FakeProvider;
if (!fake.SupportsVirtualButtons) return;

// Render an active stock switch tile in the provider image. Tile definitions and
// native frame data are supplied by the plugin's visual design.
var ui = fake.CreatePersonal("main-menu", bounds, switchTileImage, observers);
ui.SetButtonTargets([new SceneButtonTarget("open", 1, 1), new SceneButtonTarget("back", 2, 1)]);

context.Tasks.Every("menu-input", TimeSpan.FromMilliseconds(50), () =>
{
    while (ui.TryTakeButtonPress(out var press) && press is not null)
    {
        // press.Target.Key / LocalX / LocalY identifies this provider's button.
        // press.Observer contains its exact PlayerId / PlayerSessionId.
        // Apply plugin business rules before asynchronous work or a privileged action.
    }
});
```

Targets must be unique active tiles inside the provider (max 256). Empty targets disable input. Registration is attached to the provider lifetime, and local target coordinates move with the provider. There is no automatic switch frame animation or wiring: render the desired visual state using the existing provider update methods. The provided graphic must actually be an interactable stock-client switch for normal clicks to arrive.

Input requirements and lifecycle:

- The connection must match the current scene session, world, and player's exact observer membership. The player must be alive, within the existing virtual-object interaction reach (8 tiles), and fully connected when native metadata is available.
- The target must be the top visible provider at the observed position. An overlapping provider or a personal air background cannot expose a covered target. The plugin can only consume presses for its own provider lease.
- Real-world switch interactions outside an overlay continue through normal native handling. Switches under visible virtual tiles are never forwarded to canonical wiring; registered targets can produce a menu intent, unregistered targets do not.
- Input is bounded/coalesced to the latest intent per live session, with a five-second expiry. Moving, replacing, disabling or disposing the provider, changing targets or session membership, leaving interaction reach, or covering it with another provider discards stale presses when consumed. This is an intent queue, not a reliable delivery log.
- Poll from the game thread as shown; subscriptions and provider leases retire with the plugin. Verify exact session and your own permission/business-state rules again if an action is delayed. The press indicates attempted stock input, not trusted proof of purchase, reward eligibility or privileged command authorization.

The original TUI WireKite drag/slider pointer protocol (`Begin` / `Moving` / `End`) is not implemented by this API. That requires a separate typed capture/cancel interaction lifecycle. Existing virtual sign/chest/display APIs remain independent and can be composed with these menu buttons.
