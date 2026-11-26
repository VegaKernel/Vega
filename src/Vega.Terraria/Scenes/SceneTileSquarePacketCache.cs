using System.Buffers;
using Terraria.Net.Sockets;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Terraria.Networking;

namespace Vega.Terraria.Scenes;

/// <summary>
/// A one-chunk, one-composition cache. Each observer is independently resolved before sharing
/// the packet; neither global scope nor a stale scene snapshot is proof of identical visibility.
/// </summary>
internal sealed class SceneTileSquarePacketCache(int x, int y, int width, int height) : IDisposable
{
    private readonly int count = checked(width * height);
    private ResolvedTile[]? previous;
    private ResolvedTile[]? scratch;
    private byte[]? previousPacket;
    private bool disposed;

    public byte[] Resolve(PlayerId observer, Func<PlayerId, int, int, SceneTile> resolver, bool[] frameImportant)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        scratch ??= ArrayPool<ResolvedTile>.Shared.Rent(count);
        Span<ResolvedTile> current = scratch.AsSpan(0, count);
        int index = 0;
        for (int tileX = x; tileX < x + width; tileX++)
        for (int tileY = y; tileY < y + height; tileY++)
        {
            SceneTile tile = resolver(observer, tileX, tileY);
            bool includeFrame = tile.Flags.HasFlag(SceneTileFlags.Active) &&
                (uint)tile.TileType < (uint)frameImportant.Length && frameImportant[tile.TileType];
            current[index++] = new(tile, includeFrame);
        }

        if (previousPacket is not null && current.SequenceEqual(previous.AsSpan(0, count)))
            return previousPacket;

        // Allocate a new immutable exact-length packet; it may remain queued in native sockets
        // after this cache and both composition rentals are disposed.
        byte[] packet = Serialize(current);
        (previous, scratch) = (scratch, previous);
        previousPacket = packet;
        return packet;
    }

    public static byte[] PrepareSendBuffer(ISocket socket, byte[] packet) =>
        // Native TcpSocket writes without mutating the buffer; the ordered adapter takes its
        // own copy synchronously. Do not share arrays with arbitrary plugin-defined sockets.
        socket.GetType() == typeof(TcpSocket) || socket is NativeTcpConnection ? packet : packet.ToArray();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (previous is not null) ArrayPool<ResolvedTile>.Shared.Return(previous);
        if (scratch is not null) ArrayPool<ResolvedTile>.Shared.Return(scratch);
        previous = scratch = null;
        previousPacket = null;
    }

    private byte[] Serialize(ReadOnlySpan<ResolvedTile> tiles)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(checked(16 + count * 16));
        try
        {
            using var stream = new MemoryStream(buffer, writable: true);
            stream.Position = 2;
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)20);
            writer.Write((short)x);
            writer.Write((short)y);
            writer.Write((byte)width);
            writer.Write((byte)height);
            writer.Write((byte)0);
            foreach (ResolvedTile tile in tiles) WriteTile(writer, tile.Tile, tile.IncludeFrame);

            int length = checked((int)stream.Position);
            if (length > short.MaxValue) throw new InvalidDataException("Scene tile-square packet exceeds protocol length.");
            stream.Position = 0;
            writer.Write((short)length);
            return buffer.AsSpan(0, length).ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void WriteTile(BinaryWriter writer, SceneTile tile, bool includeFrame)
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
            if (includeFrame)
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

    private static void Set(ref byte value, int bit, bool enabled)
    {
        if (enabled) value |= (byte)(1 << bit);
    }

    private readonly record struct ResolvedTile(SceneTile Tile, bool IncludeFrame);
}
