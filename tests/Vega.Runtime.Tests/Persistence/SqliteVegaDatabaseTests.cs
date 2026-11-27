using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Authorization;
using Vega.Contracts.Regions;
using Vega.Contracts.Moderation;
using Vega.Contracts.Worlds;
using Vega.Persistence.Sqlite;

namespace Vega.Runtime.Tests.Persistence;

[TestFixture]
public sealed class SqliteVegaDatabaseTests
{
    private string testDirectory = null!;
    private string databasePath = null!;

    [SetUp]
    public void SetUp()
    {
        testDirectory = Path.Combine(
            Path.GetTempPath(),
            "Vega.Persistence.Tests",
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
    public async Task CleanDatabaseInitializesOneBaselineAndCanBeReopened()
    {
        SqliteVegaDatabase first = await OpenAsync();
        SqliteVegaDatabase second = await OpenAsync();

        Assert.Multiple(() =>
        {
            Assert.That(first.SchemaVersion, Is.EqualTo(3));
            Assert.That(second.SchemaVersion, Is.EqualTo(3));
            Assert.That(first.DatabasePath, Is.EqualTo(Path.GetFullPath(databasePath)));
            Assert.That(File.Exists(databasePath), Is.True);
            Assert.That(ScalarInt64("SELECT COUNT(*) FROM vega_schema_history;"), Is.EqualTo(3));
            Assert.That(ScalarInt64("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'global_bans';"), Is.EqualTo(1));
            Assert.That(ScalarInt64("SELECT COUNT(*) FROM vega_groups;"), Is.EqualTo(3));
        });
    }

    [Test]
    public async Task ProviderNeutralImportPreservesIdentityAclCacheAndRevokedBans()
    {
        SqliteVegaDatabase database = await OpenAsync();
        Assert.That((await database.Authorization.CreateGroupAsync("builders", "default", "Server")).Succeeded, Is.True);
        await ExternalVegaDatabaseContractTests.AssertOfflineImportFamiliesAsync(database);
    }

    [Test]
    public async Task ExistingSchemaOneUpgradesAndPresentationKeepsRevisionAuditAndPermissions()
    {
        SqliteVegaDatabase original = await OpenAsync();
        var created = await original.Authorization.CreateGroupAsync("builders", "default", "Server");
        Assert.That(created.Succeeded, Is.True);
        // Reconstitute the previous schema layout in this test-owned database.
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE vega_play_sessions; DROP TABLE vega_group_presentation; DELETE FROM vega_schema_history WHERE version >= 2;";
            await command.ExecuteNonQueryAsync();
        }
        var migrated = await OpenAsync();
        Assert.That(migrated.SchemaVersion, Is.EqualTo(3));
        Assert.That(migrated.Authorization.TryGetGroup("builders", out var before), Is.True);
        Assert.That(before!.Presentation, Is.EqualTo(GroupChatPresentation.Default));
        var style = new GroupChatPresentation("[Builder] ", "!", "12ab34");
        var changed = await migrated.Authorization.SetPresentationAsync("builders", style, before.Revision, "Server");
        Assert.That(changed.Succeeded, Is.True);
        Assert.That(changed.After!.Revision, Is.EqualTo(before.Revision + 1));
        Assert.That(changed.After.Permissions, Is.EqualTo(before.Permissions));
        Assert.That(changed.After.ParentName, Is.EqualTo(before.ParentName));
        Assert.That((await migrated.Authorization.SetPresentationAsync("builders", GroupChatPresentation.Default, before.Revision, "Server")).Code,
            Is.EqualTo("group.revision-conflict"));
        Assert.That((await migrated.Authorization.SetPresentationAsync("builders", new("bad\ntext"), changed.After.Revision, "Server")).Code,
            Is.EqualTo("group.invalid-presentation"));
        var reopened = await OpenAsync();
        Assert.That(reopened.Authorization.TryGetGroup("builders", out var persisted), Is.True);
        Assert.That(persisted!.Presentation, Is.EqualTo(style with { Color = "12AB34" }));
        Assert.That(ScalarInt64("SELECT COUNT(*) FROM vega_group_presentation;"), Is.EqualTo(1));
        Assert.That(ScalarInt64("SELECT COUNT(*) FROM vega_administration_audit WHERE operation = 'group.presentation';"), Is.EqualTo(1));
        Assert.That((await reopened.Authorization.DeleteGroupAsync("builders", persisted.Revision, "Server")).Succeeded, Is.True);
        Assert.That(ScalarInt64("SELECT COUNT(*) FROM vega_group_presentation;"), Is.Zero);
    }

    [Test]
    public async Task OnlineBackupIsConsistentAndReopensIndependently()
    {
        SqliteVegaDatabase database = await OpenAsync();
        await database.Authorization.CreateGroupAsync("builders", "default", "Server");
        string backupPath = Path.Combine(testDirectory, "backup", database.BackupFileName);

        await database.WriteBackupAsync(backupPath);

        bool walCreatedByBackup = File.Exists(backupPath + "-wal");
        bool shmCreatedByBackup = File.Exists(backupPath + "-shm");
        await using var connection = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM vega_groups WHERE name = 'builders';";
        long groupCount = Convert.ToInt64(await command.ExecuteScalarAsync());
        Assert.Multiple(() =>
        {
            Assert.That(groupCount, Is.EqualTo(1));
            Assert.That(walCreatedByBackup, Is.False);
            Assert.That(shmCreatedByBackup, Is.False);
        });
    }

    [Test]
    public async Task AccountsGroupsPasswordsAndOptimisticRevisionsPersistAcrossRestart()
    {
        SqliteVegaDatabase database = await OpenAsync();
        var builders = await database.Authorization.CreateGroupAsync(
            "Builders",
            "default",
            "Server");
        var granted = await database.Authorization.SetPermissionAsync(
            "builders",
            "vega.world.build",
            PermissionEffect.Grant,
            builders.After!.Revision,
            "Server");
        var denied = await database.Authorization.SetPermissionAsync(
            "builders",
            "vega.world.destroy",
            PermissionEffect.Deny,
            granted.After!.Revision,
            "Server");
        var created = await database.Accounts.CreateAsync(
            "Alice",
            "correct horse battery staple",
            "builders",
            "Server");
        var conflict = await database.Accounts.SetCultureAsync(
            "alice",
            "ru-RU",
            expectedRevision: 999,
            "Server");
        var culture = await database.Accounts.SetCultureAsync(
            "alice",
            "ru-RU",
            created.After!.Revision,
            "Server");

        SqliteVegaDatabase reopened = await OpenAsync();
        var account = await reopened.Accounts.FindByNameAsync("ALICE");

        Assert.Multiple(() =>
        {
            Assert.That(denied.Succeeded, Is.True);
            Assert.That(reopened.Authorization.HasPermission("builders", "vega.world.build"), Is.True);
            Assert.That(reopened.Authorization.HasPermission("builders", "vega.world.destroy"), Is.False);
            Assert.That(conflict.Code, Is.EqualTo("account.revision-conflict"));
            Assert.That(culture.Succeeded, Is.True);
            Assert.That(account!.Culture, Is.EqualTo("ru-RU"));
            Assert.That(account.Revision, Is.EqualTo(2));
            Assert.That(reopened.Accounts.VerifyPasswordAsync(
                "alice",
                "correct horse battery staple").AsTask().Result, Is.True);
            Assert.That(reopened.Accounts.VerifyPasswordAsync("alice", "wrong password").AsTask().Result, Is.False);
            Assert.That(ScalarInt64("SELECT COUNT(*) FROM vega_administration_audit;"), Is.EqualTo(5));
        });
    }

    [Test]
    public async Task GroupInheritanceCyclesAreRejected()
    {
        SqliteVegaDatabase database = await OpenAsync();
        var first = await database.Authorization.CreateGroupAsync("first", "default", "Server");
        var second = await database.Authorization.CreateGroupAsync("second", "first", "Server");

        var cycle = await database.Authorization.SetParentAsync(
            "first",
            "second",
            first.After!.Revision,
            "Server");

        Assert.Multiple(() =>
        {
            Assert.That(second.Succeeded, Is.True);
            Assert.That(cycle.Succeeded, Is.False);
            Assert.That(cycle.Code, Is.EqualTo("group.inheritance-cycle"));
        });
    }

    [Test]
    public async Task TemporaryRoleLeaseReplacesAtomicallyPersistsAndRejectsStaleRevocation()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-23T12:00:00Z");
        SqliteVegaDatabase database = await OpenAsync();
        await database.Authorization.CreateGroupAsync("builder", "default", "Server");
        await database.Authorization.CreateGroupAsync("moderator", "default", "Server");
        var account = await database.Accounts.CreateAsync(
            "Alice",
            "correct horse battery staple",
            "default",
            "Server");

        var first = await database.RoleLeases.GrantAsync(
            account.After!.Id,
            "builder",
            now,
            now.AddHours(1),
            "Server");
        var second = await database.RoleLeases.GrantAsync(
            account.After.Id,
            "moderator",
            now.AddMinutes(1),
            now.AddHours(2),
            "Server");
        RoleLeaseMutationResult stale = await database.RoleLeases.RevokeAsync(
            first.After!.Id,
            first.After.Revision,
            "stale-expiry");

        SqliteVegaDatabase reopened = await OpenAsync();
        RoleLeaseSnapshot? restored = reopened.RoleLeases.FindActive(account.After.Id, now.AddMinutes(2));
        RoleLeaseMutationResult revoked = await reopened.RoleLeases.RevokeAsync(
            second.After!.Id,
            second.After.Revision,
            "Server");

        Assert.Multiple(() =>
        {
            Assert.That(first.Succeeded, Is.True);
            Assert.That(second.Succeeded, Is.True);
            Assert.That(stale.Code, Is.EqualTo("role-lease.revision-conflict"));
            Assert.That(restored!.Id, Is.EqualTo(second.After.Id));
            Assert.That(restored.GroupName, Is.EqualTo("moderator"));
            Assert.That(reopened.RoleLeases.FindActive(account.After.Id, now.AddMinutes(2)), Is.Null);
            Assert.That(revoked.Succeeded, Is.True);
            Assert.That(reopened.RoleLeases.SnapshotActive(now.AddHours(3)), Is.Empty);
        });
    }

    [Test]
    public async Task AccountMutePersistsMatchesExpiresAndRevokesWithAudit()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var mute = new MuteSnapshot(
            MuteId.New(),
            MuteSubjectKind.Account,
            "Alice",
            "chat abuse",
            "Server",
            now,
            now.AddHours(2),
            1);
        SqliteVegaDatabase database = await OpenAsync();

        MuteMutationResult created = await database.Mutes.CreateAsync(mute);
        SqliteVegaDatabase reopened = await OpenAsync();
        MuteSnapshot? matched = reopened.Mutes.FindActive("DifferentPlayerName", "alice", now.AddMinutes(1));
        MuteMutationResult stale = await reopened.Mutes.RevokeAsync(
            mute.Id, expectedRevision: 9, "stale", now.AddMinutes(2));
        MuteMutationResult revoked = await reopened.Mutes.RevokeAsync(
            mute.Id, mute.Revision, "Server", now.AddMinutes(3));

        Assert.Multiple(() =>
        {
            Assert.That(created.Succeeded, Is.True);
            Assert.That(matched!.Reason, Is.EqualTo("chat abuse"));
            Assert.That(stale.Code, Is.EqualTo("mute.revision-conflict"));
            Assert.That(revoked.Succeeded, Is.True);
            Assert.That(reopened.Mutes.FindActive("Alice", "Alice", now.AddMinutes(4)), Is.Null);
            Assert.That(reopened.Mutes.SnapshotActive(now.AddHours(3)), Is.Empty);
        });
    }

    [Test]
    public async Task WorldScopedRegionsPersistPriorityAccessFlagsAndOptimisticRevision()
    {
        SqliteVegaDatabase database = await OpenAsync();
        WorldId world = new("arena");
        RegionMutationResult outer = await database.Regions.CreateAsync(
            world, "outer", 0, 0, 100, 100, 1, "alice", "Server");
        Assert.That(database.Regions.SnapshotRegions(world), Has.Count.EqualTo(1));
        RegionMutationResult inner = await database.Regions.CreateAsync(
            world, "inner", 10, 10, 20, 20, 5, "bob", "Server");
        Assert.That(database.Regions.SnapshotRegions(world), Has.Count.EqualTo(2));
        RegionMutationResult member = await database.Regions.SetAccessAsync(
            world,
            "inner",
            RegionAccessKind.Member,
            "alice",
            true,
            inner.After!.Revision,
            "Server");
        RegionMutationResult flagged = await database.Regions.UpdateAsync(
            world,
            "inner",
            new RegionUpdate(Flag: "build", FlagValue: false),
            member.After!.Revision,
            "Server");
        RegionSnapshot? cachedTop = database.Regions.FindTopRegion(world, 15, 15);
        RegionMutationResult conflict = await database.Regions.UpdateAsync(
            world,
            "inner",
            new RegionUpdate(Priority: 10),
            expectedRevision: 1,
            "Server");

        SqliteVegaDatabase reopened = await OpenAsync();
        RegionSnapshot? top = reopened.Regions.FindTopRegion(world, 15, 15);

        Assert.Multiple(() =>
        {
            Assert.That(outer.Succeeded, Is.True);
            Assert.That(flagged.Succeeded, Is.True);
            Assert.That(cachedTop!.Members, Does.Contain("alice"));
            Assert.That(cachedTop.Flags["build"], Is.False);
            Assert.That(conflict.Code, Is.EqualTo("region.revision-conflict"));
            Assert.That(top!.Name, Is.EqualTo("inner"));
            Assert.That(top.Priority, Is.EqualTo(5));
            Assert.That(top.Members, Does.Contain("alice"));
            Assert.That(top.Flags["build"], Is.False);
            Assert.That(reopened.Regions.SnapshotRegions(WorldId.Primary), Is.Empty);
        });
    }

    [Test]
    public async Task RegionLifecycleKeepsStableWorldIdentityOfflineOwnerAndConcurrentRevision()
    {
        SqliteVegaDatabase database = await OpenAsync();
        WorldId stableWorld = new("world-42");
        WorldId deletedWorld = new("world-to-delete");
        RegionMutationResult outer = await database.Regions.CreateAsync(
            stableWorld, "spawn", 0, 0, 100, 100, 1, "OfflineOwner", "Server");
        RegionMutationResult inner = await database.Regions.CreateAsync(
            stableWorld, "vip", 10, 10, 10, 10, 1, null, "Server");
        RegionMutationResult group = await database.Regions.SetAccessAsync(
            stableWorld, "vip", RegionAccessKind.Group, "builders", true,
            inner.After!.Revision, "Server");
        RegionMutationResult disposable = await database.Regions.CreateAsync(
            deletedWorld, "spawn", 0, 0, 20, 20, 1, null, "Server");

        Task<RegionMutationResult>[] competing =
        [
            database.Regions.UpdateAsync(stableWorld, "spawn", new RegionUpdate(Flag: "edit-a", FlagValue: true),
                outer.After!.Revision, "operator-a").AsTask(),
            database.Regions.UpdateAsync(stableWorld, "spawn", new RegionUpdate(Flag: "edit-b", FlagValue: true),
                outer.After!.Revision, "operator-b").AsTask()
        ];
        RegionMutationResult[] edits = await Task.WhenAll(competing);
        RegionMutationResult deleted = await database.Regions.DeleteAsync(
            deletedWorld, "spawn", disposable.After!.Revision, "Server");
        SqliteVegaDatabase reopened = await OpenAsync();

        Assert.Multiple(() =>
        {
            Assert.That(edits.Count(static edit => edit.Succeeded), Is.EqualTo(1));
            Assert.That(edits.Count(static edit => edit.Code == "region.revision-conflict"), Is.EqualTo(1));
            Assert.That(deleted.Succeeded, Is.True);
            Assert.That(reopened.Regions.SnapshotRegions(deletedWorld), Is.Empty);
            Assert.That(reopened.Regions.SnapshotRegions(stableWorld), Has.Count.EqualTo(2));
            Assert.That(reopened.Regions.SnapshotRegions(stableWorld).Single(region => region.Name == "spawn").Owner,
                Is.EqualTo("offlineowner"));
            Assert.That(reopened.Regions.FindTopRegion(stableWorld, 12, 12)!.Name, Is.EqualTo("vip"));
            Assert.That(group.After!.Groups, Does.Contain("builders"));
        });
    }

    [Test]
    public void DomainStoreCannotRunBeforeRequiredRuntimeInitialization()
    {
        SqliteVegaDatabase database = SqliteVegaDatabase.Create(
            new SqliteVegaDatabaseOptions(databasePath));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            database.GlobalBans.SnapshotActive(DateTimeOffset.UtcNow))!;

        Assert.Multiple(() =>
        {
            Assert.That(database.IsReady, Is.False);
            Assert.That(error.Message, Does.Contain("has not completed runtime initialization"));
            Assert.That(File.Exists(databasePath), Is.False);
            Assert.That(Directory.Exists(Path.GetDirectoryName(databasePath)!), Is.False);
        });
    }

    [Test]
    public async Task ActiveBanPersistsAcrossDatabaseReopen()
    {
        DateTimeOffset createdAt = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
        GlobalBanCreated created = CreateBan(createdAt);
        SqliteVegaDatabase first = await OpenAsync();

        bool changed = await first.GlobalBans.ApplyCreatedAsync(created);
        SqliteVegaDatabase reopened = await OpenAsync();
        GlobalBanCreated[] active = reopened.GlobalBans
            .SnapshotActive(createdAt.AddMinutes(1))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(active, Has.Length.EqualTo(1));
            Assert.That(active[0], Is.EqualTo(created));
        });
    }

    [Test]
    public async Task DuplicateAndStaleRevisionsDoNotOverwriteNewerState()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
        GlobalBanCreated revisionTwo = CreateBan(now) with
        {
            Reason = "new reason",
            Revision = 2
        };
        GlobalBanCreated stale = CreateBan(now) with
        {
            Reason = "stale reason",
            Revision = 1
        };
        SqliteVegaDatabase database = await OpenAsync();

        bool initial = await database.GlobalBans.ApplyCreatedAsync(revisionTwo);
        bool duplicate = await database.GlobalBans.ApplyCreatedAsync(revisionTwo);
        bool staleChanged = await database.GlobalBans.ApplyCreatedAsync(stale);
        GlobalBanCreated active = database.GlobalBans.SnapshotActive(now)[0];

        Assert.Multiple(() =>
        {
            Assert.That(initial, Is.True);
            Assert.That(duplicate, Is.False);
            Assert.That(staleChanged, Is.False);
            Assert.That(active.Revision, Is.EqualTo(2));
            Assert.That(active.Reason, Is.EqualTo("new reason"));
        });
    }

    [Test]
    public async Task RevocationTombstoneSurvivesRestartAndRejectsOlderCreate()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
        GlobalBanCreated created = CreateBan(now);
        var revoked = new GlobalBanRevoked(
            created.BanId,
            "console",
            now.AddMinutes(1),
            2);
        SqliteVegaDatabase first = await OpenAsync();

        Assert.That(await first.GlobalBans.ApplyRevokedAsync(revoked), Is.True);
        SqliteVegaDatabase reopened = await OpenAsync();
        bool staleCreateChanged = await reopened.GlobalBans.ApplyCreatedAsync(created);

        Assert.Multiple(() =>
        {
            Assert.That(staleCreateChanged, Is.False);
            Assert.That(reopened.GlobalBans.SnapshotActive(now.AddMinutes(2)), Is.Empty);
        });
    }

    [Test]
    public async Task ExpiredAndRevokedBansAreExcludedFromActiveSnapshot()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
        SqliteVegaDatabase database = await OpenAsync();
        GlobalBanCreated expired = CreateBan(now.AddMinutes(-2)) with
        {
            ExpiresAt = now.AddMinutes(-1)
        };
        GlobalBanCreated revoked = CreateBan(now) with
        {
            BanId = GlobalBanId.New(),
            Subject = new GlobalBanSubject(GlobalBanSubjectKind.IpAddress, "192.0.2.4")
        };
        GlobalBanCreated active = CreateBan(now) with
        {
            BanId = GlobalBanId.New(),
            Subject = new GlobalBanSubject(GlobalBanSubjectKind.Network, "2001:db8::/48")
        };

        await database.GlobalBans.ApplyCreatedAsync(expired);
        await database.GlobalBans.ApplyCreatedAsync(revoked);
        await database.GlobalBans.ApplyRevokedAsync(new GlobalBanRevoked(
            revoked.BanId,
            "console",
            now.AddSeconds(1),
            2));
        await database.GlobalBans.ApplyCreatedAsync(active);

        Assert.That(database.GlobalBans.SnapshotActive(now), Is.EqualTo(new[] { active }));
    }

    [Test]
    public async Task DatabaseFromANewerVegaSchemaIsRejectedWithoutDowngrade()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE vega_schema_history (
                    version INTEGER NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL,
                    applied_at_utc TEXT NOT NULL
                );
                INSERT INTO vega_schema_history VALUES (99, 'future', '2026-08-20T12:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
        }

        InvalidDataException error = Assert.ThrowsAsync<InvalidDataException>(async () =>
            await OpenAsync())!;

        Assert.Multiple(() =>
        {
            Assert.That(error.Message, Does.Contain("newer than supported schema"));
            Assert.That(ScalarInt64("SELECT MAX(version) FROM vega_schema_history;"), Is.EqualTo(99));
        });
    }

    private ValueTask<SqliteVegaDatabase> OpenAsync() =>
        SqliteVegaDatabase.OpenAsync(new SqliteVegaDatabaseOptions(databasePath));

    private long ScalarInt64(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static GlobalBanCreated CreateBan(DateTimeOffset createdAt) =>
        new(
            new GlobalBanId(Guid.Parse("7d72b775-ded6-4426-bc5e-3d1f5fa46c04")),
            new GlobalBanSubject(GlobalBanSubjectKind.Account, "alice"),
            "test reason",
            "console",
            createdAt,
            createdAt.AddHours(1),
            1);
}
