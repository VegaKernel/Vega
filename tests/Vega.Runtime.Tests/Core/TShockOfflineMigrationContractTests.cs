using System.Text.Json;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using NUnit.Framework;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;
using Vega.Modules.Navigation;
using Vega.Persistence;
using Vega.Runtime;
using Vega.Sandboxes;
using static Vega.Runtime.Tests.Core.TShockMySqlMigrationContractTests;
using static Vega.Terraria.Players.TerrariaSandboxSscCharacterAdapter;

namespace Vega.Runtime.Tests.Core;

[TestFixture, NonParallelizable]
public sealed class TShockOfflineMigrationContractTests
{
    private const string ReaderVariable = "VEGA_TEST_TSHOCK_MYSQL_READER";
    private const string TargetVariable = "VEGA_TEST_VEGA_IMPORT_MYSQL";
    private const string RuntimeSourceVariable = "VEGA_TSHOCK_MYSQL_CONNECTION";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [TestCase("sqlite")]
    [TestCase("mysql")]
    public async Task MySqlSourceImportsAllFamiliesThroughOfflineCommands(string destination)
    {
        string? creator = Environment.GetEnvironmentVariable(TShockMySqlMigrationContractTests.EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(creator)) Assert.Ignore("Disposable TShock MySQL source is not configured.");
        string? target = Environment.GetEnvironmentVariable(TargetVariable);
        if (destination == "mysql")
        {
            if (string.IsNullOrWhiteSpace(target)) Assert.Ignore("Disposable Vega MySQL target is not configured.");
            if (!new MySqlConnectionStringBuilder(target).Database.StartsWith("vega_test_import", StringComparison.Ordinal))
                Assert.Fail("The test target must be a dedicated vega_test_import schema.");
        }
        await CreateFixtureAsync(creator!);
        string source = Environment.GetEnvironmentVariable(ReaderVariable) ?? creator!;
        string? previousSource = Environment.GetEnvironmentVariable(RuntimeSourceVariable);
        string root = Path.Combine(Path.GetTempPath(), "vega-tshock-offline-contract", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        string map = Path.Combine(root, "mapping.json");
        await File.WriteAllTextAsync(map, GroupMappingJson);
        var options = VegaDatabaseOptions.Default with { Provider = destination, ConnectionStringEnvironmentVariable = TargetVariable };
        await File.WriteAllTextAsync(Path.Combine(root, "config", "database.json"), JsonSerializer.Serialize(options, Json));
        Guid world = Guid.NewGuid();
        try
        {
            Environment.SetEnvironmentVariable(RuntimeSourceVariable, source);
            Assert.That(Run("accounts", map), Is.Zero);
            Assert.That(Run("bans"), Is.Zero);
            Assert.That(Run("bans"), Is.Zero, "Repeat ban import must be insert-only.");
            Assert.That(Run("regions", FixtureWorldId.ToString(), map), Is.Zero);
            Assert.That(Run("warps", FixtureWorldId.ToString(), world.ToString("D")), Is.Zero);
            Assert.That(Run("ssc", "classic"), Is.Zero);
            Assert.That(Run("ssc", "classic"), Is.Zero, "Identical SSC state is retained.");

            var database = VegaDatabaseFactory.Create(options, root);
            await database.InitializeAsync();
            var account = await database.Accounts.FindByNameAsync(FixtureAccountName);
            Assert.That(account?.Id.Value, Is.EqualTo(FixtureAccountId));
            Assert.That(await database.Accounts.VerifyPasswordAsync(FixtureAccountName, FixturePassword), Is.True);
            Assert.That(database.Accounts.SnapshotAccounts(), Has.Count.EqualTo(2));
            Assert.That(database.GlobalBans.SnapshotActive(DateTimeOffset.UtcNow), Has.Count.EqualTo(2));
            var regions = database.Regions.SnapshotRegions(WorldId.Primary);
            Assert.That(regions, Has.Count.EqualTo(2));
            Assert.That(regions.Single(r => r.Name == "town").Members, Does.Contain(FixtureAccountName));
            var warps = await new WarpStore(Path.Combine(root, "data", "warps")).ReadAsync(world);
            Assert.That(warps, Has.Count.EqualTo(2));
            Assert.That(warps.Single(w => w.Name == "staff").Hidden, Is.True);
            var character = new SandboxSscStore(Path.Combine(root, "data", "sandboxes", "ssc.json"))
                .Read(FixtureAccountId.ToString(), WorldId.Primary, SandboxSscMode.Inherit);
            Assert.That(character, Is.Not.Null);
            var state = JsonSerializer.Deserialize<CharacterState>(character!.Payload, Json)!;
            Assert.That(state.Loadouts[2].Dye[^1].Type, Is.EqualTo(350));
            Assert.That(state.Team, Is.EqualTo(3));

            // Exercise the existing no-overwrite invariants through the real CLI.
            Assert.That(Run("accounts", map), Is.EqualTo(2));
            Assert.That(Run("regions", FixtureWorldId.ToString(), map), Is.EqualTo(2));
            Assert.That(Run("warps", FixtureWorldId.ToString(), world.ToString("D")), Is.EqualTo(2));
            if (destination == "mysql")
            {
                var backups = Directory.GetFiles(Path.Combine(root, "data", "migration-backups"), "*.sql", SearchOption.AllDirectories);
                Assert.That(backups.Length, Is.GreaterThanOrEqualTo(4));
                Assert.That(backups.All(path => new FileInfo(path).Length > 0), Is.True);
            }

            // Guard the same-source schema before initializing a Vega destination.
            string sameRoot = Path.Combine(root, "same-source");
            Directory.CreateDirectory(Path.Combine(sameRoot, "config"));
            await File.WriteAllTextAsync(Path.Combine(sameRoot, "config", "database.json"), JsonSerializer.Serialize(
                options with { Provider = "mysql", ConnectionStringEnvironmentVariable = RuntimeSourceVariable }, Json));
            Assert.That(OfflineMigrationCommands.TryRun(["--vega-import-tshock-accounts", "@mysql", map], sameRoot), Is.EqualTo(2));
            int? Run(string family, params string[] tail) => OfflineMigrationCommands.TryRun(["--vega-import-tshock-" + family, "@mysql", .. tail], root);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuntimeSourceVariable, previousSource);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
