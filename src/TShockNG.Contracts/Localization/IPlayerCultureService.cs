namespace TShockNG.Contracts.Localization;

public interface IPlayerCultureService
{
    PlayerCulture Resolve(string? accountCulture, string? clientCulture, string serverCulture);
}
