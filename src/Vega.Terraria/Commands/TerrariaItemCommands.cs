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

        var grants = new ItemGrantCommandService(
            new TerrariaPlayerInventoryService(gameThread),
            new TerrariaItemCatalog());
        IDisposable[] registrations =
        [
            registry.Register(new GiveCommand(players, players, grants)),
            registry.Register(new ItemCommand(players, grants))
        ];
        return new RegistrationSet(registrations);
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

internal sealed class GiveCommand(
    IPlayerDirectory players,
    IPlayerMessenger messenger,
    ItemGrantCommandService grants) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "give",
        ["g"],
        ["vega.inventory.give"],
        Syntax: "give <item-id|\"item name\"> <player> [amount] [prefix-id|name]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count is < 2 or > 4 ||
            (invocation.Arguments.Count >= 3 &&
             (!int.TryParse(invocation.Arguments[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount) ||
              amount <= 0)))
        {
            return Usage();
        }

        IReadOnlyList<PlayerSnapshot> matches = OnlinePlayerSearch.Resolve(players, invocation.Arguments[1]);
        if (matches.Count == 0)
        {
            return ItemCommandResult.Failure("player.not_found", "Player not found.");
        }

        if (matches.Count > 1)
        {
            return ItemCommandResult.Failure(
                "player.ambiguous",
                $"Multiple players matched: {string.Join(", ", matches.Select(static player => player.Name))}.");
        }

        int? requestedAmount = invocation.Arguments.Count >= 3
            ? int.Parse(invocation.Arguments[2], CultureInfo.InvariantCulture)
            : null;
        string? prefixQuery = invocation.Arguments.Count == 4 ? invocation.Arguments[3] : null;
        ItemGrantResolution resolution = grants.Resolve(
            invocation.Arguments[0],
            requestedAmount,
            prefixQuery);
        if (resolution.Error is CommandExecutionResult error)
        {
            return error;
        }

        ItemGrantPlan plan = resolution.Plan!;
        PlayerSnapshot target = matches[0];
        InventoryMutationResult result = await grants
            .GrantAsync(target.Id, plan, cancellationToken)
            .ConfigureAwait(false);
        CommandExecutionResult commandResult = ItemCommandResult.FromMutation(target, plan, result);
        if (commandResult.Status != CommandExecutionStatus.Success)
        {
            return commandResult;
        }

        string actor = invocation.Principal is IPlayerCommandPrincipal player
            ? player.PlayerName
            : invocation.Principal.Name;
        await messenger.SendMessageAsync(
                target.Id,
                $"{actor} gave you {plan.Stack}x {plan.Item.Name}.",
                cancellationToken)
            .ConfigureAwait(false);

        return commandResult;
    }

    private static CommandExecutionResult Usage() => ItemCommandResult.InvalidUsage(
        "command.give.usage",
        "Usage: give <item-id|\"item name\"> <online-player> [amount] [prefix-id|name]. Alias: g.");
}

internal sealed class ItemCommand(
    IPlayerDirectory players,
    ItemGrantCommandService grants) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "item",
        ["i"],
        ["vega.inventory.item"],
        AllowServer: false,
        Syntax: "item <item name|id> [amount] [prefix-id|name]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 0 || invocation.Principal is not IPlayerCommandPrincipal principal)
        {
            return Usage();
        }

        int amountIndex = -1;
        int amount = 0;
        for (int i = 1; i < invocation.Arguments.Count; i++)
        {
            if (int.TryParse(invocation.Arguments[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out amount))
            {
                amountIndex = i;
                break;
            }
        }

        if ((amountIndex >= 0 && (amount <= 0 || invocation.Arguments.Count > amountIndex + 2)) ||
            (amountIndex < 0 && string.IsNullOrWhiteSpace(string.Join(" ", invocation.Arguments))))
        {
            return Usage();
        }

        string itemQuery = amountIndex < 0
            ? string.Join(" ", invocation.Arguments)
            : string.Join(" ", invocation.Arguments.Take(amountIndex));
        string? prefixQuery = amountIndex >= 0 && invocation.Arguments.Count == amountIndex + 2
            ? invocation.Arguments[amountIndex + 1]
            : null;
        ItemGrantResolution resolution = grants.Resolve(
            itemQuery,
            amountIndex >= 0 ? amount : null,
            prefixQuery);
        if (resolution.Error is CommandExecutionResult error)
        {
            return error;
        }

        if (!players.TryGet(principal.PlayerId, out PlayerSnapshot? target) || target is null)
        {
            return ItemCommandResult.Failure("player.not_found", "Player is no longer online.");
        }

        ItemGrantPlan plan = resolution.Plan!;
        InventoryMutationResult result = await grants
            .GrantAsync(target.Id, plan, cancellationToken)
            .ConfigureAwait(false);
        return ItemCommandResult.FromMutation(target, plan, result);
    }

    private static CommandExecutionResult Usage() => ItemCommandResult.InvalidUsage(
        "command.item.usage",
        "Usage: item <item name|id> [amount] [prefix-id|name]. Alias: i.");
}

internal sealed record ItemGrantPlan(ItemDefinition Item, int Stack, byte Prefix);

internal readonly record struct ItemGrantResolution(
    ItemGrantPlan? Plan,
    CommandExecutionResult? Error);

internal sealed class ItemGrantCommandService(
    IPlayerInventoryGrantService inventories,
    IItemCatalog items)
{
    public ItemGrantResolution Resolve(string itemQuery, int? requestedAmount, string? prefixQuery)
    {
        ItemResolutionResult itemResolution = items.Resolve(itemQuery);
        if (itemResolution.Status == ItemResolutionStatus.NotFound)
        {
            return Failed("item.not_found", $"Item '{itemQuery}' was not found.");
        }

        if (itemResolution.Status == ItemResolutionStatus.Ambiguous)
        {
            string candidates = string.Join(
                ", ",
                itemResolution.Candidates.Select(static item => $"{item.Name} ({item.Type})"));
            return Failed(
                "item.ambiguous",
                candidates.Length == 0
                    ? $"Item '{itemQuery}' is ambiguous."
                    : $"Item '{itemQuery}' is ambiguous: {candidates}.");
        }

        ItemDefinition item = itemResolution.Item!;
        int stack = requestedAmount is null
            ? item.OnlyNeedOneInInventory ? 1 : item.MaxStack
            : Math.Min(requestedAmount.Value, item.MaxStack);
        byte prefix = 0;
        if (!string.IsNullOrWhiteSpace(prefixQuery))
        {
            ItemPrefixResolutionResult prefixResolution = items.ResolvePrefix(prefixQuery, item);
            if (prefixResolution.Status == ItemResolutionStatus.NotFound)
            {
                return Failed("prefix.not_found", $"No prefix matched '{prefixQuery}'.");
            }

            if (prefixResolution.Status == ItemResolutionStatus.Ambiguous)
            {
                string candidates = string.Join(
                    ", ",
                    prefixResolution.Candidates.Select(static candidate => $"{candidate.Name} ({candidate.Id})"));
                return Failed(
                    "prefix.ambiguous",
                    candidates.Length == 0
                        ? $"Prefix '{prefixQuery}' is ambiguous."
                        : $"Prefix '{prefixQuery}' is ambiguous: {candidates}.");
            }

            prefix = prefixResolution.Prefix!.Id;
        }

        return new ItemGrantResolution(new ItemGrantPlan(item, stack, prefix), null);
    }

    public async ValueTask<InventoryMutationResult> GrantAsync(
        PlayerId playerId,
        ItemGrantPlan plan,
        CancellationToken cancellationToken)
    {
        PlayerInventorySnapshot? snapshot = await inventories
            .CaptureAsync(playerId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return new InventoryMutationResult(
                InventoryMutationStatus.PlayerNotFound,
                ErrorCode: "player.not_found");
        }

        InventoryGrantRequest request = new(plan.Item.Type, plan.Stack, plan.Prefix);
        InventoryMutationResult result = await inventories
            .GrantAsync(playerId, snapshot.Revision, request, cancellationToken)
            .ConfigureAwait(false);
        if (result.Status != InventoryMutationStatus.RevisionConflict)
        {
            return result;
        }

        snapshot = await inventories.CaptureAsync(playerId, cancellationToken).ConfigureAwait(false);
        return snapshot is null
            ? new InventoryMutationResult(
                InventoryMutationStatus.PlayerNotFound,
                ErrorCode: "player.not_found")
            : await inventories
                .GrantAsync(playerId, snapshot.Revision, request, cancellationToken)
                .ConfigureAwait(false);
    }

    private static ItemGrantResolution Failed(string code, string text) =>
        new(null, ItemCommandResult.Failure(code, text));
}

internal static class ItemCommandResult
{
    public static CommandExecutionResult FromMutation(
        PlayerSnapshot target,
        ItemGrantPlan plan,
        InventoryMutationResult result) => result.Status switch
    {
        InventoryMutationStatus.Applied => CommandExecutionResult.WithMessages(
            MessageDescriptor.Success(
                    "command.item-grant.applied",
                    target.Name,
                    plan.Item.Name,
                    plan.Item.Type,
                    plan.Stack,
                    result.Snapshot?.Revision)
                .WithDefaultText("Gave {3}x {1} (item {2}) to {0}; inventory revision={4}.")),
        InventoryMutationStatus.InventoryFull =>
            Failure("inventory.full", $"{target.Name} does not have enough main-inventory space for {plan.Stack}x {plan.Item.Name}."),
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

    public static CommandExecutionResult InvalidUsage(string code, string text) =>
        new(
            CommandExecutionStatus.InvalidUsage,
            code,
            [MessageDescriptor.Error(code).WithDefaultText(text)]);

    public static CommandExecutionResult Failure(string code, string text) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code).WithDefaultText(text));
}
