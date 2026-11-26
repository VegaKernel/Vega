using NUnit.Framework;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;
using Vega.Sandboxes;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class SandboxSscMigrationTests
{
    private string root = null!;
    private string legacyPath = null!;
    private string sharedPath = null!;
    private DateTimeOffset now;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "vega-ssc-migration", Guid.NewGuid().ToString("N"));
        legacyPath = Path.Combine(root, "primary-characters.json");
        sharedPath = Path.Combine(root, "sandboxes", "ssc.json");
        now = DateTimeOffset.UtcNow;
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Test]
    public void MigrationPreservesArchiveAndExistingWorldsAndAllocatesNewRevision()
    {
        var legacy = new SandboxSscStore(legacyPath);
        var shared = new SandboxSscStore(sharedPath);
        Assert.That(legacy.Save("1", WorldId.Primary, SandboxSscMode.Inherit, "first", 0, now).IsSuccess, Is.True);
        Assert.That(legacy.Save("2", WorldId.Primary, SandboxSscMode.Inherit, "same", 0, now).IsSuccess, Is.True);
        var existing = shared.Save("2", WorldId.Primary, SandboxSscMode.Inherit, "same", 0, now).State!;
        var arena = new WorldId("arena");
        var isolated = shared.Save("1", arena, SandboxSscMode.Isolated, "isolated", 0, now).State!;
        byte[] original = File.ReadAllBytes(legacyPath);
        byte[] oldShared = File.ReadAllBytes(sharedPath);

        var result = shared.MigrateLegacyPrimary(legacyPath, now);
        var reopened = new SandboxSscStore(sharedPath);
        var restored = reopened.Read("1", arena, SandboxSscMode.Inherit)!;
        Assert.Multiple(() =>
        {
            Assert.That(result.Imported, Is.EqualTo(1));
            Assert.That(result.AlreadyPresent, Is.EqualTo(1));
            Assert.That(File.Exists(legacyPath), Is.False);
            Assert.That(File.ReadAllBytes(result.LegacyArchive), Is.EqualTo(original));
            Assert.That(File.ReadAllBytes(result.SharedBackup!), Is.EqualTo(oldShared));
            Assert.That(restored.Payload, Is.EqualTo("first"));
            Assert.That(restored.Revision, Is.GreaterThan(isolated.Revision));
            Assert.That(restored.UpdatedAt, Is.EqualTo(now));
            Assert.That(reopened.Read("2", WorldId.Primary, SandboxSscMode.Inherit), Is.EqualTo(existing));
            Assert.That(reopened.Read("1", arena, SandboxSscMode.Isolated), Is.EqualTo(isolated));
        });
    }

    [Test]
    public void ConflictingCharacterRejectsWholeMigrationWithoutChangingFiles()
    {
        var legacy = new SandboxSscStore(legacyPath);
        var shared = new SandboxSscStore(sharedPath);
        legacy.Save("1", WorldId.Primary, SandboxSscMode.Inherit, "new", 0, now);
        legacy.Save("2", WorldId.Primary, SandboxSscMode.Inherit, "old", 0, now);
        shared.Save("2", WorldId.Primary, SandboxSscMode.Inherit, "different", 0, now);
        byte[] original = File.ReadAllBytes(legacyPath);
        byte[] oldShared = File.ReadAllBytes(sharedPath);

        Assert.Throws<InvalidDataException>(() => shared.MigrateLegacyPrimary(legacyPath, now));
        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(legacyPath), Is.EqualTo(original));
            Assert.That(File.ReadAllBytes(sharedPath), Is.EqualTo(oldShared));
            Assert.That(File.Exists(legacyPath + ".migrated"), Is.False);
            Assert.That(shared.Read("1", WorldId.Primary, SandboxSscMode.Inherit), Is.Null);
        });
    }

    [Test]
    public void ActiveTransferBlocksMigrationAndExpiredTransferDoesNot()
    {
        var legacy = new SandboxSscStore(legacyPath);
        var shared = new SandboxSscStore(sharedPath);
        legacy.Save("1", WorldId.Primary, SandboxSscMode.Inherit, "first", 0, now);
        Assert.That(shared.PrepareTransfer("1", WorldId.Primary, new WorldId("arena"), SandboxSscMode.Inherit,
            SandboxSscMode.Isolated, now, TimeSpan.FromSeconds(30)).Accepted, Is.True);
        byte[] original = File.ReadAllBytes(sharedPath);
        Assert.Throws<InvalidDataException>(() => shared.MigrateLegacyPrimary(legacyPath, now));
        Assert.That(File.ReadAllBytes(sharedPath), Is.EqualTo(original));
        Assert.That(shared.MigrateLegacyPrimary(legacyPath, now.AddMinutes(1)).Imported, Is.EqualTo(1));
    }

    [Test]
    public void SnapshotReadDoesNotCreateOrRewriteStore()
    {
        var shared = new SandboxSscStore(sharedPath);
        Assert.That(shared.Read("1", WorldId.Primary, SandboxSscMode.Inherit), Is.Null);
        Assert.That(File.Exists(sharedPath), Is.False);
        shared.Save("1", WorldId.Primary, SandboxSscMode.Inherit, "first", 0, now);
        var marker = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(sharedPath, marker);
        Assert.That(shared.Read("1", WorldId.Primary, SandboxSscMode.Inherit)?.Payload, Is.EqualTo("first"));
        Assert.That(File.GetLastWriteTimeUtc(sharedPath), Is.EqualTo(marker));
    }

    [Test]
    public void UnchangedPeriodicCheckpointRetainsRevisionWithoutRewriteButStillChecksConcurrency()
    {
        var shared = new SandboxSscStore(sharedPath);
        var original = shared.Save("1", WorldId.Primary, SandboxSscMode.Inherit, "first", 0, now).State!;
        var marker = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(sharedPath, marker);
        var checkpoint = shared.SaveCheckpoint("1", WorldId.Primary, SandboxSscMode.Inherit, "first", original.Revision, now.AddSeconds(5));
        Assert.That(checkpoint.State, Is.EqualTo(original));
        Assert.That(File.GetLastWriteTimeUtc(sharedPath), Is.EqualTo(marker));
        var changed = new SandboxSscStore(sharedPath).SaveCheckpoint("1", WorldId.Primary, SandboxSscMode.Inherit, "second", original.Revision, now.AddSeconds(10));
        Assert.That(changed.State?.Revision, Is.GreaterThan(original.Revision));
        Assert.That(shared.SaveCheckpoint("1", WorldId.Primary, SandboxSscMode.Inherit, "first", original.Revision, now.AddSeconds(15)).Status,
            Is.EqualTo(SandboxSscWriteStatus.Conflict));
        Assert.That(shared.Read("1", WorldId.Primary, SandboxSscMode.Inherit)?.Payload, Is.EqualTo("second"));
    }
}
