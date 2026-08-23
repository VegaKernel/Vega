using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Contracts.Operations;
using Vega.Contracts.Regions;
using Vega.Contracts.Worlds;
using Vega.Core.Worlds;
using Vega.Persistence.Sqlite;
using Vega.Regions;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class RegionWorldResetParticipantTests
{
    [Test]
    public async Task FailedWorldCommitRestoresRegionPersistenceWithAclAndFlags()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Vega.RegionReset.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            SqliteVegaDatabase database = await SqliteVegaDatabase.OpenAsync(
                new SqliteVegaDatabaseOptions(Path.Combine(directory, "vega.db")));
            RegionMutationResult mutation = await database.Regions.CreateAsync(
                WorldId.Primary, "spawn", 10, 20, 30, 40, 7, "Alice", "Server");
            RegionSnapshot region = mutation.After!;
            mutation = await database.Regions.UpdateAsync(
                WorldId.Primary,
                region.Name,
                new RegionUpdate(Flag: "protected", FlagValue: true),
                region.Revision,
                "Server");
            region = mutation.After!;
            mutation = await database.Regions.SetAccessAsync(
                WorldId.Primary, region.Name, RegionAccessKind.Member, "Bob", true, region.Revision, "Server");
            region = mutation.After!;
            mutation = await database.Regions.SetAccessAsync(
                WorldId.Primary, region.Name, RegionAccessKind.Group, "builder", true, region.Revision, "Server");
            Assert.That(mutation.Succeeded, Is.True);

            using var orchestrator = new WorldResetOrchestrator(
                new FailingBackend(),
                [new RegionWorldResetParticipant(database.Regions)],
                new NullLogger());
            WorldResetResult result = await orchestrator.ExecuteAsync(new WorldResetPlan(
                Guid.NewGuid(),
                WorldId.Primary,
                Path.Combine(directory, "current.wld"),
                Path.Combine(directory, "replacement.wld"),
                "Server",
                DateTimeOffset.UtcNow));

            RegionSnapshot restored = database.Regions.SnapshotRegions(WorldId.Primary).Single();
            Assert.Multiple(() =>
            {
                Assert.That(result.Succeeded, Is.False);
                Assert.That(restored.Name, Is.EqualTo("spawn"));
                Assert.That(restored.Owner, Is.EqualTo("Alice"));
                Assert.That(restored.Priority, Is.EqualTo(7));
                Assert.That(restored.Flags["protected"], Is.True);
                Assert.That(restored.Members, Does.Contain("Bob"));
                Assert.That(restored.Groups, Does.Contain("builder"));
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private sealed class FailingBackend : IWorldResetBackend
    {
        public string Name => "failing";

        public ValueTask ValidateAsync(WorldResetPlan plan, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<IPreparedWorldReset> PrepareAsync(
            WorldResetPlan plan,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IPreparedWorldReset>(new Prepared());

        private sealed class Prepared : IPreparedWorldReset
        {
            public ValueTask CommitAsync(CancellationToken cancellationToken = default) =>
                ValueTask.FromException(new IOException("synthetic commit failure"));

            public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class NullLogger : IOperationsLogger
    {
        public void Log(
            OperationsLogLevel level,
            string source,
            string message,
            Exception? exception = null,
            string? correlationId = null)
        {
        }
    }
}
