using System.Diagnostics;
using ReLogic.OS;
using Terraria.ID;

namespace Vega.Terraria;

/// <summary>
/// Owns the Terraria process bootstrap used by Vega.
/// </summary>
public static class TerrariaServerHost
{
    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        var bootstrapStarted = Stopwatch.GetTimestamp();
        var legacyHooksAttached = false;

        try
        {
            PrepareSavePath(args);
            InitialiseTerrariaInternals();

            // Temporary seam: only hook dispatch/deinitialization still comes from TSAPI.
            // The process bootstrap itself is now owned by Vega.Terraria.
            LegacyTsapiBridge.AttachHooks(args);
            legacyHooksAttached = true;

            Console.WriteLine(
                $"Vega Terraria bootstrap ready in {Stopwatch.GetElapsedTime(bootstrapStarted).TotalMilliseconds:F1} ms");

            if (args.Any(static arg => string.Equals(arg, "-skipassemblyload", StringComparison.OrdinalIgnoreCase)))
            {
                global::Terraria.Main.SkipAssemblyLoad = true;
            }

            global::Terraria.WindowsLaunch.Main(args);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Vega Terraria runtime failed:\n{ex}");
            return 1;
        }
        finally
        {
            if (legacyHooksAttached)
            {
                LegacyTsapiBridge.Shutdown();
            }

            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        }
    }

    private static void PrepareSavePath(string[] args)
    {
        global::Terraria.Program.LaunchParameters = global::Terraria.Utils.ParseArguements(args);
        global::Terraria.Program.SavePath = global::Terraria.Program.LaunchParameters.ContainsKey("-savedirectory")
            ? global::Terraria.Program.LaunchParameters["-savedirectory"]
            : Platform.Get<IPathService>().GetStoragePath("Terraria");
    }

    private static void InitialiseTerrariaInternals()
    {
        ItemID.Sets.Explosives = ItemID.Sets.Factory.CreateBoolSet(
        [
            ItemID.Bomb,
            ItemID.StickyBomb,
            ItemID.BouncyBomb,
            ItemID.BombFish,
            ItemID.DirtBomb,
            ItemID.DirtStickyBomb,
            ItemID.ScarabBomb,
            ItemID.SuperBomb,
            ItemID.SuperStickyBomb,
            ItemID.FreezeBomb,
            ItemID.GrenadeLauncher,
            ItemID.RocketLauncher,
            ItemID.SnowmanCannon,
            ItemID.Celeb2,
            ItemID.RocketII,
            ItemID.RocketIV,
            ItemID.ClusterRocketII,
            ItemID.MiniNukeII,
            ItemID.DryRocket,
            ItemID.WetRocket,
            ItemID.LavaRocket,
            ItemID.HoneyRocket,
            ItemID.Dynamite,
            ItemID.Explosives,
            ItemID.StickyDynamite
        ]);

        TileID.Sets.Corrupt[TileID.CorruptGrass] = true;
        TileID.Sets.Corrupt[TileID.CorruptPlants] = true;
        TileID.Sets.Corrupt[TileID.CorruptThorns] = true;
        TileID.Sets.Corrupt[TileID.CorruptIce] = true;
        TileID.Sets.Corrupt[TileID.CorruptHardenedSand] = true;
        TileID.Sets.Corrupt[TileID.CorruptSandstone] = true;
        TileID.Sets.Corrupt[TileID.Ebonstone] = true;
        TileID.Sets.Corrupt[TileID.Ebonsand] = true;

        TileID.Sets.Crimson[TileID.FleshBlock] = true;
        TileID.Sets.Crimson[TileID.CrimsonGrass] = true;
        TileID.Sets.Crimson[TileID.FleshIce] = true;
        TileID.Sets.Crimson[TileID.CrimsonPlants] = true;
        TileID.Sets.Crimson[TileID.Crimstone] = true;
        TileID.Sets.Crimson[TileID.Crimsand] = true;
        TileID.Sets.Crimson[TileID.CrimsonVines] = true;
        TileID.Sets.Crimson[TileID.CrimsonThorns] = true;
        TileID.Sets.Crimson[TileID.CrimsonHardenedSand] = true;
        TileID.Sets.Crimson[TileID.CrimsonSandstone] = true;

        TileID.Sets.Hallow[TileID.HallowedGrass] = true;
        TileID.Sets.Hallow[TileID.HallowedPlants] = true;
        TileID.Sets.Hallow[TileID.HallowedPlants2] = true;
        TileID.Sets.Hallow[TileID.HallowedVines] = true;
        TileID.Sets.Hallow[TileID.HallowedIce] = true;
        TileID.Sets.Hallow[TileID.HallowHardenedSand] = true;
        TileID.Sets.Hallow[TileID.HallowSandstone] = true;
        TileID.Sets.Hallow[TileID.Pearlsand] = true;
        TileID.Sets.Hallow[TileID.Pearlstone] = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        Console.Error.WriteLine($"Unhandled Vega Terraria runtime exception:\n{args.ExceptionObject}");
    }
}
