using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;

namespace Vega.Modules.Administration;

public sealed class HealCommand(IPlayerDirectory players, IPlayerVitalOperations vitals) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("heal", [], ["vega.players.heal"], Syntax: "heal [player] [amount]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        int amount = int.MaxValue;
        if (invocation.Arguments.Count > 2 || invocation.Arguments.Count == 2 &&
            (!int.TryParse(invocation.Arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out amount) || amount <= 0))
            return new(CommandExecutionStatus.InvalidUsage, "heal.usage");
        PlayerSnapshot? target = invocation.Arguments.Count == 0
            ? VitalTargets.Self(players, invocation) : VitalTargets.Find(players, invocation, invocation.Arguments[0]);
        if (target is null) return CommandExecutionResult.Failed("heal.player_not_unique");
        var result = await vitals.HealAsync(target, amount, token).ConfigureAwait(false);
        return VitalTargets.Result(result, "heal.applied", "Healed {0}.", target.Name);
    }
}

public sealed class BuffCommand(IPlayerDirectory players, IPlayerVitalOperations vitals, bool others = false) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = others
        ? new("gbuff", ["buffplayer"], ["vega.players.buff.others"], Syntax: "gbuff <player> <buff> [seconds]")
        : new("buff", [], ["vega.players.buff"], AllowServer: false, Syntax: "buff <buff> [seconds]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        int required = others ? 2 : 1;
        int seconds = 60;
        if (invocation.Arguments.Count < required || invocation.Arguments.Count > required + 1 ||
            invocation.Arguments.Count == required + 1 &&
            (!int.TryParse(invocation.Arguments[required], NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds is < 1 or > 3600))
            return new(CommandExecutionStatus.InvalidUsage, "buff.usage");
        PlayerSnapshot? target = others ? VitalTargets.Find(players, invocation, invocation.Arguments[0]) : VitalTargets.Self(players, invocation);
        if (target is null) return CommandExecutionResult.Failed("buff.player_not_unique");
        string buff = invocation.Arguments[required - 1];
        var result = await vitals.BuffAsync(target, buff, seconds, token).ConfigureAwait(false);
        return VitalTargets.Result(result, "buff.applied", "Applied {0} to {1} for {2} seconds.", buff, target.Name, seconds);
    }
}

public sealed class KillCommand(IPlayerDirectory players, IPlayerVitalOperations vitals) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("kill", ["slay"], ["vega.players.kill"], Syntax: "kill <player>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count == 0) return new(CommandExecutionStatus.InvalidUsage, "kill.usage");
        var target = VitalTargets.Find(players, invocation, string.Join(' ', invocation.Arguments));
        if (target is null) return CommandExecutionResult.Failed("kill.player_not_unique");
        if (target.IsDead) return CommandExecutionResult.Failed("kill.already_dead");
        var result = await vitals.KillAsync(target, token).ConfigureAwait(false);
        return VitalTargets.Result(result, "kill.requested", "Requested death of {0}.", target.Name);
    }
}

public sealed class SlapCommand(IPlayerDirectory players, IPlayerVitalOperations vitals) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("slap", [], ["vega.players.slap"], Syntax: "slap <player> [damage:0..32767]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        int damage = 5;
        if (invocation.Arguments.Count is < 1 or > 2 || invocation.Arguments.Count == 2 &&
            (!int.TryParse(invocation.Arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out damage) || damage < 0 || damage > short.MaxValue))
            return new(CommandExecutionStatus.InvalidUsage, "slap.usage");
        var target = VitalTargets.Find(players, invocation, invocation.Arguments[0]);
        if (target is null) return CommandExecutionResult.Failed("slap.player_not_unique");
        if (target.IsDead) return CommandExecutionResult.Failed("slap.already_dead");
        if (!invocation.Principal.HasPermission("vega.players.kill")) damage = Math.Min(15, damage);
        var result = await vitals.SlapAsync(target, damage, token).ConfigureAwait(false);
        return VitalTargets.Result(result, "slap.requested", "Requested {0} damage to {1}.", damage, target.Name);
    }
}

public sealed class RespawnCommand(IPlayerDirectory players, IPlayerVitalOperations vitals) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("respawn", [], ["vega.players.respawn"], Syntax: "respawn [player]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count > 0 && !invocation.Principal.HasPermission("vega.players.respawn.others"))
            return new(CommandExecutionStatus.Forbidden, "respawn.others_permission");
        var target = invocation.Arguments.Count == 0 ? VitalTargets.Self(players, invocation)
            : VitalTargets.Find(players, invocation, string.Join(' ', invocation.Arguments));
        if (target is null) return CommandExecutionResult.Failed("respawn.player_not_unique");
        if (!target.IsDead) return CommandExecutionResult.Failed("respawn.not_dead");
        var result = await vitals.RespawnAsync(target, token).ConfigureAwait(false);
        return VitalTargets.Result(result, "respawn.requested", "Requested respawn of {0}.", target.Name);
    }
}

internal static class VitalTargets
{
    public static PlayerSnapshot? Self(IPlayerDirectory players, CommandInvocation invocation) =>
        invocation.Principal is IPlayerCommandPrincipal { SessionId: { } session } principal &&
        players.TryGet(principal.PlayerId, out var player) && player?.SessionId == session && player.WorldId == invocation.WorldId ? player : null;

    public static PlayerSnapshot? Find(IPlayerDirectory players, CommandInvocation invocation, string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return null;
        var online = players.SnapshotOnlinePlayers().Where(p => p.WorldId == invocation.WorldId && p.SessionId is not null).ToArray();
        var matches = online.Where(p => string.Equals(p.Name, search, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0 && int.TryParse(search, NumberStyles.None, CultureInfo.InvariantCulture, out int slot))
            matches = online.Where(p => p.Id.Slot == slot).ToArray();
        if (matches.Length == 0) matches = online.Where(p => p.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static CommandExecutionResult Result(PlayerOperationResult result, string key, string text, params object?[] args) => result.IsSuccess
        ? CommandExecutionResult.WithMessages(MessageDescriptor.Success(key, args).WithDefaultText(text))
        : CommandExecutionResult.Failed(result.ErrorCode ?? "player.operation_failed");
}
