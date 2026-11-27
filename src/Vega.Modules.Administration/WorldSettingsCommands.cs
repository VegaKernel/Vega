using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Modules.Administration;

public sealed class WorldSpawnSettingsCommand(IWorldEnvironmentService world, bool rate) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = rate
        ? new("spawnrate", [], ["vega.world.spawnrate"], Syntax: "spawnrate [1..60000]")
        : new("maxspawns", [], ["vega.world.maxspawns"], Syntax: "maxspawns [0..200]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count == 0)
        {
            var snapshot = await world.CaptureAsync(invocation.WorldId, token).ConfigureAwait(false);
            int? current = rate ? snapshot?.DefaultSpawnRate : snapshot?.DefaultMaxSpawns;
            return current is int value ? Report(value) : CommandExecutionResult.Failed("world.spawn_settings_unavailable");
        }
        if (invocation.Arguments.Count != 1 || !int.TryParse(invocation.Arguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out int setting) ||
            (rate ? setting is < 1 or > 60000 : setting is < 0 or > 200))
            return new(CommandExecutionStatus.InvalidUsage, "world.spawn_settings_usage");
        var patch = rate ? new WorldEnvironmentPatch(DefaultSpawnRate: setting) : new WorldEnvironmentPatch(DefaultMaxSpawns: setting);
        var result = await world.PatchAsync(new(invocation.WorldId, patch, invocation.Principal.Name), token).ConfigureAwait(false);
        return result.IsSuccess ? Report(setting) : CommandExecutionResult.Failed(result.ErrorCode ?? "world.spawn_settings_failed");
    }

    private CommandExecutionResult Report(int value) => CommandExecutionResult.WithMessages(MessageDescriptor.Info("world.spawn_settings", Descriptor.Name, value)
        .WithDefaultText("{0}: {1}."));
}

public sealed class WorldModeCommand(IWorldEnvironmentService world) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("worldmode", ["gamemode"], ["vega.world.mode"], Syntax: "worldmode [normal|expert|master]");
    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count == 0)
        {
            var snapshot = await world.CaptureAsync(invocation.WorldId, token).ConfigureAwait(false);
            return snapshot?.GameMode is int current ? Report(current) : CommandExecutionResult.Failed("world.mode_unavailable");
        }
        if (invocation.Arguments.Count != 1) return new(CommandExecutionStatus.InvalidUsage, "world.mode_usage");
        int mode = invocation.Arguments[0].ToLowerInvariant() switch { "normal" or "classic" or "0" => 0, "expert" or "1" => 1, "master" or "2" => 2, _ => -1 };
        if (mode < 0) return new(CommandExecutionStatus.InvalidUsage, "world.mode_usage");
        var result = await world.PatchAsync(new(invocation.WorldId, new(GameMode: mode), invocation.Principal.Name), token).ConfigureAwait(false);
        return result.IsSuccess ? Report(mode) : CommandExecutionResult.Failed(result.ErrorCode ?? "world.mode_failed");
    }

    private static CommandExecutionResult Report(int mode) => CommandExecutionResult.WithMessages(MessageDescriptor.Info("world.mode", mode switch
    { 0 => "normal", 1 => "expert", 2 => "master", 3 => "journey", _ => mode.ToString(CultureInfo.InvariantCulture) }).WithDefaultText("World mode: {0}."));
}

public sealed class WorldPointCommand(IWorldEnvironmentService world, IPlayerDirectory players, bool dungeon) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = dungeon
        ? new("setdungeon", [], ["vega.world.setdungeon"], Syntax: "setdungeon [tile-x tile-y]")
        : new("setspawn", [], ["vega.world.setspawn"], Syntax: "setspawn [tile-x tile-y]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        WorldTilePosition position;
        if (invocation.Arguments.Count == 0 && VitalTargets.Self(players, invocation) is { IsDead: false } player)
            position = new((int)MathF.Floor(player.Position.X / 16), (int)MathF.Floor(player.Position.Y / 16));
        else if (invocation.Arguments.Count == 2 && int.TryParse(invocation.Arguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out int x) &&
            int.TryParse(invocation.Arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out int y)) position = new(x, y);
        else return new(CommandExecutionStatus.InvalidUsage, "world.point_usage");
        var patch = dungeon ? new WorldEnvironmentPatch(Dungeon: position) : new WorldEnvironmentPatch(Spawn: position);
        var result = await world.PatchAsync(new(invocation.WorldId, patch, invocation.Principal.Name), token).ConfigureAwait(false);
        return result.IsSuccess ? CommandExecutionResult.WithMessages(MessageDescriptor.Success("world.point", dungeon ? "Dungeon" : "Spawn", position.X, position.Y)
            .WithDefaultText("{0} set to ({1}, {2}).")) : CommandExecutionResult.Failed(result.ErrorCode ?? "world.point_failed");
    }
}

public sealed class ForceSeasonCommand(IWorldEnvironmentService world, bool halloween) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = halloween
        ? new("forcehalloween", [], ["vega.world.forcehalloween"], Syntax: "forcehalloween [on|off|status]")
        : new("forcexmas", [], ["vega.world.forcexmas"], Syntax: "forcexmas [on|off|status]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count > 1) return new(CommandExecutionStatus.InvalidUsage, "world.season_usage");
        string action = invocation.Arguments.Count == 0 ? "toggle" : invocation.Arguments[0].ToLowerInvariant();
        if (action is not ("toggle" or "on" or "off" or "status")) return new(CommandExecutionStatus.InvalidUsage, "world.season_usage");
        var snapshot = await world.CaptureAsync(invocation.WorldId, token).ConfigureAwait(false);
        if (snapshot is null) return CommandExecutionResult.Failed("world.season_unavailable");
        bool enabled = halloween ? snapshot.ForceHalloween : snapshot.ForceChristmas;
        if (action != "status")
        {
            enabled = action == "on" || action == "toggle" && !enabled;
            var patch = halloween ? new WorldEnvironmentPatch(ForceHalloween: enabled) : new WorldEnvironmentPatch(ForceChristmas: enabled);
            var result = await world.PatchAsync(new(invocation.WorldId, patch, invocation.Principal.Name), token).ConfigureAwait(false);
            if (!result.IsSuccess) return CommandExecutionResult.Failed(result.ErrorCode ?? "world.season_failed");
        }
        return CommandExecutionResult.WithMessages(MessageDescriptor.Info("world.season", halloween ? "Halloween" : "Christmas", enabled)
            .WithDefaultText("{0} forced override: {1}."));
    }
}

public sealed class ClearAnglerCommand(IWorldEnvironmentService world) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("clearangler", [], ["vega.world.clearangler"], Syntax: "clearangler [player-name]");
    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count > 1 || invocation.Arguments.Count == 1 && string.IsNullOrWhiteSpace(invocation.Arguments[0]))
            return new(CommandExecutionStatus.InvalidUsage, "world.clearangler_usage");
        string? player = invocation.Arguments.Count == 1 ? invocation.Arguments[0] : null;
        var result = await world.PatchAsync(new(invocation.WorldId, player is null ? new(ClearAnglerCompletion: true) : new(ClearAnglerPlayer: player), invocation.Principal.Name), token).ConfigureAwait(false);
        return result.IsSuccess ? CommandExecutionResult.WithMessages(player is null
            ? MessageDescriptor.Success("world.clearangler").WithDefaultText("Today's angler completion list has been cleared.")
            : MessageDescriptor.Success("world.clearangler.player", player).WithDefaultText("Today's angler completion has been cleared for {0}."))
            : CommandExecutionResult.Failed(result.ErrorCode ?? "world.clearangler_failed");
    }
}

public sealed class HardmodeCommand(IWorldEnvironmentService world) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("hardmode", [], ["vega.world.hardmode"], Syntax: "hardmode [status|start confirm]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        var args = invocation.Arguments;
        bool start = args.Count == 2 && args[0].Equals("start", StringComparison.OrdinalIgnoreCase) && args[1].Equals("confirm", StringComparison.OrdinalIgnoreCase);
        if (!start && !(args.Count == 0 || args.Count == 1 && args[0].Equals("status", StringComparison.OrdinalIgnoreCase)))
            return new(CommandExecutionStatus.InvalidUsage, "world.hardmode_usage");
        var snapshot = await world.CaptureAsync(invocation.WorldId, token).ConfigureAwait(false);
        if (snapshot is null) return CommandExecutionResult.Failed("world.hardmode_unavailable");
        if (!start || snapshot.Hardmode) return CommandExecutionResult.WithMessages(MessageDescriptor.Info("world.hardmode_status", snapshot.Hardmode)
            .WithDefaultText("Hardmode: {0}."));
        var result = await world.PatchAsync(new(invocation.WorldId, new(StartHardmode: true), invocation.Principal.Name), token).ConfigureAwait(false);
        return result.IsSuccess ? CommandExecutionResult.WithMessages(MessageDescriptor.Warning("world.hardmode_started")
            .WithDefaultText("Hardmode started; vanilla world transformation runs in the background."))
            : CommandExecutionResult.Failed(result.ErrorCode ?? "world.hardmode_failed");
    }
}
