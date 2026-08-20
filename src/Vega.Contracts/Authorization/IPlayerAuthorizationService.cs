using Vega.Contracts.Players;

namespace Vega.Contracts.Authorization;

/// <summary>
/// Resolves effective authorization for a connected player session.
/// Group storage and inheritance remain separate concerns owned by <see cref="IAuthorizationService"/>.
/// </summary>
public interface IPlayerAuthorizationService
{
    bool HasPermission(PlayerId playerId, string permission);
}
