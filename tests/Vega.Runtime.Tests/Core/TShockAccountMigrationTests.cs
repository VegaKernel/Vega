extern alias LegacyBCrypt;

using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Persistence.Relational;
using Vega.Persistence.Sqlite;
using Vega.Runtime;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class TShockAccountMigrationTests
{
    [Test]
    public async Task ExplicitGroupMappingAndOriginalIdWithPasswordUpgradeOnSuccessfulLogin()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-tshock-account-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "tshock.sqlite");
        string map = Path.Combine(root, "groups.json");
        string target = Path.Combine(root, "vega.db");
        const string password = "test-only-example-credential";
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Pooling = false }.ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE Users (ID INTEGER PRIMARY KEY, Username TEXT, Password TEXT, Usergroup TEXT, LastAccessed TEXT, Registered TEXT, KnownIps TEXT, UUID TEXT);
                    INSERT INTO Users VALUES (42,'Example',@hash,'member','2026-10-01T12:00:00','2026-09-01T12:00:00','["192.0.2.5"]','fixture-uuid');
                    """;
                command.Parameters.AddWithValue("@hash", LegacyBCrypt::BCrypt.Net.BCrypt.HashPassword(password, 4));
                command.ExecuteNonQuery();
            }
            File.WriteAllText(map, "{\"member\":\"default\"}");
            byte[] original = File.ReadAllBytes(source);
            var imports = TShockAccountMigration.Read(source, map);
            Assert.That(imports.Single().Id, Is.EqualTo(42));
            Assert.That(File.ReadAllBytes(source), Is.EqualTo(original));

            var db = await SqliteVegaDatabase.OpenAsync(new(target));
            Assert.That(await db.ImportAccountsAsync(imports), Is.EqualTo(1));
            var account = await db.Accounts.FindByNameAsync("Example");
            Assert.That(account!.Id.Value, Is.EqualTo(42));
            Assert.That(account.GroupName, Is.EqualTo("default"));
            Assert.That(account.RegisteredUtc, Is.EqualTo(DateTimeOffset.Parse("2026-09-01T12:00:00Z")));
            Assert.That(account.LastKnownIp, Is.EqualTo("192.0.2.5"));
            Assert.That(account.LastKnownUuid, Is.EqualTo("fixture-uuid"));
            Assert.That(await db.Accounts.VerifyPasswordAsync("example", "incorrect-test-password"), Is.False);
            Assert.That(await db.Accounts.VerifyPasswordAsync("example", password), Is.True);

            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT password_hash FROM vega_accounts WHERE id=42;";
                // Assert only the encoding family, never emit a hash in test diagnostics.
                Assert.That((command.ExecuteScalar() as string)?.StartsWith("pbkdf2-sha256$", StringComparison.Ordinal), Is.True);
            }
            var reopened = await SqliteVegaDatabase.OpenAsync(new(target));
            Assert.That(await reopened.Accounts.VerifyPasswordAsync("example", password), Is.True);
            Assert.ThrowsAsync<InvalidDataException>(async () => await reopened.ImportAccountsAsync(imports));
            File.WriteAllText(map, "{\"another-group\":\"default\"}");
            Assert.Throws<InvalidDataException>(() => TShockAccountMigration.Read(source, map));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [Test]
    public void InvalidOrExcessiveLegacyHashEncodingIsRejected()
    {
        Assert.That(LegacyPasswordHash.IsSupported(null), Is.False);
        Assert.That(LegacyPasswordHash.IsSupported("invalid"), Is.False);
        Assert.That(LegacyPasswordHash.IsSupported("$2a$31$" + new string('A', 53)), Is.False);
    }
}
