using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Items;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;

namespace Vega.Modules.Administration;

public sealed class ItemCommand(IPlayerDirectory players, IPlayerItemDelivery delivery, bool giveOthers = false) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = giveOthers
        ? new("give", ["g"], ["vega.items.give"], Syntax: "give <item> <player> [stack] [prefix-id]")
        : new("item", ["i"], ["vega.items.self"], AllowServer: false, Syntax: "item <item> [stack] [prefix-id]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        var args = invocation.Arguments;
        int required = giveOthers ? 2 : 1;
        int stack = 1, prefix = 0;
        if (args.Count < required || args.Count > required + 2 ||
            args.Count > required && (!int.TryParse(args[required], NumberStyles.None, CultureInfo.InvariantCulture, out stack) || stack <= 0) ||
            args.Count > required + 1 && !int.TryParse(args[required + 1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix))
            return new(CommandExecutionStatus.InvalidUsage, "item.usage", [MessageDescriptor.Error("item.usage", Descriptor.Syntax).WithDefaultText("Usage: {0}.")]);

        PlayerSnapshot? target = giveOthers ? VitalTargets.Find(players, invocation, args[1]) : VitalTargets.Self(players, invocation);
        if (target is null)
            return CommandExecutionResult.Failed("item.player_unavailable");

        var result = await delivery.GiveAsync(target!, args[0], stack, prefix, token).ConfigureAwait(false);
        return result.IsSuccess
            ? CommandExecutionResult.WithMessages(MessageDescriptor.Success("item.given", stack, args[0], target!.Name).WithDefaultText("Gave {0} x {1} to {2}."))
            : CommandExecutionResult.Failed(result.ErrorCode ?? "item.failed", MessageDescriptor.Error("item.failed", result.ErrorCode)
                .WithDefaultText("Item delivery failed ({0})."));
    }
}
