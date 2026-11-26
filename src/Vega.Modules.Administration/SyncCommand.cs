using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Players;

namespace Vega.Modules.Administration;

public sealed class SyncCommand(IPlayerDirectory players, IPlayerOperations operations) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("sync", [], ["vega.world.sync"], Syntax: "sync [radius:1..16] | sync <player> [radius]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        var args = invocation.Arguments;
        int radius = 10;
        if (args.Count > 2) return Usage();
        PlayerSnapshot? target;
        if (args.Count == 0) target = VitalTargets.Self(players, invocation);
        else if (args.Count == 1 && int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int requested))
        {
            radius = requested;
            target = VitalTargets.Self(players, invocation);
        }
        else
        {
            if (!invocation.Principal.HasPermission("vega.world.sync.others")) return new(CommandExecutionStatus.Forbidden, "sync.others_permission");
            target = VitalTargets.Find(players, invocation, args[0]);
            if (args.Count == 2 && !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out radius)) return Usage();
        }
        if (radius is < 1 or > 16) return Usage();
        if (target is null) return CommandExecutionResult.Failed("sync.player_not_unique");
        return VitalTargets.Result(await operations.ResynchronizeAreaAsync(target, radius, token).ConfigureAwait(false), "sync.sent", "Sent local tile state to {0}.", target.Name);
    }

    private static CommandExecutionResult Usage() => new(CommandExecutionStatus.InvalidUsage, "sync.usage");
}
