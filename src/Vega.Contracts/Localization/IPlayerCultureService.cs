namespace Vega.Contracts.Localization;

public interface IPlayerCultureService
{
    PlayerCulture ServerCulture { get; }

    IReadOnlyList<PlayerCulture> AvailableCultures { get; }

    PlayerCulture Resolve(
        string subjectId,
        string? accountCulture = null,
        string? clientCulture = null);

    bool TrySetSession(string subjectId, string culture, out PlayerCulture? normalized);

    void ResetSession(string subjectId);

    void RemoveSession(string subjectId);
}
