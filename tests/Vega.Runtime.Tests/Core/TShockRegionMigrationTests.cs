using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Contracts.Worlds;
using Vega.Persistence.Sqlite;
using Vega.Runtime;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class TShockRegionMigrationTests
{
    [Test]
    public async Task PrimaryRegionsKeepPriorityProtectionAndMappedMembershipAcrossRestart()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-tshock-region-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "tshock.sqlite");
        string map = Path.Combine(root, "groups.json");
        var options = new SqliteVegaDatabaseOptions(Path.Combine(root, "vega.db"));
        try
        {
            var db = await SqliteVegaDatabase.OpenAsync(options);
            var account = (await db.Accounts.CreateAsync("owner", "test-only-account-password", "default", "console")).After!;
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Pooling = false }.ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE Users (ID INTEGER PRIMARY KEY, Username TEXT);
                    INSERT INTO Users VALUES (@account,'Owner');
                    CREATE TABLE Regions (Id INTEGER PRIMARY KEY, RegionName TEXT, X1 INTEGER, Y1 INTEGER, width INTEGER, height INTEGER, Owner TEXT, UserIds TEXT, "Groups" TEXT, Protected INTEGER, Z INTEGER, WorldID TEXT);
                    INSERT INTO Regions VALUES (1,'town',10,20,30,40,'Owner',@member,'member',1,5,'123');
                    INSERT INTO Regions VALUES (2,'public',10,20,10,10,'Server','','',0,10,'123');
                    INSERT INTO Regions VALUES (3,'other',10,20,30,40,'Server','','',1,0,'456');
                    """;
                command.Parameters.AddWithValue("@account", account.Id.Value);
                command.Parameters.AddWithValue("@member", account.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }
            File.WriteAllText(map, "{\"member\":\"default\"}");
            byte[] original = File.ReadAllBytes(source);
            var imported = TShockRegionMigration.Read(source, 123, map, db.Accounts.SnapshotAccounts());
            Assert.That(await db.ImportPrimaryRegionsAsync(imported), Is.EqualTo(2));
            var restarted = await SqliteVegaDatabase.OpenAsync(options);
            var regions = restarted.Regions.SnapshotRegions(WorldId.Primary);
            var town = regions.Single(region => region.Name == "town");
            Assert.Multiple(() =>
            {
                Assert.That(town.Owner, Is.EqualTo("owner"));
                Assert.That(town.Members, Does.Contain("owner"));
                Assert.That(town.Groups, Does.Contain("default"));
                Assert.That(town.Flags["protected"], Is.True);
                Assert.That(restarted.Regions.FindTopRegion(WorldId.Primary, 11, 21)?.Name, Is.EqualTo("public"));
                Assert.That(regions.Single(region => region.Name == "public").Flags["protected"], Is.False);
                Assert.That(regions.Single(region => region.Name == "public").Owner, Is.Null);
                Assert.That(File.ReadAllBytes(source), Is.EqualTo(original));
            });
            Assert.ThrowsAsync<InvalidDataException>(async () => await restarted.ImportPrimaryRegionsAsync(imported));
            Assert.Throws<InvalidDataException>(() => TShockRegionMigration.Read(source, 123, map, []));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
