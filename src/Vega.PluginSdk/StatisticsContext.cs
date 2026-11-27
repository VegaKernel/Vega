using Vega.Contracts.Players;

namespace Vega.PluginSdk;

/// <summary>Read-only statistics for the exact authenticated player in the hosted world.</summary>
public interface IPluginStatistics
{
    bool IsAvailable { get; }
    ValueTask<PlayerPlaytimeView?> ReadPlaytimeAsync(PlayerSnapshot player, CancellationToken cancellationToken = default);
}
