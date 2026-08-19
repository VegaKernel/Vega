using ReLogic.OS;
using Terraria.ID;
using Vega.Contracts.Runtime;
using Vega.Terraria.Hooking;
using Vega.Terraria.Threading;

namespace Vega.Terraria;

/// <summary>
/// Owns the Terraria process bootstrap used by Vega.
/// </summary>
public static class TerrariaServerHost
{
    public static int Run(string[] args, long processStartedAt)
    {
        ArgumentNullException.ThrowIfNull(args);

        var lifecycle = new RuntimeLifecycleTracker(processStartedAt);
        lifecycle.Transitioned += ReportLifecycleTransition;
        lifecycle.Advance(RuntimeLifecycleStage.Bootstrap);

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        var legacyHooksAttached = false;

        using var nativeLifecycleHooks = new NativeLifecycleHooks(lifecycle);
        using var gameThreadDispatcher = new OtapiGameThreadDispatcher();

        try
        {
            PrepareSavePath(args);
            InitialiseTerrariaInternals();

            nativeLifecycleHooks.Attach();
            gameThreadDispatcher.Attach();

            // Temporary seam: legacy TShockAPI still receives its historical TSAPI events.
            // Vega readiness and game-thread scheduling no longer depend on that layer.
            LegacyTsapiBridge.AttachHooks(args);
            legacyHooksAttached = true;

            lifecycle.Advance(RuntimeLifecycleStage.RuntimeReady);

            // No Vega-native module loader exists yet, so registration is an empty bounded stage.
            lifecycle.Advance(RuntimeLifecycleStage.ModulesRegistered);

            if (args.Any(static arg => string.Equals(arg, "-skipassemblyload", StringComparison.OrdinalIgnoreCase)))
            {
                global::Terraria.Main.SkipAssemblyLoad = true;
            }

            lifecycle.Advance(RuntimeLifecycleStage.WorldLoading);
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

            lifecycle.Transitioned -= ReportLifecycleTransition;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        }
    }

    private static void ReportLifecycleTransition(RuntimeLifecycleTransition transition)
    {
        Console.WriteLine(
            $"Vega {transition.Stage,-18} {transition.Elapsed.TotalMilliseconds,8:F1} ms " +
            $"(+{transition.StageDuration.TotalMilliseconds:F1} ms)");

        if (transition.Stage == RuntimeLifecycleStage.RuntimeReady &&
            transition.StageDuration > TimeSpan.FromMilliseconds(100))
        {
            Console.Error.WriteLine(
                $"Vega startup budget warning: bootstrap took {transition.StageDuration.TotalMilliseconds:F1} ms (> 100 ms).");
        }

        if (transition.Stage == RuntimeLifecycleStage.WorldLoading &&
            transition.Elapsed > TimeSpan.FromMilliseconds(300))
        {
            Console.Error.WriteLine(
                $"Vega startup budget warning: pre-world overhead reached {transition.Elapsed.TotalMilliseconds:F1} ms (> 300 ms).");
        }

        if (transition.Stage == RuntimeLifecycleStage.WorldReady)
        {
            Console.WriteLine(
                $"Terraria world load       {transition.StageDuration.TotalMilliseconds,8:F1} ms");
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
        TileID.Sets.Crimstone] = true;
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
