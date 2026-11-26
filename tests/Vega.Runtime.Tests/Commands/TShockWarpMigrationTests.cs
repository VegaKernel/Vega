using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Modules.Navigation;
using Vega.Runtime;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class TShockWarpMigrationTests
{
    [Test]
    public async Task ReadOnlyWorldScopedImportPreservesVisibilityAndRefusesOverwrite()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-warp-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "tshock.sqlite");
        var guid = Guid.NewGuid();
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Pooling = false }.ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE Warps (Id INTEGER PRIMARY KEY, WarpName TEXT, X INTEGER, Y INTEGER, WorldID TEXT, Private TEXT);
                    INSERT INTO Warps VALUES (1,'town',100,200,'123',NULL);
                    INSERT INTO Warps VALUES (2,'staff',110,210,'123','1');
                    INSERT INTO Warps VALUES (3,'other-world',120,220,'456','0');
                    """;
                command.ExecuteNonQuery();
            }

            byte[] original = File.ReadAllBytes(source);
            var imported = TShockWarpMigration.Read(source, 123);
            var store = new WarpStore(Path.Combine(root, "warps"));
            Assert.That(await store.ImportNewAsync(guid, imported), Is.True);
            var persisted = await store.ReadAsync(guid);
            Assert.Multiple(() =>
            {
                Assert.That(persisted.Select(p => p.Name), Is.EqualTo(new[] { "town", "staff" }));
                Assert.That(persisted[0].Position.X, Is.EqualTo(1600));
                Assert.That(persisted[0].Position.Y, Is.EqualTo(3206));
                Assert.That(persisted[0].Hidden, Is.False);
                Assert.That(persisted[1].Hidden, Is.True);
                Assert.That(File.ReadAllBytes(source), Is.EqualTo(original));
            });
            Assert.That(await store.ImportNewAsync(guid, TShockWarpMigration.Read(source, 456)), Is.False);
            Assert.That(await store.ReadAsync(guid), Is.EqualTo(persisted));
            Assert.That(TShockWarpMigration.Read(source, 789), Is.Empty);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public void DuplicateOrReservedNamesDoNotCreateDestination()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-warp-import", Guid.NewGuid().ToString("N"));
        var store = new WarpStore(root);
        var world = Guid.NewGuid();
        try
        {
            Assert.ThrowsAsync<InvalidDataException>(async () => await store.ImportNewAsync(world,
                [new("town", new(1000, 1000)), new("TOWN", new(1100, 1100))]));
            Assert.ThrowsAsync<InvalidDataException>(async () => await store.ImportNewAsync(world,
                [new("list", new(1000, 1000))]));
            Assert.That(Directory.Exists(root), Is.False);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
