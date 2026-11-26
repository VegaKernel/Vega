using Vega.Contracts.Players;

namespace Vega.Contracts.Items;

/// <summary>Session-bound delivery of an ordinary native item, not custom item packet overrides.</summary>
public interface IPlayerItemDelivery
{
    ValueTask<PlayerOperationResult> GiveAsync(PlayerSnapshot player, string item, int stack, int prefix, CancellationToken cancellationToken = default);
}
