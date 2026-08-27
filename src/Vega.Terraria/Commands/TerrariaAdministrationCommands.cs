using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Terraria.Players;
using Vega.Terraria.Threading;
using Vega.Terraria.Worlds;

namespace Vega.Terraria.Commands;

internal static class TerrariaAdministrationCommandRegistration
{
    public static IDisposable Register(
        ICommandDispatcher dispatcher,
        WorldId runtimeWorldId,
        TerrariaRuntimeGameThreadDispatcher gameThread,
        TerrariaPlayerBoundary players,
        IWorldBuildProtectionService buildProtection)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(gameThread);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(buildProtection);

        if (dispatcher is not ICommandRegistry registry)
        {
            throw new InvalidOperationException(
                "The Vega command dispatcher must also expose ICommandRegistry for built-in Terraria administration commands.");
        }

        IWorldEnvironmentService environment = new TerrariaWorldEnvironmentService(gameThread, runtimeWorldId);
        IWorldNpcService npcs = new TerrariaWorldNpcService(gameThread, runtimeWorldId);
        IWorldNpcSpawnControl npcSpawns = new TerrariaNpcSpawnControl(gameThread, runtimeWorldId);
        IWorldAnglerQuestService anglerQuests = new TerrariaAnglerQuestService(gameThread, runtimeWorldId);
        IPlayerRecallOperations recall = new TerrariaPlayerRecallService(gameThread);
        IPlayerTeleportConsentService teleportConsent = new RuntimePlayerTeleportConsentService();
        var wallOfFlesh = new TerrariaWallOfFleshSpawner(gameThread, runtimeWorldId);

        ICommandHandler[] handlers =
        [
            new BroadcastCommand(players, players),
            new WorldInfoCommand(environment),
            new WorldModeCommand(environment),
            new AntiBuildCommand(buildProtection, players, players),
            new TimeCommand(environment),
            new WeatherCommand(environment),
            new WindCommand(environment),
            new TeleportCommand(players, players, players, teleportConsent),
            new TeleportHereCommand(players, players, players),
            new TeleportPositionCommand(players, players, environment),
            new SpawnCommand(players, recall),
            new PositionCommand(players),
            new TeleportAllowCommand(players, teleportConsent),
            new NpcListCommand(npcs),
            new SpawnMobCommand(
                players,
                npcs,
                new TerrariaNpcCatalog(),
                npcSpawns,
                wallOfFlesh,
                players),
            new ButcherCommand(npcs, new TerrariaNpcCatalog(), players, players),
            new RenameNpcCommand(npcs, new TerrariaNpcCatalog(), players, players),
            new MaxSpawnsCommand(npcSpawns, players, players),
            new SpawnRateCommand(npcSpawns, players, players),
            new ClearAnglerCommand(anglerQuests)
        ];

        var registrations = handlers.Select(registry.Register).ToList();
        registrations.Add(TerrariaItemCommandRegistration.Register(dispatcher, gameThread, players));
        return new CommandRegistrationSet(registrations.ToArray());
    }

    private sealed class CommandRegistrationSet(IDisposable[] registrations) : IDisposable
    {
        private IDisposable[]? owned = registrations;

        public void Dispose()
        {
            IDisposable[]? snapshot = Interlocked.Exchange(ref owned, null);
            if (snapshot is null)
            {
                return;
            }

            for (int i = snapshot.Length - 1; i >= 0; i--)
            {
                snapshot[i].Dispose();
            }
        }
    }
}

internal sealed class BroadcastCommand(
    IPlayerDirectory players,
    IPlayerMessenger messenger) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "broadcast",
        ["bc", "say"],
        ["vega.admin.broadcast"],
        Syntax: "broadcast <message>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 0)
        {
            return AdminCommandResults.Usage("command.broadcast.usage", "Usage: broadcast <message>.");
        }

        string message = string.Join(" ", invocation.Arguments);
        int delivered = 0;
        int failed = 0;
        foreach (PlayerSnapshot player in players.SnapshotOnlinePlayers())
        {
            PlayerOperationResult result = await messenger
                .SendMessageAsync(player.Id, message, cancellationToken)
                .ConfigureAwait(false);
            if (result.IsSuccess) delivered++;
            else failed++;
        }

        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.broadcast.sent", delivered, failed)
                .WithDefaultText("Broadcast delivered to {0} player(s); {1} delivery failure(s)."));
    }
}

internal sealed class WorldModeCommand(IWorldEnvironmentService environment) : ICommandHandler
{
    private static readonly IReadOnlyDictionary<string, int> Modes =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["normal"] = 0,
            ["expert"] = 1,
            ["master"] = 2,
            ["journey"] = 3,
            ["creative"] = 3
        };

    private static readonly string[] CanonicalNames = ["normal", "expert", "master", "journey"];

    public CommandDescriptor Descriptor { get; } = new(
        "worldmode",
        ["gamemode"],
        ["vega.world.mode"],
        Syntax: "worldmode <normal|expert|master|journey|creative|0..3>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 0)
        {
            return Usage();
        }

        string value = invocation.Arguments[0];
        int mode;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
        {
            if (numeric < 0 || numeric > 3)
            {
                return Invalid();
            }

            mode = numeric;
        }
        else if (!Modes.TryGetValue(value, out mode))
        {
            return Invalid();
        }

        WorldEnvironmentMutationResult result = await environment.PatchAsync(
            new WorldEnvironmentMutationRequest(
                invocation.WorldId,
                new WorldEnvironmentPatch(GameMode: mode),
                invocation.Principal.Name),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return AdminCommandResults.Failure(
                result.ErrorCode ?? "command.worldmode.failed",
                "World mode could not be changed.");
        }

        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.worldmode.changed", CanonicalNames[mode])
                .WithDefaultText("World mode set to {0}."));
    }

    private static CommandExecutionResult Usage() => AdminCommandResults.Usage(
        "command.worldmode.usage",
        "Usage: worldmode <mode>. Valid modes: normal, expert, master, journey, creative.");

    private static CommandExecutionResult Invalid() => AdminCommandResults.Failure(
        "command.worldmode.invalid",
        "Invalid world mode. Valid modes: normal, expert, master, journey, creative.");
}

internal sealed class AntiBuildCommand(
    IWorldBuildProtectionService protection,
    IPlayerDirectory players,
    IPlayerMessenger messenger) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "antibuild",
        [],
        ["vega.world.antibuild"],
        Syntax: "antibuild");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        bool enabled = protection.Toggle();
        string text = enabled ? "Anti-build is now on." : "Anti-build is now off.";
        foreach (PlayerSnapshot player in players.SnapshotOnlinePlayers())
        {
            if (invocation.Principal is IPlayerCommandPrincipal caller && caller.PlayerId == player.Id)
            {
                continue;
            }

            await messenger.SendMessageAsync(player.Id, text, cancellationToken).ConfigureAwait(false);
        }

        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.antibuild.changed", enabled)
                .WithDefaultText(text));
    }
}

internal sealed class WorldInfoCommand(IWorldEnvironmentService environment) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "worldinfo",
        ["world"],
        ["vega.world.info"],
        Syntax: "worldinfo");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 0)
        {
            return AdminCommandResults.Usage("command.worldinfo.usage", "Usage: worldinfo.");
        }

        WorldEnvironmentSnapshot? snapshot = await environment
            .CaptureAsync(invocation.WorldId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return AdminCommandResults.Failure("command.worldinfo.unavailable", "World state is unavailable.");
        }

        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Info("command.worldinfo.summary")
                .WithDefaultText(
                    $"World {snapshot.WorldId}: {snapshot.WidthTiles}x{snapshot.HeightTiles} tiles, " +
                    $"spawn=({snapshot.SpawnTileX},{snapshot.SpawnTileY}), " +
                    $"{(snapshot.IsDaytime ? "day" : "night")} time={snapshot.TimeTicks:F0}, moon={snapshot.MoonPhase}, " +
                    $"hardmode={snapshot.Hardmode}, bloodMoon={snapshot.BloodMoon}, eclipse={snapshot.Eclipse}, " +
                    $"rain={snapshot.Raining} ({snapshot.RainIntensity:F2}), wind={snapshot.WindSpeed:F2}."));
    }
}

internal sealed class TimeCommand(IWorldEnvironmentService environment) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "time",
        [],
        ["vega.world.time"],
        Syntax: "time [day|night|dawn|noon|dusk|midnight|<ticks>]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 0)
        {
            WorldEnvironmentSnapshot? current = await environment
                .CaptureAsync(invocation.WorldId, cancellationToken)
                .ConfigureAwait(false);
            return current is null
                ? AdminCommandResults.Failure("command.time.unavailable", "World time is unavailable.")
                : CommandExecutionResult.WithMessages(
                    MessageDescriptor.Info("command.time.current")
                        .WithDefaultText($"{(current.IsDaytime ? "Day" : "Night")}, time={current.TimeTicks:F0}."));
        }

        if (invocation.Arguments.Count != 1 || !TryParse(invocation.Arguments[0], out WorldEnvironmentPatch patch))
        {
            return AdminCommandResults.Usage(
                "command.time.usage",
                "Usage: time [day|night|dawn|noon|dusk|midnight|<ticks>].");
        }

        WorldEnvironmentMutationResult result = await environment
            .PatchAsync(new WorldEnvironmentMutationRequest(invocation.WorldId, patch, invocation.Principal.Name), cancellationToken)
            .ConfigureAwait(false);
        return AdminCommandResults.WorldMutation(result, "command.time.changed", "World time changed.");
    }

    private static bool TryParse(string value, out WorldEnvironmentPatch patch)
    {
        switch (value.ToLowerInvariant())
        {
            case "day":
            case "dawn":
                patch = new WorldEnvironmentPatch(IsDaytime: true, TimeTicks: 0);
                return true;
            case "noon":
                patch = new WorldEnvironmentPatch(IsDaytime: true, TimeTicks: 27000);
                return true;
            case "night":
            case "dusk":
                patch = new WorldEnvironmentPatch(IsDaytime: false, TimeTicks: 0);
                return true;
            case "midnight":
                patch = new WorldEnvironmentPatch(IsDaytime: false, TimeTicks: 16200);
                return true;
            default:
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ticks) &&
                    double.IsFinite(ticks) && ticks >= 0 && ticks <= 54000)
                {
                    patch = new WorldEnvironmentPatch(TimeTicks: ticks);
                    return true;
                }

                patch = default!;
                return false;
        }
    }
}

internal sealed class WeatherCommand(IWorldEnvironmentService environment) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "weather",
        ["rain"],
        ["vega.world.weather"],
        Syntax: "weather [clear|rain [0..1]]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 0)
        {
            WorldEnvironmentSnapshot? current = await environment
                .CaptureAsync(invocation.WorldId, cancellationToken)
                .ConfigureAwait(false);
            return current is null
                ? AdminCommandResults.Failure("command.weather.unavailable", "Weather state is unavailable.")
                : CommandExecutionResult.WithMessages(
                    MessageDescriptor.Info("command.weather.current")
                        .WithDefaultText($"Rain={current.Raining}, intensity={current.RainIntensity:F2}."));
        }

        WorldEnvironmentPatch patch;
        if (invocation.Arguments.Count == 1 &&
            string.Equals(invocation.Arguments[0], "clear", StringComparison.OrdinalIgnoreCase))
        {
            patch = new WorldEnvironmentPatch(Raining: false, RainIntensity: 0);
        }
        else if (invocation.Arguments.Count is 1 or 2 &&
                 string.Equals(invocation.Arguments[0], "rain", StringComparison.OrdinalIgnoreCase))
        {
            float intensity = 1f;
            if (invocation.Arguments.Count == 2 &&
                (!float.TryParse(invocation.Arguments[1], NumberStyles.Float, CultureInfo.InvariantCulture, out intensity) ||
                 !float.IsFinite(intensity) || intensity is < 0 or > 1))
            {
                return Usage();
            }
            patch = new WorldEnvironmentPatch(Raining: true, RainIntensity: intensity);
        }
        else
        {
            return Usage();
        }

        WorldEnvironmentMutationResult result = await environment
            .PatchAsync(new WorldEnvironmentMutationRequest(invocation.WorldId, patch, invocation.Principal.Name), cancellationToken)
            .ConfigureAwait(false);
        return AdminCommandResults.WorldMutation(result, "command.weather.changed", "Weather changed.");
    }

    private static CommandExecutionResult Usage() => AdminCommandResults.Usage(
        "command.weather.usage",
        "Usage: weather [clear|rain [0..1]].");
}

internal sealed class WindCommand(IWorldEnvironmentService environment) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "wind",
        [],
        ["vega.world.weather"],
        Syntax: "wind [speed:-1..1]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 0)
        {
            WorldEnvironmentSnapshot? current = await environment
                .CaptureAsync(invocation.WorldId, cancellationToken)
                .ConfigureAwait(false);
            return current is null
                ? AdminCommandResults.Failure("command.wind.unavailable", "Wind state is unavailable.")
                : CommandExecutionResult.WithMessages(
                    MessageDescriptor.Info("command.wind.current")
                        .WithDefaultText($"Wind speed={current.WindSpeed:F2}."));
        }

        if (invocation.Arguments.Count != 1 ||
            !float.TryParse(invocation.Arguments[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float speed) ||
            !float.IsFinite(speed) || speed is < -1 or > 1)
        {
            return AdminCommandResults.Usage("command.wind.usage", "Usage: wind [speed:-1..1].");
        }

        WorldEnvironmentMutationResult result = await environment
            .PatchAsync(
                new WorldEnvironmentMutationRequest(
                    invocation.WorldId,
                    new WorldEnvironmentPatch(WindSpeed: speed),
                    invocation.Principal.Name),
                cancellationToken)
            .ConfigureAwait(false);
        return AdminCommandResults.WorldMutation(result, "command.wind.changed", "Wind speed changed.");
    }
}

internal sealed class TeleportCommand(
    IPlayerDirectory players,
    IPlayerOperations operations,
    IPlayerMessenger messenger,
    IPlayerTeleportConsentService? consent = null) : ICommandHandler
{
    private readonly IPlayerTeleportConsentService teleportConsent = consent ?? AllowAllTeleportConsent.Instance;
    public CommandDescriptor Descriptor { get; } = new(
        "tp",
        [],
        ["vega.players.teleport"],
        AllowServer: false,
        Syntax: "tp <target> | tp <player|*> <target>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count is < 1 or > 2)
        {
            return AdminCommandResults.Usage(
                "command.tp.usage",
                invocation.Principal.HasPermission("vega.players.teleport.others")
                    ? "Usage: tp <target> | tp <player|*> <target>."
                    : "Usage: tp <target>.");
        }

        if (invocation.Arguments.Count == 1)
        {
            if (!PlayerResolver.TryCaller(players, invocation, out PlayerSnapshot? caller))
            {
                return AdminCommandResults.Failure(
                    "command.tp.player-unavailable",
                    "This command requires an active player.");
            }

            if (!PlayerResolver.TryResolve(players, invocation.Arguments[0], out PlayerSnapshot? target, out string? error))
            {
                return AdminCommandResults.Failure("command.tp.invalid-target", error!);
            }

            if (!CanReceive(target!, invocation.Principal))
            {
                return IncomingDisabled(target!.Name);
            }

            PlayerOperationResult selfResult = await operations
                .TeleportAsync(caller!.Id, target!.Position, cancellationToken)
                .ConfigureAwait(false);
            if (!selfResult.IsSuccess)
            {
                return AdminCommandResults.PlayerMutation(
                    selfResult,
                    "command.tp.applied",
                    $"Teleported to {target.Name}.");
            }

            if (!invocation.Principal.HasPermission("vega.players.teleport.silent") && caller.Id != target.Id)
            {
                await messenger.SendMessageAsync(
                    target.Id,
                    $"{caller.Name} teleported to you.",
                    cancellationToken).ConfigureAwait(false);
            }

            return CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.tp.applied")
                    .WithDefaultText($"Teleported to {target.Name}."));
        }

        if (!invocation.Principal.HasPermission("vega.players.teleport.others"))
        {
            return AdminCommandResults.Failure(
                "command.tp.others-forbidden",
                "You do not have permission to teleport other players.");
        }

        if (!PlayerResolver.TryResolve(players, invocation.Arguments[1], out PlayerSnapshot? destination, out string? targetError))
        {
            return AdminCommandResults.Failure("command.tp.invalid-target", targetError!);
        }

        if (!CanReceive(destination!, invocation.Principal))
        {
            return IncomingDisabled(destination!.Name);
        }

        bool silent = invocation.Principal.HasPermission("vega.players.teleport.silent");
        if (invocation.Arguments[0] == "*")
        {
            if (!invocation.Principal.HasPermission("vega.players.teleport.all"))
            {
                return AdminCommandResults.Failure(
                    "command.tp.all-forbidden",
                    "You do not have permission to teleport all players.");
            }

            PlayerId? actorId = (invocation.Principal as IPlayerCommandPrincipal)?.PlayerId;
            foreach (PlayerSnapshot source in players.SnapshotOnlinePlayers().Where(player => player.Id != actorId))
            {
                PlayerOperationResult result = await operations
                    .TeleportAsync(source.Id, destination!.Position, cancellationToken)
                    .ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    return AdminCommandResults.PlayerMutation(
                        result,
                        "command.tp.applied",
                        $"Teleported everyone to {destination.Name}.");
                }

                await NotifyTeleportAsync(invocation, source, destination!, silent, cancellationToken)
                    .ConfigureAwait(false);
            }

            return CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.tp.applied")
                    .WithDefaultText($"Teleported everyone to {destination!.Name}."));
        }

        if (!PlayerResolver.TryResolve(players, invocation.Arguments[0], out PlayerSnapshot? sourcePlayer, out string? sourceError))
        {
            return AdminCommandResults.Failure("command.tp.invalid-player", sourceError!);
        }

        if (!CanReceive(sourcePlayer!, invocation.Principal))
        {
            return IncomingDisabled(sourcePlayer!.Name);
        }

        PlayerOperationResult operation = await operations
            .TeleportAsync(sourcePlayer!.Id, destination!.Position, cancellationToken)
            .ConfigureAwait(false);
        if (!operation.IsSuccess)
        {
            return AdminCommandResults.PlayerMutation(
                operation,
                "command.tp.applied",
                $"Teleported {sourcePlayer.Name} to {destination.Name}.");
        }

        await NotifyTeleportAsync(invocation, sourcePlayer, destination, silent, cancellationToken)
            .ConfigureAwait(false);
        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.tp.applied")
                .WithDefaultText($"Teleported {sourcePlayer.Name} to {destination.Name}."));
    }

    private async ValueTask NotifyTeleportAsync(
        CommandInvocation invocation,
        PlayerSnapshot source,
        PlayerSnapshot destination,
        bool silent,
        CancellationToken cancellationToken)
    {
        PlayerId? actorId = (invocation.Principal as IPlayerCommandPrincipal)?.PlayerId;
        if (source.Id != actorId)
        {
            await messenger.SendMessageAsync(
                source.Id,
                silent
                    ? $"You were teleported to {destination.Name}."
                    : $"{invocation.Principal.Name} teleported you to {destination.Name}.",
                cancellationToken).ConfigureAwait(false);
        }

        if (destination.Id != actorId)
        {
            await messenger.SendMessageAsync(
                destination.Id,
                silent
                    ? $"{source.Name} was teleported to you."
                    : $"{invocation.Principal.Name} teleported {source.Name} to you.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private bool CanReceive(PlayerSnapshot player, ICommandPrincipal principal) =>
        teleportConsent.AllowsIncoming(player) ||
        principal.HasPermission("vega.players.teleport.override");

    private static CommandExecutionResult IncomingDisabled(string playerName) =>
        AdminCommandResults.Failure(
            "command.tp.incoming-disabled",
            $"{playerName} has disabled incoming teleports.");

    private sealed class AllowAllTeleportConsent : IPlayerTeleportConsentService
    {
        public static AllowAllTeleportConsent Instance { get; } = new();
        public bool AllowsIncoming(PlayerSnapshot player) => true;
        public bool Toggle(PlayerSnapshot player) => true;
    }
}

internal sealed class TeleportHereCommand(
    IPlayerDirectory players,
    IPlayerOperations operations,
    IPlayerMessenger messenger) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "tphere",
        [],
        ["vega.players.teleport.others"],
        AllowServer: false,
        Syntax: "tphere <player|*>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count < 1 ||
            !PlayerResolver.TryCaller(players, invocation, out PlayerSnapshot? caller))
        {
            return AdminCommandResults.Usage(
                "command.tphere.usage",
                invocation.Principal.HasPermission("vega.players.teleport.all")
                    ? "Usage: tphere <player|*>."
                    : "Usage: tphere <player>.");
        }

        string search = string.Join(" ", invocation.Arguments);
        if (search == "*")
        {
            if (!invocation.Principal.HasPermission("vega.players.teleport.all"))
            {
                return AdminCommandResults.Failure(
                    "command.tphere.all-forbidden",
                    "You do not have permission to teleport all other players.");
            }

            foreach (PlayerSnapshot player in players.SnapshotOnlinePlayers().Where(player => player.Id != caller!.Id))
            {
                PlayerOperationResult multiResult = await operations
                    .TeleportAsync(player.Id, caller!.Position, cancellationToken)
                    .ConfigureAwait(false);
                if (!multiResult.IsSuccess)
                {
                    return AdminCommandResults.PlayerMutation(
                        multiResult,
                        "command.tphere.applied",
                        "Teleported everyone to yourself.");
                }

                await messenger.SendMessageAsync(
                    player.Id,
                    $"You were teleported to {caller.Name}.",
                    cancellationToken).ConfigureAwait(false);
            }

            return CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.tphere.applied")
                    .WithDefaultText("Teleported everyone to yourself."));
        }

        if (!PlayerResolver.TryResolve(players, search, out PlayerSnapshot? target, out string? error))
        {
            return AdminCommandResults.Failure("command.tphere.invalid-target", error!);
        }

        PlayerOperationResult result = await operations
            .TeleportAsync(target!.Id, caller!.Position, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return AdminCommandResults.PlayerMutation(
                result,
                "command.tphere.applied",
                $"Teleported {target.Name} to yourself.");
        }

        await messenger.SendMessageAsync(
            target.Id,
            $"You were teleported to {caller.Name}.",
            cancellationToken).ConfigureAwait(false);
        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.tphere.applied")
                .WithDefaultText($"Teleported {target.Name} to yourself."));
    }
}

internal sealed class TeleportPositionCommand(
    IPlayerDirectory players,
    IPlayerOperations operations,
    IWorldEnvironmentService environment) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "tppos",
        [],
        ["vega.players.teleport.position"],
        AllowServer: false,
        Syntax: "tppos <tile-x> <tile-y>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 2 ||
            !PlayerResolver.TryCaller(players, invocation, out PlayerSnapshot? player))
        {
            return AdminCommandResults.Usage("command.tppos.usage", "Usage: tppos <tile-x> <tile-y>.");
        }

        if (!int.TryParse(invocation.Arguments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tileX) ||
            !int.TryParse(invocation.Arguments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tileY))
        {
            return AdminCommandResults.Failure(
                "command.tppos.invalid-coordinates",
                "The destination coordinates do not look like valid integers.");
        }

        WorldEnvironmentSnapshot? world = await environment.CaptureAsync(invocation.WorldId, cancellationToken).ConfigureAwait(false);
        if (world is null)
        {
            return AdminCommandResults.Failure("command.tppos.world-unavailable", "World dimensions are unavailable.");
        }

        tileX = Math.Clamp(tileX, 0, world.WidthTiles - 1);
        tileY = Math.Clamp(tileY, 0, world.HeightTiles - 1);
        var destination = new PlayerPosition(tileX * 16f, tileY * 16f);
        PlayerOperationResult result = await operations.TeleportAsync(player!.Id, destination, cancellationToken).ConfigureAwait(false);
        return AdminCommandResults.PlayerMutation(
            result,
            "command.tppos.applied",
            $"Teleported to {tileX}, {tileY}.");
    }
}

internal sealed class SpawnCommand(
    IPlayerDirectory players,
    IPlayerRecallOperations recall) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "spawn",
        [],
        ["vega.players.spawn"],
        AllowServer: false,
        Syntax: "spawn");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Principal is not IPlayerCommandPrincipal principal ||
            !players.TryGet(principal.PlayerId, out PlayerSnapshot? player) ||
            player is null)
        {
            return AdminCommandResults.Failure(
                "command.spawn.player-unavailable",
                "This command requires an active player.");
        }

        PlayerOperationResult result = await recall
            .RecallWorldSpawnAsync(player.Id, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.spawn.applied")
                    .WithDefaultText("Teleported to the map's spawn point."))
            : AdminCommandResults.Failure(
                result.ErrorCode ?? "command.spawn.failed",
                "Could not teleport you to the map's spawn point.");
    }
}

internal sealed class PositionCommand(IPlayerDirectory players) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "pos",
        [],
        ["vega.players.position.get"],
        AllowServer: false,
        Syntax: "pos [player]");

    public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        PlayerSnapshot? player;
        if (invocation.Arguments.Count == 0 && PlayerResolver.TryCaller(players, invocation, out player))
        {
        }
        else if (invocation.Arguments.Count > 0 &&
                 PlayerResolver.TryResolve(
                     players,
                     string.Join(" ", invocation.Arguments),
                     out player,
                     out string? error))
        {
        }
        else
        {
            return ValueTask.FromResult(AdminCommandResults.Usage("command.pos.usage", "Usage: pos [player]."));
        }

        int tileX = (int)(player!.Position.X / 16f);
        int tileY = (int)(player.Position.Y / 16f);
        return ValueTask.FromResult(CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.pos.current")
                .WithDefaultText(
                    $"Location of {player.Name} is ({tileX}, {tileY}).")));
    }
}

internal sealed class NpcListCommand(IWorldNpcService npcs) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "npcs",
        ["npclist"],
        ["vega.npc.inspect"],
        Syntax: "npcs [limit]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        int limit = 20;
        if (invocation.Arguments.Count > 1 ||
            (invocation.Arguments.Count == 1 &&
             (!int.TryParse(invocation.Arguments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 200)))
        {
            return AdminCommandResults.Usage("command.npcs.usage", "Usage: npcs [limit:1..200].");
        }

        IReadOnlyList<NpcSnapshot> snapshot = await npcs.SnapshotAsync(invocation.WorldId, cancellationToken).ConfigureAwait(false);
        NpcSnapshot[] visible = snapshot.Take(limit).ToArray();
        if (visible.Length == 0)
        {
            return CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.npcs.empty").WithDefaultText("There are no active NPCs."));
        }

        return CommandExecutionResult.WithMessages(visible.Select(npc =>
            MessageDescriptor.Info("command.npcs.entry")
                .WithDefaultText(
                    $"#{npc.Handle.Slot} rev={npc.Handle.Revision} type={npc.NetId} {npc.Name} " +
                    $"life={npc.Life}/{npc.LifeMax} pos=({npc.Position.X:F0},{npc.Position.Y:F0}) boss={npc.Boss} town={npc.TownNpc}"))
            .ToArray());
    }
}

internal sealed class SpawnMobCommand(
    IPlayerDirectory players,
    IWorldNpcService npcs,
    INpcCatalog catalog,
    IWorldNpcSpawnControl spawnControl,
    IWallOfFleshSpawner wallOfFlesh,
    IPlayerMessenger messenger) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "spawnmob",
        ["sm"],
        ["vega.npc.spawn"],
        AllowServer: false,
        Syntax: "spawnmob <npc-name|id> [amount]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count is < 1 or > 2 ||
            string.IsNullOrWhiteSpace(invocation.Arguments[0]) ||
            invocation.Principal is not IPlayerCommandPrincipal principal ||
            !players.TryGet(principal.PlayerId, out PlayerSnapshot? caller) ||
            caller is null)
        {
            return Usage();
        }

        int requestedAmount = 1;
        if (invocation.Arguments.Count == 2 &&
            (!int.TryParse(invocation.Arguments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out requestedAmount) ||
             requestedAmount <= 0))
        {
            return Usage();
        }

        NpcResolutionResult resolution = catalog.Resolve(invocation.Arguments[0]);
        if (resolution.Status == NpcResolutionStatus.Ambiguous)
        {
            return AdminCommandResults.Failure(
                "command.spawnmob.npc-ambiguous",
                $"NPC is ambiguous: {string.Join(", ", resolution.Candidates.Select(static npc => $"{npc.Name} ({npc.NetId})"))}.");
        }

        if (resolution.Status == NpcResolutionStatus.NotFound || resolution.Npc is null)
        {
            return AdminCommandResults.Failure(
                "command.spawnmob.npc-not-found",
                $"'{invocation.Arguments[0]}' is not a valid NPC.");
        }

        NpcDefinition definition = resolution.Npc;
        if (definition.NetId <= 0 || definition.NetId >= global::Terraria.ID.NPCID.Count)
        {
            return AdminCommandResults.Failure("command.spawnmob.invalid-npc", "The selected NPC cannot be spawned.");
        }

        if (definition.NetId == global::Terraria.ID.NPCID.WallofFlesh)
        {
            if (requestedAmount != 1)
            {
                return AdminCommandResults.Failure(
                    "command.spawnmob.wof-amount",
                    "Wall of Flesh can only be spawned once at a time.");
            }

            PlayerOperationResult result = await wallOfFlesh
                .SpawnAsync(invocation.WorldId, caller.Position, cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return AdminCommandResults.Failure(
                    result.ErrorCode ?? "command.spawnmob.wof-failed",
                    "Unable to spawn a Wall of Flesh based on its current state or your current location.");
            }

            return await CompleteAsync(invocation, definition.Name, 1, cancellationToken).ConfigureAwait(false);
        }

        NpcSpawnLimitSnapshot? limits = await spawnControl
            .CaptureAsync(invocation.WorldId, cancellationToken)
            .ConfigureAwait(false);
        if (limits is null)
        {
            return AdminCommandResults.Failure(
                "command.spawnmob.capacity-unavailable",
                "The runtime NPC capacity is unavailable.");
        }

        int amount = Math.Min(requestedAmount, limits.Capacity);

        int spawned = 0;
        string? lastError = null;
        for (int i = 0; i < amount; i++)
        {
            NpcOperationResult result = await npcs.SpawnAsync(
                new NpcSpawnRequest(
                    invocation.WorldId,
                    definition.NetId,
                    caller.Position,
                    invocation.Principal.Name,
                    caller.Id.Slot),
                cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                lastError = result.ErrorCode ?? result.Status.ToString();
                break;
            }
            spawned++;
        }

        if (spawned != amount)
        {
            return AdminCommandResults.Failure(
                "command.spawnmob.failed",
                $"Spawned {spawned}/{amount} NPC(s); last failure: {lastError ?? "unknown"}.");
        }

        return await CompleteAsync(invocation, definition.Name, spawned, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CommandExecutionResult> CompleteAsync(
        CommandInvocation invocation,
        string npcName,
        int amount,
        CancellationToken cancellationToken)
    {
        string action = $"spawned {npcName} {amount} time{(amount == 1 ? string.Empty : "s")}.";
        string message = $"{invocation.Principal.Name} {action}";
        if (!invocation.Silent)
        {
            await NpcCommandBroadcast.SendToOthersAsync(
                players,
                messenger,
                invocation,
                message,
                cancellationToken).ConfigureAwait(false);
        }

        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.spawnmob.applied")
                .WithDefaultText(invocation.Silent ? $"You {action}" : message));
    }

    private static CommandExecutionResult Usage() => AdminCommandResults.Usage(
        "command.spawnmob.usage",
        "Usage: spawnmob <npc-name|id> [positive amount]. Quote multi-word NPC names.");
}

internal sealed class TeleportAllowCommand(
    IPlayerDirectory players,
    IPlayerTeleportConsentService consent) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "tpallow",
        [],
        ["vega.players.teleport.allow"],
        AllowServer: false,
        Syntax: "tpallow");

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!PlayerResolver.TryCaller(players, invocation, out PlayerSnapshot? player))
        {
            return ValueTask.FromResult(AdminCommandResults.Failure(
                "command.tpallow.player-unavailable",
                "This command requires an active player."));
        }

        bool allowed = consent.Toggle(player!);
        return ValueTask.FromResult(CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.tpallow.changed")
                .WithDefaultText(allowed
                    ? "Incoming teleports are now allowed."
                    : "Incoming teleports are now disabled.")));
    }
}

internal sealed class ButcherCommand(
    IWorldNpcService npcs,
    INpcCatalog catalog,
    IPlayerDirectory players,
    IPlayerMessenger messenger) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "butcher",
        [],
        ["vega.npc.butcher"],
        Syntax: "butcher [npc-name|id]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count > 1)
        {
            return AdminCommandResults.Usage("command.butcher.usage", "Usage: butcher [npc-name|id].");
        }

        IReadOnlyList<NpcSnapshot> snapshot = await npcs.SnapshotAsync(invocation.WorldId, cancellationToken).ConfigureAwait(false);
        IEnumerable<NpcSnapshot> selected;
        if (invocation.Arguments.Count == 0)
        {
            selected = snapshot.Where(static npc =>
                !npc.TownNpc && npc.NetId != global::Terraria.ID.NPCID.TargetDummy);
        }
        else
        {
            NpcResolutionResult resolution = catalog.Resolve(invocation.Arguments[0]);
            if (resolution.Status == NpcResolutionStatus.NotFound)
            {
                return AdminCommandResults.Failure(
                    "command.butcher.npc-not-found",
                    $"'{invocation.Arguments[0]}' is not a valid NPC.");
            }

            if (resolution.Status == NpcResolutionStatus.Ambiguous)
            {
                return AdminCommandResults.Failure(
                    "command.butcher.npc-ambiguous",
                    $"NPC is ambiguous: {string.Join(", ", resolution.Candidates.Select(static npc => $"{npc.Name} ({npc.NetId})"))}.");
            }

            int netId = resolution.Npc!.NetId;
            selected = netId == 0
                ? snapshot.Where(static npc =>
                    !npc.TownNpc && npc.NetId != global::Terraria.ID.NPCID.TargetDummy)
                : snapshot.Where(npc => npc.NetId == netId);
        }

        int killed = 0;
        int conflicts = 0;
        foreach (NpcSnapshot npc in selected.ToArray())
        {
            NpcOperationResult result = await npcs
                .KillAsync(new NpcKillRequest(npc.Handle, invocation.Principal.Name), cancellationToken)
                .ConfigureAwait(false);
            if (result.IsSuccess) killed++;
            else conflicts++;
        }

        string message = $"{invocation.Principal.Name} butchered {killed} NPC(s).";
        if (!invocation.Silent)
        {
            await NpcCommandBroadcast.SendToOthersAsync(
                players,
                messenger,
                invocation,
                message,
                cancellationToken).ConfigureAwait(false);
        }

        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.butcher.applied")
                .WithDefaultText(conflicts == 0
                    ? message
                    : $"{message} {conflicts} changed or failed before the strike."));
    }
}

internal sealed class RenameNpcCommand(
    IWorldNpcService npcs,
    INpcCatalog catalog,
    IPlayerDirectory players,
    IPlayerMessenger messenger) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "renamenpc",
        [],
        ["vega.npc.rename"],
        Syntax: "renamenpc <npc-name|id> <new-name>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 2)
        {
            return AdminCommandResults.Usage(
                "command.renamenpc.usage",
                "Usage: renamenpc <npc-name|id> <new-name>.");
        }

        string newName = invocation.Arguments[1];
        if (string.IsNullOrWhiteSpace(newName) || newName.Length > 200)
        {
            return AdminCommandResults.Failure(
                "command.renamenpc.invalid-name",
                "The new NPC name must contain 1..200 characters.");
        }

        NpcResolutionResult resolution = catalog.Resolve(invocation.Arguments[0]);
        if (resolution.Status == NpcResolutionStatus.NotFound)
        {
            return AdminCommandResults.Failure(
                "command.renamenpc.npc-not-found",
                $"'{invocation.Arguments[0]}' is not a valid NPC.");
        }

        if (resolution.Status == NpcResolutionStatus.Ambiguous)
        {
            return AdminCommandResults.Failure(
                "command.renamenpc.npc-ambiguous",
                $"NPC is ambiguous: {string.Join(", ", resolution.Candidates.Select(static npc => $"{npc.Name} ({npc.NetId})"))}.");
        }

        int netId = resolution.Npc!.NetId;
        IReadOnlyList<NpcSnapshot> snapshot = await npcs
            .SnapshotAsync(invocation.WorldId, cancellationToken)
            .ConfigureAwait(false);
        NpcSnapshot[] selected = snapshot
            .Where(npc => netId == 0 ? !npc.TownNpc : npc.TownNpc && npc.NetId == netId)
            .ToArray();
        int renamed = 0;
        int conflicts = 0;
        foreach (NpcSnapshot npc in selected)
        {
            NpcOperationResult result = await npcs
                .RenameAsync(
                    new NpcRenameRequest(npc.Handle, newName, invocation.Principal.Name),
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.IsSuccess) renamed++;
            else conflicts++;
        }

        if (renamed == 0)
        {
            return AdminCommandResults.Failure(
                "command.renamenpc.no-active-match",
                $"Could not rename {invocation.Arguments[0]}.");
        }

        string message = $"{invocation.Principal.Name} renamed the {invocation.Arguments[0]}.";
        await NpcCommandBroadcast.SendToOthersAsync(
            players,
            messenger,
            invocation,
            message,
            cancellationToken).ConfigureAwait(false);
        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.renamenpc.applied")
                .WithDefaultText(conflicts == 0
                    ? message
                    : $"{message} {conflicts} changed or failed before rename."));
    }
}

internal sealed class MaxSpawnsCommand(
    IWorldNpcSpawnControl spawnControl,
    IPlayerDirectory players,
    IPlayerMessenger messenger) : ICommandHandler
{
    private const int DefaultMaximumSpawns = 5;

    public CommandDescriptor Descriptor { get; } = new(
        "maxspawns",
        [],
        ["vega.npc.maxspawns"],
        Syntax: "maxspawns [default|0..capacity]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        NpcSpawnLimitSnapshot? current = await spawnControl
            .CaptureAsync(invocation.WorldId, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            return AdminCommandResults.Failure(
                "command.maxspawns.unavailable",
                "The NPC spawn limit is unavailable.");
        }

        if (invocation.Arguments.Count == 0)
        {
            return CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.maxspawns.current")
                    .WithDefaultText($"Maximum simultaneous NPC spawns: {current.MaximumSpawns}."));
        }

        string value = invocation.Arguments[0];
        int maximumSpawns;
        if (value.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            maximumSpawns = DefaultMaximumSpawns;
        }
        else if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out maximumSpawns) ||
                 maximumSpawns < 0 || maximumSpawns > current.Capacity)
        {
            return AdminCommandResults.Failure(
                "command.maxspawns.invalid-value",
                $"Maximum spawns must be 'default' or an integer from 0 to {current.Capacity}.");
        }

        NpcSpawnLimitMutationResult result = await spawnControl
            .SetMaximumSpawnsAsync(
                invocation.WorldId,
                maximumSpawns,
                invocation.Principal.Name,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return AdminCommandResults.Failure(
                result.ErrorCode ?? "command.maxspawns.failed",
                $"Could not change the NPC spawn limit: {result.ErrorCode ?? result.Status.ToString()}.");
        }

        string message = $"{invocation.Principal.Name} set maximum simultaneous NPC spawns to {maximumSpawns}.";
        if (!invocation.Silent)
        {
            await NpcCommandBroadcast.SendToOthersAsync(
                players,
                messenger,
                invocation,
                message,
                cancellationToken).ConfigureAwait(false);
        }

        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.maxspawns.applied").WithDefaultText(message));
    }
}

internal sealed class SpawnRateCommand(
    IWorldNpcSpawnControl spawnControl,
    IPlayerDirectory players,
    IPlayerMessenger messenger) : ICommandHandler
{
    private const int DefaultSpawnRate = 600;

    public CommandDescriptor Descriptor { get; } = new(
        "spawnrate",
        [],
        ["vega.npc.spawnrate"],
        Syntax: "spawnrate [default|non-negative-rate]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        NpcSpawnRateSnapshot? current = await spawnControl
            .CaptureSpawnRateAsync(invocation.WorldId, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            return AdminCommandResults.Failure(
                "command.spawnrate.unavailable",
                "The NPC spawn rate is unavailable.");
        }

        if (invocation.Arguments.Count == 0)
        {
            return CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.spawnrate.current")
                    .WithDefaultText($"Current spawn rate: {current.SpawnRate}."));
        }

        string value = invocation.Arguments[0];
        int spawnRate;
        if (value.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            spawnRate = DefaultSpawnRate;
        }
        else if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out spawnRate) ||
                 spawnRate < 0)
        {
            return AdminCommandResults.Failure(
                "command.spawnrate.invalid-value",
                "The spawn rate must be 'default' or a non-negative integer.");
        }

        NpcSpawnRateMutationResult result = await spawnControl
            .SetSpawnRateAsync(
                invocation.WorldId,
                spawnRate,
                invocation.Principal.Name,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return AdminCommandResults.Failure(
                result.ErrorCode ?? "command.spawnrate.failed",
                $"Could not change the NPC spawn rate: {result.ErrorCode ?? result.Status.ToString()}.");
        }

        string message = $"{invocation.Principal.Name} changed the spawn rate to {spawnRate}.";
        if (!invocation.Silent)
        {
            await NpcCommandBroadcast.SendToOthersAsync(
                players,
                messenger,
                invocation,
                message,
                cancellationToken).ConfigureAwait(false);
        }

        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Info("command.spawnrate.applied")
                .WithDefaultText(invocation.Silent
                    ? $"Changed the spawn rate to {spawnRate}."
                    : message));
    }
}

internal sealed class ClearAnglerCommand(IWorldAnglerQuestService anglerQuests) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "clearangler",
        [],
        ["vega.angler.clear"],
        Syntax: "clearangler [player-name]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        string? playerName = invocation.Arguments.Count == 0
            ? null
            : invocation.Arguments[0];
        if (playerName is not null && string.IsNullOrWhiteSpace(playerName))
        {
            return AdminCommandResults.Failure(
                "command.clearangler.invalid-player",
                "Player name cannot be empty.");
        }

        AnglerQuestClearResult result = await anglerQuests
            .ClearAsync(
                new AnglerQuestClearRequest(
                    invocation.WorldId,
                    playerName,
                    invocation.Principal.Name),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Status == AnglerQuestClearStatus.NotFound)
        {
            return AdminCommandResults.Failure(
                "command.clearangler.not-found",
                "Failed to find any users by that name on today's angler completion list.");
        }

        if (!result.IsSuccess)
        {
            return AdminCommandResults.Failure(
                result.ErrorCode ?? "command.clearangler.failed",
                $"Could not clear today's angler completion list: {result.ErrorCode ?? result.Status.ToString()}.");
        }

        string message = playerName is null
            ? "Cleared all users from today's angler quest completion list."
            : $"Removed {result.RemovedEntries} player entr{(result.RemovedEntries == 1 ? "y" : "ies")} from today's angler quest completion list.";
        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.clearangler.applied")
                .WithDefaultText(message));
    }
}

internal static class NpcCommandBroadcast
{
    public static async ValueTask SendToOthersAsync(
        IPlayerDirectory players,
        IPlayerMessenger messenger,
        CommandInvocation invocation,
        string message,
        CancellationToken cancellationToken)
    {
        PlayerId? callerId = (invocation.Principal as IPlayerCommandPrincipal)?.PlayerId;
        foreach (PlayerSnapshot player in players.SnapshotOnlinePlayers())
        {
            if (player.Id == callerId)
            {
                continue;
            }

            await messenger.SendMessageAsync(player.Id, message, cancellationToken).ConfigureAwait(false);
        }
    }
}

internal static class PlayerResolver
{
    public static bool TryCaller(IPlayerDirectory players, CommandInvocation invocation, out PlayerSnapshot? player)
    {
        if (invocation.Principal is IPlayerCommandPrincipal caller)
        {
            return players.TryGet(caller.PlayerId, out player) && player is not null;
        }

        player = null;
        return false;
    }

    public static bool TryResolve(
        IPlayerDirectory players,
        string rawSearch,
        out PlayerSnapshot? player,
        out string? error)
    {
        player = null;
        error = null;
        string search = rawSearch.Trim();
        if (search.Length == 0)
        {
            error = "Player name is empty.";
            return false;
        }

        bool slotOnly = search.StartsWith("tsi:", StringComparison.OrdinalIgnoreCase);
        bool nameOnly = search.StartsWith("tsn:", StringComparison.OrdinalIgnoreCase);
        if (slotOnly || nameOnly)
        {
            search = search[4..];
        }

        IReadOnlyList<PlayerSnapshot> online = players.SnapshotOnlinePlayers();
        if (!nameOnly && int.TryParse(search, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot))
        {
            player = online.FirstOrDefault(candidate => candidate.Id.Slot == slot);
            if (player is not null || slotOnly)
            {
                error = player is null ? $"Player slot {slot} is not online." : null;
                return player is not null;
            }
        }
        else if (slotOnly)
        {
            error = "Invalid player slot.";
            return false;
        }

        if (nameOnly)
        {
            player = online.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, search, StringComparison.OrdinalIgnoreCase));
            error = player is null ? $"Player '{search}' is not online." : null;
            return player is not null;
        }

        PlayerSnapshot[] matches = online
            .Where(candidate => candidate.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToArray();
        if (matches.Length == 1)
        {
            player = matches[0];
            return true;
        }

        error = matches.Length == 0
            ? $"Player '{search}' was not found."
            : $"Player name is ambiguous: {string.Join(", ", matches.Select(static match => match.Name))}.";
        return false;
    }
}

internal static class AdminCommandResults
{
    public static CommandExecutionResult Usage(string code, string text) => new(
        CommandExecutionStatus.InvalidUsage,
        code,
        [MessageDescriptor.Error(code).WithDefaultText(text)]);

    public static CommandExecutionResult Failure(string code, string text) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code).WithDefaultText(text));

    public static CommandExecutionResult PlayerMutation(PlayerOperationResult result, string key, string successText) =>
        result.IsSuccess
            ? CommandExecutionResult.WithMessages(MessageDescriptor.Success(key).WithDefaultText(successText))
            : Failure(result.ErrorCode ?? "player.operation.failed", $"Player operation failed: {result.ErrorCode ?? result.Status.ToString()}.");

    public static CommandExecutionResult WorldMutation(
        WorldEnvironmentMutationResult result,
        string key,
        string successText) =>
        result.IsSuccess
            ? CommandExecutionResult.WithMessages(MessageDescriptor.Success(key).WithDefaultText(successText))
            : Failure(result.ErrorCode ?? "world.environment.failed", $"World operation failed: {result.ErrorCode ?? result.Status.ToString()}.");
}
