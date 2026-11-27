using Multiplicity.Packets.Views;
using ReLogic.OS;
using Terraria.ID;
using Vega.Contracts.Authorization;
using Vega.Contracts.Accounts;
using Vega.Contracts.Commands;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;
using Vega.Contracts.Events;
using Vega.Contracts.Runtime;
using Vega.Contracts.Worlds;
using Vega.Contracts.Localization;
using Vega.Contracts.Moderation;
using Vega.Contracts.Messaging;
using Vega.Contracts.Scenes;
using Vega.Terraria.Hooking;
using Vega.Terraria.Operations;
using Vega.Terraria.Players;
using Vega.Terraria.Networking;
using Vega.Terraria.Threading;
using Vega.Terraria.Worlds;

namespace Vega.Terraria;

/// <summary>
/// Owns the Terraria process bootstrap used by Vega.
/// </summary>
public static class TerrariaServerHost
{
    public static int Run(
        string[] args,
        WorldId runtimeWorldId,
        IRuntimeLifecycleController lifecycle,
        IEventPipeline<WorldHardmodeStarting> hardmodeStarting,
        TerrariaRuntimeGameThreadDispatcher gameThreadDispatcher,
        TerrariaPlayerBoundary playerBoundary,
        TerrariaConnectionMetadataRegistry connectionMetadata,
        TerrariaOperationsState operationsState,
        INetworkMetricsRecorder networkMetrics,
        ISecuritySignalRecorder securitySignals,
        IOperationsLogger logger,
        TerrariaWorldImageService worldImages,
        TerrariaWorldMaintenanceService worldMaintenance,
        IWorldClockController worldClock,
        IReadOnlyList<IRuntimeStartupParticipant> startupParticipants,
        IPacketProcessor<PacketView> playerOwnershipSecurity,
        ICommandDispatcher commandDispatcher,
        IPlayerAuthorizationService playerAuthorization,
        IPlayerAccountSessionService accountSessions,
        IMuteService mutes,
        IChatPipeline chat,
        IChestInteractionPolicy chestPolicy,
        ISceneRuntime scenes,
        ISceneVisibilityRuntime visibility,
        ISceneRenderSink sceneRenderer,
        ILocalizationService localization,
        IPlayerCultureService cultures,
        ServerNetworkOptions? networkOptions = null,
        IConnectionAdmissionPolicy? admission = null,
        Func<Vega.Contracts.Players.PlayerSnapshot, bool>? primaryGameplayReadiness = null,
        TerrariaSandboxSscCharacterAdapter? earlyJoinCharacters = null,
        IPacketProcessor<PacketView>? joinInventoryPolicy = null,
        bool authoritativeCharacters = false,
        IWorldGameplayEvents? worldEvents = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(hardmodeStarting);
        ArgumentNullException.ThrowIfNull(gameThreadDispatcher);
        ArgumentNullException.ThrowIfNull(playerBoundary);
        ArgumentNullException.ThrowIfNull(connectionMetadata);
        ArgumentNullException.ThrowIfNull(operationsState);
        ArgumentNullException.ThrowIfNull(networkMetrics);
        ArgumentNullException.ThrowIfNull(securitySignals);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(worldImages);
        ArgumentNullException.ThrowIfNull(worldMaintenance);
        ArgumentNullException.ThrowIfNull(worldClock);
        ArgumentNullException.ThrowIfNull(startupParticipants);
        ArgumentNullException.ThrowIfNull(playerOwnershipSecurity);
        ArgumentNullException.ThrowIfNull(commandDispatcher);
        ArgumentNullException.ThrowIfNull(playerAuthorization);
        ArgumentNullException.ThrowIfNull(accountSessions);
        ArgumentNullException.ThrowIfNull(mutes);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(chestPolicy);
        ArgumentNullException.ThrowIfNull(scenes);
        ArgumentNullException.ThrowIfNull(visibility);
        ArgumentNullException.ThrowIfNull(sceneRenderer);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(cultures);

        void Report(RuntimeLifecycleTransition transition)
        {
            if (transition.Stage == RuntimeLifecycleStage.NetworkReady)
            {
                operationsState.ResetTickRateStatistics();
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
        using var gameplayEvents = worldEvents is null ? null : new NativeGameplayEventHooks(runtimeWorldId, worldEvents, gameThreadDispatcher, logger);
        using var networkMetricsHooks = new NativeNetworkMetricsHooks(networkMetrics);
        ServerNetworkOptions networkLimits = ServerNetworkOptions.Normalize(networkOptions ?? new());
        using var networkBudgetHooks = new NativeNetworkBudgetHooks(networkLimits);
        int distantMovementRate = networkLimits.DistantMovementUpdatesPerSecond;
        using var movementInterest = distantMovementRate > 0
            ? new NativeMovementInterestHooks(distantMovementRate, () => gameThreadDispatcher.IsGameThread) : null;
        using var orderedTransport = networkLimits.UseOrderedTcp
            ? new NativeTcpTransportHost(new NativeTcpTransportOptions
            {
                MaxCallbacksPerPass = networkLimits.OrderedCallbacksPerPass,
                MeasureQueueAge = networkLimits.OrderedQueueDiagnostics,
                MaxBatchRequests = networkLimits.OrderedBatchRequests
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
        using var connectionLifecycleHooks = new NativeConnectionLifecycleHooks(connectionMetadata);
        using var worldDecisionHooks = new NativeWorldDecisionHooks(hardmodeStarting);
        Func<Vega.Contracts.Players.PlayerSnapshot, bool>? validatedReadiness = primaryGameplayReadiness is null ? null : player =>
            gameThreadDispatcher.IsGameThread && NativeGameplayReadinessHooks.IsFullyConnected(player.Id.Slot) &&
            playerBoundary.TryGetSessionPlayer(player, out _) && primaryGameplayReadiness(player);
        if (authoritativeCharacters && (validatedReadiness is null || earlyJoinCharacters is null))
            throw new InvalidOperationException("Authoritative character mode requires native character lifecycle and readiness.");
        using var gameplayReadinessHooks = validatedReadiness is null ? null : new NativeGameplayReadinessHooks(playerBoundary, validatedReadiness, logger, authoritativeCharacters);
        using var playerPacketSecurityHooks = new NativePlayerPacketSecurityHooks(
            playerOwnershipSecurity,
            connectionMetadata,
            logger,
            networkMetrics,
            securitySignals,
            admission,
            joinInventoryPolicy);
        using var consoleCommandHooks = new NativeConsoleCommandHooks(
            commandDispatcher,
            localization,
            cultures,
            runtimeWorldId);
        using var playerCommandHooks = new NativePlayerCommandHooks(
            commandDispatcher,
            playerAuthorization,
            playerBoundary,
            playerBoundary,
            logger,
            localization,
            cultures,
            playerBoundary,
            accountSessions,
            chat,
            validatedReadiness);
        using var chestPolicyHooks = new NativeChestPolicyHooks(
            playerBoundary,
            chestPolicy,
            gameThreadDispatcher,
            logger,
            runtimeWorldId);
        using var sceneNetworkHooks = new NativeSceneNetworkHooks(
            scenes,
            sceneRenderer,
            playerBoundary,
            runtimeWorldId,
            gameThreadDispatcher);
        using var sceneVisibilityHooks = new NativeSceneVisibilityHooks(
            visibility,
            playerBoundary,
            runtimeWorldId);
        using var worldClockSynchronizer = new TerrariaWorldClockSynchronizer(worldClock, logger, runtimeWorldId);

        void OnBeforeWorldData(int slot, global::Terraria.Net.Sockets.ISocket socket)
        {
            earlyJoinCharacters!.ObserveCharacterReceived(slot, socket);
            if (authoritativeCharacters && !earlyJoinCharacters.PrepareAuthoritativeJoin(slot, socket))
                TerminatePendingCharacter(slot, socket);
        }

        void OnEnteringWorld(int slot, global::Terraria.Net.Sockets.ISocket socket)
        {
            try
            {
                if (!earlyJoinCharacters!.SynchronizePendingJoin(slot, socket)) TerminatePendingCharacter(slot, socket);
            }
            catch (Exception ex)
            {
                logger.Log(OperationsLogLevel.Error, "ssc", "Could not synchronize pending server character.", ex);
                TerminatePendingCharacter(slot, socket);
            }
        }

        void TerminatePendingCharacter(int slot, global::Terraria.Net.Sockets.ISocket socket)
        {
            if ((uint)slot >= (uint)global::Terraria.Netplay.Clients.Length ||
                global::Terraria.Netplay.Clients[slot] is not { } client || !ReferenceEquals(client.Socket, socket)) return;
            logger.Log(OperationsLogLevel.Warning, "ssc", "Pending server character initialization failed; connection is closing.");
            client.RequestTermination("Server character initialization failed.");
        }

        void OnBindingEnded(PlayerAccountBinding binding)
        {
            void Reset()
            {
                if (!playerBoundary.TryGet(binding.PlayerId, out var player) || player?.SessionId != binding.SessionId ||
                    accountSessions.TryGetBinding(player, out _) || !playerBoundary.TryGetSessionPlayer(player, out _)) return;
                if (!earlyJoinCharacters!.ResetPendingCharacter(player))
                    logger.Log(OperationsLogLevel.Warning, "ssc", "Could not clear the pending character after logout; gameplay remains gated.");
            }
            if (gameThreadDispatcher.IsGameThread) Reset();
            else if (!gameThreadDispatcher.TryPost("ssc.logout.reset", Reset))
                logger.Log(OperationsLogLevel.Warning, "ssc", "Character reset could not enter the game-thread queue; gameplay remains gated.");
        }

        try
        {
            PrepareSavePath(args);
            InitialiseTerrariaInternals();
            global::Terraria.Main.ServerSideCharacter = authoritativeCharacters;
            if (authoritativeCharacters) logger.Log(OperationsLogLevel.Information, "ssc", "Native server-side character mode enabled.");

            nativeLifecycleHooks.Attach();
            gameThreadDispatcher.Attach();
            gameplayEvents?.Attach();
            playerBoundary.Attach();
            operationsState.Attach();
            networkBudgetHooks.Attach();
            connectionLifecycleHooks.Attach();
            if (earlyJoinCharacters is not null)
            {
                TerrariaRuntime.Hooks.Netplay.ServerPlayerBeforeWorldData += OnBeforeWorldData;
                TerrariaRuntime.Hooks.Netplay.ServerConnectionClosing += earlyJoinCharacters.ForgetCharacterReceived;
                TerrariaRuntime.Hooks.Netplay.ServerConnectionClosed += earlyJoinCharacters.ForgetCharacterReceived;
            }
            if (authoritativeCharacters)
            {
                TerrariaRuntime.Hooks.Netplay.ServerPlayerEnteringWorld += OnEnteringWorld;
                accountSessions.BindingEnded += OnBindingEnded;
            }
            if (orderedTransport is not null)
            {
                orderedTransport.Attach();
                logger.Log(OperationsLogLevel.Information, "network", "Experimental ordered TCP transport selected; native packet processing remains unchanged.");
            }
            worldImages.Attach();
            worldMaintenance.Attach();
            worldClockSynchronizer.Attach();
            worldDecisionHooks.Attach();
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

            InitializeRuntimeParticipants(startupParticipants, logger);

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
            if (earlyJoinCharacters is not null)
            {
                TerrariaRuntime.Hooks.Netplay.ServerPlayerBeforeWorldData -= OnBeforeWorldData;
                TerrariaRuntime.Hooks.Netplay.ServerConnectionClosing -= earlyJoinCharacters.ForgetCharacterReceived;
                TerrariaRuntime.Hooks.Netplay.ServerConnectionClosed -= earlyJoinCharacters.ForgetCharacterReceived;
            }
            if (authoritativeCharacters)
            {
                TerrariaRuntime.Hooks.Netplay.ServerPlayerEnteringWorld -= OnEnteringWorld;
                accountSessions.BindingEnded -= OnBindingEnded;
            }
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
