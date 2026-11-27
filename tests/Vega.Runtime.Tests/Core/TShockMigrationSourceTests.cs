using NUnit.Framework;
using Vega.Runtime;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
[NonParallelizable]
public sealed class TShockMigrationSourceTests
{
    [Test]
    public void SourceSelectionPreservesSqlitePathAndRequiresExplicitMySqlSchema()
    {
        string path = Path.Combine(Path.GetTempPath(), "tshock fixture.sqlite");
        var sqlite = TShockMigrationSource.Parse(path);
        Assert.That(sqlite.IsMySql, Is.False);
        Assert.That(sqlite.SqlitePath, Is.EqualTo(Path.GetFullPath(path)));
        Assert.That(sqlite.GroupsIdentifier, Is.EqualTo("\"Groups\""));
        Assert.That(() => TShockMigrationSource.FromMySqlConnectionString(null), Throws.TypeOf<InvalidDataException>());
        Assert.That(() => TShockMigrationSource.FromMySqlConnectionString("Server=localhost;User ID=fixture;"), Throws.TypeOf<InvalidDataException>());
        var mysql = TShockMigrationSource.FromMySqlConnectionString("Server=localhost;User ID=fixture;Database=tshock_fixture;");
        Assert.That(mysql.IsMySql, Is.True);
        Assert.That(mysql.SqlitePath, Is.Null);
        Assert.That(mysql.GroupsIdentifier, Is.EqualTo("`Groups`"));
        Assert.That(() => mysql.EnsureDifferentMySqlDestination("Server=127.0.0.1;User ID=fixture;Database=TSHOCK_FIXTURE;"), Throws.TypeOf<InvalidDataException>());
        Assert.That(() => mysql.EnsureDifferentMySqlDestination("Server=localhost;User ID=fixture;Database=vega_fixture;"), Throws.Nothing);
    }

    [Test]
    public void MySqlSentinelReadsOnlyExplicitEnvironmentAndSanitizesInvalidSettings()
    {
        string key = TShockMigrationSource.MySqlEnvironmentVariable;
        string? previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, null);
            Assert.That(() => TShockMigrationSource.Parse(TShockMigrationSource.MySqlArgument), Throws.TypeOf<InvalidDataException>());
            Environment.SetEnvironmentVariable(key, "Server=localhost;User ID=fixture;Database=tshock_fixture;Password=synthetic-only;");
            Assert.That(TShockMigrationSource.Parse(TShockMigrationSource.MySqlArgument).IsMySql, Is.True);
            Environment.SetEnvironmentVariable(key, "Server=localhost;User ID=fixture;Database=tshock_fixture;Password=synthetic-only;NotAnOption=synthetic-only;");
            var failure = Assert.Throws<InvalidDataException>(() => TShockMigrationSource.Parse(TShockMigrationSource.MySqlArgument));
            Assert.That(failure!.ToString(), Does.Not.Contain("synthetic-only"));
        }
        finally { Environment.SetEnvironmentVariable(key, previous); }
    }
}
