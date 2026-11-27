using Vega.Contracts.Players;

namespace Vega.PluginSdk;

/// <summary>Typed, native-authoritative actions on an exact player session; no raw packet access.</summary>
public interface IPluginPlayerActions
{
    bool VitalsAvailable { get; }
    bool ItemDeliveryAvailable { get; }

    ValueTask<PlayerOperationResult> HealAsync(PlayerSnapshot player, int amount, CancellationToken cancellationToken = default);
    ValueTask<PlayerOperationResult> BuffAsync(PlayerSnapshot player, string buff, int seconds, CancellationToken cancellationToken = default);
    /// <summary>Requests native death; Applied does not acknowledge the client's state transition.</summary>
    ValueTask<PlayerOperationResult> KillAsync(PlayerSnapshot player, CancellationToken cancellationToken = default);
    ValueTask<PlayerOperationResult> SlapAsync(PlayerSnapshot player, int damage, CancellationToken cancellationToken = default);
    /// <summary>Requests native respawn; Applied does not acknowledge the client's state transition.</summary>
    ValueTask<PlayerOperationResult> RespawnAsync(PlayerSnapshot player, CancellationToken cancellationToken = default);
    /// <summary>Delivers an ordinary native item, subject to native limits and content restrictions.</summary>
    ValueTask<PlayerOperationResult> GiveAsync(PlayerSnapshot player, string item, int stack = 1, int prefix = 0,
        CancellationToken cancellationToken = default);
}
