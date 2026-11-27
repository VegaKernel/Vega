using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

internal sealed partial class HotModuleRegistrationScope
{
    private sealed class PluginStatistics(HotModuleRegistrationScope owner, WorldId? world,
        IPlayerDirectory? directory, IPlayerPlaytimeSessionReader? reader) : IPluginStatistics
    {
        public bool IsAvailable => world.HasValue && directory is not null && reader is not null;

        public ValueTask<PlayerPlaytimeView?> ReadPlaytimeAsync(PlayerSnapshot player, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(player);
            if (!IsAvailable) throw new NotSupportedException("Player statistics are unavailable in this host.");
            return owner.ReadServiceAsync<PlayerPlaytimeView?>(async token =>
            {
                if (!IsCurrent(player)) return null;
                var result = await reader!.ReadCurrentAsync(player, token).ConfigureAwait(false);
                return IsCurrent(player) ? result : null;
            }, cancellationToken);
        }

        private bool IsCurrent(PlayerSnapshot player) => player.WorldId == world && player.SessionId.HasValue &&
            directory!.TryGet(player.Id, out var current) && current is not null && current.SessionId == player.SessionId && current.WorldId == world;
    }
}
