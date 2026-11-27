using System.Globalization;
using TerraRuntime.Schematics;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;
using Vega.Modules.Scenes;
using Vega.Terraria.Worlds;

namespace Vega.Runtime;

/// <summary>Internal one-shot child mode; never loads a user source world for mutation.</summary>
internal static class OfflineSchematicTemplateCommand
{
    public static int? TryRun(string[] args)
    {
        if (args.Length == 0 || args[0] != "--vega-build-schematic-template") return null;
        try
        {
            if (args.Length != 7 ||
                !int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out int x) ||
                !int.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out int y) ||
                !long.TryParse(args[6], NumberStyles.None, CultureInfo.InvariantCulture, out long maximumBytes) || maximumBytes <= 0)
                throw new ArgumentException("Invalid schematic template build arguments.");
            string source = Path.GetFullPath(args[1]);
            string schematic = Path.GetFullPath(args[2]);
            string destination = Path.GetFullPath(args[3]);
            if (!File.Exists(source) || !string.Equals(Path.GetExtension(source), ".wld", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(schematic) || !string.Equals(Path.GetExtension(schematic), ".trschem", StringComparison.OrdinalIgnoreCase) ||
                Directory.Exists(destination) || File.Exists(destination) || new FileInfo(source).Length > maximumBytes)
                throw new InvalidDataException("A bounded existing base world, schematic and a new destination directory are required.");

            SchematicDocument document = SchematicFile.Load(schematic);
            if (document.Tiles.Length > WorldTileOperationLimits.MaximumTiles || document.WorldItems.Length != 0 ||
                document.TileEntities.Any(static entity => entity is SchematicLogicSensorTileEntity or SchematicTeleportationPylonTileEntity))
                throw new SchematicFormatException("Schematic exceeds the native placement limit or contains unsupported entity records.");
            var projection = SchematicSceneProjection.Create(document, "materialization", x, y, SceneObserverScope.Global);
            string drive = Path.GetPathRoot(destination)!;
            long required = checked(new FileInfo(source).Length * 2 + 64L * 1024 * 1024);
            if (new DriveInfo(drive).AvailableFreeSpace < required)
                throw new IOException("Insufficient space for an isolated native template and save backup.");

            Directory.CreateDirectory(destination);
            string world = Path.Combine(destination, "world.wld");
            // CreateNew semantics: never point native world loading/saving at the original source.
            File.Copy(source, world, overwrite: false);
            return TerrariaSchematicTemplateHost.Build(world, document, projection.Tiles, projection.Bounds, maximumBytes);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Native schematic template build failed: {ex.Message}");
            return 1;
        }
    }
}
