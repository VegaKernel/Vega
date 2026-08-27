using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Players;

namespace Vega.Terraria.Commands;

internal static class TerrariaPlayerLifecycleCommandRegistration
{
    public static IDisposable Register(ICommandDispatcher dispatcher, Terraria.Players.TerrariaPlayerBoundary players)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(players);

        if (dispatcher is not ICommandRegistry registry)
        {
            throw new InvalidOperationException(
                "The Vega command dispatcher must expose ICommandRegistry for built-in player lifecycle commands.");
        }

        ICommandHandler[] handlers =
        [
            new HealCommand(players, players),
            new KillCommand(players, players),
            new BuffCommand(players, players),
            new GBuffCommand(players, players),
            new RespawnCommand(players, players)
        ];

        return new RegistrationSet(handlers.Select(registry.Register).ToArray());
    }

    private sealed class RegistrationSet(IDisposable[] registrations) : IDisposable
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

internal sealed class HealCommand(
    IPlayerDirectory players,
    IPlayerLifecycleOperations operations) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "heal",
        [],
        ["vega.players.heal"],
        Syntax: "heal [player] [amount]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        PlayerSnapshot? target;
        int? amount = null;

        if (invocation.Arguments.Count == 0)
        {
            if (!PlayerResolver.TryCaller(players, invocation, out target))
            {
                return Usage();
            }
        }
        else if (invocation.Arguments.Count is 1 or 2)
        {
            if (!PlayerResolver.TryResolve(
                    players,
                    invocation.Arguments[0],
                    out target,
                    out string? error))
            {
                return AdminCommandResults.Failure("command.heal.invalid-player", error!);
            }

            if (invocation.Arguments.Count == 2)
            {
                if (!int.TryParse(
                        invocation.Arguments[1],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int parsed) || parsed <= 0)
                {
                    return Usage();
                }

                amount = parsed;
            }
        }
        else
        {
            return Usage();
        }

        PlayerOperationResult result = await operations
            .HealAsync(target!.Id, amount, cancellationToken)
            .ConfigureAwait(false);
        return AdminCommandResults.PlayerMutation(
            result,
            "command.heal.applied",
            amount is int applied
                ? $"Healed {target.Name} by up to {applied} life."
                : $"Restored {target.Name} to full life.");
    }

    private static CommandExecutionResult Usage() => AdminCommandResults.Usage(
        "command.heal.usage",
        "Usage: heal [player] [amount]. The server console must specify a player.");
}

internal sealed class KillCommand(
    IPlayerDirectory players,
    IPlayerLifecycleOperations operations) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "kill",
        ["slay"],
        ["vega.players.kill"],
        Syntax: "kill [player]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        PlayerSnapshot? target;
        if (invocation.Arguments.Count == 0)
        {
            if (!PlayerResolver.TryCaller(players, invocation, out target))
            {
                return Usage();
            }
        }
        else if (invocation.Arguments.Count == 1)
        {
            if (!PlayerResolver.TryResolve(
                    players,
                    invocation.Arguments[0],
                    out target,
                    out string? error))
            {
                return AdminCommandResults.Failure("command.kill.invalid-player", error!);
            }
        }
        else
        {
            return Usage();
        }

        string reason = $"{target!.Name} was killed by {invocation.Principal.Name}.";
        PlayerOperationResult result = await operations
            .KillAsync(target.Id, reason, cancellationToken)
            .ConfigureAwait(false);
        return AdminCommandResults.PlayerMutation(
            result,
            "command.kill.applied",
            $"Killed {target.Name}.");
    }

    private static CommandExecutionResult Usage() => AdminCommandResults.Usage(
        "command.kill.usage",
        "Usage: kill [player]. The server console must specify a player.");
}

internal static class BuffCommandParsing
{
    public const int DefaultSeconds = 60;
    public const int MaximumSeconds = (int.MaxValue / 60) - 1;

    public static bool TryParseDuration(string raw, out int seconds)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
        {
            return false;
        }

        seconds = seconds < 0 || seconds > MaximumSeconds ? MaximumSeconds : seconds;
        return seconds > 0;
    }

    public static bool TryResolveBuff(string raw, out int buffType, out string? error)
    {
        error = null;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out buffType))
        {
            if (buffType > 0 && buffType < global::Terraria.ID.BuffID.Count)
            {
                return true;
            }

            error = $"Buff id {buffType} is outside the Terraria buff registry.";
            return false;
        }

        string search = raw.Trim();
        if (search.Length == 0)
        {
            buffType = 0;
            error = "Buff name is empty.";
            return false;
        }

        (int Id, string Name)[] names = Enumerable.Range(1, global::Terraria.ID.BuffID.Count - 1)
            .Select(static id => (Id: id, Name: global::Terraria.Lang.GetBuffName(id)))
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Name))
            .ToArray();

        (int Id, string Name) exact = names
            .FirstOrDefault(entry => entry.Name.Equals(search, StringComparison.OrdinalIgnoreCase));
        if (exact.Id > 0)
        {
            buffType = exact.Id;
            return true;
        }

        (int Id, string Name)[] matches = names
            .Where(entry => entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Take(9)
            .ToArray();
        if (matches.Length == 1)
        {
            buffType = matches[0].Id;
            return true;
        }

        buffType = 0;
        error = matches.Length == 0
            ? $"No buff matches '{search}'."
            : $"Buff name is ambiguous: {string.Join(", ", matches.Take(8).Select(static entry => entry.Name))}.";
        return false;
    }
}

internal sealed class BuffCommand(
    IPlayerDirectory players,
    IPlayerLifecycleOperations operations) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "buff",
        [],
        ["vega.players.buff"],
        AllowServer: false,
        Syntax: "buff <buff-name|id> [seconds]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count is < 1 or > 2 ||
            invocation.Principal is not IPlayerCommandPrincipal principal ||
            !players.TryGet(principal.PlayerId, out PlayerSnapshot? target) ||
            target is null)
        {
            return Usage();
        }

        if (!BuffCommandParsing.TryResolveBuff(
                invocation.Arguments[0],
                out int buffType,
                out string? buffError))
        {
            return AdminCommandResults.Failure("command.buff.invalid-buff", buffError!);
        }

        int seconds = BuffCommandParsing.DefaultSeconds;
        if (invocation.Arguments.Count == 2 &&
            !BuffCommandParsing.TryParseDuration(invocation.Arguments[1], out seconds))
        {
            return Usage();
        }

        int durationTicks = checked(seconds * 60);
        PlayerOperationResult result = await operations
            .ApplyBuffAsync(target.Id, buffType, durationTicks, cancellationToken)
            .ConfigureAwait(false);
        return AdminCommandResults.PlayerMutation(
            result,
            "command.buff.applied",
            $"Applied {global::Terraria.Lang.GetBuffName(buffType)} ({buffType}) to {target.Name} for {seconds} second(s).");
    }

    private static CommandExecutionResult Usage() => AdminCommandResults.Usage(
        "command.buff.usage",
        $"Usage: buff <buff-name|id> [seconds]. Duration must be positive; negative values use Terraria's maximum duration ({BuffCommandParsing.MaximumSeconds}).");
}

internal sealed class GBuffCommand(
    IPlayerDirectory players,
    IPlayerLifecycleOperations operations) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "gbuff",
        ["buffplayer"],
        ["vega.players.buff.others"],
        Syntax: "gbuff <player> <buff-name|id> [seconds]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count is < 2 or > 3)
        {
            return Usage();
        }

        if (!PlayerResolver.TryResolve(
                players,
                invocation.Arguments[0],
                out PlayerSnapshot? target,
                out string? playerError))
        {
            return AdminCommandResults.Failure("command.gbuff.invalid-player", playerError!);
        }

        if (!BuffCommandParsing.TryResolveBuff(
                invocation.Arguments[1],
                out int buffType,
                out string? buffError))
        {
            return AdminCommandResults.Failure("command.gbuff.invalid-buff", buffError!);
        }

        int seconds = BuffCommandParsing.DefaultSeconds;
        if (invocation.Arguments.Count == 3 &&
            !BuffCommandParsing.TryParseDuration(invocation.Arguments[2], out seconds))
        {
            return Usage();
        }

        int durationTicks = checked(seconds * 60);
        PlayerOperationResult result = await operations
            .ApplyBuffAsync(target!.Id, buffType, durationTicks, cancellationToken)
            .ConfigureAwait(false);
        return AdminCommandResults.PlayerMutation(
            result,
            "command.gbuff.applied",
            $"Applied {global::Terraria.Lang.GetBuffName(buffType)} ({buffType}) to {target.Name} for {seconds} second(s).");
    }

    private static CommandExecutionResult Usage() => AdminCommandResults.Usage(
        "command.gbuff.usage",
        $"Usage: gbuff <player> <buff-name|id> [seconds]. Duration must be positive; negative values use Terraria's maximum duration ({BuffCommandParsing.MaximumSeconds}).");
}

internal sealed class RespawnCommand(
    IPlayerDirectory players,
    IPlayerLifecycleOperations operations) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "respawn",
        ["revive"],
        ["vega.players.respawn"],
        Syntax: "respawn [player]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        PlayerSnapshot? target;
        if (invocation.Arguments.Count == 0)
        {
            if (!PlayerResolver.TryCaller(players, invocation, out target))
            {
                return Usage();
            }
        }
        else if (invocation.Arguments.Count == 1)
        {
            if (!PlayerResolver.TryResolve(
                    players,
                    invocation.Arguments[0],
                    out target,
                    out string? error))
            {
                return AdminCommandResults.Failure("command.respawn.invalid-player", error!);
            }
        }
        else
        {
            return Usage();
        }

        PlayerOperationResult result = await operations
            .RespawnAsync(target!.Id, cancellationToken)
            .ConfigureAwait(false);
        return AdminCommandResults.PlayerMutation(
            result,
            "command.respawn.applied",
            $"Respawned {target.Name}.");
    }

    private static CommandExecutionResult Usage() => AdminCommandResults.Usage(
        "command.respawn.usage",
        "Usage: respawn [player]. The server console must specify a player.");
}
