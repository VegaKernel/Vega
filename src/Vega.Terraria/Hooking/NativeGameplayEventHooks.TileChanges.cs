using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;
using Vega.Terraria.Worlds;
using NativeMessageHooks = On.Terraria.MessageBuffer;

namespace Vega.Terraria.Hooking;

internal sealed partial class NativeGameplayEventHooks
{
    internal const int MaximumObservedTileCells = 4096;
    private IDisposable? tileAcceptedSubscription;
    private bool tileChangesAttached;
    private TileObservation tileObservation;
    internal bool TileChangesObservationAvailable => tileChangesAttached;

    private struct TileObservation(int slot)
    {
        public int Slot { get; } = slot;
        public bool Active { get; } = true;
        public PlayerTileInteractionEvent? Intent { get; set; }
        public SceneBounds Bounds { get; set; }
        public SceneTile[]? Before { get; set; }
        public object? Grid { get; set; }
        public Guid? WorldFile { get; set; }
    }

    private void AttachTileChangesHooks()
    {
        if (players is null) return;
        try
        {
            NativeMessageHooks.GetData += ObserveNativeTileInteraction;
            tileChangesAttached = true;
            tileAcceptedSubscription = events.Subscribe<PlayerTileInteractionAcceptedEvent>(CaptureAcceptedTileInteraction);
        }
        catch (Exception ex)
        {
            DetachTileChangesHooks();
            logger.Log(OperationsLogLevel.Warning, "world-events", "Native committed tile observation is unavailable.", ex);
        }
    }

    private void DetachTileChangesHooks()
    {
        tileAcceptedSubscription?.Dispose();
        tileAcceptedSubscription = null;
        if (tileChangesAttached)
        {
            tileChangesAttached = false;
            try { NativeMessageHooks.GetData -= ObserveNativeTileInteraction; }
            catch (Exception ex) { logger.Log(OperationsLogLevel.Warning, "world-events", "Unable to detach committed tile observation.", ex); }
        }
        tileObservation = default;
    }

    internal void ObserveNativeTileInteraction(NativeMessageHooks.orig_GetData orig, global::Terraria.MessageBuffer buffer,
        int start, int length, out int messageType)
    {
        // No copies, frame objects or tile scans on the ordinary no-subscriber path.
        if (!Volatile.Read(ref attached) || !Observed<PlayerTileChangesCommittedEvent>())
        {
            orig(buffer, start, length, out messageType);
            return;
        }
        TileObservation prior = tileObservation;
        TileObservation frame;
        tileObservation = new TileObservation(buffer.whoAmI);
        try
        {
            orig(buffer, start, length, out messageType);
            frame = tileObservation;
        }
        finally { tileObservation = prior; }
        // The accepted intent was captured by the existing last policy, inside this native call.
        // A cancelled or ineffective interaction leaves no changed cells and emits no commit event.
        try { PublishTileChanges(frame); }
        catch (Exception ex) { logger.Log(OperationsLogLevel.Warning, "world-events", "Committed tile observation failed.", ex); }
    }

    private void CaptureAcceptedTileInteraction(PlayerTileInteractionAcceptedEvent accepted)
    {
        var intent = accepted.Intent;
        ref var frame = ref tileObservation;
        if (!frame.Active || frame.Intent.HasValue || frame.Slot != intent.PlayerId.Slot || intent.WorldId != world ||
            !IsExactTileSession(intent) || global::Terraria.Main.tile is not { } grid || !TryGetObservedBounds(intent, out var bounds)) return;
        try
        {
            var cells = new SceneTile[checked(bounds.Width * bounds.Height)];
            int i = 0;
            for (int y = bounds.Y; y < bounds.Bottom; y++)
                for (int x = bounds.X; x < bounds.Right; x++)
                    cells[i++] = TerrariaWorldTileService.Snapshot(grid[x, y]);
            frame.Intent = intent;
            frame.Bounds = bounds;
            frame.Before = cells;
            frame.Grid = grid;
            frame.WorldFile = global::Terraria.Main.ActiveWorldFileData?.UniqueId;
        }
        catch (Exception ex) { logger.Log(OperationsLogLevel.Warning, "world-events", "Unable to capture bounded tile interaction.", ex); }
    }

    private bool IsExactTileSession(PlayerTileInteractionEvent intent) => players is not null &&
        players.TryGet(intent.PlayerId, out var player) && player is { SessionId: { } session } && session == intent.SessionId && player.WorldId == world &&
        players.TryGetSessionPlayer(player, out _);

    private static bool TryGetObservedBounds(PlayerTileInteractionEvent intent, out SceneBounds bounds)
    {
        bounds = default;
        if (global::Terraria.Main.maxTilesX <= 0 || global::Terraria.Main.maxTilesY <= 0) return false;
        int padding = intent.Kind is TileInteractionKind.Tile or TileInteractionKind.Paint or TileInteractionKind.Liquid ? 2 : 4;
        long left = Math.Min(intent.Start.X, intent.End.X), top = Math.Min(intent.Start.Y, intent.End.Y);
        long right = Math.Max(intent.Start.X, intent.End.X), bottom = Math.Max(intent.Start.Y, intent.End.Y);
        if (left < 0 || top < 0 || right >= global::Terraria.Main.maxTilesX || bottom >= global::Terraria.Main.maxTilesY) return false;
        left = Math.Max(0, left - padding); top = Math.Max(0, top - padding);
        right = Math.Min(global::Terraria.Main.maxTilesX - 1, right + padding);
        bottom = Math.Min(global::Terraria.Main.maxTilesY - 1, bottom + padding);
        long width = right - left + 1, height = bottom - top + 1;
        if (width * height > MaximumObservedTileCells) return false;
        bounds = new((int)left, (int)top, (int)width, (int)height);
        return true;
    }

    private void PublishTileChanges(TileObservation frame)
    {
        if (frame.Intent is not { } intent || frame.Before is not { } before || !Observed<PlayerTileChangesCommittedEvent>() ||
            !IsExactTileSession(intent) || global::Terraria.Main.tile is not { } grid || !ReferenceEquals(grid, frame.Grid) ||
            global::Terraria.Main.ActiveWorldFileData?.UniqueId != frame.WorldFile) return;
        var bounds = frame.Bounds;
        List<PlayerTileCellChange>? changes = null;
        int i = 0;
        for (int y = bounds.Y; y < bounds.Bottom; y++)
            for (int x = bounds.X; x < bounds.Right; x++)
            {
                var original = before[i++];
                var after = TerrariaWorldTileService.Snapshot(grid[x, y]);
                if (original != after) (changes ??= []).Add(new(new(x, y), original, after));
            }
        if (changes is not null) Publish(new PlayerTileChangesCommittedEvent(intent, bounds, changes.AsReadOnly()));
    }
}
