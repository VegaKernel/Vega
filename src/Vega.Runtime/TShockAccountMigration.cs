using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vega.Persistence.Relational;
using Vega.Persistence.Sqlite;

namespace Vega.Runtime;

internal static class TShockAccountMigration
{
    public static IReadOnlyList<LegacyAccountImport> Read(string sourcePath, string mappingPath)
    {
        var mapping = ReadGroupMapping(mappingPath);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(sourcePath), Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ID, Username, Password, Usergroup, LastAccessed FROM Users ORDER BY ID;";
        using var reader = command.ExecuteReader();
        var accounts = new List<LegacyAccountImport>();
        while (reader.Read())
        {
            int id = reader.GetInt32(0);
            string name = reader.GetString(1);
            string hash = reader.GetString(2);
            string group = reader.GetString(3);
            if (id <= 0 || string.IsNullOrWhiteSpace(name) || name.Length > 255 || name != name.Trim() || accounts.Count == 100_000)
                throw new InvalidDataException($"TShock account {id} has an unsupported identity or the import limit was exceeded.");
            if (!LegacyPasswordHash.IsSupported(hash))
                throw new InvalidDataException($"TShock account {id} has an unsupported password encoding or work factor; reset that credential in TShock first.");
            if (!mapping.TryGetValue(group, out string? mapped))
                throw new InvalidDataException($"TShock account {id} has a group without an explicit Vega mapping.");
            DateTimeOffset? lastSeen = null;
            if (!reader.IsDBNull(4) && !string.IsNullOrWhiteSpace(reader.GetString(4)))
            {
                if (!DateTimeOffset.TryParseExact(reader.GetString(4), "s", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset timestamp))
                    throw new InvalidDataException($"TShock account {id} has an unsupported LastAccessed timestamp.");
                lastSeen = timestamp;
            }
            accounts.Add(new(id, name.ToLowerInvariant(), mapped, hash, lastSeen));
        }
        return accounts.AsReadOnly();
    }

    internal static Dictionary<string, string> ReadGroupMapping(string mappingPath)
    {
        if (new FileInfo(mappingPath).Length > 1024 * 1024) throw new InvalidDataException("Group mapping is too large.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(mappingPath));
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Group mapping must be a JSON object.");
        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in document.RootElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(pair.Name) || pair.Value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(pair.Value.GetString()) || !mapping.TryAdd(pair.Name, pair.Value.GetString()!.Trim().ToLowerInvariant()))
                throw new InvalidDataException("Group mapping contains an invalid or duplicate entry.");
        }

        return mapping;
    }
}
