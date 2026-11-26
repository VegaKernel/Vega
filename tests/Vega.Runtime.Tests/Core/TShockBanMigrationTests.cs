using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Cluster.Scenarios.Bans;
using Vega.Contracts.Bans;
using Vega.Contracts.Cluster.Events;
using Vega.Core.Bans;
using Vega.Persistence.Sqlite;
using Vega.Runtime;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class TShockBanMigrationTests
{
    [Test]
    public async Task ImportsActiveBansTransactionallyAndDoesNotReactivateRevokedIds()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-tshock-ban-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "tshock.sqlite");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Pooling = false }.ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE PlayerBans (TicketNumber INTEGER PRIMARY KEY, Identifier TEXT, Reason TEXT, BanningUser TEXT, Date INTEGER, Expiration INTEGER);
                    INSERT INTO PlayerBans VALUES (1,'uuid:Persisted-Token','reason','operator',@date,@never);
                    INSERT INTO PlayerBans VALUES (2,'ip:192.0.2.32','reason','operator',@date,@future);
                    INSERT INTO PlayerBans VALUES (3,'name:Expired','old','operator',@date,@past);
                    INSERT INTO PlayerBans VALUES (4,'acc:Example','reason','operator',@date,@never);
                    """;
                command.Parameters.AddWithValue("@date", now.AddDays(-2).UtcTicks);
                command.Parameters.AddWithValue("@past", now.AddDays(-1).UtcTicks);
                command.Parameters.AddWithValue("@future", now.AddDays(1).UtcTicks);
                command.Parameters.AddWithValue("@never", DateTime.MaxValue.Ticks);
                command.ExecuteNonQuery();
            }

            byte[] original = File.ReadAllBytes(source);
            var validator = new GlobalBanService(new InMemoryGlobalBanReplica());
            var records = TShockBanMigration.Read(source, now, validator);
            Assert.That(records.Count, Is.EqualTo(3));
            Assert.That(records[0].Subject.Value, Is.EqualTo("Persisted-Token"));
            Assert.That(records[0].ExpiresAt, Is.Null);
            Assert.That(records[1].ExpiresAt, Is.EqualTo(now.AddDays(1)));
            Assert.That(File.ReadAllBytes(source), Is.EqualTo(original));

            var db = await SqliteVegaDatabase.OpenAsync(new(Path.Combine(root, "vega.db")));
            Assert.That(await db.ImportBansAsync(records), Is.EqualTo(3));
            var reopened = await SqliteVegaDatabase.OpenAsync(new(Path.Combine(root, "vega.db")));
            var service = new GlobalBanService(reopened.GlobalBans);
            Assert.That((await service.FindActiveBanAsync(new BanIdentity(null, null, "Persisted-Token", null)))?.Id,
                Is.EqualTo(records[0].BanId));
            Assert.That(await reopened.GlobalBans.ApplyRevokedAsync(new(records[0].BanId, "operator", now, 2)), Is.True);
            Assert.That(await reopened.ImportBansAsync(TShockBanMigration.Read(source, now, validator)), Is.Zero);
            Assert.That(await service.FindActiveBanAsync(new BanIdentity(null, null, "Persisted-Token", null)), Is.Null);
            Assert.That(reopened.GlobalBans.SnapshotActive(now).Count, Is.EqualTo(2));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
