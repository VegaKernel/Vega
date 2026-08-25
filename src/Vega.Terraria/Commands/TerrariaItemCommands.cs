using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Items;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Terraria.Items;
using Vega.Terraria.Players;
using Vega.Terraria.Threading;

namespace Vega.Terraria.Commands;

internal static class TerrariaItemCommandRegistration
{
    public static IDisposable Register(
        ICommandDispatcher dispatcher,
        TerrariaRuntimeGameThreadDispatcher gameThread,
        TerrariaPlayerBoundary players)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(gameThread);
        ArgumentNullException.ThrowIfNull(players);

        if (dispatcher is not ICommandRegistry registry)
        {
            throw new InvalidOperationException(
                "The Vega command dispatcher must expose ICommandRegistry for built-in item commands.");
        }

        var inventories = new TerrariaPlayerInventoryService(gameThread);
        var items = new TerrariaItemCatalog();
        return registry.Register(new GiveCommand(players, inventories, items));
    }
}

internal sealed class GiveCommand(
    IPlayerDirectory players,
    IPlayerInventoryGrantService inventories,
    IItemCatalog items) : ICommandHandler
{
    private const int MaximumAttempts = 3;

    public CommandDescriptor Descriptor { get; } = new(
        "give",
        ["item"],
        ["vega.inventory.give"],
        Syntax: "give <player> <item-id|\"item name\"> [stack] [prefix]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count is < 2 or > 4 ||
            (invocation.Arguments.Count >= 3 &&
             (!int.TryParse(invocation.Arguments[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedStack) ||
              parsedStack <= 0)) ||
            (invocation.Arguments.Count == 4 &&
             !byte.TryParse(invocation.Arguments[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            return Usage();
        }

        if (!players.TryFindExact(invocation.Arguments[0], out PlayerSnapshot? target) || target is null)
        {
            return Failure("player.not_found", "Player not found.");
        }

        ItemResolutionResult resolution = items.Resolve(invocation.Arguments[1]);
        if (resolution.Status == ItemResolutionStatus.NotFound)
        {
            return Failure("item.not_found", $"Item '{invocation.Arguments[1]}' was not found.");
        }

        if (resolution.Status == ItemResolutionStatus.Ambiguous)
        {
            string candidates = string.Join(
                ", ",
                resolution.Candidates.Select(static item => $"{item.Name} ({item.Type})"));
            return Failure(
                "item.ambiguous",
                candidates.Length == 0
                    ? $"Item '{invocation.Arguments[1]}' is ambiguous."
                    : $"Item '{invocation.Arguments[1]}' is ambiguous: {candidates}.");
        }

        ItemDefinition item = resolution.Item!;
        int stack = invocation.Arguments.Count >= 3
            ? int.Parse(invocation.Arguments[2], CultureInfo.InvariantCulture)
            : 1;
        byte prefix = invocation.Arguments.Count == 4
            ? byte.Parse(invocation.Arguments[3], CultureInfo.InvariantCulture)
            : (byte)0;

        PlayerInventorySnapshot? snapshot = await inventories
            .CaptureAsync(target.Id, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return Failure("player.not_found", "Player is no longer online.");
        }

        InventoryMutationResult result = default;
        for (int attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            result = await inventories.GrantAsync(
                    target.Id,
                    snapshot.Revision,
                    new InventoryGrantRequest(item.Type, stack, prefix),
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.Status != InventoryMutationStatus.RevisionConflict || result.Snapshot is null)
            {
                break;
            }

            snapshot = result.Snapshot;
        }

        return result.Status switch
        {
            InventoryMutationStatus.Applied => CommandExecutionResult.WithMessages(
                MessageDescriptor.Success(
                        "command.give.applied",
                        target.Name,
                        item.Name,
                        item.Type,
                        stack,
                        result.Snapshot?.Revision ?? snapshot.Revision)
                    .WithDefaultText("Gave {0} {3}x {1} (item {2}); inventory revision={4}.")),
            InventoryMutationStatus.InventoryFull =>
                Failure("inventory.full", $"{target.Name} does not have enough main-inventory space for {stack}x {item.Name}."),
            InventoryMutationStatus.RevisionConflict =>
                Failure("inventory.revision_conflict", $"{target.Name}'s inventory kept changing; the grant was not applied."),
            InventoryMutationStatus.InvalidItem =>
                Failure("inventory.invalid_item", "The requested item, stack or prefix is invalid."),
            InventoryMutationStatus.PlayerNotFound =>
                Failure("player.not_found", "Player is no longer online."),
            InventoryMutationStatus.DispatcherUnavailable =>
                Failure("inventory.dispatcher_unavailable", "The authoritative game thread is unavailable."),
            InventoryMutationStatus.Cancelled =>
                Failure("inventory.cancelled", "The item grant was cancelled."),
            _ => Failure(result.ErrorCode ?? "inventory.failed", "The item grant failed.")
        };
    }

    private static CommandExecutionResult Usage() => Failure(
        "command.give.usage",
        "Usage: give <online-player> <item-id|\"item name\"> [stack] [prefix]. Alias: item.");

    private static CommandExecutionResult Failure(string code, string text) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code).WithDefaultText(text));
}
