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
    IOperationsLogger logger) : ISceneRenderSink, IDisposable
{
    private const int MaximumQueuedRegions = 256;
    private const int MaximumTileSquareSide = 64;
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
        if (request.WorldId != WorldId.Primary ||
            Interlocked.Increment(ref queued) > MaximumQueuedRegions)
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

        foreach (SceneSnapshot scene in scenes.SnapshotScenes().Where(scene => scene.WorldId == WorldId.Primary))
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
        IEnumerable<PlayerId> recipients = request.IsGlobal
            ? players.SnapshotOnlinePlayers().Select(static player => player.Id)
            : request.Observers;

        foreach (PlayerId recipient in recipients)
        {
            if ((uint)recipient.Slot >= (uint)global::Terraria.Netplay.Clients.Length ||
                global::Terraria.Netplay.Clients[recipient.Slot] is not { IsActive: true } client)
            {
                continue;
            }

            for (int x = startX; x < endX; x += MaximumTileSquareSide)
            {
                for (int y = startY; y < endY; y += MaximumTileSquareSide)
                {
                    int width = Math.Min(MaximumTileSquareSide, endX - x);
                    int height = Math.Min(MaximumTileSquareSide, endY - y);
                    byte[] data = BuildPacket(recipient, x, y, width, height);
                    client.Socket.AsyncSend(data, 0, data.Length, client.ServerWriteCallBack);
                }
            }
        }
    }

    private byte[] BuildPacket(PlayerId observer, int x, int y, int width, int height)
    {
        using var stream = new MemoryStream(checked(16 + width * height * 16));
        using var writer = new BinaryWriter(stream);
        stream.Position = 2;
        writer.Write((byte)20);
        writer.Write((short)x);
        writer.Write((short)y);
        writer.Write((byte)width);
        writer.Write((byte)height);
        writer.Write((byte)0);
        for (int tileX = x; tileX < x + width; tileX++)
        {
            for (int tileY = y; tileY < y + height; tileY++)
            {
                if (scenes!.TryResolveTile(
                        WorldId.Primary,
                        observer,
                        tileX,
                        tileY,
                        out SceneTile sceneTile,
                        out _))
                {
                    WriteTile(writer, sceneTile);
                }
                else
                {
                    WriteTile(writer, global::Terraria.Main.tile[tileX, tileY]);
                }
            }
        }

        long length = stream.Position;
        if (length > short.MaxValue)
        {
            throw new InvalidDataException("Scene tile-square packet exceeds protocol length.");
        }

        stream.Position = 0;
        writer.Write((short)length);
        return stream.ToArray();
    }

    private static void WriteTile(BinaryWriter writer, SceneTile tile)
    {
        bool active = tile.Flags.HasFlag(SceneTileFlags.Active);
        byte header1 = 0;
        byte header2 = 0;
        byte header3 = 0;
        Set(ref header1, 0, active);
        Set(ref header1, 2, tile.WallType > 0);
        Set(ref header1, 3, tile.LiquidAmount > 0);
        Set(ref header1, 4, tile.Flags.HasFlag(SceneTileFlags.WireRed));
        Set(ref header1, 5, tile.Flags.HasFlag(SceneTileFlags.HalfBlock));
        Set(ref header1, 6, tile.Flags.HasFlag(SceneTileFlags.Actuator));
        Set(ref header1, 7, tile.Flags.HasFlag(SceneTileFlags.Inactive));
        Set(ref header2, 0, tile.Flags.HasFlag(SceneTileFlags.WireBlue));
        Set(ref header2, 1, tile.Flags.HasFlag(SceneTileFlags.WireGreen));
        Set(ref header2, 2, active);
        Set(ref header2, 3, tile.WallType > 0);
        header2 |= (byte)((tile.Slope & 7) << 4);
        Set(ref header2, 7, tile.Flags.HasFlag(SceneTileFlags.WireYellow));
        Set(ref header3, 0, tile.Flags.HasFlag(SceneTileFlags.FullbrightBlock));
        Set(ref header3, 1, tile.Flags.HasFlag(SceneTileFlags.FullbrightWall));
        Set(ref header3, 2, tile.Flags.HasFlag(SceneTileFlags.InvisibleBlock));
        Set(ref header3, 3, tile.Flags.HasFlag(SceneTileFlags.InvisibleWall));
        writer.Write(header1);
        writer.Write(header2);
        writer.Write(header3);
        if (active) writer.Write(tile.TileColor);
        if (tile.WallType > 0) writer.Write(tile.WallColor);
        if (active)
        {
            writer.Write(tile.TileType);
            if ((uint)tile.TileType < (uint)global::Terraria.Main.tileFrameImportant.Length &&
                global::Terraria.Main.tileFrameImportant[tile.TileType])
            {
                writer.Write(tile.FrameX);
                writer.Write(tile.FrameY);
            }
        }

        if (tile.WallType > 0) writer.Write(tile.WallType);
        if (tile.LiquidAmount > 0)
        {
            writer.Write(tile.LiquidAmount);
            writer.Write(tile.LiquidKind);
        }
    }

    private static void WriteTile(BinaryWriter writer, global::Terraria.ITile tile) =>
        WriteTile(
            writer,
            new SceneTile(
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
                (tile.invisibleWall() ? SceneTileFlags.InvisibleWall : 0)));

    private static void Set(ref byte value, int bit, bool enabled)
    {
        if (enabled)
        {
            value |= (byte)(1 << bit);
        }
    }
}
