using ReLogic.OS;
using Terraria.ID;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Runtime;
using Vega.Terraria.Hooking;
using Vega.Terraria.Networking;
using Vega.Terraria.Worlds;

namespace Vega.Terraria;

/// <summary>
/// Owns the Terraria process bootstrap used by Vega.
/// </summary>
public static class TerrariaServerHost
{
    public static int Run(string[] args, TerrariaServerHostServices services)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(services);
        services.Validate();

        var world = services.World;
        var characters = services.Characters;
        var logger = services.Runtime.Logger;
        var lifecycle = services.Runtime.Lifecycle;
        var gameThreadDispatcher = services.Runtime.GameThread;
        var playerBoundary = services.Players.Boundary;

        void Report(RuntimeLifecycleTransition transition)
        {
            if (transition.Stage == RuntimeLifecycleStage.NetworkReady)
            {
                services.Runtime.Operations.ResetTickRateStatistics();
            }

            ReportLifecycleTransition(transition, logger);
        }
        void ReportUnhandled(object sender, UnhandledExceptionEventArgs eventArgs) =>
            logger.Log(
                OperationsLogLevel.Critical,
                "runtime",
                "Unhandled Vega Terraria runtime exception.",
                eventArgs.ExceptionObject as Exception);

        lifecycle.Transitioned += Report;
        lifecycle.Advance(RuntimeLifecycleStage.Bootstrap);

        AppDomain.CurrentDomain.UnhandledException += ReportUnhandled;
        using var nativeLifecycleHooks = new NativeLifecycleHooks(lifecycle);
        using var gameplayEvents = world.Events is null ? null : new NativeGameplayEventHooks(
            world.Id, world.Events, gameThreadDispatcher, logger, playerBoundary);
        using var networkMetricsHooks = new NativeNetworkMetricsHooks(services.Network.Metrics);
        ServerNetworkOptions networkLimits = ServerNetworkOptions.Normalize(services.Network.Options ?? new());
        using var networkBudgetHooks = new NativeNetworkBudgetHooks(networkLimits);
        int distantMovementRate = networkLimits.DistantMovementUpdatesPerSecond;
        using var movementInterest = distantMovementRate > 0
            ? new NativeMovementInterestHooks(distantMovementRate, () => gameThreadDispatcher.IsGameThread) : null;
        using var orderedTransport = networkLimits.UseOrderedTcp
            ? new NativeTcpTransportHost(new NativeTcpTransportOptions
            {
                MaxCallbacksPerPass = networkLimits.OrderedCallbacksPerPass,
                CompletionDrainBatch = networkLimits.OrderedCompletionDrainBatch,
                MeasureQueueAge = networkLimits.OrderedQueueDiagnostics,
                MaxBatchRequests = networkLimits.OrderedBatchRequests,
                DeferWriterWake = networkLimits.OrderedDeferredWake,
                ProducerBatching = networkLimits.OrderedProducerBatching,
                DeferredWakeFlushInterval = TimeSpan.FromMilliseconds(networkLimits.OrderedDeferredWakeMilliseconds)
            },
                onSendRejected: (generation, reason) => logger.Log(OperationsLogLevel.Warning, "network",
                    $"Ordered TCP connection {generation} closed: outbound queue rejected a send ({reason}).")) : null;
        using var queueDiagnostics = orderedTransport is not null && networkLimits.OrderedQueueDiagnostics
            ? new Timer(_ =>
            {
                var (slot, send, completion) = orderedTransport.ReadQueueAges();
                var (entries, bytes) = orderedTransport.Retained;
                logger.Log(OperationsLogLevel.Information, "ordered-tcp", FormattableString.Invariant(
                    $"queue entries={entries} bytes={bytes} oldest_send_ms={send.TotalMilliseconds:F2} send_slot={slot} oldest_completion_ms={completion.TotalMilliseconds:F2}"));
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)) : null;
        using var connectionLifecycleHooks = new NativeConnectionLifecycleHooks(services.Network.Connections);
        using var worldDecisionHooks = new NativeWorldDecisionHooks(world.HardmodeStarting);
        using var npcCombatAuthority = new NativeNpcCombatAuthorityHooks(services.Network.Metrics);
        Func<PlayerSnapshot, bool>? validatedReadiness = characters.GameplayReadiness is null ? null : player =>
            gameThreadDispatcher.IsGameThread && NativeGameplayReadinessHooks.IsFullyConnected(player.Id.Slot) &&
            playerBoundary.TryGetSessionPlayer(player, out _) && characters.GameplayReadiness(player);
        using var gameplayReadinessHooks = validatedReadiness is null ? null : new NativeGameplayReadinessHooks(
            playerBoundary, validatedReadiness, logger, characters.Authoritative);
        using var playerPacketSecurityHooks = new NativePlayerPacketSecurityHooks(
            services.Network.PacketPolicies,
            services.Network.Connections,
            logger,
            services.Network.Metrics,
            services.Network.SecuritySignals,
            services.Network.Admission,
            characters.JoinInventoryPolicy);
        using var consoleCommandHooks = new NativeConsoleCommandHooks(
            services.Commands.Dispatcher,
            services.Commands.Localization,
            services.Commands.Cultures,
            world.Id);
        using var playerCommandHooks = new NativePlayerCommandHooks(
            services.Commands.Dispatcher,
            services.Players.Authorization,
            playerBoundary,
            playerBoundary,
            logger,
            services.Commands.Localization,
            services.Commands.Cultures,
            playerBoundary,
            services.Players.Accounts,
            services.Commands.Chat,
            validatedReadiness);
        using var chestPolicyHooks = new NativeChestPolicyHooks(
            playerBoundary,
            world.ChestPolicy,
            gameThreadDispatcher,
            logger,
            world.Id);
        using var sceneNetworkHooks = new NativeSceneNetworkHooks(
            services.Scenes.Runtime,
            services.Scenes.Renderer,
            playerBoundary,
            world.Id,
            gameThreadDispatcher,
            logger);
        using var sceneVisibilityHooks = new NativeSceneVisibilityHooks(
            services.Scenes.Visibility,
            playerBoundary,
            world.Id);
        using var worldClockSynchronizer = new TerrariaWorldClockSynchronizer(world.Clock, logger, world.Id);

        using var sscLifecycleHooks = characters.EarlyJoin is null ? null : new NativeSscLifecycleHooks(
            characters.EarlyJoin, playerBoundary, services.Players.Accounts, gameThreadDispatcher, logger, characters.Authoritative);

        try
        {
            PrepareSavePath(args);
            InitialiseTerrariaInternals();
            global::Terraria.Main.ServerSideCharacter = characters.Authoritative;
            if (characters.Authoritative) logger.Log(OperationsLogLevel.Information, "ssc", "Native server-side character mode enabled.");

            nativeLifecycleHooks.Attach();
            gameThreadDispatcher.Attach();
            gameplayEvents?.Attach();
            playerBoundary.Attach();
            services.Runtime.Operations.Attach();
            networkBudgetHooks.Attach();
            connectionLifecycleHooks.Attach();
            sscLifecycleHooks?.Attach();
            if (orderedTransport is not null)
            {
                orderedTransport.Attach();
                logger.Log(OperationsLogLevel.Information, "network", "Experimental ordered TCP transport selected; native packet processing remains unchanged.");
                if (networkLimits.OrderedProducerBatching)
                    logger.Log(OperationsLogLevel.Information, "network", "Ordered producer-owned packet batching enabled with deferred wake.");
            }
            world.Images.Attach();
            world.Maintenance.Attach();
            worldClockSynchronizer.Attach();
            worldDecisionHooks.Attach();
            npcCombatAuthority.Attach();
            gameplayReadinessHooks?.Attach();
            playerPacketSecurityHooks.Attach();
            consoleCommandHooks.Attach();
            playerCommandHooks.Attach();
            chestPolicyHooks.Attach();
            sceneNetworkHooks.Attach();
            sceneVisibilityHooks.Attach();
            movementInterest?.Attach();
            networkMetricsHooks.Attach();

            lifecycle.Advance(RuntimeLifecycleStage.RuntimeReady);

            // The explicit composition root has already started and validated every Vega module.
            lifecycle.Advance(RuntimeLifecycleStage.ModulesRegistered);

            InitializeRuntimeParticipants(services.Runtime.StartupParticipants, logger);

            if (args.Any(static arg => string.Equals(arg, "-skipassemblyload", StringComparison.OrdinalIgnoreCase)))
            {
                global::Terraria.Main.SkipAssemblyLoad = true;
            }

            global::Terraria.WindowsLaunch.Main(args);
            return 0;
        }
        catch (Exception ex)
        {
            logger.Log(OperationsLogLevel.Critical, "runtime", "Vega Terraria runtime failed.", ex);
            return 1;
        }
        finally
        {
            sscLifecycleHooks?.Dispose();
            if (orderedTransport is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { orderedTransport.StopAsync(timeout.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { logger.Log(OperationsLogLevel.Warning, "network", "Ordered TCP transport shutdown exceeded its deadline."); }
                catch (Exception ex) { logger.Log(OperationsLogLevel.Warning, "network", "Ordered TCP transport shutdown reported an error.", ex); }
            }
            lifecycle.Transitioned -= Report;
            AppDomain.CurrentDomain.UnhandledException -= ReportUnhandled;
        }
    }

    private static void InitializeRuntimeParticipants(
        IReadOnlyList<IRuntimeStartupParticipant> participants,
        IOperationsLogger logger)
    {
        foreach (IRuntimeStartupParticipant participant in participants)
        {
            ArgumentNullException.ThrowIfNull(participant);
            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            participant.InitializeAsync().AsTask().GetAwaiter().GetResult();
            TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);
            logger.Log(
                OperationsLogLevel.Information,
                "startup",
                $"{participant.Name} ready in {elapsed.TotalMilliseconds:F1} ms.");
        }
    }

    private static void ReportLifecycleTransition(
        RuntimeLifecycleTransition transition,
        IOperationsLogger logger)
    {
        if (transition.Stage is RuntimeLifecycleStage.WorldLoading or RuntimeLifecycleStage.WorldReady)
        {
            return;
        }

        logger.Log(
            OperationsLogLevel.Information,
            "lifecycle",
            $"Vega {transition.Stage,-18} {transition.Elapsed.TotalMilliseconds,8:F1} ms " +
            $"(+{transition.StageDuration.TotalMilliseconds:F1} ms)");

        if (transition.Stage == RuntimeLifecycleStage.RuntimeReady &&
            transition.StageDuration > TimeSpan.FromMilliseconds(100))
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "lifecycle",
                $"Vega startup budget warning: bootstrap took {transition.StageDuration.TotalMilliseconds:F1} ms (> 100 ms).");
        }
    }

    // Must run before composition creates services that read Terraria.Main static fields.
    public static void PrepareSavePath(string[] args)
    {
        global::Terraria.Program.LaunchParameters = global::Terraria.Utils.ParseArguements(args);
        global::Terraria.Program.SavePath = global::Terraria.Program.LaunchParameters.ContainsKey("-savedirectory")
            ? global::Terraria.Program.LaunchParameters["-savedirectory"]
            : Platform.Get<IPathService>().GetStoragePath("Terraria");
    }

    internal static void InitialiseTerrariaInternals()
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

}
