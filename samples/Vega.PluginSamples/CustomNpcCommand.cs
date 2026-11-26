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
        Syntax: "samplemerchant create <pixelX> <pixelY> | remove | info | move <pixelX> | present | buy <offer>");

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
                var catalog = new NpcShopCatalog([new("torch", 8, 5, 100, CoinPrice: 100), new("rope", 965, 20, 250, CoinPrice: 250)]);
                var result = await plugin.World.Npcs.CreateActorAsync(new NpcActorDefinition(17, "Vega Merchant", Shop: catalog),
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
            if (invocation.Principal.IsServer || !plugin.Players.TryFindExact(invocation.Principal.Name, out var player) || player is null)
                return Failure("Shop operations require the interacting player.");
            if (args.Count == 1 && args[0] == "present")
            {
                var status = await actor.PresentShopAsync(player, cancellationToken).ConfigureAwait(false);
                return status == NpcShopPresentationStatus.Sent ? Info("Catalog sent to the existing shop window.") : Failure(status.ToString());
            }
            if (args.Count == 2 && args[0] == "buy")
            {
                var purchase = await actor.PurchaseAsync(player, args[1], snapshot.ShopRevision, cancellationToken).ConfigureAwait(false);
                return purchase.Status == NpcShopPurchaseStatus.Committed ? Info($"Purchased {purchase.Stack} item(s) for {purchase.CoinsPaid} copper.") : Failure(purchase.Status.ToString());
            }
            return Failure(Descriptor.Syntax);
        }
        finally { gate.Release(); }
    }

    private static CommandExecutionResult Info(string text) => CommandExecutionResult.WithMessages(MessageDescriptor.Info("samplemerchant.info").WithDefaultText(text));
    private static CommandExecutionResult Failure(string text) => CommandExecutionResult.Failed("samplemerchant.failed", MessageDescriptor.Error("samplemerchant.failed").WithDefaultText(text));
}
