using Vega.Contracts.Networking;
using Vega.Contracts.Worlds;

namespace Vega.Contracts.Players;

/// <summary>Non-sensitive detached metadata for one observed native connection, not account or device identity.</summary>
public sealed record PlayerConnectionSummary(
    PlayerId PlayerId, PlayerSessionId SessionId, WorldId WorldId, ClientPlatformInfo Platform,
    DateTimeOffset? ConnectedAt, ConnectionLoginPhase? LoginPhase)
{
    /// <summary>Wall-clock connection age only. This is not authenticated or active play time.</summary>
    public TimeSpan? ConnectionAge(DateTimeOffset now) => ConnectedAt is { } connected
        ? now >= connected ? now - connected : TimeSpan.Zero
        : null;
}

public readonly record struct PlayerPlatformCount(ClientPlatformKind Platform, int Count);

/// <summary>World-scoped counts of current sessions. Platform is client-reported, including Unknown.</summary>
public static class PlayerPlatformStatistics
{
    public static IReadOnlyList<PlayerPlatformCount> Snapshot(IEnumerable<PlayerSnapshot> players, WorldId worldId) =>
        Array.AsReadOnly(players.Where(player => player.WorldId == worldId && player.SessionId.HasValue)
            .GroupBy(player => player.Platform.Kind).OrderBy(group => group.Key)
            .Select(group => new PlayerPlatformCount(group.Key, group.Count())).ToArray());
}
