using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using Vega.Operations;
using Vega.Operations.Logging;
using Vega.Terraria.Worlds;

namespace Vega.Runtime.Tests.Worlds;

[TestFixture]
public sealed class VegaWorldImageTests
{
    [Test]
    public void WorldImageModeIsExplicitAndRemovedFromTerrariaArguments()
    {
        WorldImageOptions defaults = WorldImageOptionsParser.Parse(["-world", "world.wld"]);
        WorldImageOptions enabled = WorldImageOptionsParser.Parse(
            ["-world", "world.wld", "--vega-world-image=on"]);

        Assert.Multiple(() =>
        {
            Assert.That(defaults.Enabled, Is.False);
            Assert.That(enabled.Enabled, Is.True);
            Assert.That(enabled.DirectImageRequested, Is.False);
            Assert.That(enabled.ServerArguments, Is.EqualTo(new[] { "-world", "world.wld" }));
        });
    }

    [Test]
    public void DirectWorldImageSelectionEnablesCacheAndPreservesWldAsSaveTarget()
    {
        WorldImageOptions options = WorldImageOptionsParser.Parse(
            ["-world", "world.vworld", "-port", "7777"]);

        Assert.Multiple(() =>
        {
            Assert.That(options.Enabled, Is.True);
            Assert.That(options.DirectImageRequested, Is.True);
            Assert.That(
                options.ServerArguments,
                Is.EqualTo(new[] { "-world", "world.wld", "-port", "7777" }));
        });
    }

    [Test]
    public void DirectWorldImageSelectionCannotBeCombinedWithDisabledCache()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            WorldImageOptionsParser.Parse(
                ["-world=world.vworld", "--vega-world-image=off"]))!;

        Assert.That(error.Message, Does.Contain("cannot be selected"));
    }

    [Test]
    public void BuildProducesValidatedSectionedImage()
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = Path.Combine(directory.Path, "world.wld");
        string imagePath = Path.Combine(directory.Path, "world.vworld");
        File.WriteAllBytes(sourcePath, CreateWorld(maxTilesX: 4, maxTilesY: 5));

        WorldImageBuildResult result = VegaWorldImageCompiler.Build(sourcePath, imagePath);
        WorldImageValidationResult validation = VegaWorldImageReader.Validate(sourcePath, imagePath);

        Assert.Multiple(() =>
        {
            Assert.That(validation.IsValid, Is.True, validation.Reason);
            Assert.That(validation.Manifest?.SchemaVersion, Is.EqualTo(VegaWorldImageFormat.SchemaVersion));
            Assert.That(validation.Manifest?.MaxTilesX, Is.EqualTo(4));
            Assert.That(validation.Manifest?.MaxTilesY, Is.EqualTo(5));
            Assert.That(validation.Manifest?.SourceWorld.Offset, Is.Zero);
            Assert.That(validation.Manifest?.TileRuns.Offset, Is.EqualTo(result.SourceBytes));
            Assert.That(validation.Manifest?.TileRuns.Length, Is.EqualTo(80));
            Assert.That(validation.Manifest?.LiquidState.Length, Is.Zero);
            Assert.That(validation.Manifest?.SnapshotFlags, Is.EqualTo(WorldImageSnapshotFlags.None));
            Assert.That(result.ImageBytes, Is.GreaterThan(result.SourceBytes));
            Assert.That(File.ReadAllBytes(imagePath).AsSpan(0, (int)result.SourceBytes).ToArray(),
                Is.EqualTo(File.ReadAllBytes(sourcePath)));
        });
    }

    [Test]
    public void RuntimeSnapshotPublishesLiquidStateBoundToCanonicalSource()
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = Path.Combine(directory.Path, "world.wld");
        string imagePath = Path.Combine(directory.Path, "world.vworld");
        File.WriteAllBytes(sourcePath, CreateWorld(maxTilesX: 4, maxTilesY: 5));
        VegaWorldImageCompiler.Build(sourcePath, imagePath);
        VegaWorldImageManifest fileManifest =
            VegaWorldImageReader.Validate(sourcePath, imagePath).Manifest!;
        byte[] imageBytes = File.ReadAllBytes(imagePath);
        byte[] tileRuns = imageBytes.AsSpan(
            checked((int)fileManifest.TileRuns.Offset),
            checked((int)fileManifest.TileRuns.Length)).ToArray();
        byte[] liquidState = CreateEmptyLiquidState();
        var runtimeSnapshot = new WorldImageRuntimeSnapshot(
            VegaWorldImageReader.HashFile(sourcePath),
            4,
            5,
            tileRuns,
            liquidState);

        VegaWorldImageCompiler.Build(sourcePath, imagePath, runtimeSnapshot);
        WorldImageValidationResult validation = VegaWorldImageReader.Validate(sourcePath, imagePath);

        Assert.Multiple(() =>
        {
            Assert.That(validation.IsValid, Is.True, validation.Reason);
            Assert.That(
                validation.Manifest?.SnapshotFlags,
                Is.EqualTo(
                    WorldImageSnapshotFlags.RuntimeTiles |
                    WorldImageSnapshotFlags.LiquidRuntimeState));
            Assert.That(validation.Manifest?.LiquidState.Length, Is.EqualTo(liquidState.Length));
        });
    }

    [Test]
    public void ValidationRejectsChangedSourceContentEvenWhenSizeAndTimestampMatch()
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = Path.Combine(directory.Path, "world.wld");
        string imagePath = Path.Combine(directory.Path, "world.vworld");
        byte[] source = CreateWorld(maxTilesX: 2, maxTilesY: 3);
        File.WriteAllBytes(sourcePath, source);
        DateTime timestamp = File.GetLastWriteTimeUtc(sourcePath);
        VegaWorldImageCompiler.Build(sourcePath, imagePath);

        source[^1] ^= 0x5A;
        File.WriteAllBytes(sourcePath, source);
        File.SetLastWriteTimeUtc(sourcePath, timestamp);

        WorldImageValidationResult validation = VegaWorldImageReader.Validate(sourcePath, imagePath);
        Assert.That(validation.Reason, Is.EqualTo("source-fingerprint"));
    }

    [Test]
    public void ValidationDetectsTileSectionCorruption()
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = Path.Combine(directory.Path, "world.wld");
        string imagePath = Path.Combine(directory.Path, "world.vworld");
        File.WriteAllBytes(sourcePath, CreateWorld(maxTilesX: 3, maxTilesY: 4));
        VegaWorldImageCompiler.Build(sourcePath, imagePath);
        VegaWorldImageManifest manifest = VegaWorldImageReader.Validate(sourcePath, imagePath).Manifest!;

        using (var stream = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = manifest.TileRuns.Offset + 4;
            int value = stream.ReadByte();
            stream.Position--;
            stream.WriteByte((byte)(value ^ 0xFF));
        }

        WorldImageValidationResult validation = VegaWorldImageReader.Validate(sourcePath, imagePath);
        Assert.That(validation.Reason, Is.EqualTo("tile-section-checksum"));
    }

    [Test]
    public void FailedBuildPreservesPreviousValidImage()
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = Path.Combine(directory.Path, "world.wld");
        string imagePath = Path.Combine(directory.Path, "world.vworld");
        File.WriteAllBytes(sourcePath, CreateWorld(maxTilesX: 2, maxTilesY: 2));
        VegaWorldImageCompiler.Build(sourcePath, imagePath);
        byte[] previousImageHash = SHA256.HashData(File.ReadAllBytes(imagePath));

        File.WriteAllBytes(sourcePath, [1, 0, 0, 0]);
        Assert.Throws<InvalidDataException>(() => VegaWorldImageCompiler.Build(sourcePath, imagePath));

        Assert.That(SHA256.HashData(File.ReadAllBytes(imagePath)), Is.EqualTo(previousImageHash));
        Assert.That(File.Exists(imagePath + ".tmp"), Is.False);
    }

    [Test]
    public void SuccessfulWorldSaveRequestRebuildsTheImageFingerprint()
    {
        using var directory = new TemporaryDirectory();
        string sourcePath = Path.Combine(directory.Path, "world.wld");
        string imagePath = Path.Combine(directory.Path, "world.vworld");
        byte[] source = CreateWorld(maxTilesX: 3, maxTilesY: 4);
        File.WriteAllBytes(sourcePath, source);
        VegaWorldImageCompiler.Build(sourcePath, imagePath);

        source[^1] ^= 0x2A;
        File.WriteAllBytes(sourcePath, source);
        Assert.That(
            VegaWorldImageReader.Validate(sourcePath, imagePath).Reason,
            Is.EqualTo("source-fingerprint"));

        var lifecycle = new RuntimeLifecycleTracker(Stopwatch.GetTimestamp());
        var logger = new StructuredOperationsLogger([new BoundedOperationsLogStore()]);
        using var service = new TerrariaWorldImageService(logger, lifecycle, enabled: true);
        service.RebuildAfterSave(sourcePath);
        service.FlushPendingBuilds();

        Assert.That(VegaWorldImageReader.Validate(sourcePath, imagePath).IsValid, Is.True);
    }

    private static byte[] CreateWorld(int maxTilesX, int maxTilesY)
    {
        const int version = 279;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(version);
        writer.Write(0x6369676F6C6572L | (2UL << 56));
        writer.Write(0U);
        writer.Write(0UL);
        writer.Write((short)3);
        long positionsOffset = stream.Position;
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write((ushort)1);
        writer.Write((byte)0);

        int headerStart = (int)stream.Position;
        writer.Write("benchmark-world");
        writer.Write("seed");
        writer.Write(0UL);
        writer.Write(Guid.Empty.ToByteArray());
        writer.Write(42);
        writer.Write(0);
        writer.Write(maxTilesX * 16);
        writer.Write(0);
        writer.Write(maxTilesY * 16);
        writer.Write(maxTilesY);
        writer.Write(maxTilesX);

        int tileStart = (int)stream.Position;
        for (int x = 0; x < maxTilesX; x++)
        {
            if (maxTilesY == 1)
            {
                writer.Write((byte)0);
            }
            else
            {
                writer.Write((byte)0x40);
                writer.Write((byte)(maxTilesY - 1));
            }
        }

        int tileEnd = (int)stream.Position;
        writer.Write((byte)0xA5);
        long end = stream.Position;
        stream.Position = positionsOffset;
        writer.Write(headerStart);
        writer.Write(tileStart);
        writer.Write(tileEnd);
        stream.Position = end;
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] CreateEmptyLiquidState()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(VegaWorldImageFormat.LiquidStateMagic);
        writer.Write(VegaWorldImageFormat.LiquidStateVersion);
        writer.Write(25000); // maxLiquid
        writer.Write(0); // skipCount
        writer.Write(0); // stuckCount
        writer.Write(0); // stuckAmount
        writer.Write(10); // cycles
        writer.Write(25000); // curMaxLiquid
        writer.Write(false); // stuck
        writer.Write(false); // quickFall
        writer.Write(0); // wetCounter
        writer.Write(0); // panicCounter
        writer.Write(false); // panicMode
        writer.Write(0); // panicY
        writer.Write(0); // liquidCount
        writer.Write(0); // bufferCount
        writer.Flush();
        return stream.ToArray();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "vega-world-image-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
