namespace Vega.Contracts.Authorization;

public interface IAuthorizationService
{
    bool HasPermission(string groupName, string permission);

    bool TryGetGroup(string groupName, out GroupSnapshot? group);
}
