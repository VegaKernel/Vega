namespace Vega.Contracts.Players;

/// <summary>
/// Session-scoped incoming teleport consent shared by teleport commands and plugin consumers.
/// </summary>
public interface IPlayerTeleportConsentService
{
    bool AllowsIncoming(PlayerSnapshot player);

    bool Toggle(PlayerSnapshot player);
}
