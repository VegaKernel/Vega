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

internal sealed class BuffCommand(
    IPlayerDirectory players,
    IPlayerLifecycleOperations operations) : ICommandHandler
{
    private const int MaxSeconds = 86_400;

    public CommandDescriptor Descriptor { get; } = new(
        "buff",
        [],
        ["vega.players.buff"],
        Syntax: "buff <buff-type> <seconds> [player]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count is < 2 or > 3 ||
            !int.TryParse(
                invocation.Arguments[0],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int buffType) || buffType <= 0 ||
            !int.TryParse(
                invocation.Arguments[1],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int seconds) || seconds is < 1 or > MaxSeconds)
        {
            return Usage();
        }

        PlayerSnapshot? target;
        if (invocation.Arguments.Count == 3)
        {
            if (!PlayerResolver.TryResolve(
                    players,
                    invocation.Arguments[2],
                    out target,
                    out string? error))
            {
                return AdminCommandResults.Failure("command.buff.invalid-player", error!);
            }
        }
        else if (!PlayerResolver.TryCaller(players, invocation, out target))
        {
            return Usage();
        }

        int durationTicks = checked(seconds * 60);
        PlayerOperationResult result = await operations
            .ApplyBuffAsync(target!.Id, buffType, durationTicks, cancellationToken)
            .ConfigureAwait(false);
        return AdminCommandResults.PlayerMutation(
            result,
            "command.buff.applied",
            $"Applied buff {buffType} to {target.Name} for {seconds} second(s).");
    }

    private static CommandExecutionResult Usage() => AdminCommandResults.Usage(
        "command.buff.usage",
        $"Usage: buff <buff-type> <seconds:1..{MaxSeconds}> [player]. The server console must specify a player.");
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
