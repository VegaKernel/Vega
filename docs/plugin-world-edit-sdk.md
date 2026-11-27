# Plugin world-edit SDK

The SDK supports bounded real tile edits for WorldEdit-style plugins without replacing `Main.tile` or retaining native objects. Prepare selection transforms from detached snapshots on a background worker; apply once on the world thread with an exact optimistic compare-and-replace.

```csharp
using Vega.Contracts.Scenes;

var tiles = context.World.Tiles;
var before = await tiles.CaptureAsync(new SceneBounds(100, 120, 20, 20), token);
if (before is null) return;

// Compute the desired image outside the world thread if it is expensive.
var replacement = SceneTileArea.Fill(before.Bounds.Width, before.Bounds.Height,
    new SceneTile(1, Flags: SceneTileFlags.Active));
var edit = await tiles.ReplaceAsync(before, replacement, token);
if (!edit.IsSuccess) return; // refresh the selection rather than forcing an edit

// Keep successful receipts in the plugin's own bounded per-user history.
var undo = await tiles.UndoAsync(edit, token);
if (undo.IsSuccess)
{
    // The undo is itself a regular committed edit; inverting it performs redo.
    var redo = await tiles.UndoAsync(undo, token);
}
```

`ReplaceAsync` compares the selection's **exact tile contents**, observed revision, current world and loaded-world instance in the same game-thread operation before writing. The revision and instance ID are opaque. A matching hash/revision is not sufficient to override the exact image. If another edit happened in the selection, the operation returns `RevisionConflict` with the current snapshot without changing any tile; if an old revision has expired or the world has reloaded, capture again. Undo/redo use the same operation, so they cannot force an overwrite of another observed edit.

Every successful result includes `Before` and `After`. Receipts are deliberately not retained in Vega automatically: plugins choose their own history length and persistence. They are valid only for the currently loaded world instance and can expire after selection-cache eviction (4096 remembered shapes). The bounded cache stores only fingerprints/revision metadata, not historical tile images. A new world reload or grid replacement invalidates old receipts; do not serialize native revision tokens for use across a process restart.

The operation supports at most `WorldTileOperationLimits.MaximumTiles` tiles in one rectangle. The world thread validates tile encoding before applying changes, writes changed tiles, then verifies the resulting image. A native application error attempts rollback before any tile broadcasts; if a native provider refuses to restore the image, the operation reports a consistency failure instead of falsely claiming success. Native synchronization uses bounded tile-square chunks. If synchronization throws after a committed edit, success remains authoritative and `ErrorCode` is `world.tiles.broadcast_failed` (the caller should arrange a fresh view rather than blindly reapplying the edit).

This is a **tile-only** transaction, not a general native-world transaction. It does not track chest/sign contents, tile entities, NPCs, world progression or the history of ordinary player edits. Do not use it as an object-placement API for structured objects: use the existing typed chest, FakeProvider and schematic facilities for those records. The History plugin must still implement durable user attribution, ordinary player edit observations and object-aware undo when those capabilities become available. A sequence of separate rectangles is not atomic as a whole.
