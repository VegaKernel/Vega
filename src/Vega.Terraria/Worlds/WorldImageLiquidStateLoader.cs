using System.Text;

namespace Vega.Terraria.Worlds;

internal static class WorldImageLiquidStateLoader
{
    public static void Load(string imagePath, VegaWorldImageManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(manifest);

        if (!manifest.SnapshotFlags.HasFlag(WorldImageSnapshotFlags.LiquidRuntimeState))
        {
            throw new InvalidOperationException("The Vega world image does not contain runtime liquid state.");
        }

        if (manifest.LiquidState.Length is <= 0 or > int.MaxValue)
        {
            throw new InvalidDataException("The Vega world-image liquid state length is invalid.");
        }

        byte[] section = new byte[checked((int)manifest.LiquidState.Length)];
        using (var image = new FileStream(
                   imagePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   64 * 1024,
                   FileOptions.SequentialScan))
        {
            image.Position = manifest.LiquidState.Offset;
            image.ReadExactly(section);
        }

        using var stream = new MemoryStream(section, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(8).AsSpan().SequenceEqual(VegaWorldImageFormat.LiquidStateMagic) ||
            reader.ReadInt32() != VegaWorldImageFormat.LiquidStateVersion)
        {
            throw new InvalidDataException("The Vega world-image liquid state header is invalid.");
        }

        int maxLiquid = reader.ReadInt32();
        int skipCount = reader.ReadInt32();
        int stuckCount = reader.ReadInt32();
        int stuckAmount = reader.ReadInt32();
        int cycles = reader.ReadInt32();
        int curMaxLiquid = reader.ReadInt32();
        bool stuck = reader.ReadBoolean();
        bool quickFall = reader.ReadBoolean();
        int wetCounter = reader.ReadInt32();
        int panicCounter = reader.ReadInt32();
        bool panicMode = reader.ReadBoolean();
        int panicY = reader.ReadInt32();
        int liquidCount = reader.ReadInt32();
        int bufferCount = reader.ReadInt32();
        if (maxLiquid <= 0 || maxLiquid > global::Terraria.Main.liquid.Length ||
            skipCount < 0 || stuckCount < 0 || stuckAmount < 0 ||
            cycles <= 0 || curMaxLiquid < 0 || curMaxLiquid > maxLiquid ||
            wetCounter < 0 || wetCounter > cycles || panicCounter < 0 ||
            liquidCount < 0 || bufferCount < 0 ||
            liquidCount > global::Terraria.Main.liquid.Length ||
            bufferCount > global::Terraria.Main.liquidBuffer.Length)
        {
            throw new InvalidDataException("The Vega world-image liquid state counters are invalid.");
        }

        for (int index = 0; index < liquidCount; index++)
        {
            int x = reader.ReadInt32();
            int y = reader.ReadInt32();
            ValidateCoordinates(x, y, manifest);
            global::Terraria.Liquid liquid = global::Terraria.Main.liquid[index];
            liquid.x = x;
            liquid.y = y;
            liquid.kill = reader.ReadInt32();
            liquid.delay = reader.ReadInt32();
        }

        for (int index = 0; index < bufferCount; index++)
        {
            int x = reader.ReadInt32();
            int y = reader.ReadInt32();
            ValidateCoordinates(x, y, manifest);
            global::Terraria.LiquidBuffer liquid = global::Terraria.Main.liquidBuffer[index];
            liquid.x = x;
            liquid.y = y;
        }

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("The Vega world-image liquid state length is invalid.");
        }

        global::Terraria.Liquid.maxLiquid = maxLiquid;
        global::Terraria.Liquid.skipCount = skipCount;
        global::Terraria.Liquid.stuckCount = stuckCount;
        global::Terraria.Liquid.stuckAmount = stuckAmount;
        global::Terraria.Liquid.cycles = cycles;
        global::Terraria.Liquid.curMaxLiquid = curMaxLiquid;
        global::Terraria.Liquid.stuck = stuck;
        global::Terraria.Liquid.quickFall = quickFall;
        global::Terraria.Liquid.quickSettle = false;
        global::Terraria.Liquid.wetCounter = wetCounter;
        global::Terraria.Liquid.panicCounter = panicCounter;
        global::Terraria.Liquid.panicMode = panicMode;
        global::Terraria.Liquid.panicY = panicY;
        global::Terraria.Liquid.numLiquid = liquidCount;
        global::Terraria.LiquidBuffer.numLiquidBuffer = bufferCount;
        global::Terraria.Liquid._netChangeSet.Clear();
        global::Terraria.Liquid._swapNetChangeSet.Clear();
    }

    private static void ValidateCoordinates(int x, int y, VegaWorldImageManifest manifest)
    {
        if ((uint)x >= (uint)manifest.MaxTilesX || (uint)y >= (uint)manifest.MaxTilesY)
        {
            throw new InvalidDataException("The Vega world-image liquid queue contains invalid coordinates.");
        }
    }
}
