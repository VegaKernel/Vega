using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;

namespace Vega.Core.Commands.Administration;

public sealed class UserInfoCommand(
    IPlayerDirectory players,
    IPlayerInspectionService inspection) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "userinfo",
        ["ui"],
        ["vega.players.inspect"],
        Syntax: "userinfo <player>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 1 ||
            !players.TryFindExact(invocation.Arguments[0], out PlayerSnapshot? target) ||
            target is null)
        {
            return Failure("command.userinfo.usage", "Usage: userinfo <online-player>");
        }

        bool sensitive = invocation.Principal.HasPermission("vega.players.sensitive");
        PlayerInspectionSnapshot? snapshot = await inspection.CaptureAsync(
            target.Id,
            sensitive ? PlayerInspectionExposure.Sensitive : PlayerInspectionExposure.Redacted,
            cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return Failure("player.not_found", "Player is no longer online.");
        }

        string endpoint = snapshot.Player.RemoteEndpoint ?? "<redacted>";
        string uuid = snapshot.Player.Connection?.ClientUuid ?? "<redacted>";
        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Info("command.userinfo.summary")
                .WithDefaultText(
                    $"{snapshot.Player.Name}: slot={snapshot.Player.Id.Slot}, world={snapshot.Player.WorldId}, " +
                    $"platform={snapshot.Player.Platform.Kind}, endpoint={endpoint}, uuid={uuid}, " +
                    $"inventoryRevision={snapshot.Inventory.Revision}."));
    }

    private static CommandExecutionResult Failure(string code, string text) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code).WithDefaultText(text));
}

public sealed class InventoryCommand(
    IPlayerDirectory players,
    IPlayerInventoryService inventories) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "invsee",
        ["inventory"],
        ["vega.inventory.inspect"],
        Syntax: "invsee <player> [set <revision> <slot> <item-type> <stack> [prefix]]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 1)
        {
            return await InspectAsync(invocation.Arguments[0], cancellationToken).ConfigureAwait(false);
        }

        if (invocation.Arguments.Count is < 6 or > 7 ||
            !string.Equals(invocation.Arguments[1], "set", StringComparison.OrdinalIgnoreCase) ||
            !invocation.Principal.HasPermission("vega.inventory.mutate") ||
            !long.TryParse(invocation.Arguments[2], out long revision) ||
            !int.TryParse(invocation.Arguments[3], out int slot) ||
            !int.TryParse(invocation.Arguments[4], out int itemType) ||
            !int.TryParse(invocation.Arguments[5], out int stack) ||
            (invocation.Arguments.Count == 7 && !byte.TryParse(invocation.Arguments[6], out _)))
        {
            return Usage();
        }

        if (!players.TryFindExact(invocation.Arguments[0], out PlayerSnapshot? target) || target is null)
        {
            return Failure("player.not_found", "Player not found.");
        }

        byte prefix = invocation.Arguments.Count == 7 ? byte.Parse(invocation.Arguments[6]) : (byte)0;
        InventoryMutationResult result = await inventories.ReplaceSlotAsync(
            target.Id,
            revision,
            new InventorySlotReplacement(slot, itemType, stack, prefix),
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.invsee.updated")
                    .WithDefaultText($"{target.Name} inventory slot {slot} updated; revision={result.Snapshot!.Revision}."))
            : Failure(result.ErrorCode ?? "inventory.failed", result.ErrorCode ?? "Inventory update failed.");
    }

    private async ValueTask<CommandExecutionResult> InspectAsync(
        string name,
        CancellationToken cancellationToken)
    {
        if (!players.TryFindExact(name, out PlayerSnapshot? target) || target is null)
        {
            return Failure("player.not_found", "Player not found.");
        }

        PlayerInventorySnapshot? snapshot = await inventories
            .CaptureAsync(target.Id, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return Failure("player.not_found", "Player is no longer online.");
        }

        string items = string.Join(
            ", ",
            snapshot.Items
                .Where(static item => item.ItemType != 0 && item.Stack > 0)
                .Select(static item => $"{item.Slot}:{item.Name}x{item.Stack}")
                .Take(40));
        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Info("command.invsee.snapshot")
                .WithDefaultText(
                    $"{snapshot.PlayerName} inventory revision={snapshot.Revision}: " +
                    (items.Length == 0 ? "empty" : items)));
    }

    private static CommandExecutionResult Usage() => Failure(
        "command.invsee.usage",
        "Usage: invsee <player> [set <revision> <slot> <item-type> <stack> [prefix]]");

    private static CommandExecutionResult Failure(string code, string text) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code).WithDefaultText(text));
}
