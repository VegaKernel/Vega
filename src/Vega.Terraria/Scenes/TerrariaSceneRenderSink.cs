using System.Collections.Concurrent;
using System.Diagnostics;
using Terraria.Net.Sockets;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Scenes;

public sealed class TerrariaSceneRenderSink(
    IGameThreadDispatcher dispatcher,
    IPlayerDirectory players,
    IOperationsLogger logger,
    WorldId? worldId = null) : ISceneRenderSink, IDisposable
{
    private readonly WorldId currentWorld = worldId ?? WorldId.Primary;
    private const int MaximumQueuedRegions = 256;
    // Worst-case 15 bytes per tile + 10-byte packet header must fit Int16 length.
    internal const int MaximumTileSquareSide = 46;
    private readonly ConcurrentQueue<SceneRenderRequest> pending = new();
    private readonly object admission = new();
    private ISceneRuntime? scenes;
    private IPlayerSessionLifecycle? sessions;
    private int queued;
    private bool drainPosted;
    internal const int MaximumRecipientsPerBatch = 4;
    internal const int MaximumBatchesPerStep = 4;
    private SceneRenderRequest? activeRequest;
    private IEnumerator<(SceneBounds Bounds, int Offset, int Count)>? activeBatches;
    private (PlayerId Id, ISocket Socket)[] activeRecipients = [];

    public void Attach(ISceneRuntime runtime, IPlayerSessionLifecycle lifecycle)
    {
        if (scenes is not null)
        {
            throw new InvalidOperationException("Scene renderer is already attached.");
        }

        scenes = runtime ?? throw new ArgumentNullException(nameof(runtime));
        sessions = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        sessions.Opened += OnSessionOpened;
    }

    public void Invalidate(SceneRenderRequest request) => TryInvalidate(request);

    public bool TryInvalidate(SceneRenderRequest request)
    {
        lock (admission)
        {
            if (scenes is null) return false;
            if (request.WorldId != currentWorld)
            {
                scenes.ReportFailure(request.SceneId, "Scene belongs to another world.");
                return false;
            }
            if (Volatile.Read(ref queued) >= MaximumQueuedRegions)
            {
                scenes.ReportFailure(request.SceneId, "Dirty-region queue capacity was exceeded.");
                logger.Log(OperationsLogLevel.Warning, "scenes", "Scene dirty-region queue is full; redraw rejected.");
                return false;
            }

            Interlocked.Increment(ref queued);
            pending.Enqueue(request);
            if (drainPosted) return true;
            drainPosted = true;
            if (dispatcher.TrySchedule("scenes.render", DrainStep)) return true;

            // No drain owns the queue and no other producer can enter until this
            // lock exits. Roll back the sole reservation instead of stranding it.
            drainPosted = false;
            if (pending.TryDequeue(out _)) Interlocked.Decrement(ref queued);
            scenes.ReportFailure(request.SceneId, "Game-thread queue capacity was exceeded.");
            logger.Log(OperationsLogLevel.Warning, "scenes", "Scene redraw could not enter the game-thread queue.");
            return false;
        }
    }

    public void Dispose()
    {
        if (sessions is not null)
        {
            sessions.Opened -= OnSessionOpened;
        }

        lock (admission)
        {
            scenes = null;
            while (pending.TryDequeue(out _)) Interlocked.Decrement(ref queued);
        }

        sessions = null;
    }

    private void OnSessionOpened(PlayerSessionOpened opened)
    {
        if (scenes is null)
        {
            return;
        }

        if (scenes.HasPersonalAirMask(currentWorld, opened.PlayerId))
        {
            Invalidate(new SceneRenderRequest(default, currentWorld, default, false,
                SceneObserverScope.Personal(opened.PlayerId).Players) { RefreshLoadedSections = true });
            return;
        }

        foreach (SceneSnapshot scene in scenes.SnapshotScenes().Where(scene => scene.WorldId == currentWorld))
        {
            foreach (SceneOverlayDefinition overlay in scene.Overlays.Where(overlay =>
                         overlay.Enabled && overlay.Observers.Contains(opened.PlayerId)))
            {
                Invalidate(new SceneRenderRequest(
                    scene.Id,
                    scene.WorldId,
                    overlay.Bounds,
                    false,
                    new HashSet<PlayerId> { opened.PlayerId }));
            }
        }
    }

    private bool DrainStep()
    {
        dispatcher.AssertGameThread();
        long started = Stopwatch.GetTimestamp();
        // A batch is the smallest indivisible unit. The time budget is cooperative,
        // not a hard real-time guarantee for tile composition or socket callbacks.
        for (int step = 0; step < MaximumBatchesPerStep &&
             (step == 0 || Stopwatch.GetElapsedTime(started).TotalMilliseconds < 1); step++)
        {
            if (scenes is null)
            {
                FinishRequest();
                break;
            }
            if (activeRequest is null && !pending.TryDequeue(out activeRequest)) break;
            try
            {
                if (activeBatches is null) BeginRequest(activeRequest);
                if (activeBatches!.MoveNext())
                {
                    var (bounds, offset, count) = activeBatches.Current;
                    RenderBatch(bounds, offset, count);
                }
                else
                {
                    scenes?.ReportFailure(activeRequest.SceneId, null);
                    FinishRequest();
                }
            }
            catch (Exception ex)
            {
                scenes?.ReportFailure(activeRequest!.SceneId, ex.Message);
                logger.Log(OperationsLogLevel.Error, "scenes", "Scene redraw failed.", ex);
                FinishRequest();
            }
        }

        lock (admission)
        {
            if (scenes is not null && (activeRequest is not null || !pending.IsEmpty)) return false;
            drainPosted = false;
            return true;
        }
    }

    private void FinishRequest()
    {
        activeBatches?.Dispose();
        activeBatches = null;
        activeRecipients = [];
        if (activeRequest is null) return;
        activeRequest = null;
        Interlocked.Decrement(ref queued);
    }

    private void BeginRequest(SceneRenderRequest request)
    {
        int worldWidth = Math.Max(0, global::Terraria.Main.maxTilesX);
        int worldHeight = Math.Max(0, global::Terraria.Main.maxTilesY);
        int startX = Math.Clamp(request.DirtyRegion.X, 0, worldWidth);
        int startY = Math.Clamp(request.DirtyRegion.Y, 0, worldHeight);
        int endX = Math.Clamp(request.DirtyRegion.Right, startX, worldWidth);
        int endY = Math.Clamp(request.DirtyRegion.Bottom, startY, worldHeight);
        var recipients = request.IsGlobal
            ? players.SnapshotOnlinePlayers().Where(player => player.WorldId == currentWorld).Select(static player => player.Id)
            : request.Observers;
        var connected = new List<(PlayerId Id, ISocket Socket)>();
        foreach (PlayerId recipient in recipients)
        {
            if ((uint)recipient.Slot < (uint)global::Terraria.Netplay.Clients.Length &&
                global::Terraria.Netplay.Clients[recipient.Slot] is { IsActive: true, Socket: { } socket })
                connected.Add((recipient, socket));
        }
        activeRecipients = connected.ToArray();
        activeBatches = request.RefreshLoadedSections
            ? EnumerateLoadedBatches(worldWidth, worldHeight).GetEnumerator()
            : EnumerateBatches(new SceneBounds(startX, startY, endX - startX, endY - startY),
                activeRecipients.Length).GetEnumerator();
    }

    private IEnumerable<(SceneBounds Bounds, int Offset, int Count)> EnumerateLoadedBatches(int worldWidth, int worldHeight)
    {
        for (int i = 0; i < activeRecipients.Length; i++)
        {
            var (recipient, socket) = activeRecipients[i];
            if (global::Terraria.Netplay.Clients[recipient.Slot] is not { IsActive: true } client ||
                !ReferenceEquals(client.Socket, socket)) continue;
            foreach (SceneBounds section in EnumerateLoadedSections(client.TileSections,
                         global::Terraria.Main.sectionWidth, global::Terraria.Main.sectionHeight, worldWidth, worldHeight))
            {
                if (!client.IsActive || !ReferenceEquals(client.Socket, socket)) break;
                foreach (var batch in EnumerateBatches(section, 1))
                    yield return (batch.Bounds, i, 1);
            }
        }
    }

    internal static IEnumerable<SceneBounds> EnumerateLoadedSections(bool[,]? sections, int sectionWidth, int sectionHeight,
        int worldWidth, int worldHeight)
    {
        if (sections is null || sectionWidth <= 0 || sectionHeight <= 0) yield break;
        for (int x = 0; x < sections.GetLength(0) && x * sectionWidth < worldWidth; x++)
        for (int y = 0; y < sections.GetLength(1) && y * sectionHeight < worldHeight; y++)
        {
            if (!sections[x, y]) continue;
            yield return new SceneBounds(x * sectionWidth, y * sectionHeight,
                Math.Min(sectionWidth, worldWidth - x * sectionWidth),
                Math.Min(sectionHeight, worldHeight - y * sectionHeight));
        }
    }

    internal static IEnumerable<(SceneBounds Bounds, int Offset, int Count)> EnumerateBatches(
        SceneBounds region, int recipients)
    {
        if (recipients <= 0) yield break;
        for (int x = region.X; x < region.Right; x += MaximumTileSquareSide)
        for (int y = region.Y; y < region.Bottom; y += MaximumTileSquareSide)
        {
            var bounds = new SceneBounds(x, y,
                Math.Min(MaximumTileSquareSide, region.Right - x),
                Math.Min(MaximumTileSquareSide, region.Bottom - y));
            for (int offset = 0; offset < recipients; offset += MaximumRecipientsPerBatch)
                yield return (bounds, offset, Math.Min(MaximumRecipientsPerBatch, recipients - offset));
        }
    }

    private void RenderBatch(SceneBounds bounds, int offset, int count)
    {
        using var cache = new SceneTileSquarePacketCache(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        Func<PlayerId, int, int, SceneTile> resolve = ResolveTile;
        for (int i = offset; i < offset + count; i++)
        {
            var (recipient, socket) = activeRecipients[i];
            if (global::Terraria.Netplay.Clients[recipient.Slot] is not { IsActive: true } client ||
                !ReferenceEquals(client.Socket, socket)) continue;
            byte[] packet = cache.Resolve(recipient, resolve, global::Terraria.Main.tileFrameImportant);
            // The connection may be replaced during composition or between cooperative steps.
            if (!client.IsActive || !ReferenceEquals(client.Socket, socket)) continue;
            byte[] data = SceneTileSquarePacketCache.PrepareSendBuffer(socket, packet);
            socket.AsyncSend(data, 0, data.Length, client.ServerWriteCallBack);
        }
    }

    private SceneTile ResolveTile(PlayerId observer, int x, int y) =>
        scenes!.TryResolveTile(currentWorld, observer, x, y, out SceneTile sceneTile, out _)
            ? sceneTile : FromNativeTile(global::Terraria.Main.tile[x, y]);

    private static SceneTile FromNativeTile(global::Terraria.ITile tile) =>
        new(
            tile.type,
            tile.wall,
            tile.frameX,
            tile.frameY,
            tile.liquid,
            tile.liquidType(),
            tile.color(),
            tile.wallColor(),
            tile.slope(),
            (tile.active() ? SceneTileFlags.Active : 0) |
            (tile.wire() ? SceneTileFlags.WireRed : 0) |
            (tile.wire2() ? SceneTileFlags.WireBlue : 0) |
            (tile.wire3() ? SceneTileFlags.WireGreen : 0) |
            (tile.wire4() ? SceneTileFlags.WireYellow : 0) |
            (tile.halfBrick() ? SceneTileFlags.HalfBlock : 0) |
            (tile.actuator() ? SceneTileFlags.Actuator : 0) |
            (tile.inActive() ? SceneTileFlags.Inactive : 0) |
            (tile.fullbrightBlock() ? SceneTileFlags.FullbrightBlock : 0) |
            (tile.fullbrightWall() ? SceneTileFlags.FullbrightWall : 0) |
            (tile.invisibleBlock() ? SceneTileFlags.InvisibleBlock : 0) |
            (tile.invisibleWall() ? SceneTileFlags.InvisibleWall : 0));
}
