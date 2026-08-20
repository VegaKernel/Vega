using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Runtime;

namespace Vega.Persistence;

public interface IVegaDatabase : IRuntimeStartupParticipant
{
    string Provider { get; }

    int SchemaVersion { get; }

    bool IsReady { get; }

    IGlobalBanReplica GlobalBans { get; }
}
