using Vega.Contracts.Worlds;

namespace Vega.PluginSdk;

/// <summary>Registers an account wallet provider for explicit use by custom NPC actors. Vega owns activation lifetime.</summary>
public interface IPluginNpcEconomy
{
    IReadOnlyList<NpcEconomyProviderAddress> Snapshot();

    /// <summary>Provider must retain idempotent durable operation IDs across restarts/reloads and honor cancellation.</summary>
    void Register(string name, int version, INpcEconomyProvider provider);
}
