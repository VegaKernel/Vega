using Vega.Contracts.Players;

namespace Vega.Contracts.Items;

/// <summary>Session-bound delivery of an ordinary native item, not custom item packet overrides.</summary>
public interface IPlayerItemDelivery
{
    ValueTask<PlayerOperationResult> GiveAsync(PlayerSnapshot player, string item, int stack, int prefix, CancellationToken cancellationToken = default);

    ValueTask<PlayerOperationResult> GiveCommandAsync(PlayerSnapshot player, string item, int stack, int prefix, CancellationToken cancellationToken = default) =>
        GiveAsync(player, item, stack == 0 ? 1 : stack, prefix, cancellationToken);
}
