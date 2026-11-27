using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Items;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;

namespace Vega.Modules.Administration;

public sealed class ItemCommand(IPlayerDirectory players, IPlayerItemDelivery delivery, bool giveOthers = false,
    Func<string, int?>? resolvePrefix = null) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = giveOthers
        ? new("give", ["g"], ["vega.items.give", "tshock.item.give"], Syntax: "give <item> <player> [stack] [prefix-id|name]")
        : new("item", ["i"], ["vega.items.self", "tshock.item.spawn"], AllowServer: false, Syntax: "item <item> [stack] [prefix-id|name]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        var args = invocation.Arguments;
        int required = giveOthers ? 2 : 1;
        int stack = 0, prefix = 0;
        int amountIndex = required;
        if (!giveOthers)
        {
            amountIndex = 1;
            while (amountIndex < args.Count && !int.TryParse(args[amountIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) amountIndex++;
        }
        if (args.Count < required || args.Count > amountIndex + 2 ||
            args.Count > amountIndex && (!int.TryParse(args[amountIndex], NumberStyles.None, CultureInfo.InvariantCulture, out stack) || stack < 0))
            return new(CommandExecutionStatus.InvalidUsage, "item.usage", [MessageDescriptor.Error("item.usage", Descriptor.Syntax).WithDefaultText("Usage: {0}.")]);
        if (args.Count > amountIndex + 1 && !int.TryParse(args[amountIndex + 1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix))
        {
            if (resolvePrefix?.Invoke(args[amountIndex + 1]) is not int resolved || resolved <= 0)
                return new(CommandExecutionStatus.InvalidUsage, "item.prefix_not_unique");
            prefix = resolved;
        }

        PlayerSnapshot? target = giveOthers ? VitalTargets.Find(players, invocation, args[1]) : VitalTargets.Self(players, invocation);
        if (target is null)
            return CommandExecutionResult.Failed("item.player_unavailable");

        string item = giveOthers ? args[0] : string.Join(" ", args.Take(amountIndex));
        var result = await delivery.GiveCommandAsync(target!, item, stack, prefix, token).ConfigureAwait(false);
        int delivered = result.DeliveredItemStack ?? (stack == 0 ? 1 : stack);
        string label = result.DeliveredItemName ?? item;
        if (!result.IsSuccess)
        {
            string? message = result.ErrorCode switch
            {
                "item.pickups_disabled" => giveOthers ? "The player needs to disable the stone." : "Turn off the stone.",
                "item.inventory_full" => giveOthers ? "Player does not have free slots!" : "Your inventory seems full.",
                "restriction.item_give" => "You cannot spawn banned items.",
                "item.invalid_type" => "Invalid item type!",
                _ => null
            };
            return CommandExecutionResult.Failed(result.ErrorCode ?? "item.failed", MessageDescriptor.Error("item.failed", result.ErrorCode).WithDefaultText(message ?? "Item delivery failed ({0})."));
        }
        if (!giveOthers)
            return CommandExecutionResult.WithMessages(MessageDescriptor.Success("item.given", delivered, label, delivered > 1 ? "s" : "").WithDefaultText("Gave {0} {1}{2}."));
        if (delivery is IPlayerMessenger { SupportsSessionBoundMessages: true } messenger)
            await messenger.SendMessageAsync(target!, $"{invocation.Principal.Name} gave you {delivered} {label}{(delivered > 1 ? "s" : "")}.", token).ConfigureAwait(false);
        return CommandExecutionResult.WithMessages(MessageDescriptor.Success("item.given", delivered, target!.Name, label, delivered > 1 ? "s" : "").WithDefaultText("Gave {0} {1} {2}{3}."));
    }
}
