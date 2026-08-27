using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Terraria.Worlds;

namespace Vega.Terraria.Commands;

internal static class WorldAdministrationCommandSupport
{
    public static async ValueTask BroadcastAsync(
        IPlayerDirectory players,
        IPlayerMessenger messenger,
        CommandInvocation invocation,
        string message,
        CancellationToken cancellationToken)
    {
        if (invocation.Silent)
        {
            return;
        }

        foreach (PlayerSnapshot player in players.SnapshotOnlinePlayers())
        {
            if (invocation.Principal is IPlayerCommandPrincipal caller && caller.PlayerId == player.Id)
            {
                continue;
            }

            await messenger.SendMessageAsync(player.Id, message, cancellationToken).ConfigureAwait(false);
        }
    }

    public static CommandExecutionResult FromResult(
        WorldAdministrationResult result,
        string successCode,
        string successText)
    {
        if (result.IsSuccess)
        {
            return CommandExecutionResult.WithMessages(
                MessageDescriptor.Success(successCode, result.Value ?? string.Empty)
                    .WithDefaultText(successText));
        }

        return result.Status switch
        {
            WorldAdministrationStatus.Invalid => AdminCommandResults.Failure(
                result.ErrorCode ?? "command.world.invalid",
                "Invalid world administration request."),
            WorldAdministrationStatus.PlayerNotFound => AdminCommandResults.Failure(
                result.ErrorCode ?? "command.world.player_not_found",
                "The invoking player is not available."),
            WorldAdministrationStatus.AlreadyRunning => AdminCommandResults.Failure(
                result.ErrorCode ?? "command.world.already_running",
                "The world operation is already running."),
            WorldAdministrationStatus.Rejected => AdminCommandResults.Failure(
                result.ErrorCode ?? "command.world.rejected",
                "The world operation was rejected."),
            WorldAdministrationStatus.DispatcherUnavailable => AdminCommandResults.Failure(
                result.ErrorCode ?? "command.world.dispatcher_unavailable",
                "The Terraria game thread is unavailable."),
            WorldAdministrationStatus.Cancelled => AdminCommandResults.Failure(
                result.ErrorCode ?? "command.world.cancelled",
                "The world operation was cancelled."),
            _ => AdminCommandResults.Failure(
                result.ErrorCode ?? "command.world.failed",
                "The world operation failed.")
        };
    }

    public static bool TryCaller(CommandInvocation invocation, out PlayerId playerId)
    {
        if (invocation.Principal is IPlayerCommandPrincipal caller)
        {
            playerId = caller.PlayerId;
            return true;
        }

        playerId = default;
        return false;
    }
}

internal sealed class GrowCommand(IWorldAdministrationService world) : ICommandHandler
{
    private const string EvilPermission = "vega.world.grow.evil";
    private const string Plants =
        "basic, sakura, willow, boreal, mahogany, ebonwood, shadewood, pearlwood, " +
        "palm, corruptpalm, crimsonpalm, hallowpalm, topaz, amethyst, sapphire, emerald, ruby, diamond, amber, " +
        "cactus, herb, mushroom";

    public CommandDescriptor Descriptor { get; } = new(
        "grow",
        [],
        ["vega.world.grow"],
        AllowServer: false,
        Syntax: "grow <plant|help>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!WorldAdministrationCommandSupport.TryCaller(invocation, out PlayerId playerId))
        {
            return Usage();
        }

        if (invocation.Arguments.Count == 0 ||
            string.Equals(invocation.Arguments[0], "help", StringComparison.OrdinalIgnoreCase))
        {
            return CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.grow.help", Plants)
                    .WithDefaultText("Available plants: {0}. Evil/hallow variants require vega.world.grow.evil."));
        }

        if (invocation.Arguments.Count != 1)
        {
            return Usage();
        }

        WorldAdministrationResult result = await world.GrowAsync(
            invocation.WorldId,
            playerId,
            invocation.Arguments[0],
            invocation.Principal.HasPermission(EvilPermission),
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.grow.applied", result.Value ?? invocation.Arguments[0])
                    .WithDefaultText("Tried to grow a {0}."))
            : WorldAdministrationCommandSupport.FromResult(result, "command.grow.applied", "Plant growth requested.");
    }

    private static CommandExecutionResult Usage() => AdminCommandResults.Usage(
        "command.grow.usage",
        "Usage: grow <plant|help>.");
}

internal sealed class ForceHalloweenCommand(
    IWorldAdministrationService world,
    IPlayerDirectory players,
    IPlayerMessenger messenger) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "forcehalloween",
        [],
        ["vega.world.halloween"],
        Syntax: "forcehalloween");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        WorldAdministrationResult result = await world
            .ToggleHalloweenAsync(invocation.WorldId, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return WorldAdministrationCommandSupport.FromResult(result, "command.halloween.changed", "Halloween mode changed.");
        }

        string state = result.Value ?? "changed";
        string text = $"{invocation.Principal.Name} {state} halloween mode.";
        await WorldAdministrationCommandSupport.BroadcastAsync(players, messenger, invocation, text, cancellationToken)
            .ConfigureAwait(false);
        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.halloween.changed", state)
                .WithDefaultText($"Halloween mode {state}."));
    }
}

internal sealed class ForceXmasCommand(
    IWorldAdministrationService world,
    IPlayerDirectory players,
    IPlayerMessenger messenger) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "forcexmas",
        [],
        ["vega.world.xmas"],
        Syntax: "forcexmas");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        WorldAdministrationResult result = await world
            .ToggleXmasAsync(invocation.WorldId, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return WorldAdministrationCommandSupport.FromResult(result, "command.xmas.changed", "Xmas mode changed.");
        }

        string state = result.Value ?? "changed";
        string text = $"{invocation.Principal.Name} {state} xmas mode.";
        await WorldAdministrationCommandSupport.BroadcastAsync(players, messenger, invocation, text, cancellationToken)
            .ConfigureAwait(false);
        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.xmas.changed", state)
                .WithDefaultText($"Xmas mode {state}."));
    }
}

internal sealed class WorldEventCommand(
    IWorldAdministrationService world,
    IPlayerDirectory players,
    IPlayerMessenger messenger) : ICommandHandler
{
    private const string EventHelp =
        "meteor, fullmoon, bloodmoon, eclipse, invasion, sandstorm, rain, lanternsnight, meteorshower";
    private const string InvasionHelp = "goblins, snowmen, pirates, pumpkinmoon, frostmoon, martians";

    public CommandDescriptor Descriptor { get; } = new(
        "worldevent",
        [],
        ["vega.world.events"],
        Syntax: "worldevent <event> [variant] [wave]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 0 || invocation.Arguments.Count > 3)
        {
            return AdminCommandResults.Usage(
                "command.worldevent.usage",
                $"Usage: worldevent <event> [variant] [wave]. Events: {EventHelp}. Invasions: {InvasionHelp}.");
        }

        int wave = 1;
        if (invocation.Arguments.Count == 3 &&
            (!int.TryParse(invocation.Arguments[2], out wave) || wave <= 0))
        {
            return AdminCommandResults.Failure("command.worldevent.invalid_wave", "Event wave must be a positive integer.");
        }

        var request = new WorldEventRequest(
            invocation.Arguments[0],
            invocation.Arguments.Count >= 2 ? invocation.Arguments[1] : null,
            wave);
        WorldAdministrationResult result = await world
            .TriggerEventAsync(invocation.WorldId, request, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return WorldAdministrationCommandSupport.FromResult(result, "command.worldevent.changed", "World event changed.");
        }

        string value = result.Value ?? invocation.Arguments[0];
        await WorldAdministrationCommandSupport.BroadcastAsync(
            players,
            messenger,
            invocation,
            $"{invocation.Principal.Name} changed world event state: {value}.",
            cancellationToken).ConfigureAwait(false);
        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.worldevent.changed", value)
                .WithDefaultText("World event state changed: {0}."));
    }
}

internal sealed class HardmodeCommand(IWorldAdministrationService world) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "hardmode",
        [],
        ["vega.world.hardmode"],
        Syntax: "hardmode");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        WorldAdministrationResult result = await world
            .ToggleHardmodeAsync(invocation.WorldId, cancellationToken)
            .ConfigureAwait(false);
        return WorldAdministrationCommandSupport.FromResult(
            result,
            "command.hardmode.changed",
            result.IsSuccess ? $"Hardmode is now {result.Value}." : "Hardmode could not be changed.");
    }
}

internal sealed class EvilCommand(IWorldAdministrationService world) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "evil",
        [],
        ["vega.world.evil"],
        Syntax: "evil");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        WorldAdministrationResult result = await world
            .ToggleEvilAsync(invocation.WorldId, cancellationToken)
            .ConfigureAwait(false);
        return WorldAdministrationCommandSupport.FromResult(
            result,
            "command.evil.changed",
            result.IsSuccess ? $"World evil switched to {result.Value}." : "World evil could not be changed.");
    }
}

internal sealed class ProtectSpawnCommand(
    IWorldEnvironmentService environment,
    IWorldBuildProtectionService protection,
    IPlayerDirectory players,
    IPlayerMessenger messenger) : ICommandHandler
{
    private const int TShockDefaultRadius = 10;

    public CommandDescriptor Descriptor { get; } = new(
        "protectspawn",
        [],
        ["vega.world.editspawn"],
        Syntax: "protectspawn");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        WorldEnvironmentSnapshot? snapshot = await environment
            .CaptureAsync(invocation.WorldId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return AdminCommandResults.Failure("command.protectspawn.unavailable", "World spawn is unavailable.");
        }

        bool enabled = protection.ToggleSpawnProtection(
            snapshot.SpawnTileX,
            snapshot.SpawnTileY,
            TShockDefaultRadius);
        string text = enabled ? "Spawn is now protected." : "Spawn is now open.";
        await WorldAdministrationCommandSupport.BroadcastAsync(players, messenger, invocation, text, cancellationToken)
            .ConfigureAwait(false);
        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.protectspawn.changed", enabled, TShockDefaultRadius)
                .WithDefaultText(text));
    }
}

internal sealed class SetSpawnCommand(
    IWorldAdministrationService world,
    IWorldBuildProtectionService protection) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "setspawn",
        [],
        ["vega.world.setspawn"],
        AllowServer: false,
        Syntax: "setspawn");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!WorldAdministrationCommandSupport.TryCaller(invocation, out PlayerId playerId))
        {
            return AdminCommandResults.Usage("command.setspawn.usage", "Usage: setspawn from a player session.");
        }

        WorldAdministrationResult result = await world
            .SetSpawnAsync(invocation.WorldId, playerId, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess && result.TileX is int x && result.TileY is int y)
        {
            protection.UpdateSpawnPosition(x, y);
        }

        return WorldAdministrationCommandSupport.FromResult(
            result,
            "command.setspawn.changed",
            result.IsSuccess ? "Spawn has now been set at your location." : "Spawn could not be changed.");
    }
}

internal sealed class SetDungeonCommand(IWorldAdministrationService world) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "setdungeon",
        [],
        ["vega.world.setdungeon"],
        AllowServer: false,
        Syntax: "setdungeon");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!WorldAdministrationCommandSupport.TryCaller(invocation, out PlayerId playerId))
        {
            return AdminCommandResults.Usage("command.setdungeon.usage", "Usage: setdungeon from a player session.");
        }

        WorldAdministrationResult result = await world
            .SetDungeonAsync(invocation.WorldId, playerId, cancellationToken)
            .ConfigureAwait(false);
        return WorldAdministrationCommandSupport.FromResult(
            result,
            "command.setdungeon.changed",
            result.IsSuccess ? "The dungeon position has now been set at your location." : "Dungeon position could not be changed.");
    }
}

internal sealed class SettleCommand(IWorldAdministrationService world) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "settle",
        [],
        ["vega.world.settle"],
        Syntax: "settle");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        WorldAdministrationResult result = await world
            .SettleLiquidsAsync(invocation.WorldId, cancellationToken)
            .ConfigureAwait(false);
        return result.Status == WorldAdministrationStatus.AlreadyRunning
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Warning("command.settle.already_running")
                    .WithDefaultText("Liquids are already settling."))
            : WorldAdministrationCommandSupport.FromResult(
                result,
                "command.settle.started",
                "Settling liquids.");
    }
}