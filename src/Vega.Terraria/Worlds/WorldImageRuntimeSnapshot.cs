using System.Security.Cryptography;
using System.Text;

namespace Vega.Terraria.Worlds;

/// <summary>
/// Immutable, versioned data captured on Terraria's game thread immediately after a save.
/// File I/O and publication of the resulting image can then continue on a worker thread.
/// </summary>
internal sealed record WorldImageRuntimeSnapshot(
    byte[] SourceSha256,
    int MaxTilesX,
    int MaxTilesY,
    byte[] TileRuns,
    byte[] LiquidState)
{
    public static WorldImageRuntimeSnapshot Capture(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        int maxTilesX = global::Terraria.Main.maxTilesX;
        int maxTilesY = global::Terraria.Main.maxTilesY;
        if (maxTilesX <= 0 || maxTilesY <= 0)
        {
            throw new InvalidOperationException("Terraria world dimensions are unavailable for a runtime snapshot.");
        }

        byte[] tileRuns = CaptureTileRuns(maxTilesX, maxTilesY);
        byte[] liquidState = CaptureLiquidState(maxTilesX, maxTilesY);
        byte[] sourceSha256 = VegaWorldImageReader.HashFile(sourcePath);
        return new WorldImageRuntimeSnapshot(
            sourceSha256,
            maxTilesX,
            maxTilesY,
            tileRuns,
            liquidState);
    }

    private static byte[] CaptureTileRuns(int maxTilesX, int maxTilesY)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        int chunkCount = (maxTilesX + VegaWorldImageFormat.TileChunkColumns - 1) /
            VegaWorldImageFormat.TileChunkColumns;
        writer.Write(chunkCount);

        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            int startX = chunk * VegaWorldImageFormat.TileChunkColumns;
            int columnCount = Math.Min(VegaWorldImageFormat.TileChunkColumns, maxTilesX - startX);
            writer.Write(startX);
            writer.Write(columnCount);
            long recordCountPosition = stream.Position;
            writer.Write(0);
            int recordCount = 0;

            for (int x = startX; x < startX + columnCount; x++)
            {
                int y = 0;
                while (y < maxTilesY)
                {
                    PackedTile current = PackedTile.From(global::Terraria.Main.tile[x, y]);
                    int runLength = 0;
                    while (y + runLength + 1 < maxTilesY &&
                           runLength < ushort.MaxValue &&
                           current == PackedTile.From(global::Terraria.Main.tile[x, y + runLength + 1]))
                    {
                        runLength++;
                    }

                    current.Write(writer, (ushort)runLength);
                    recordCount++;
                    y += runLength + 1;
                }
            }

            long end = stream.Position;
            stream.Position = recordCountPosition;
            writer.Write(recordCount);
            stream.Position = end;
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] CaptureLiquidState(int maxTilesX, int maxTilesY)
    {
        int liquidCount = global::Terraria.Liquid.numLiquid;
        int bufferCount = global::Terraria.LiquidBuffer.numLiquidBuffer;
        if (liquidCount < 0 || liquidCount > global::Terraria.Main.liquid.Length ||
            bufferCount < 0 || bufferCount > global::Terraria.Main.liquidBuffer.Length)
        {
            throw new InvalidOperationException("Terraria liquid queues are inconsistent.");
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(VegaWorldImageFormat.LiquidStateMagic);
        writer.Write(VegaWorldImageFormat.LiquidStateVersion);
        writer.Write(global::Terraria.Liquid.maxLiquid);
        writer.Write(global::Terraria.Liquid.skipCount);
        writer.Write(global::Terraria.Liquid.stuckCount);
        writer.Write(global::Terraria.Liquid.stuckAmount);
        writer.Write(global::Terraria.Liquid.cycles);
        writer.Write(global::Terraria.Liquid.curMaxLiquid);
        writer.Write(global::Terraria.Liquid.stuck);
        writer.Write(global::Terraria.Liquid.quickFall);
        writer.Write(global::Terraria.Liquid.wetCounter);
        writer.Write(global::Terraria.Liquid.panicCounter);
        writer.Write(global::Terraria.Liquid.panicMode);
        writer.Write(global::Terraria.Liquid.panicY);
        writer.Write(liquidCount);
        writer.Write(bufferCount);

        for (int index = 0; index < liquidCount; index++)
        {
            global::Terraria.Liquid liquid = global::Terraria.Main.liquid[index];
            ValidateCoordinates(liquid.x, liquid.y, maxTilesX, maxTilesY);
            writer.Write(liquid.x);
            writer.Write(liquid.y);
            writer.Write(liquid.kill);
            writer.Write(liquid.delay);
        }

        for (int index = 0; index < bufferCount; index++)
        {
            global::Terraria.LiquidBuffer liquid = global::Terraria.Main.liquidBuffer[index];
            ValidateCoordinates(liquid.x, liquid.y, maxTilesX, maxTilesY);
            writer.Write(liquid.x);
            writer.Write(liquid.y);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static void ValidateCoordinates(int x, int y, int maxTilesX, int maxTilesY)
    {
        if ((uint)x >= (uint)maxTilesX || (uint)y >= (uint)maxTilesY)
        {
            throw new InvalidOperationException("Terraria liquid queue contains out-of-world coordinates.");
        }
    }

    private readonly record struct PackedTile(
        ushort Type,
        ushort Wall,
        byte Liquid,
        ushort STileHeader,
        byte BTileHeader,
        byte BTileHeader2,
        byte BTileHeader3,
        short FrameX,
        short FrameY)
    {
        public static PackedTile From(global::Terraria.ITile tile) =>
            new(
                tile.type,
                tile.wall,
                tile.liquid,
                tile.sTileHeader,
                tile.bTileHeader,
                tile.bTileHeader2,
                tile.bTileHeader3,
                tile.frameX,
                tile.frameY);

        public void Write(BinaryWriter writer, ushort runLength)
        {
            writer.Write(Type);
            writer.Write(Wall);
            writer.Write(Liquid);
            writer.Write(STileHeader);
            writer.Write(BTileHeader);
            writer.Write(BTileHeader2);
            writer.Write(BTileHeader3);
            writer.Write(FrameX);
            writer.Write(FrameY);
            writer.Write(runLength);
        }
    }
}
