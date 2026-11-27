using Vega.Contracts.Worlds;

namespace Vega.Contracts.Players;

/// <summary>TShock-compatible name-prefix/slot selectors, restricted to the command's world.</summary>
public static class PlayerTargetLookup
{
    public static IReadOnlyList<PlayerSnapshot> FindByNameOrSlot(IPlayerDirectory players, WorldId worldId, string rawSearch)
    {
        string search = rawSearch.Trim();
        bool exactIndexOnly = search.StartsWith("tsi:", StringComparison.OrdinalIgnoreCase);
        bool exactNameOnly = search.StartsWith("tsn:", StringComparison.OrdinalIgnoreCase);
        if (exactIndexOnly || exactNameOnly) search = search[4..];
        if (search.Length == 0) return [];

        PlayerSnapshot[] online = players.SnapshotOnlinePlayers().Where(player => player.WorldId == worldId).ToArray();
        if (exactNameOnly)
            return online.Where(player => string.Equals(player.Name, search, StringComparison.OrdinalIgnoreCase)).ToArray();
        var matches = new List<PlayerSnapshot>();
        if (int.TryParse(search, out int slot))
            matches.AddRange(online.Where(player => player.Id.Slot == slot));
        if (exactIndexOnly) return matches;
        foreach (PlayerSnapshot player in online)
            if (player.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase) && matches.All(match => match.Id != player.Id))
                matches.Add(player);
        return matches;
    }
}
