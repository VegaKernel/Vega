using System.Buffers;
using System.Buffers.Binary;

namespace Vega.Terraria.Worlds;

internal static class WorldImageTileLoader
{
    private const int MaximumDecoderWorkers = 4;
    private const int ChunkHeaderSize = sizeof(int) * 3;
    private const int TileRunSize =
        sizeof(ushort) +
        sizeof(ushort) +
        sizeof(byte) +
        sizeof(ushort) +
        sizeof(byte) +
        sizeof(byte) +
        sizeof(byte) +
        sizeof(short) +
        sizeof(short) +
        sizeof(ushort);

    public static void Load(string imagePath, VegaWorldImageManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(manifest);

        if (global::Terraria.Main.maxTilesX != manifest.MaxTilesX ||
            global::Terraria.Main.maxTilesY != manifest.MaxTilesY)
        {
            throw new InvalidDataException("Vega world-image dimensions do not match the loaded Terraria header.");
        }

        if (manifest.TileRuns.Length is <= 0 or > int.MaxValue)
        {
            throw new InvalidDataException("Vega world-image tile section is too large to decode.");
        }

        int sectionLength = checked((int)manifest.TileRuns.Length);
        byte[] section = ArrayPool<byte>.Shared.Rent(sectionLength);
        try
        {
            using (var stream = new FileStream(
                       imagePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       64 * 1024,
                       FileOptions.SequentialScan))
            {
                stream.Position = manifest.TileRuns.Offset;
                stream.ReadExactly(section.AsSpan(0, sectionLength));
            }

            ChunkDescriptor[] chunks = ParseChunks(
                section,
                sectionLength,
                manifest.MaxTilesX,
                manifest.MaxTilesY);
            DecodeChunks(section, chunks, manifest.MaxTilesY);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(section);
        }

        global::Terraria.WorldGen.AddUpAlignmentCounts(clearCounts: true);
        if (manifest.TerrariaVersion < 105)
        {
            global::Terraria.WorldGen.FixHearts();
        }
    }

    private static ChunkDescriptor[] ParseChunks(
        byte[] section,
        int sectionLength,
        int maxTilesX,
        int maxTilesY)
    {
        int offset = 0;
        int chunkCount = ReadInt32(section, sectionLength, ref offset);
        int expectedChunks = (maxTilesX + VegaWorldImageFormat.TileChunkColumns - 1) /
            VegaWorldImageFormat.TileChunkColumns;
        if (chunkCount != expectedChunks)
        {
            throw new InvalidDataException("Vega world-image tile chunk count is invalid.");
        }

        var chunks = new ChunkDescriptor[chunkCount];
        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            if (offset > sectionLength - ChunkHeaderSize)
            {
                throw new InvalidDataException("Vega world-image tile chunk header is truncated.");
            }

            int startX = ReadInt32(section, sectionLength, ref offset);
            int columnCount = ReadInt32(section, sectionLength, ref offset);
            int recordCount = ReadInt32(section, sectionLength, ref offset);
            int expectedStartX = chunk * VegaWorldImageFormat.TileChunkColumns;
            int expectedColumns = Math.Min(
                VegaWorldImageFormat.TileChunkColumns,
                maxTilesX - expectedStartX);
            int maximumRecords = checked(expectedColumns * maxTilesY);
            if (startX != expectedStartX ||
                columnCount != expectedColumns ||
                recordCount <= 0 ||
                recordCount > maximumRecords)
            {
                throw new InvalidDataException("Vega world-image tile chunk header is invalid.");
            }

            int recordBytes = checked(recordCount * TileRunSize);
            if (offset > sectionLength - recordBytes)
            {
                throw new InvalidDataException("Vega world-image tile chunk exceeds its manifest section.");
            }

            chunks[chunk] = new ChunkDescriptor(
                startX,
                columnCount,
                recordCount,
                offset,
                recordBytes);
            offset += recordBytes;
        }

        if (offset != sectionLength)
        {
            throw new InvalidDataException("Vega world-image tile section length does not match its manifest.");
        }

        return chunks;
    }

    private static void DecodeChunks(
        byte[] section,
        ChunkDescriptor[] chunks,
        int maxTilesY)
    {
        int[] globalCounts = global::Terraria.WorldGen.tileCounts;
        if (!SupportsParallelTileWrites() || chunks.Length < 2 || Environment.ProcessorCount < 2)
        {
            foreach (ChunkDescriptor chunk in chunks)
            {
                DecodeChunk(section, chunk, maxTilesY, globalCounts);
            }

            return;
        }

        var chunkCounts = new int[chunks.Length][];
        try
        {
            Parallel.For(
                0,
                chunks.Length,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(
                        MaximumDecoderWorkers,
                        Math.Min(Environment.ProcessorCount, chunks.Length))
                },
                chunkIndex =>
                {
                    int[] localCounts = ArrayPool<int>.Shared.Rent(globalCounts.Length);
                    Array.Clear(localCounts, 0, globalCounts.Length);
                    chunkCounts[chunkIndex] = localCounts;
                    DecodeChunk(section, chunks[chunkIndex], maxTilesY, localCounts);
                });

            foreach (int[] localCounts in chunkCounts)
            {
                for (int type = 0; type < globalCounts.Length; type++)
                {
                    globalCounts[type] += localCounts[type];
                }
            }
        }
        finally
        {
            foreach (int[]? localCounts in chunkCounts)
            {
                if (localCounts is not null)
                {
                    ArrayPool<int>.Shared.Return(localCounts);
                }
            }
        }
    }

    private static bool SupportsParallelTileWrites()
    {
        Type collectionType = global::Terraria.Main.tile.GetType();
        return collectionType.IsGenericType &&
            string.Equals(
                collectionType.GetGenericTypeDefinition().FullName,
                "TerrariaRuntime.DefaultCollection`1",
                StringComparison.Ordinal);
    }

    private static void DecodeChunk(
        byte[] section,
        ChunkDescriptor chunk,
        int maxTilesY,
        int[] tileCounts)
    {
        int x = chunk.StartX;
        int y = 0;
        int offset = chunk.RecordOffset;
        int recordEnd = checked(chunk.RecordOffset + chunk.RecordBytes);
        for (int record = 0; record < chunk.RecordCount; record++)
        {
            TileRun run = ReadTileRun(section, ref offset);
            if (x >= chunk.StartX + chunk.ColumnCount || run.RunLength >= maxTilesY - y)
            {
                throw new InvalidDataException("Vega world-image tile run crosses a chunk boundary.");
            }

            ApplyRun(x, y, run, tileCounts);
            y += run.RunLength + 1;
            if (y == maxTilesY)
            {
                x++;
                y = 0;
            }
        }

        if (offset != recordEnd || x != chunk.StartX + chunk.ColumnCount || y != 0)
        {
            throw new InvalidDataException("Vega world-image tile chunk is incomplete.");
        }
    }

    private static TileRun ReadTileRun(byte[] source, ref int offset)
    {
        ushort type = ReadUInt16(source, ref offset);
        ushort wall = ReadUInt16(source, ref offset);
        byte liquid = source[offset++];
        ushort sTileHeader = ReadUInt16(source, ref offset);
        byte bTileHeader = source[offset++];
        byte bTileHeader2 = source[offset++];
        byte bTileHeader3 = source[offset++];
        short frameX = unchecked((short)ReadUInt16(source, ref offset));
        short frameY = unchecked((short)ReadUInt16(source, ref offset));
        ushort runLength = ReadUInt16(source, ref offset);

        return new TileRun(
            type,
            wall,
            liquid,
            sTileHeader,
            bTileHeader,
            bTileHeader2,
            bTileHeader3,
            frameX,
            frameY,
            runLength);
    }

    private static int ReadInt32(byte[] source, int length, ref int offset)
    {
        if (offset > length - sizeof(int))
        {
            throw new InvalidDataException("Vega world-image tile section is truncated.");
        }

        int value = BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);
        return value;
    }

    private static ushort ReadUInt16(byte[] source, ref int offset)
    {
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(offset, sizeof(ushort)));
        offset += sizeof(ushort);
        return value;
    }

    private static void ApplyRun(int x, int y, TileRun run, int[] tileCounts)
    {
        global::Terraria.ITile tile = global::Terraria.Main.tile[x, y];
        if (run.Type != 0) tile.type = run.Type;
        if (run.Wall != 0) tile.wall = run.Wall;
        if (run.Liquid != 0) tile.liquid = run.Liquid;
        if (run.STileHeader != 0) tile.sTileHeader = run.STileHeader;
        if (run.BTileHeader != 0) tile.bTileHeader = run.BTileHeader;
        if (run.BTileHeader2 != 0) tile.bTileHeader2 = run.BTileHeader2;
        if (run.BTileHeader3 != 0) tile.bTileHeader3 = run.BTileHeader3;
        if (run.FrameX != 0) tile.frameX = run.FrameX;
        if (run.FrameY != 0) tile.frameY = run.FrameY;

        if ((run.STileHeader & 0x20) != 0)
        {
            if (y <= global::Terraria.Main.worldSurface)
            {
                if (y + run.RunLength <= global::Terraria.Main.worldSurface)
                {
                    tileCounts[run.Type] += (run.RunLength + 1) * 5;
                }
                else
                {
                    int surfaceTiles = (int)(global::Terraria.Main.worldSurface - y + 1);
                    int undergroundTiles = run.RunLength + 1 - surfaceTiles;
                    tileCounts[run.Type] += surfaceTiles * 5 + undergroundTiles;
                }
            }
            else
            {
                tileCounts[run.Type] += run.RunLength + 1;
            }
        }

        for (int runOffset = 1; runOffset <= run.RunLength; runOffset++)
        {
            global::Terraria.Main.tile[x, y + runOffset].CopyFrom(tile);
        }
    }

    private readonly record struct ChunkDescriptor(
        int StartX,
        int ColumnCount,
        int RecordCount,
        int RecordOffset,
        int RecordBytes);

    private readonly record struct TileRun(
        ushort Type,
        ushort Wall,
        byte Liquid,
        ushort STileHeader,
        byte BTileHeader,
        byte BTileHeader2,
        byte BTileHeader3,
        short FrameX,
        short FrameY,
        ushort RunLength);
}
