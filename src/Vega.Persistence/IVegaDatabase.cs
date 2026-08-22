using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Runtime;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Regions;
using Vega.Contracts.Moderation;

namespace Vega.Persistence;

public interface IVegaDatabase : IRuntimeStartupParticipant
{
    string Provider { get; }

    int SchemaVersion { get; }

    bool IsReady { get; }

    IGlobalBanReplica GlobalBans { get; }

    IAccountService Accounts { get; }

    IAuthorizationService Authorization { get; }

    IRoleLeaseService RoleLeases { get; }

    IMuteService Mutes { get; }

    IRegionService Regions { get; }
}
