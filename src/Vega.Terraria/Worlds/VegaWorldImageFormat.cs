using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Vega.Terraria.Worlds;

internal static class VegaWorldImageFormat
{
    public const int SchemaVersion = 2;
    public const int TileChunkColumns = 64;
    public const int FooterSize = 52;
    public const int LiquidStateVersion = 1;
    public const string CompilerParameters =
        "runtime-snapshot-v2;chunk-columns=64;tile-record-bytes=16;liquid-state=v1";

    public static ReadOnlySpan<byte> ManifestMagic => "VGWRLD01"u8;

    public static ReadOnlySpan<byte> FooterMagic => "VGFTR001"u8;

    public static ReadOnlySpan<byte> LiquidStateMagic => "VGLIQ001"u8;
}

internal enum WorldImageSection : byte
{
    SourceWorld = 1,
    TileRuns = 2,
    LiquidState = 3
}

[Flags]
internal enum WorldImageSnapshotFlags : byte
{
    None = 0,
    RuntimeTiles = 1 << 0,
    LiquidRuntimeState = 1 << 1
}

internal sealed record VegaWorldImageManifest(
    int SchemaVersion,
    int TerrariaVersion,
    long SourceLength,
    long SourceLastWriteUtcTicks,
    int MaxTilesX,
    int MaxTilesY,
    long SourceTileEnd,
    WorldImageSnapshotFlags SnapshotFlags,
    string CompilerParameters,
    WorldImageSectionEntry SourceWorld,
    WorldImageSectionEntry TileRuns,
    WorldImageSectionEntry LiquidState);

internal sealed record WorldImageSectionEntry(
    WorldImageSection Id,
    long Offset,
    long Length,
    byte[] Sha256);

internal sealed record WorldImageValidationResult(
    bool IsValid,
    string Reason,
    VegaWorldImageManifest? Manifest = null)
{
    public static WorldImageValidationResult Valid(VegaWorldImageManifest manifest) =>
        new(true, "hit", manifest);

    public static WorldImageValidationResult Invalid(string reason) =>
        new(false, reason);
}

internal sealed record WorldImageBuildResult(
    string ImagePath,
    long SourceBytes,
    long ImageBytes,
    TimeSpan Duration);

internal static class VegaWorldImageReader
{
    public static WorldImageValidationResult Validate(string sourcePath, string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        if (!File.Exists(imagePath))
        {
            return WorldImageValidationResult.Invalid("absent");
        }

        try
        {
            var sourceInfo = new FileInfo(sourcePath);
            if (!sourceInfo.Exists)
            {
                return WorldImageValidationResult.Invalid("source-absent");
            }

            using var image = new FileStream(
                imagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            if (image.Length < VegaWorldImageFormat.FooterSize)
            {
                return WorldImageValidationResult.Invalid("truncated-footer");
            }

            image.Position = image.Length - VegaWorldImageFormat.FooterSize;
            using var reader = new BinaryReader(image, Encoding.UTF8, leaveOpen: true);
            if (!reader.ReadBytes(8).AsSpan().SequenceEqual(VegaWorldImageFormat.FooterMagic))
            {
                return WorldImageValidationResult.Invalid("bad-footer-magic");
            }

            long manifestOffset = reader.ReadInt64();
            int manifestLength = reader.ReadInt32();
            byte[] expectedManifestHash = reader.ReadBytes(32);
            if (manifestOffset < 0 || manifestLength <= 0 ||
                manifestOffset + manifestLength > image.Length - VegaWorldImageFormat.FooterSize)
            {
                return WorldImageValidationResult.Invalid("bad-manifest-range");
            }

            image.Position = manifestOffset;
            byte[] manifestBytes = reader.ReadBytes(manifestLength);
            if (!SHA256.HashData(manifestBytes).AsSpan().SequenceEqual(expectedManifestHash))
            {
                return WorldImageValidationResult.Invalid("manifest-checksum");
            }

            VegaWorldImageManifest manifest = ReadManifest(manifestBytes);
            string? error = ValidateSourceMetadata(manifest, sourceInfo, sourcePath)
                ?? ValidateSections(manifest, image, manifestOffset, sourcePath)
                ?? ValidateSnapshotFlags(manifest);
            if (error is not null) return WorldImageValidationResult.Invalid(error);

            return WorldImageValidationResult.Valid(manifest);
        }
        catch (EndOfStreamException)
        {
            return WorldImageValidationResult.Invalid("truncated");
        }
        catch (IOException)
        {
            return WorldImageValidationResult.Invalid("io-error");
        }
        catch (UnauthorizedAccessException)
        {
            return WorldImageValidationResult.Invalid("access-denied");
        }
        catch (InvalidDataException)
        {
            return WorldImageValidationResult.Invalid("invalid-format");
        }
    }

    private static string? ValidateSourceMetadata(VegaWorldImageManifest manifest, FileInfo sourceInfo, string sourcePath)
    {
        if (manifest.SchemaVersion != VegaWorldImageFormat.SchemaVersion)
        {
            return "schema-version";
        }

        if (!string.Equals(
                manifest.CompilerParameters,
                VegaWorldImageFormat.CompilerParameters,
                StringComparison.Ordinal))
        {
            return "compiler-parameters";
        }

        if (manifest.SourceLength != sourceInfo.Length)
        {
            return "source-size";
        }

        int sourceVersion = ReadTerrariaVersion(sourcePath);
        if (manifest.TerrariaVersion != sourceVersion)
        {
            return "terraria-version";
        }
        return null;
    }

    private static string? ValidateSections(VegaWorldImageManifest manifest, FileStream image, long manifestOffset, string sourcePath)
    {
        if (!IsSectionRangeValid(manifest.SourceWorld, image.Length) ||
            !IsSectionRangeValid(manifest.TileRuns, image.Length) ||
            !IsSectionRangeValid(manifest.LiquidState, image.Length) ||
            manifest.SourceWorld.Offset != 0 ||
            manifest.SourceWorld.Length != manifest.SourceLength ||
            manifest.TileRuns.Offset != manifest.SourceWorld.Length ||
            manifest.LiquidState.Offset != manifest.TileRuns.Offset + manifest.TileRuns.Length ||
            manifest.LiquidState.Offset + manifest.LiquidState.Length != manifestOffset)
        {
            return "section-range";
        }

        byte[] sourceHash = HashFile(sourcePath);
        if (!sourceHash.AsSpan().SequenceEqual(manifest.SourceWorld.Sha256))
        {
            return "source-fingerprint";
        }

        if (!HashSection(image, manifest.SourceWorld).AsSpan().SequenceEqual(manifest.SourceWorld.Sha256))
        {
            return "source-section-checksum";
        }

        if (!HashSection(image, manifest.TileRuns).AsSpan().SequenceEqual(manifest.TileRuns.Sha256))
        {
            return "tile-section-checksum";
        }

        if (!HashSection(image, manifest.LiquidState).AsSpan().SequenceEqual(manifest.LiquidState.Sha256))
        {
            return "liquid-section-checksum";
        }
        return null;
    }

    private static string? ValidateSnapshotFlags(VegaWorldImageManifest manifest)
    {
        bool hasRuntimeLiquidState =
            manifest.SnapshotFlags.HasFlag(WorldImageSnapshotFlags.LiquidRuntimeState);
        const WorldImageSnapshotFlags knownSnapshotFlags =
            WorldImageSnapshotFlags.RuntimeTiles |
            WorldImageSnapshotFlags.LiquidRuntimeState;
        if ((manifest.SnapshotFlags & ~knownSnapshotFlags) != 0 ||
            hasRuntimeLiquidState != (manifest.LiquidState.Length > 0) ||
            hasRuntimeLiquidState &&
            !manifest.SnapshotFlags.HasFlag(WorldImageSnapshotFlags.RuntimeTiles))
        {
            return "snapshot-flags";
        }
        return null;
    }

    internal static byte[] HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return SHA256.HashData(stream);
    }

    internal static byte[] HashSection(Stream stream, WorldImageSectionEntry section)
    {
        long originalPosition = stream.Position;
        stream.Position = section.Offset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long remaining = section.Length;
            while (remaining > 0)
            {
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }

            return hash.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            stream.Position = originalPosition;
        }
    }

    private static VegaWorldImageManifest ReadManifest(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (!reader.ReadBytes(8).AsSpan().SequenceEqual(VegaWorldImageFormat.ManifestMagic))
        {
            throw new InvalidDataException("Invalid Vega world-image manifest magic.");
        }

        int schemaVersion = reader.ReadInt32();
        int terrariaVersion = reader.ReadInt32();
        long sourceLength = reader.ReadInt64();
        long sourceLastWriteUtcTicks = reader.ReadInt64();
        int maxTilesX = reader.ReadInt32();
        int maxTilesY = reader.ReadInt32();
        long sourceTileEnd = reader.ReadInt64();
        var snapshotFlags = (WorldImageSnapshotFlags)reader.ReadByte();
        string compilerParameters = reader.ReadString();
        int sectionCount = reader.ReadInt32();
        if (sectionCount != 3)
        {
            throw new InvalidDataException("Unexpected Vega world-image section count.");
        }

        WorldImageSectionEntry first = ReadSection(reader);
        WorldImageSectionEntry second = ReadSection(reader);
        WorldImageSectionEntry third = ReadSection(reader);
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Unexpected data after the Vega world-image manifest.");
        }

        WorldImageSectionEntry[] sections = [first, second, third];
        WorldImageSectionEntry? source = sections.SingleOrDefault(
            static section => section.Id == WorldImageSection.SourceWorld);
        WorldImageSectionEntry? tiles = sections.SingleOrDefault(
            static section => section.Id == WorldImageSection.TileRuns);
        WorldImageSectionEntry? liquid = sections.SingleOrDefault(
            static section => section.Id == WorldImageSection.LiquidState);
        if (source is null || tiles is null || liquid is null)
        {
            throw new InvalidDataException("Required Vega world-image sections are missing.");
        }

        return new VegaWorldImageManifest(
            schemaVersion,
            terrariaVersion,
            sourceLength,
            sourceLastWriteUtcTicks,
            maxTilesX,
            maxTilesY,
            sourceTileEnd,
            snapshotFlags,
            compilerParameters,
            source,
            tiles,
            liquid);
    }

    private static WorldImageSectionEntry ReadSection(BinaryReader reader)
    {
        var id = (WorldImageSection)reader.ReadByte();
        long offset = reader.ReadInt64();
        long length = reader.ReadInt64();
        byte[] hash = reader.ReadBytes(32);
        if (hash.Length != 32)
        {
            throw new EndOfStreamException();
        }

        return new WorldImageSectionEntry(id, offset, length, hash);
    }

    private static bool IsSectionRangeValid(WorldImageSectionEntry section, long imageLength) =>
        section.Offset >= 0 && section.Length >= 0 && section.Offset <= imageLength - section.Length;

    private static int ReadTerrariaVersion(string sourcePath)
    {
        using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream);
        return reader.ReadInt32();
    }
}

internal static class VegaWorldImageCompiler
{
    private const ushort NoTile = ushort.MaxValue;

    public static WorldImageBuildResult Build(
        string sourcePath,
        string imagePath,
        WorldImageRuntimeSnapshot? runtimeSnapshot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        string? directory = Path.GetDirectoryName(imagePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = imagePath + ".tmp";
        FileInfo sourceInfo = new(sourcePath);
        byte[] sourceHash = VegaWorldImageReader.HashFile(sourcePath);
        WorldSourceLayout layout = ReadSourceLayout(sourcePath, runtimeSnapshot is not null);
        if (runtimeSnapshot is not null)
        {
            if (!sourceHash.AsSpan().SequenceEqual(runtimeSnapshot.SourceSha256))
            {
                throw new InvalidDataException("The canonical world changed after its runtime snapshot was captured.");
            }

            if (runtimeSnapshot.MaxTilesX != layout.MaxTilesX ||
                runtimeSnapshot.MaxTilesY != layout.MaxTilesY)
            {
                throw new InvalidDataException("Runtime snapshot dimensions do not match the canonical world.");
            }
        }

        try
        {
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.ReadWrite,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.SequentialScan))
            {
                using (var source = new FileStream(
                           sourcePath,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read,
                           64 * 1024,
                           FileOptions.SequentialScan))
                {
                    source.CopyTo(output, 64 * 1024);
                }

                long tileOffset = output.Position;
                if (runtimeSnapshot is not null)
                {
                    runtimeSnapshot.WriteTileRuns(output);
                }
                else
                {
                    using var source = new FileStream(
                        sourcePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        64 * 1024,
                        FileOptions.SequentialScan);
                    using var sourceReader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
                    using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
                    WriteTileRuns(sourceReader, writer, layout);
                }

                long tileLength = output.Position - tileOffset;
                var sourceSection = new WorldImageSectionEntry(
                    WorldImageSection.SourceWorld,
                    0,
                    sourceInfo.Length,
                    sourceHash);
                var tileSectionWithoutHash = new WorldImageSectionEntry(
                    WorldImageSection.TileRuns,
                    tileOffset,
                    tileLength,
                    []);
                byte[] tileHash = VegaWorldImageReader.HashSection(output, tileSectionWithoutHash);
                var tileSection = tileSectionWithoutHash with { Sha256 = tileHash };
                long liquidOffset = output.Position;
                if (runtimeSnapshot is not null)
                {
                    output.Write(runtimeSnapshot.LiquidState);
                }

                var liquidSectionWithoutHash = new WorldImageSectionEntry(
                    WorldImageSection.LiquidState,
                    liquidOffset,
                    output.Position - liquidOffset,
                    []);
                byte[] liquidHash = VegaWorldImageReader.HashSection(output, liquidSectionWithoutHash);
                var liquidSection = liquidSectionWithoutHash with { Sha256 = liquidHash };
                var manifest = new VegaWorldImageManifest(
                    VegaWorldImageFormat.SchemaVersion,
                    layout.TerrariaVersion,
                    sourceInfo.Length,
                    sourceInfo.LastWriteTimeUtc.Ticks,
                    layout.MaxTilesX,
                    layout.MaxTilesY,
                    layout.TileEnd,
                    runtimeSnapshot is null
                        ? WorldImageSnapshotFlags.None
                        : WorldImageSnapshotFlags.RuntimeTiles |
                            WorldImageSnapshotFlags.LiquidRuntimeState,
                    VegaWorldImageFormat.CompilerParameters,
                    sourceSection,
                    tileSection,
                    liquidSection);

                byte[] manifestBytes = WriteManifest(manifest);
                long manifestOffset = output.Position;
                output.Write(manifestBytes);
                output.Write(VegaWorldImageFormat.FooterMagic);
                using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(manifestOffset);
                    writer.Write(manifestBytes.Length);
                    writer.Write(SHA256.HashData(manifestBytes));
                }

                output.Flush(flushToDisk: true);
            }

            WorldImageValidationResult validation = VegaWorldImageReader.Validate(sourcePath, temporaryPath);
            if (!validation.IsValid)
            {
                throw new InvalidDataException($"Generated Vega world image failed validation: {validation.Reason}.");
            }

            if (File.Exists(imagePath))
            {
                File.Replace(temporaryPath, imagePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, imagePath);
            }

            return new WorldImageBuildResult(
                imagePath,
                sourceInfo.Length,
                new FileInfo(imagePath).Length,
                System.Diagnostics.Stopwatch.GetElapsedTime(started));
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static WorldSourceLayout ReadSourceLayout(string sourcePath, bool runtimeCaptured)
    {
        using var stream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        int version = reader.ReadInt32();
        // Terraria 1.4.6.1 (329) retains this header layout. Only accept its
        // tiles from native capture; the legacy file tile decoder is not yet qualified.
        if (version is <= 87 || (version > 319 && !(runtimeCaptured && version == 329)))
        {
            throw new InvalidDataException($"Terraria world version {version} is not supported by world-image schema {VegaWorldImageFormat.SchemaVersion} for this capture mode.");
        }

        stream.Position = 0;
        reader.ReadInt32();
        if (version >= 135)
        {
            stream.Position += 20;
        }

        short positionCount = reader.ReadInt16();
        if (positionCount < 3)
        {
            throw new InvalidDataException("Terraria world does not contain the required section pointers.");
        }

        int[] positions = new int[positionCount];
        for (int index = 0; index < positions.Length; index++)
        {
            positions[index] = reader.ReadInt32();
        }

        ushort importanceCount = reader.ReadUInt16();
        bool[] importance = new bool[importanceCount];
        byte bits = 0;
        byte mask = 128;
        for (int index = 0; index < importance.Length; index++)
        {
            if (mask == 128)
            {
                bits = reader.ReadByte();
                mask = 1;
            }
            else
            {
                mask <<= 1;
            }

            importance[index] = (bits & mask) == mask;
        }

        stream.Position = positions[0];
        reader.ReadString();
        if (version >= 179)
        {
            if (version == 179)
            {
                reader.ReadInt32();
            }
            else
            {
                reader.ReadString();
            }

            reader.ReadUInt64();
        }

        if (version >= 181)
        {
            stream.Position += 16;
        }

        reader.ReadInt32();
        reader.ReadInt32();
        reader.ReadInt32();
        reader.ReadInt32();
        reader.ReadInt32();
        int maxTilesY = reader.ReadInt32();
        int maxTilesX = reader.ReadInt32();
        if (maxTilesX <= 0 || maxTilesY <= 0 || positions[1] <= positions[0] || positions[2] <= positions[1])
        {
            throw new InvalidDataException("Terraria world dimensions or tile section pointers are invalid.");
        }

        return new WorldSourceLayout(
            version,
            maxTilesX,
            maxTilesY,
            positions[1],
            positions[2],
            importance);
    }

    private static void WriteTileRuns(
        BinaryReader reader,
        BinaryWriter writer,
        WorldSourceLayout layout)
    {
        reader.BaseStream.Position = layout.TileStart;
        int chunkCount = (layout.MaxTilesX + VegaWorldImageFormat.TileChunkColumns - 1) /
            VegaWorldImageFormat.TileChunkColumns;
        writer.Write(chunkCount);
        var packedTile = new global::Terraria.Tile();

        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            int startX = chunk * VegaWorldImageFormat.TileChunkColumns;
            int columnCount = Math.Min(VegaWorldImageFormat.TileChunkColumns, layout.MaxTilesX - startX);
            writer.Write(startX);
            writer.Write(columnCount);
            long recordCountPosition = writer.BaseStream.Position;
            writer.Write(0);
            int recordCount = 0;

            for (int x = startX; x < startX + columnCount; x++)
            {
                for (int y = 0; y < layout.MaxTilesY; y++)
                {
                    TileRun run = ReadTileRun(reader, layout.Importance);
                    if (run.RunLength >= layout.MaxTilesY - y)
                    {
                        throw new InvalidDataException("A Terraria tile run crosses a column boundary.");
                    }

                    WriteTileRun(writer, run, packedTile);
                    recordCount++;
                    y += run.RunLength;
                }
            }

            long end = writer.BaseStream.Position;
            writer.BaseStream.Position = recordCountPosition;
            writer.Write(recordCount);
            writer.BaseStream.Position = end;
        }

        if (reader.BaseStream.Position != layout.TileEnd)
        {
            throw new InvalidDataException("Terraria tile decoder did not end at the declared section boundary.");
        }
    }

    private static TileRun ReadTileRun(BinaryReader reader, bool[] importance)
    {
        byte header1 = reader.ReadByte();
        byte header2 = 0;
        byte header3 = 0;
        byte header4 = 0;
        if ((header1 & 1) != 0)
        {
            header2 = reader.ReadByte();
            if ((header2 & 1) != 0)
            {
                header3 = reader.ReadByte();
                if ((header3 & 1) != 0)
                {
                    header4 = reader.ReadByte();
                }
            }
        }

        ushort type = NoTile;
        ushort wall = 0;
        short frameX = -1;
        short frameY = -1;
        byte tileColor = 0;
        byte wallColor = 0;
        byte liquid = 0;
        ushort flags = 0;
        byte slope = 0;

        if ((header1 & 2) != 0)
        {
            flags |= TileRunFlags.Active;
            type = (header1 & 0x20) != 0
                ? (ushort)(reader.ReadByte() | (reader.ReadByte() << 8))
                : reader.ReadByte();
            if (type >= importance.Length)
            {
                throw new InvalidDataException($"Tile type {type} exceeds the importance table.");
            }

            if (importance[type])
            {
                frameX = reader.ReadInt16();
                frameY = reader.ReadInt16();
                if (type == 144)
                {
                    frameY = 0;
                }
            }

            if ((header3 & 8) != 0)
            {
                tileColor = reader.ReadByte();
            }
        }

        if ((header1 & 4) != 0)
        {
            wall = reader.ReadByte();
            if ((header3 & 0x10) != 0)
            {
                wallColor = reader.ReadByte();
            }
        }

        byte liquidKind = (byte)((header1 & 0x18) >> 3);
        if (liquidKind != 0)
        {
            liquid = reader.ReadByte();
            if ((header3 & 0x80) != 0)
            {
                flags |= TileRunFlags.Shimmer;
            }
            else if (liquidKind == 2)
            {
                flags |= TileRunFlags.Lava;
            }
            else if (liquidKind == 3)
            {
                flags |= TileRunFlags.Honey;
            }
        }

        if ((header2 & 2) != 0) flags |= TileRunFlags.Wire1;
        if ((header2 & 4) != 0) flags |= TileRunFlags.Wire2;
        if ((header2 & 8) != 0) flags |= TileRunFlags.Wire3;
        byte slopeCode = (byte)((header2 & 0x70) >> 4);
        if (slopeCode == 1)
        {
            flags |= TileRunFlags.HalfBrick;
        }
        else if (slopeCode > 1)
        {
            slope = (byte)(slopeCode - 1);
        }

        if ((header3 & 2) != 0) flags |= TileRunFlags.Actuator;
        if ((header3 & 4) != 0) flags |= TileRunFlags.Inactive;
        if ((header3 & 0x20) != 0) flags |= TileRunFlags.Wire4;
        if ((header3 & 0x40) != 0)
        {
            wall = (ushort)(wall | (reader.ReadByte() << 8));
        }

        if ((header4 & 2) != 0) flags |= TileRunFlags.InvisibleBlock;
        if ((header4 & 4) != 0) flags |= TileRunFlags.InvisibleWall;
        if ((header4 & 8) != 0) flags |= TileRunFlags.FullbrightBlock;
        if ((header4 & 0x10) != 0) flags |= TileRunFlags.FullbrightWall;

        int runLength = ((header1 & 0xC0) >> 6) switch
        {
            0 => 0,
            1 => reader.ReadByte(),
            _ => reader.ReadInt16()
        };
        if (runLength < 0)
        {
            throw new InvalidDataException("Terraria tile run length is negative.");
        }

        return new TileRun(
            type,
            wall,
            frameX,
            frameY,
            liquid,
            tileColor,
            wallColor,
            flags,
            slope,
            (ushort)runLength);
    }

    private static void WriteTileRun(BinaryWriter writer, TileRun run, global::Terraria.ITile tile)
    {
        tile.ClearEverything();
        if ((run.Flags & TileRunFlags.Active) != 0)
        {
            tile.active(active: true);
            tile.type = run.Type;
            tile.frameX = run.FrameX;
            tile.frameY = run.FrameY;
            tile.color(run.TileColor);
        }

        tile.wall = run.Wall < global::Terraria.ID.WallID.Count ? run.Wall : (ushort)0;
        tile.wallColor(run.WallColor);
        tile.liquid = run.Liquid;
        tile.lava((run.Flags & TileRunFlags.Lava) != 0);
        tile.honey((run.Flags & TileRunFlags.Honey) != 0);
        tile.shimmer((run.Flags & TileRunFlags.Shimmer) != 0);
        tile.wire((run.Flags & TileRunFlags.Wire1) != 0);
        tile.wire2((run.Flags & TileRunFlags.Wire2) != 0);
        tile.wire3((run.Flags & TileRunFlags.Wire3) != 0);
        tile.wire4((run.Flags & TileRunFlags.Wire4) != 0);
        tile.actuator((run.Flags & TileRunFlags.Actuator) != 0);
        tile.inActive((run.Flags & TileRunFlags.Inactive) != 0);
        tile.invisibleBlock((run.Flags & TileRunFlags.InvisibleBlock) != 0);
        tile.invisibleWall((run.Flags & TileRunFlags.InvisibleWall) != 0);
        tile.fullbrightBlock((run.Flags & TileRunFlags.FullbrightBlock) != 0);
        tile.fullbrightWall((run.Flags & TileRunFlags.FullbrightWall) != 0);
        if (run.Type != NoTile)
        {
            tile.halfBrick((run.Flags & TileRunFlags.HalfBrick) != 0);
            tile.slope(run.Slope);
        }

        writer.Write(tile.type);
        writer.Write(tile.wall);
        writer.Write(tile.liquid);
        writer.Write(tile.sTileHeader);
        writer.Write(tile.bTileHeader);
        writer.Write(tile.bTileHeader2);
        writer.Write(tile.bTileHeader3);
        writer.Write(tile.frameX);
        writer.Write(tile.frameY);
        writer.Write(run.RunLength);
    }

    private static byte[] WriteManifest(VegaWorldImageManifest manifest)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(VegaWorldImageFormat.ManifestMagic);
        writer.Write(manifest.SchemaVersion);
        writer.Write(manifest.TerrariaVersion);
        writer.Write(manifest.SourceLength);
        writer.Write(manifest.SourceLastWriteUtcTicks);
        writer.Write(manifest.MaxTilesX);
        writer.Write(manifest.MaxTilesY);
        writer.Write(manifest.SourceTileEnd);
        writer.Write((byte)manifest.SnapshotFlags);
        writer.Write(manifest.CompilerParameters);
        writer.Write(3);
        WriteSection(writer, manifest.SourceWorld);
        WriteSection(writer, manifest.TileRuns);
        WriteSection(writer, manifest.LiquidState);
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteSection(BinaryWriter writer, WorldImageSectionEntry section)
    {
        writer.Write((byte)section.Id);
        writer.Write(section.Offset);
        writer.Write(section.Length);
        writer.Write(section.Sha256);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record WorldSourceLayout(
        int TerrariaVersion,
        int MaxTilesX,
        int MaxTilesY,
        long TileStart,
        long TileEnd,
        bool[] Importance);

    private readonly record struct TileRun(
        ushort Type,
        ushort Wall,
        short FrameX,
        short FrameY,
        byte Liquid,
        byte TileColor,
        byte WallColor,
        ushort Flags,
        byte Slope,
        ushort RunLength);
}

internal static class TileRunFlags
{
    public const ushort Active = 1 << 0;
    public const ushort Lava = 1 << 1;
    public const ushort Honey = 1 << 2;
    public const ushort Shimmer = 1 << 3;
    public const ushort Wire1 = 1 << 4;
    public const ushort Wire2 = 1 << 5;
    public const ushort Wire3 = 1 << 6;
    public const ushort Wire4 = 1 << 7;
    public const ushort HalfBrick = 1 << 8;
    public const ushort Actuator = 1 << 9;
    public const ushort Inactive = 1 << 10;
    public const ushort InvisibleBlock = 1 << 11;
    public const ushort InvisibleWall = 1 << 12;
    public const ushort FullbrightBlock = 1 << 13;
    public const ushort FullbrightWall = 1 << 14;
}
