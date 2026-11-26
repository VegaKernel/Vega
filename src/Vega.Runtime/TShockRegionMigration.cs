using System.Globalization;
using Microsoft.Data.Sqlite;
using Vega.Contracts.Accounts;
using Vega.Contracts.Regions;
using Vega.Contracts.Worlds;

namespace Vega.Runtime;

internal static class TShockRegionMigration
{
    public static IReadOnlyList<RegionSnapshot> Read(string source, int legacyWorldId, string groupMapping,
        IReadOnlyList<AccountSnapshot> accounts)
    {
        var groups = TShockAccountMigration.ReadGroupMapping(groupMapping);
        var byId = accounts.ToDictionary(account => account.Id.Value);
        var byName = accounts.ToDictionary(account => account.Name, StringComparer.OrdinalIgnoreCase);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(source), Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ConnectionString);
        connection.Open();
        // Verify that imported account IDs still refer to the same source names, rather than just matching a number.
        using var users = connection.CreateCommand();
        users.CommandText = "SELECT ID, Username FROM Users;";
        var sourceAccounts = new Dictionary<int, string>();
        using (var sourceReader = users.ExecuteReader())
            while (sourceReader.Read()) sourceAccounts.Add(sourceReader.GetInt32(0), sourceReader.GetString(1));

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, RegionName, X1, Y1, width, height, Owner, UserIds, \"Groups\", Protected, Z FROM Regions WHERE WorldID = @world ORDER BY Id;";
        command.Parameters.AddWithValue("@world", legacyWorldId.ToString(CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        var result = new List<RegionSnapshot>();
        while (reader.Read())
        {
            int id = reader.GetInt32(0);
            string name = reader.GetString(1).Trim().ToLowerInvariant();
            int x = reader.GetInt32(2), y = reader.GetInt32(3), width = reader.GetInt32(4), height = reader.GetInt32(5);
            string? owner = Text(6);
            if (!string.IsNullOrWhiteSpace(owner))
            {
                if (byName.TryGetValue(owner, out var account) && sourceAccounts.TryGetValue(account.Id.Value, out string? sourceName) &&
                    string.Equals(sourceName, account.Name, StringComparison.OrdinalIgnoreCase)) owner = account.Name;
                else if (string.Equals(owner, "Server", StringComparison.OrdinalIgnoreCase)) owner = null;
                else throw new InvalidDataException($"Region {id} has an unresolved owner account.");
            }
            else owner = null;
            var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in Split(7))
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int memberId) ||
                    !byId.TryGetValue(memberId, out var account) || !sourceAccounts.TryGetValue(memberId, out var sourceName) ||
                    !string.Equals(sourceName, account.Name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Region {id} has an unresolved member account.");
                members.Add(account.Name);
            }
            var allowedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string group in Split(8))
            {
                if (!groups.TryGetValue(group, out var mapped)) throw new InvalidDataException($"Region {id} has an unmapped group.");
                allowedGroups.Add(mapped);
            }
            if (result.Count == 100_000 || id <= 0 || name.Length is 0 or > 255 || x < 0 || y < 0 || width <= 0 || height <= 0 ||
                (long)x + width > int.MaxValue || (long)y + height > int.MaxValue)
                throw new InvalidDataException($"Region {id} has invalid bounds, name or identity.");
            result.Add(new(id, name, x, y, width, height, owner)
            {
                WorldId = WorldId.Primary, Priority = reader.GetInt32(10), Revision = 1,
                Members = members, Groups = allowedGroups,
                Flags = new Dictionary<string, bool> { ["protected"] = reader.GetInt32(9) != 0 }
            });

            string? Text(int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
            string[] Split(int column) => (Text(column) ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }
        return result.AsReadOnly();
    }
}
