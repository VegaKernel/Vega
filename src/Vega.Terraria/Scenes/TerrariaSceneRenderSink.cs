using System.Collections.Concurrent;
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
    private ISceneRuntime? scenes;
    private IPlayerSessionLifecycle? sessions;
    private int queued;
    private int drainPosted;

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

    public void Invalidate(SceneRenderRequest request)
    {
        if (request.WorldId != currentWorld)
        {
            scenes?.ReportFailure(request.SceneId, "Scene belongs to another world.");
            return;
        }
        if (Interlocked.Increment(ref queued) > MaximumQueuedRegions)
        {
            Interlocked.Decrement(ref queued);
            scenes?.ReportFailure(request.SceneId, "Dirty-region queue capacity was exceeded.");
            logger.Log(
                OperationsLogLevel.Warning,
                "scenes",
                "Scene dirty-region queue is full; update was dropped.");
            return;
        }

        pending.Enqueue(request);
        PostDrain();
    }

    public void Dispose()
    {
        if (sessions is not null)
        {
            sessions.Opened -= OnSessionOpened;
        }

        while (pending.TryDequeue(out _))
        {
        }

        queued = 0;
        scenes = null;
        sessions = null;
    }

    private void OnSessionOpened(PlayerSessionOpened opened)
    {
        if (scenes is null)
        {
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

    private void PostDrain()
    {
        if (Interlocked.Exchange(ref drainPosted, 1) != 0)
        {
            return;
        }

        if (!dispatcher.TryPost("scenes.render", Drain))
        {
            Interlocked.Exchange(ref drainPosted, 0);
            logger.Log(
                OperationsLogLevel.Warning,
                "scenes",
                "Scene redraw could not enter the game-thread queue.");
        }
    }

    private void Drain()
    {
        dispatcher.AssertGameThread();
        try
        {
            while (pending.TryDequeue(out SceneRenderRequest? request))
            {
                Interlocked.Decrement(ref queued);
                try
                {
                    Render(request);
                    scenes?.ReportFailure(request.SceneId, null);
                }
                catch (Exception ex)
                {
                    scenes?.ReportFailure(request.SceneId, ex.Message);
                    logger.Log(OperationsLogLevel.Error, "scenes", "Scene redraw failed.", ex);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref drainPosted, 0);
            if (!pending.IsEmpty)
            {
                PostDrain();
            }
        }
    }

    private void Render(SceneRenderRequest request)
    {
        if (scenes is null || global::Terraria.Main.maxTilesX <= 0 || global::Terraria.Main.maxTilesY <= 0)
        {
            return;
        }

        int startX = Math.Clamp(request.DirtyRegion.X, 0, global::Terraria.Main.maxTilesX);
        int startY = Math.Clamp(request.DirtyRegion.Y, 0, global::Terraria.Main.maxTilesY);
        int endX = Math.Clamp(request.DirtyRegion.Right, startX, global::Terraria.Main.maxTilesX);
        int endY = Math.Clamp(request.DirtyRegion.Bottom, startY, global::Terraria.Main.maxTilesY);
        PlayerId[] recipients = (request.IsGlobal
            ? players.SnapshotOnlinePlayers().Where(player => player.WorldId == currentWorld).Select(static player => player.Id)
            : request.Observers).ToArray();
        if (recipients.Length == 0) return;
        Func<PlayerId, int, int, SceneTile> resolve = ResolveTile;
        // Each recipient still sees every chunk in the same order. Cache only the immediately
        // preceding composition for this chunk, never a frame or a whole-region observer guess.
        for (int x = startX; x < endX; x += MaximumTileSquareSide)
        {
            for (int y = startY; y < endY; y += MaximumTileSquareSide)
            {
                int width = Math.Min(MaximumTileSquareSide, endX - x);
                int height = Math.Min(MaximumTileSquareSide, endY - y);
                using var cache = new SceneTileSquarePacketCache(x, y, width, height);
                foreach (PlayerId recipient in recipients)
                {
                    if ((uint)recipient.Slot >= (uint)global::Terraria.Netplay.Clients.Length ||
                        global::Terraria.Netplay.Clients[recipient.Slot] is not { IsActive: true } client)
                        continue;

                    var socket = client.Socket;
                    byte[] packet = cache.Resolve(recipient, resolve, global::Terraria.Main.tileFrameImportant);
                    // Resolve first, then deliver only to that same native connection. A reentrant
                    // slot replacement must not receive the preceding occupant's composition.
                    if (!client.IsActive || !ReferenceEquals(client.Socket, socket)) continue;
                    byte[] data = SceneTileSquarePacketCache.PrepareSendBuffer(socket, packet);
                    socket.AsyncSend(data, 0, data.Length, client.ServerWriteCallBack);
                }
            }
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
