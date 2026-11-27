using System.Reflection;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Core.Commands.Players;
using Vega.Core.Commands.System;
using Vega.Modules.Administration;
using Vega.Terraria.Players;
using Vega.Terraria.Threading;
using Vega.Terraria.Worlds;

namespace Vega.Runtime;

internal static class RuntimeCommandRegistration
{
    internal static IDisposable RegisterPlayer(CommandDispatcher commandDispatcher, TerrariaPlayerBoundary playerBoundary, string runtimeDataRoot) =>
        commandDispatcher.RegisterBatch([
            new PlatformCommand(playerBoundary),
            new PlatformCommand(playerBoundary, summary: true),
            new BroadcastCommand(playerBoundary, playerBoundary),
            new ItemCommand(playerBoundary, playerBoundary, giveOthers: true, resolvePrefix: TerrariaReferenceDataExporter.ResolvePrefixName),
            new ItemCommand(playerBoundary, playerBoundary, resolvePrefix: TerrariaReferenceDataExporter.ResolvePrefixName),
            new HealCommand(playerBoundary, playerBoundary),
            new BuffCommand(playerBoundary, playerBoundary),
            new BuffCommand(playerBoundary, playerBoundary, others: true),
            new KillCommand(playerBoundary, playerBoundary),
            new SlapCommand(playerBoundary, playerBoundary),
            new RespawnCommand(playerBoundary, playerBoundary),
            new SyncCommand(playerBoundary, playerBoundary),
            new AnnoyCommand(playerBoundary, playerBoundary),
            new FireworkCommand(playerBoundary, playerBoundary),
            new GodModeCommand(playerBoundary, playerBoundary),
            new PlayerDeathStatisticsCommand(playerBoundary, pvp: false, all: false),
            new PlayerDeathStatisticsCommand(playerBoundary, pvp: true, all: false),
            new PlayerDeathStatisticsCommand(playerBoundary, pvp: false, all: true),
            new PlayerDeathStatisticsCommand(playerBoundary, pvp: true, all: true),
            new ServerTextCommand("motd", Path.Combine(runtimeDataRoot, "motd.txt"), Path.Combine(AppContext.BaseDirectory, "config", "motd.txt")),
            new ServerTextCommand("rules", Path.Combine(runtimeDataRoot, "rules.txt"), Path.Combine(AppContext.BaseDirectory, "config", "rules.txt")),
            new ServerVersionCommand(
                Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
                typeof(global::Terraria.Main).Assembly.GetName().Version?.ToString() ?? "unknown"),
            new HelpCommand(commandDispatcher),
            new AliasesCommand(commandDispatcher),
        ]);

    internal static IDisposable RegisterWorld(CommandDispatcher commandDispatcher, TerrariaPlayerBoundary playerBoundary,
        TerrariaWorldEnvironmentService worldEnvironment, TerrariaWorldNpcService worldNpcs, TerrariaWorldEntityMaintenance worldEntities,
        TerrariaRuntimeGameThreadDispatcher gameThreadDispatcher, WorldId runtimeWorldId, string runtimeDataRoot)
    {
        var worldNativeActions = new TerrariaWorldNativeActions(gameThreadDispatcher, runtimeWorldId, playerBoundary);
        return commandDispatcher.RegisterBatch([
            new TimeCommand(worldEnvironment),
            new WindCommand(worldEnvironment),
            new WorldInfoCommand(worldEnvironment),
            new WorldEventCommand(worldEnvironment),
            new WorldModeCommand(worldEnvironment),
            new WorldSpawnSettingsCommand(worldEnvironment, rate: true),
            new WorldSpawnSettingsCommand(worldEnvironment, rate: false),
            new WorldPointCommand(worldEnvironment, playerBoundary, dungeon: false),
            new WorldPointCommand(worldEnvironment, playerBoundary, dungeon: true),
            new ForceSeasonCommand(worldEnvironment, halloween: true),
            new ForceSeasonCommand(worldEnvironment, halloween: false),
            new ClearAnglerCommand(worldEnvironment),
            new HardmodeCommand(worldEnvironment),
            new BossDamageCommand(new TerrariaBossDamageHistory(gameThreadDispatcher, runtimeWorldId)),
            new EvilCommand(worldNativeActions),
            new SettleCommand(worldNativeActions),
            new GrowCommand(worldNativeActions, playerBoundary),
            new ReferenceDataCommand(new TerrariaReferenceDataExporter(runtimeDataRoot, runtimeWorldId)),
            new SpawnMobCommand(playerBoundary, worldNpcs, worldNpcs),
            new SpawnMobCommand(playerBoundary, worldNpcs, worldNpcs, boss: true),
            new ButcherCommand(worldNpcs, worldNpcs),
            new RenameNpcCommand(worldNpcs, worldNpcs),
            new ClearCommand(worldEntities, worldNpcs, gameThreadDispatcher),
        ]);
    }
}
