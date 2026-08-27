using System.Globalization;

namespace Vega.Contracts.Players;

/// <summary>
/// Resolves the online-player selectors shared by administration commands.
/// Supports a slot, a case-insensitive name prefix, and the TShock-compatible
/// <c>tsi:</c>/<c>tsn:</c> exact selector prefixes.
/// </summary>
public static class OnlinePlayerSearch
{
    public static IReadOnlyList<PlayerSnapshot> Resolve(IPlayerDirectory players, string rawSearch)
    {
        ArgumentNullException.ThrowIfNull(players);
        if (string.IsNullOrWhiteSpace(rawSearch))
        {
            return [];
        }

        string search = rawSearch.Trim();
        bool exactIndexOnly = search.StartsWith("tsi:", StringComparison.OrdinalIgnoreCase);
        bool exactNameOnly = search.StartsWith("tsn:", StringComparison.OrdinalIgnoreCase);
        if (exactIndexOnly || exactNameOnly)
        {
            search = search[4..];
            if (search.Length == 0)
            {
                return [];
            }
        }

        IReadOnlyList<PlayerSnapshot> online = players.SnapshotOnlinePlayers();
        var matches = new List<PlayerSnapshot>();
        if (int.TryParse(search, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot))
        {
            PlayerSnapshot? bySlot = online.FirstOrDefault(player => player.Id.Slot == slot);
            if (bySlot is not null)
            {
                matches.Add(bySlot);
            }

            if (exactIndexOnly)
            {
                return matches;
            }
        }
        else if (exactIndexOnly)
        {
            return [];
        }

        if (exactNameOnly)
        {
            PlayerSnapshot? exact = online.FirstOrDefault(player =>
                string.Equals(player.Name, search, StringComparison.OrdinalIgnoreCase));
            return exact is null ? [] : [exact];
        }

        foreach (PlayerSnapshot player in online)
        {
            if (player.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase) &&
                matches.All(match => match.Id != player.Id))
            {
                matches.Add(player);
            }
        }

        return matches;
    }
}
