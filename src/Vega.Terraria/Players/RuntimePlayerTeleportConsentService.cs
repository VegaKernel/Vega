using System.Collections.Concurrent;
using Vega.Contracts.Players;

namespace Vega.Terraria.Players;

public sealed class RuntimePlayerTeleportConsentService : IPlayerTeleportConsentService
{
    private sealed record Entry(PlayerSessionId? SessionId, bool AllowsIncoming);

    private readonly ConcurrentDictionary<PlayerId, Entry> entries = [];

    public bool AllowsIncoming(PlayerSnapshot player)
    {
        ArgumentNullException.ThrowIfNull(player);
        Entry entry = entries.AddOrUpdate(
            player.Id,
            _ => new Entry(player.SessionId, true),
            (_, current) => current.SessionId == player.SessionId
                ? current
                : new Entry(player.SessionId, true));
        return entry.AllowsIncoming;
    }

    public bool Toggle(PlayerSnapshot player)
    {
        ArgumentNullException.ThrowIfNull(player);
        Entry entry = entries.AddOrUpdate(
            player.Id,
            _ => new Entry(player.SessionId, false),
            (_, current) => current.SessionId == player.SessionId
                ? current with { AllowsIncoming = !current.AllowsIncoming }
                : new Entry(player.SessionId, false));
        return entry.AllowsIncoming;
    }
}
