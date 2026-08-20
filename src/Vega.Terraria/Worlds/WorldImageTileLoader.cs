using System.Text;
namespace Vega.Terraria.Worlds;

internal static class WorldImageTileLoader
{
    public static void Load(string imagePath, VegaWorldImageManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(manifest);

        if (global::Terraria.Main.maxTilesX != manifest.MaxTilesX ||
            global::Terraria.Main.maxTilesY != manifest.MaxTilesY)
        {
            throw new InvalidDataException("Vega world-image dimensions do not match the loaded Terraria header.");
        }

        using var stream = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        stream.Position = manifest.TileRuns.Offset;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        int chunkCount = reader.ReadInt32();
        int expectedChunks = (manifest.MaxTilesX + VegaWorldImageFormat.TileChunkColumns - 1) /
            VegaWorldImageFormat.TileChunkColumns;
        if (chunkCount != expectedChunks)
        {
            throw new InvalidDataException("Vega world-image tile chunk count is invalid.");
        }

        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            int startX = reader.ReadInt32();
            int columnCount = reader.ReadInt32();
            int recordCount = reader.ReadInt32();
            int expectedStartX = chunk * VegaWorldImageFormat.TileChunkColumns;
            int expectedColumns = Math.Min(
                VegaWorldImageFormat.TileChunkColumns,
                manifest.MaxTilesX - expectedStartX);
            if (startX != expectedStartX || columnCount != expectedColumns || recordCount <= 0)
            {
                throw new InvalidDataException("Vega world-image tile chunk header is invalid.");
            }

            int x = startX;
            int y = 0;
            for (int record = 0; record < recordCount; record++)
            {
                TileRun run = ReadTileRun(reader);
                if (x >= startX + columnCount || run.RunLength >= manifest.MaxTilesY - y)
                {
                    throw new InvalidDataException("Vega world-image tile run crosses a chunk boundary.");
                }

                ApplyRun(x, y, run);
                y += run.RunLength + 1;
                if (y == manifest.MaxTilesY)
                {
                    x++;
                    y = 0;
                }
            }

            if (x != startX + columnCount || y != 0)
            {
                throw new InvalidDataException("Vega world-image tile chunk is incomplete.");
            }
        }

        if (stream.Position != manifest.TileRuns.Offset + manifest.TileRuns.Length)
        {
            throw new InvalidDataException("Vega world-image tile section length does not match its manifest.");
        }

        global::Terraria.WorldGen.AddUpAlignmentCounts(clearCounts: true);
        if (manifest.TerrariaVersion < 105)
        {
            global::Terraria.WorldGen.FixHearts();
        }
    }

    private static TileRun ReadTileRun(BinaryReader reader) =>
        new(
            reader.ReadUInt16(),
            reader.ReadUInt16(),
            reader.ReadByte(),
            reader.ReadUInt16(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadInt16(),
            reader.ReadInt16(),
            reader.ReadUInt16());

    private static void ApplyRun(int x, int y, TileRun run)
    {
        global::Terraria.ITile tile = global::Terraria.Main.tile[x, y];
        tile.type = run.Type;
        tile.wall = run.Wall;
        tile.liquid = run.Liquid;
        tile.sTileHeader = run.STileHeader;
        tile.bTileHeader = run.BTileHeader;
        tile.bTileHeader2 = run.BTileHeader2;
        tile.bTileHeader3 = run.BTileHeader3;
        tile.frameX = run.FrameX;
        tile.frameY = run.FrameY;

        if (tile.active())
        {
            if (y <= global::Terraria.Main.worldSurface)
            {
                if (y + run.RunLength <= global::Terraria.Main.worldSurface)
                {
                    global::Terraria.WorldGen.tileCounts[run.Type] += (run.RunLength + 1) * 5;
                }
                else
                {
                    int surfaceTiles = (int)(global::Terraria.Main.worldSurface - y + 1);
                    int undergroundTiles = run.RunLength + 1 - surfaceTiles;
                    global::Terraria.WorldGen.tileCounts[run.Type] += surfaceTiles * 5 + undergroundTiles;
                }
            }
            else
            {
                global::Terraria.WorldGen.tileCounts[run.Type] += run.RunLength + 1;
            }
        }

        for (int offset = 1; offset <= run.RunLength; offset++)
        {
            global::Terraria.Main.tile[x, y + offset].CopyFrom(tile);
        }
    }

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
