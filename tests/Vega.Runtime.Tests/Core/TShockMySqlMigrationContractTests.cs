extern alias LegacyBCrypt;

using System.Globalization;
using System.Text.Json;
using MySqlConnector;
using NUnit.Framework;
using Vega.Cluster.Scenarios.Bans;
using Vega.Contracts.Accounts;
using Vega.Core.Bans;
using Vega.Runtime;
using static Vega.Terraria.Players.TerrariaSandboxSscCharacterAdapter;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
[NonParallelizable]
public sealed class TShockMySqlMigrationContractTests
{
    internal const string EnvironmentVariable = "VEGA_TEST_TSHOCK_MYSQL";
    internal const int FixtureAccountId = 42;
    internal const int FixtureOtherAccountId = 43;
    internal const int FixtureWorldId = 123;
    internal const string FixtureAccountName = "example";
    internal const string FixturePassword = "test-only-tshock-mysql-fixture";
    internal const string GroupMappingJson = "{\"member\":\"default\"}";
    private const string Marker = "vega-tshock-mysql-contract-v1";
    private static readonly string[] Tables = ["Users", "PlayerBans", "Regions", "Warps", "tsCharacter", "VegaMigrationFixture"];

    [Test]
    public async Task PinnedMySqlSchemaSupportsAllFiveReadOnlyConverters()
    {
        string? connectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString)) Assert.Ignore($"Set {EnvironmentVariable} to a disposable synthetic TShock schema.");
        await CreateFixtureAsync(connectionString!);
        var source = TShockMigrationSource.FromMySqlConnectionString(connectionString);
        string root = Path.Combine(Path.GetTempPath(), "vega-tshock-mysql-contract", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string mapping = Path.Combine(root, "groups.json");
            await File.WriteAllTextAsync(mapping, GroupMappingJson);
            var imports = TShockAccountMigration.Read(source, mapping);
            Assert.That(imports.Select(account => account.Id), Is.EqualTo(new[] { FixtureAccountId, FixtureOtherAccountId }));
            Assert.That(imports[0].Name, Is.EqualTo(FixtureAccountName));
            Assert.That(imports[0].LastSeen, Is.EqualTo(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)));
            Assert.That(imports[1].LastSeen, Is.Null);
            AccountSnapshot[] accounts = imports.Select(account => new AccountSnapshot(new(account.Id), account.Name, account.Group, account.LastSeen)).ToArray();
            var bans = TShockBanMigration.Read(source, new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero), new GlobalBanService(new InMemoryGlobalBanReplica()));
            Assert.That(bans.Count, Is.EqualTo(2));
            Assert.That(bans[0].ExpiresAt, Is.Null);
            Assert.That(bans[1].ExpiresAt, Is.EqualTo(new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero)));
            var regions = TShockRegionMigration.Read(source, FixtureWorldId, mapping, accounts);
            Assert.That(regions.Select(region => region.Name), Is.EqualTo(new[] { "town", "public" }));
            Assert.That(regions[0].Members, Does.Contain(FixtureAccountName));
            Assert.That(regions[0].Groups, Does.Contain("default"));
            Assert.That(regions[0].Flags["protected"], Is.True);
            Assert.That(regions[1].Owner, Is.Null);
            Assert.That(regions[1].Flags["protected"], Is.False);
            var warps = TShockWarpMigration.Read(source, FixtureWorldId);
            Assert.That(warps.Select(warp => warp.Name), Is.EqualTo(new[] { "town", "staff" }));
            Assert.That(warps[0].Hidden, Is.False);
            Assert.That(warps[1].Hidden, Is.True);
            Assert.That(warps[0].Position.Y, Is.EqualTo(3206f));
            var payloads = TShockCharacterMigration.Read(source, accounts, 0);
            var state = JsonSerializer.Deserialize<CharacterState>(payloads["42"], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.That(payloads.Count, Is.EqualTo(1));
            Assert.That(state.Inventory[0].Type, Is.EqualTo(1));
            Assert.That(state.Loadouts[2].Dye[^1].Type, Is.EqualTo(350));
            Assert.That(state.HairColor, Is.EqualTo(new ColorState(10, 20, 30, 255)));
            Assert.That(state.VoicePitchOffset, Is.EqualTo(.375f));
            Assert.That(state.Team, Is.EqualTo(3));

            // Only the synthetic marker is addressed, and the statement cannot
            // match any rows. The server's transaction must forbid it anyway.
            using (var readOnly = source.OpenReadOnly())
            using (var command = readOnly.CreateCommand())
            {
                command.CommandText = "UPDATE VegaMigrationFixture SET marker=marker WHERE 1=0;";
                Assert.That(() => command.ExecuteNonQuery(), Throws.TypeOf<MySqlException>());
            }
            await CreateFixtureAsync(connectionString!); // verifies exact fixture identity, never rewrites it
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Creates a synthetic schema once, or verifies the fixture marker on reuse by the offline CLI test.</summary>
    internal static async Task CreateFixtureAsync(string connectionString)
    {
        var settings = new MySqlConnectionStringBuilder(connectionString) { Pooling = false, PersistSecurityInfo = false };
        if (!settings.Database.StartsWith("vega_test_tshock", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The disposable TShock MySQL fixture database must start with vega_test_tshock.");
        await using var connection = new MySqlConnection(settings.ConnectionString);
        await connection.OpenAsync();
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var identify = connection.CreateCommand())
        {
            identify.CommandText = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA=DATABASE();";
            await using var reader = await identify.ExecuteReaderAsync();
            while (await reader.ReadAsync()) existing.Add(reader.GetString(0));
        }
        if (existing.Count != 0)
        {
            if (!existing.SetEquals(Tables)) throw new InvalidDataException("The synthetic TShock test schema must be empty or contain the exact fixture tables.");
            await using var verify = connection.CreateCommand();
            verify.CommandText = "SELECT marker FROM VegaMigrationFixture;";
            if (!string.Equals((string?)await verify.ExecuteScalarAsync(), Marker, StringComparison.Ordinal))
                throw new InvalidDataException("Synthetic TShock fixture marker is absent or incorrect.");
            return;
        }

        // Literal columns/types mirror pinned TShock User/Ban/Region/Warp/Character
        // managers; especially TEXT LastAccessed/Private, BIGINT UTC ticks, signed
        // INT packed colors, and FLOAT voicePitchOffset. No ALTER/DROP/overwrite.
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Users (ID INT PRIMARY KEY, Username VARCHAR(32), Password VARCHAR(128), UUID VARCHAR(128),
                Usergroup TEXT, Registered TEXT, LastAccessed TEXT, KnownIPs TEXT);
            CREATE TABLE PlayerBans (TicketNumber INT PRIMARY KEY, Identifier TEXT, Reason TEXT, BanningUser TEXT, `Date` BIGINT, Expiration BIGINT);
            CREATE TABLE Regions (Id INT PRIMARY KEY, X1 INT, Y1 INT, width INT, height INT, RegionName VARCHAR(50), WorldID VARCHAR(50),
                UserIds TEXT, Protected INT, `Groups` TEXT, Owner VARCHAR(50), Z INT);
            CREATE TABLE Warps (Id INT PRIMARY KEY, WarpName VARCHAR(50), X INT, Y INT, WorldID VARCHAR(50), `Private` TEXT);
            CREATE TABLE tsCharacter (Account INT PRIMARY KEY, Health INT, MaxHealth INT, Mana INT, MaxMana INT,
                Inventory TEXT, extraSlot INT, spawnX INT, spawnY INT, skinVariant INT, hair INT, hairDye INT,
                hairColor INT, pantsColor INT, shirtColor INT, underShirtColor INT, shoeColor INT, hideVisuals INT,
                skinColor INT, eyeColor INT, questsCompleted INT, usingBiomeTorches INT, happyFunTorchTime INT,
                unlockedBiomeTorches INT, currentLoadoutIndex INT, ateArtisanBread INT, usedAegisCrystal INT,
                usedAegisFruit INT, usedArcaneCrystal INT, usedGalaxyPearl INT, usedGummyWorm INT, usedAmbrosia INT,
                unlockedSuperCart INT, enabledSuperCart INT, deathsPVE INT, deathsPVP INT, voiceVariant INT,
                voicePitchOffset FLOAT, team INT);
            INSERT INTO Users (ID,Username,Password,Usergroup,LastAccessed) VALUES
                (42,'Example',@hash,'member','2026-10-01T12:00:00'), (43,'Other',@hash,'member',NULL);
            INSERT INTO PlayerBans VALUES (1,'uuid:Fixture-UUID','reason','fixture',@date,@never),
                (2,'ip:192.0.2.32',NULL,NULL,@date,@future), (3,'name:Expired','old','fixture',@date,@past);
            INSERT INTO Regions VALUES (1,10,20,30,40,'town','123','42',1,'member','Example',5),
                (2,10,20,10,10,'public','123',NULL,0,NULL,'Server',10), (3,10,20,30,40,'other','456',NULL,1,NULL,'Server',0);
            INSERT INTO Warps VALUES (1,'town',100,200,'123',NULL), (2,'staff',110,210,'123','1'), (3,'other-world',120,220,'456','0');
            INSERT INTO tsCharacter (Account,Health,MaxHealth,Mana,MaxMana,Inventory,spawnX,spawnY,hairColor,hideVisuals,currentLoadoutIndex,usedAegisCrystal,voicePitchOffset,team)
                VALUES (42,140,400,60,200,@inventory,100,200,@color,5,2,1,0.375,3);
            CREATE TABLE VegaMigrationFixture (marker VARCHAR(64) PRIMARY KEY);
            INSERT INTO VegaMigrationFixture VALUES (@marker);
            """;
        command.Parameters.AddWithValue("@hash", LegacyBCrypt::BCrypt.Net.BCrypt.HashPassword(FixturePassword, 4));
        command.Parameters.AddWithValue("@date", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks);
        command.Parameters.AddWithValue("@past", new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks);
        command.Parameters.AddWithValue("@future", new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks);
        command.Parameters.AddWithValue("@never", DateTime.MaxValue.Ticks);
        command.Parameters.AddWithValue("@inventory", string.Join("~", Enumerable.Range(1, 350).Select(item => string.Create(CultureInfo.InvariantCulture, $"{item},1,0,0"))));
        command.Parameters.AddWithValue("@color", unchecked((int)0xFF1E140A));
        command.Parameters.AddWithValue("@marker", Marker);
        await command.ExecuteNonQueryAsync();
    }
}
