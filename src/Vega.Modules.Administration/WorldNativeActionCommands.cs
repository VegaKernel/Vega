using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Modules.Administration;

public sealed class EvilCommand(IWorldNativeMaintenanceOperations world) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("evil", [], ["vega.world.evil"], Syntax: "evil [crimson|corruption]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count > 1) return new(CommandExecutionStatus.InvalidUsage, "evil.usage");
        bool? target = invocation.Arguments.Count == 0 ? null : invocation.Arguments[0].ToLowerInvariant() switch
        { "crimson" => true, "corruption" => false, _ => null };
        if (invocation.Arguments.Count == 1 && target is null) return new(CommandExecutionStatus.InvalidUsage, "evil.usage");
        var result = await world.SwitchEvilAsync(invocation.WorldId, target, token).ConfigureAwait(false);
        return result.IsSuccess && result.Crimson.HasValue ? CommandExecutionResult.WithMessages(MessageDescriptor.Success("evil.updated",
            result.Crimson.Value ? "crimson" : "corruption").WithDefaultText("World evil is now {0}. Existing tiles are unchanged."))
            : CommandExecutionResult.Failed(result.ErrorCode ?? "evil.failed");
    }
}

public sealed class SettleCommand(IWorldNativeMaintenanceOperations world) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("settle", [], ["vega.world.settle"], Syntax: "settle");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count != 0) return new(CommandExecutionStatus.InvalidUsage, "settle.usage");
        var result = await world.SettleAsync(invocation.WorldId, token).ConfigureAwait(false);
        if (!result.IsSuccess) return CommandExecutionResult.Failed(result.ErrorCode ?? "settle.failed");
        return CommandExecutionResult.WithMessages(result.Status == WorldNativeActionStatus.AlreadyActive
            ? MessageDescriptor.Info("settle.already_active").WithDefaultText("Liquids are already settling.")
            : MessageDescriptor.Success("settle.started").WithDefaultText("Native liquid settling started."));
    }
}

/// <summary>Explicit admin growth on compatible natural ground. No terrain preparation or ordinary region bypass.</summary>
public sealed class GrowCommand(IWorldNativeMaintenanceOperations world, IPlayerDirectory players) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("grow", [], ["vega.world.grow"], Syntax: "grow <tree-type> [tile-x tile-y (console)] | grow help");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        var args = invocation.Arguments;
        if (args.Count == 0 || args.Count == 1 && args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
            return CommandExecutionResult.WithMessages(MessageDescriptor.Info("grow.types", string.Join(", ", Enum.GetNames<WorldGrowKind>())
                .ToLowerInvariant()).WithDefaultText("Native growth on compatible existing ground: {0}. Console: grow <type> <tile-x> <tile-y>."));
        if (args.Count is not (1 or 3) || !Enum.TryParse<WorldGrowKind>(args[0], true, out var kind) ||
            !Enum.IsDefined(kind) || !kind.ToString().Equals(args[0], StringComparison.OrdinalIgnoreCase))
            return new(CommandExecutionStatus.InvalidUsage, "grow.usage");

        PlayerSnapshot? player = null;
        WorldTilePosition? coordinates = null;
        if (invocation.Principal.IsServer)
        {
            if (args.Count != 3 || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int x) ||
                !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int y))
                return new(CommandExecutionStatus.InvalidUsage, "grow.usage");
            coordinates = new(x, y);
        }
        else
        {
            if (args.Count != 1) return new(CommandExecutionStatus.InvalidUsage, "grow.usage");
            // Native growth can cross more than the player's root tile. Require explicit admin rights
            // for every protection layer rather than pretending to authorize just the root tile.
            if (!invocation.Principal.HasPermission("vega.regions.bypass") ||
                !invocation.Principal.HasPermission("vega.world.build.bypass") ||
                !invocation.Principal.HasPermission("vega.world.spawn.bypass"))
                return new(CommandExecutionStatus.Forbidden, "grow.admin_required");
            if (kind is WorldGrowKind.Shadewood or WorldGrowKind.Ebonwood or WorldGrowKind.Pearlwood or
                WorldGrowKind.HallowPalm or WorldGrowKind.CrimsonPalm or WorldGrowKind.CorruptPalm &&
                !invocation.Principal.HasPermission("vega.world.grow.evil"))
                return new(CommandExecutionStatus.Forbidden, "grow.evil_permission");
            player = VitalTargets.Self(players, invocation);
            if (player is null || player.IsDead) return CommandExecutionResult.Failed("grow.player_unavailable");
        }

        var result = await world.GrowAsync(new(invocation.WorldId, kind, player, coordinates), token).ConfigureAwait(false);
        return result.IsSuccess && result.Position is { } position ? CommandExecutionResult.WithMessages(MessageDescriptor.Success("grow.completed",
            kind.ToString(), position.X, position.Y).WithDefaultText("Native {0} growth succeeded at ({1}, {2})."))
            : CommandExecutionResult.Failed(result.ErrorCode ?? "grow.failed");
    }
}
