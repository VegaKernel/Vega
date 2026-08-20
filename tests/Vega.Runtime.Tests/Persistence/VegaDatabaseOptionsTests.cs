using NUnit.Framework;
using Vega.Persistence;
using Vega.Persistence.MySql;
using Vega.Persistence.PostgreSql;
using Vega.Persistence.Sqlite;

namespace Vega.Runtime.Tests.Persistence;

[TestFixture]
[NonParallelizable]
public sealed class VegaDatabaseOptionsTests
{
    private string testDirectory = null!;
    private string environmentVariable = null!;

    [SetUp]
    public void SetUp()
    {
        testDirectory = Path.Combine(
            Path.GetTempPath(),
            "Vega.DatabaseOptions.Tests",
            Guid.NewGuid().ToString("N"));
        environmentVariable = "VEGA_TEST_DATABASE_" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(testDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(environmentVariable, null);
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [TestCase("postgres", VegaDatabaseProvider.PostgreSql)]
    [TestCase("postgresql", VegaDatabaseProvider.PostgreSql)]
    [TestCase("mysql", VegaDatabaseProvider.MySql)]
    [TestCase("mariadb", VegaDatabaseProvider.MySql)]
    [TestCase("sqlite", VegaDatabaseProvider.Sqlite)]
    public void ProviderAliasesAreExplicit(string value, VegaDatabaseProvider expected)
    {
        VegaDatabaseOptions options = Options(value);

        Assert.That(options.ParseProvider(), Is.EqualTo(expected));
    }

    [Test]
    public void DefaultProviderResolvesSqlitePathInsideServerDirectory()
    {
        IVegaDatabase database = VegaDatabaseFactory.Create(
            VegaDatabaseOptions.Default,
            testDirectory);

        Assert.Multiple(() =>
        {
            Assert.That(database, Is.TypeOf<SqliteVegaDatabase>());
            Assert.That(database.Provider, Is.EqualTo("sqlite"));
            Assert.That(database.IsReady, Is.False);
            Assert.That(
                ((SqliteVegaDatabase)database).DatabasePath,
                Is.EqualTo(Path.Combine(testDirectory, "data", "vega.db")));
        });
    }

    [Test]
    public void PostgreSqlProviderIsCreatedWithoutOpeningANetworkConnection()
    {
        Environment.SetEnvironmentVariable(
            environmentVariable,
            "Host=127.0.0.1;Port=1;Database=vega;Username=vega;Password=test;Timeout=1");

        IVegaDatabase database = VegaDatabaseFactory.Create(
            Options("postgresql"),
            testDirectory);

        Assert.Multiple(() =>
        {
            Assert.That(database, Is.TypeOf<PostgreSqlVegaDatabase>());
            Assert.That(database.Provider, Is.EqualTo("postgresql"));
            Assert.That(database.IsReady, Is.False);
        });
    }

    [Test]
    public void MySqlProviderIsCreatedWithoutOpeningANetworkConnection()
    {
        Environment.SetEnvironmentVariable(
            environmentVariable,
            "Server=127.0.0.1;Port=1;Database=vega;User ID=vega;Password=test;Connection Timeout=1");

        IVegaDatabase database = VegaDatabaseFactory.Create(
            Options("mysql"),
            testDirectory);

        Assert.Multiple(() =>
        {
            Assert.That(database, Is.TypeOf<MySqlVegaDatabase>());
            Assert.That(database.Provider, Is.EqualTo("mysql"));
            Assert.That(database.IsReady, Is.False);
        });
    }

    [Test]
    public void ExternalProviderWithoutASecretFailsBeforeRuntimeStartup()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            VegaDatabaseFactory.Create(Options("postgresql"), testDirectory))!;

        Assert.That(error.Message, Does.Contain(environmentVariable));
    }

    [Test]
    public void InvalidConfigurationDoesNotSilentlyFallBackToSqlite()
    {
        string path = Path.Combine(testDirectory, "database.json");
        File.WriteAllText(
            path,
            """
            {
              "provider": "oracle",
              "sqlitePath": "data/vega.db",
              "busyTimeoutMilliseconds": 5000,
              "connectionStringEnvironmentVariable": "VEGA_DATABASE_CONNECTION_STRING",
              "connectionString": null
            }
            """);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            VegaDatabaseOptionsStore.Load(path))!;

        Assert.That(error.Message, Does.Contain("Unknown Vega database provider"));
    }

    private VegaDatabaseOptions Options(string provider) => new(
        provider,
        "data/vega.db",
        5_000,
        environmentVariable,
        null);
}
