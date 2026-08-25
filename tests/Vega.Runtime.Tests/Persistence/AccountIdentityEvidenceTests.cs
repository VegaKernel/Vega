using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Contracts.Accounts;
using Vega.Persistence.Sqlite;

namespace Vega.Runtime.Tests.Persistence;

[TestFixture]
public sealed class AccountIdentityEvidenceTests
{
    private string testDirectory = null!;
    private string databasePath = null!;

    [SetUp]
    public void SetUp()
    {
        testDirectory = Path.Combine(
            Path.GetTempPath(),
            "Vega.AccountIdentityEvidence.Tests",
            Guid.NewGuid().ToString("N"));
        databasePath = Path.Combine(testDirectory, "data", "vega.db");
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Test]
    public async Task SharedUuidAndIpProduceRankedPossibleAccountLinks()
    {
        SqliteVegaDatabase database = await SqliteVegaDatabase.OpenAsync(
            new SqliteVegaDatabaseOptions(databasePath));
        AccountSnapshot alice = (await database.Accounts.CreateAsync(
            "Alice", "correct horse battery staple", "default", "Server")).After!;
        AccountSnapshot bob = (await database.Accounts.CreateAsync(
            "Bob", "correct horse battery staple", "default", "Server")).After!;
        AccountSnapshot carol = (await database.Accounts.CreateAsync(
            "Carol", "correct horse battery staple", "default", "Server")).After!;
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");

        await database.AccountIdentities.RecordAsync(
            alice.Id, alice.Name, "device-a", "203.0.113.10", now);
        await database.AccountIdentities.RecordAsync(
            alice.Id, alice.Name, "device-a", "203.0.113.10", now.AddMinutes(1));
        await database.AccountIdentities.RecordAsync(
            bob.Id, bob.Name, "device-a", "203.0.113.20", now.AddMinutes(2));
        await database.AccountIdentities.RecordAsync(
            carol.Id, carol.Name, "device-c", "203.0.113.10", now.AddMinutes(3));

        SqliteVegaDatabase reopened = await SqliteVegaDatabase.OpenAsync(
            new SqliteVegaDatabaseOptions(databasePath));
        AccountIdentityEvidenceSnapshot[] aliceEvidence = reopened.AccountIdentities
            .SnapshotEvidence()
            .Where(item => item.AccountId == alice.Id)
            .ToArray();
        PossibleAccountLinkSnapshot[] links = reopened.AccountIdentities
            .FindPossibleAccounts(alice.Id)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(aliceEvidence, Has.Length.EqualTo(2));
            Assert.That(aliceEvidence.Single(item => item.Kind == AccountIdentityEvidenceKind.ClientUuid).ObservationCount,
                Is.EqualTo(2));
            Assert.That(links.Select(static item => item.AccountName), Is.EqualTo(new[] { bob.Name, carol.Name }));
            Assert.That(links[0].SharedClientUuid, Is.True);
            Assert.That(links[0].SharedIpAddress, Is.False);
            Assert.That(links[1].SharedClientUuid, Is.False);
            Assert.That(links[1].SharedIpAddress, Is.True);
        });
    }

    [Test]
    public async Task RecordingNewEvidenceInvalidatesTheReadCacheImmediately()
    {
        SqliteVegaDatabase database = await SqliteVegaDatabase.OpenAsync(
            new SqliteVegaDatabaseOptions(databasePath));
        AccountSnapshot alice = (await database.Accounts.CreateAsync(
            "Alice", "correct horse battery staple", "default", "Server")).After!;
        AccountSnapshot bob = (await database.Accounts.CreateAsync(
            "Bob", "correct horse battery staple", "default", "Server")).After!;
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");

        await database.AccountIdentities.RecordAsync(
            alice.Id, alice.Name, "shared-device", null, now);
        Assert.That(database.AccountIdentities.SnapshotEvidence(), Has.Count.EqualTo(1));

        await database.AccountIdentities.RecordAsync(
            bob.Id, bob.Name, "shared-device", null, now.AddSeconds(1));
        PossibleAccountLinkSnapshot[] links = database.AccountIdentities
            .FindPossibleAccounts(alice.Id)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(database.AccountIdentities.SnapshotEvidence(), Has.Count.EqualTo(2));
            Assert.That(links, Has.Length.EqualTo(1));
            Assert.That(links[0].AccountName, Is.EqualTo(bob.Name));
            Assert.That(links[0].SharedClientUuid, Is.True);
        });
    }

    [Test]
    public async Task EvidenceIsPartOfSingleBaselineWithoutSchemaMigration()
    {
        SqliteVegaDatabase database = await SqliteVegaDatabase.OpenAsync(
            new SqliteVegaDatabaseOptions(databasePath));

        Assert.Multiple(() =>
        {
            Assert.That(database.SchemaVersion, Is.EqualTo(1));
            Assert.That(ScalarInt64(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'vega_account_identity_evidence';"),
                Is.EqualTo(1));
            Assert.That(ScalarInt64("SELECT COUNT(*) FROM vega_schema_history;"), Is.EqualTo(1));
        });
    }

    private long ScalarInt64(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
