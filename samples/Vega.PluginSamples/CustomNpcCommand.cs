using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.PluginSdk;

namespace Vega.PluginSamples;

internal sealed class CustomNpcCommand(IVegaPluginContext plugin) : ICommandHandler
{
    private INpcActor? merchant;
    private readonly SemaphoreSlim gate = new(1, 1);
    public CommandDescriptor Descriptor { get; } = new("samplemerchant", [], ["vega.samples.world"],
        Syntax: "samplemerchant create <pixelX> <pixelY> | remove | info | move <pixelX> | patrol <leftX> <rightX> | present | buy <offer> [operation-guid] | talk | choose <revision> <choice> | quests | accept|turnin <quest> | balance <currency> | credit <currency> <amount> <operation-guid>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var args = invocation.Arguments;
            if (args.Count == 3 && args[0] == "create" && int.TryParse(args[1], out int x) && int.TryParse(args[2], out int y))
            {
                if (merchant is not null && await merchant.CaptureAsync(cancellationToken) is not null) return Failure("Merchant already exists.");
                merchant?.Dispose();
                var catalog = new NpcShopCatalog([new("torch", 8, 5, 100, CoinPrice: 100), new("rope", 965, 20, 250, CoinPrice: 250),
                    new NpcShopOffer("wood", 9, 10, 0) { ItemPrice = new(8, 3) },
                    new NpcShopOffer("token-rope", 965, 20, 0) { EconomyPrice = new("tokens", 5) }]);
                var dialogue = new NpcDialogueDefinition("greeting", [new("greeting", "Welcome, traveler!", [new("quest", "Any work?", "work"), new("bye", "Goodbye")]),
                    new("work", "Bring me five torches. Use /samplemerchant accept light, then /samplemerchant turnin light.", [new("back", "Back", "greeting"), new("bye", "Goodbye")])]);
                var quests = new NpcQuestCatalog([new("light", "Light the way", "Bring five torches for a healing potion.", new(8, 5), new(188, 1))]);
                var result = await plugin.World.Npcs.CreateActorAsync(new NpcActorDefinition(17, "Vega Merchant", Shop: catalog)
                    { Dialogue = dialogue, Quests = quests, PersistentQuestScope = "Vega.Samples.Merchant.Quests", EconomyScope = "Vega.Samples.Merchant.Wallet" },
                    new PlayerPosition(x, y), cancellationToken).ConfigureAwait(false);
                merchant = result.Actor;
                return merchant is not null ? Info("Custom merchant created.") : Failure(result.ErrorCode ?? result.Status.ToString());
            }
            if (merchant is not { } actor) return Failure("Create a merchant first.");
            if (args.Count == 1 && args[0] == "remove") { actor.Dispose(); merchant = null; return Info("Merchant retired."); }
            var snapshot = await actor.CaptureAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot is null) return Failure("Merchant no longer exists.");
            if (args.Count == 1 && args[0] == "info") return Info($"{snapshot.Name}, slot={snapshot.Npc.Slot}, catalog revision={snapshot.ShopRevision}.");
            if (args.Count == 2 && args[0] == "move" && int.TryParse(args[1], out int target))
                return await actor.SetIntentAsync(new NpcActorIntent(NpcActorIntentKind.MoveTo, new(target, snapshot.Npc.Position.Y)), cancellationToken)
                    ? Info("Movement intent updated.") : Failure("Actor unavailable.");
            if (args.Count == 3 && args[0] == "patrol" && int.TryParse(args[1], out int left) && int.TryParse(args[2], out int right))
                return await actor.SetIntentAsync(new NpcActorIntent(NpcActorIntentKind.Patrol)
                    { Waypoints = [new(left, snapshot.Npc.Position.Y), new(right, snapshot.Npc.Position.Y)] }, cancellationToken)
                    ? Info("Patrol route updated.") : Failure("Invalid route or actor unavailable.");
            if (invocation.Principal.IsServer || !plugin.Players.TryFindExact(invocation.Principal.Name, out var player) || player is null)
                return Failure("Shop operations require the interacting player.");
            if (args.Count == 1 && args[0] == "present")
            {
                var status = await actor.PresentShopAsync(player, cancellationToken).ConfigureAwait(false);
                return status == NpcShopPresentationStatus.Sent ? Info("Catalog sent to the existing shop window.") : Failure(status.ToString());
            }
            if (args.Count is 2 or 3 && args[0] == "buy")
            {
                NpcShopPurchaseResult purchase;
                if (args.Count == 3)
                {
                    if (!Guid.TryParse(args[2], out var operationId)) return Failure("Invalid wallet operation GUID.");
                    purchase = await actor.PurchaseCurrencyAsync(player, args[1], snapshot.ShopRevision, operationId, cancellationToken).ConfigureAwait(false);
                }
                else purchase = await actor.PurchaseAsync(player, args[1], snapshot.ShopRevision, cancellationToken).ConfigureAwait(false);
                string paid = purchase.EconomyPaid is { } wallet ? $"{wallet.Amount} {wallet.CurrencyId}" :
                    purchase.ItemPaid is { } currency ? $"{currency.Amount} of item {currency.ItemType}" : $"{purchase.CoinsPaid} copper";
                return purchase.Status == NpcShopPurchaseStatus.Committed ? Info($"Purchased {purchase.Stack} item(s) for {paid}.") :
                    Failure(purchase.OperationId is { } id ? $"{purchase.Status}; operation {id:D}" : purchase.Status.ToString());
            }
            if (args.Count == 2 && args[0] == "balance")
            {
                var balance = await actor.GetCurrencyBalanceAsync(player, args[1], cancellationToken);
                return Info($"{args[1]}: {balance.Balance} ({balance.Status}){(balance.PendingOperationId is { } id ? $"; pending {id:D}" : "")}");
            }
            if (args.Count == 4 && args[0] == "credit" && long.TryParse(args[2], out long amount) && Guid.TryParse(args[3], out var creditId))
            {
                var credit = await actor.CreditCurrencyAsync(player, new(args[1], amount), creditId, cancellationToken);
                return credit.Status is NpcEconomyStatus.Applied or NpcEconomyStatus.AlreadyApplied
                    ? Info($"Balance {args[1]}: {credit.Balance} ({credit.Status})") : Failure(credit.Status.ToString());
            }
            if (args.Count == 1 && args[0] == "talk") return Dialogue(await actor.BeginDialogueAsync(player, cancellationToken));
            if (args.Count == 3 && args[0] == "choose" && long.TryParse(args[1], out long revision))
                return Dialogue(await actor.ChooseDialogueAsync(player, revision, args[2], cancellationToken));
            if (args.Count == 1 && args[0] == "quests")
            {
                var available = await actor.GetQuestsAsync(player, cancellationToken);
                return Info(available.Count == 0 ? "No nearby quests." : string.Join(" | ", available.Select(q => $"{q.Definition.Id}: {q.Definition.Title} ({q.State})")));
            }
            if (args.Count == 2 && args[0] is "accept" or "turnin")
            {
                var quest = args[0] == "accept" ? await actor.AcceptQuestAsync(player, args[1], snapshot.QuestRevision, cancellationToken)
                    : await actor.TurnInQuestAsync(player, args[1], snapshot.QuestRevision, cancellationToken);
                return quest.Status == NpcQuestStatus.Applied ? Info($"{quest.Quest?.Definition.Title}: {quest.Quest?.State}") :
                    Failure(quest.OperationId is { } id ? $"{quest.InventoryFailure?.ToString() ?? quest.Status.ToString()}; operation {id:D}" :
                        quest.InventoryFailure?.ToString() ?? quest.Status.ToString());
            }
            return Failure(Descriptor.Syntax);
        }
        finally { gate.Release(); }
    }

    private static CommandExecutionResult Dialogue(NpcDialogueResult result)
    {
        if (result.Status == NpcDialogueStatus.Closed) return Info("Conversation ended.");
        if (result.Status != NpcDialogueStatus.Presented || result.View is not { } view) return Failure(result.Status.ToString());
        return Info(view.Text + (view.Choices.Count == 0 ? "" : " | " + string.Join(" | ", view.Choices.Select(choice =>
            $"{choice.Text}: /samplemerchant choose {view.Revision} {choice.Id}"))));
    }

    private static CommandExecutionResult Info(string text) => CommandExecutionResult.WithMessages(MessageDescriptor.Info("samplemerchant.info").WithDefaultText(text));
    private static CommandExecutionResult Failure(string text) => CommandExecutionResult.Failed("samplemerchant.failed", MessageDescriptor.Error("samplemerchant.failed").WithDefaultText(text));
}
