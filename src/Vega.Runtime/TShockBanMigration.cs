using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Vega.Contracts.Cluster.Events;
using Vega.Core.Bans;

namespace Vega.Runtime;

internal static class TShockBanMigration
{
    public static IReadOnlyList<GlobalBanCreated> Read(string path, DateTimeOffset now, GlobalBanService validator)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path), Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TicketNumber, Identifier, Reason, BanningUser, Date, Expiration FROM PlayerBans WHERE Expiration > @now AND Date <= @now ORDER BY TicketNumber;";
        command.Parameters.AddWithValue("@now", now.UtcTicks);
        using var reader = command.ExecuteReader();
        var bans = new List<GlobalBanCreated>();
        while (reader.Read())
        {
            int ticket = reader.GetInt32(0);
            string identifier = reader.GetString(1);
            int delimiter = identifier.IndexOf(':');
            if (ticket <= 0 || delimiter <= 0 || bans.Count == 100_000)
                throw new InvalidDataException("TShock ban ticket or import count is invalid.");
            var kind = identifier[..delimiter] switch
            {
                "ip" => GlobalBanSubjectKind.IpAddress,
                "uuid" => GlobalBanSubjectKind.Uuid,
                "name" => GlobalBanSubjectKind.PlayerName,
                "acc" => GlobalBanSubjectKind.Account,
                _ => throw new InvalidDataException($"TShock ban ticket {ticket} has an unsupported identifier type.")
            };
            if (!validator.TryNormalizeSubject(new(kind, identifier[(delimiter + 1)..]), out var subject, out _))
                throw new InvalidDataException($"TShock ban ticket {ticket} has an invalid identifier.");
            long date = reader.GetInt64(4);
            long expiry = reader.GetInt64(5);
            if (date < 0 || expiry <= date || expiry > DateTime.MaxValue.Ticks)
                throw new InvalidDataException($"TShock ban ticket {ticket} has an invalid date.");
            // Stable ID for repeat imports. A previously revoked imported ban is never reactivated by import.
            string key = string.Create(CultureInfo.InvariantCulture, $"vega:tshock:ban:{ticket}:{date}:{identifier}");
            Guid id = new(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
            string reason = reader.IsDBNull(2) ? "" : reader.GetString(2);
            string actor = reader.IsDBNull(3) ? "" : reader.GetString(3);
            bans.Add(new(new(id), subject!, string.IsNullOrWhiteSpace(reason) ? "Imported TShock ban" : reason,
                string.IsNullOrWhiteSpace(actor) ? "TShock" : actor, new DateTimeOffset(date, TimeSpan.Zero),
                expiry == DateTime.MaxValue.Ticks ? null : new DateTimeOffset(expiry, TimeSpan.Zero), 1));
        }
        return bans.AsReadOnly();
    }
}
