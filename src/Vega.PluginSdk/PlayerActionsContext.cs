using Vega.Contracts.Players;
using Vega.Contracts.Items;

namespace Vega.PluginSdk;

/// <summary>Typed, native-authoritative actions on an exact player session; no raw packet access.</summary>
public interface IPluginPlayerActions
{
    bool VitalsAvailable { get; }
    bool ItemDeliveryAvailable { get; }
    /// <summary>Activation-local named native item recipes and optional transient world-item stats.</summary>
    IPluginCustomItems CustomItems => throw new NotSupportedException("Named item recipes are unavailable in this host.");

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

/// <summary>Plugin-owned catalog of named native item recipes; lifetime and delivery are owned by the host.</summary>
public interface IPluginCustomItems
{
    bool IsAvailable { get; }
    bool SupportsWorldItemStats => false;
    /// <summary>Persistent server-owned virtual identities, independent of physical inventory and world-item tweaks.</summary>
    IPluginCustomItemInstances Instances => throw new NotSupportedException("Custom item instances are unavailable in this host.");
    /// <summary>Registers a definition for the current activation; duplicate names are rejected.</summary>
    void Register(CustomItemRecipe recipe);
    IReadOnlyList<CustomItemRecipe> Snapshot();
    /// <summary>Delivers the recipe under the exact-session native constraints. WorldStats are transient world-item stats only.</summary>
    ValueTask<PlayerOperationResult> GiveAsync(PlayerSnapshot player, string name, CancellationToken cancellationToken = default);
}
