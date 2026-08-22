using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Contracts.Authorization;
using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Localization;
using Vega.Contracts.Players;
using Vega.Core.Authorization;
using Vega.Core.Bans;
using Vega.Core.Localization;
using Vega.Persistence.Sqlite;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class PlayerAccountSessionServiceTests
{
    private string testDirectory = null!;

    [SetUp]
    public void SetUp() => testDirectory = Path.Combine(
        Path.GetTempPath(),
        "Vega.AccountSession.Tests",
        Guid.NewGuid().ToString("N"));

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
    public async Task LoginProjectsInheritedWildcardDeniesAndGroupMarkers()
    {
        SqliteVegaDatabase database = await OpenAsync();
        GroupMutationResult createdGroup = await database.Authorization.CreateGroupAsync(
            "builders", "default", "Server");
        GroupMutationResult wildcard = await database.Authorization.SetPermissionAsync(
            "builders", "*", PermissionEffect.Grant, createdGroup.After!.Revision, "Server");
        GroupMutationResult denied = await database.Authorization.SetPermissionAsync(
            "builders", "vega.blocked", PermissionEffect.Deny, wildcard.After!.Revision, "Server");
        await database.Accounts.CreateAsync(
            "alice", "correct horse battery staple", "builders", "Server");
        var lifecycle = new FakeLifecycle();
        using var permissions = new PlayerSessionAuthorizationService(lifecycle);
        var cultures = new LocalizationService("en-US", ["en-US", "ru-RU"]);
        using var sessions = new PlayerAccountSessionService(
            database.Accounts, database.Authorization, permissions, cultures, lifecycle);
        var playerId = new PlayerId(7);

        PlayerAccountSessionResult result = await sessions.LoginAsync(
            playerId, "ALICE", "correct horse battery staple");
        await database.Authorization.SetPermissionAsync(
            "builders", "vega.live-update", PermissionEffect.Deny, denied.After!.Revision, "Server");

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Account!.LastSeenUtc, Is.Not.Null);
            Assert.That(permissions.HasPermission(playerId, "vega.anything"), Is.True);
            Assert.That(permissions.HasPermission(playerId, "vega.blocked"), Is.False);
            Assert.That(permissions.HasPermission(playerId, "vega.live-update"), Is.False);
            Assert.That(permissions.HasPermission(playerId, "vega.group.builders"), Is.True);
            Assert.That(permissions.HasPermission(playerId, "vega.group.default"), Is.True);
            Assert.That(sessions.TryGetAccount(playerId, out var account), Is.True);
            Assert.That(account!.Name, Is.EqualTo("alice"));
        });
    }

    [Test]
    public async Task RegisterPasswordChangeLogoutAndDisconnectClearSessionState()
    {
        SqliteVegaDatabase database = await OpenAsync();
        var lifecycle = new FakeLifecycle();
        using var permissions = new PlayerSessionAuthorizationService(lifecycle);
        var cultures = new LocalizationService("en-US", ["en-US", "ru-RU"]);
        using var sessions = new PlayerAccountSessionService(
            database.Accounts, database.Authorization, permissions, cultures, lifecycle);
        var playerId = new PlayerId(3);

        PlayerAccountSessionResult registered = await sessions.RegisterAsync(
            playerId, "Player", "initial password");
        Assert.That(await sessions.SetCultureAsync(playerId, "ru-RU"), Is.True);
        PlayerAccountSessionResult changed = await sessions.ChangePasswordAsync(
            playerId, "initial password", "replacement password");
        PlayerAccountSessionResult loggedOut = sessions.Logout(playerId);
        PlayerAccountSessionResult loggedIn = await sessions.LoginAsync(
            playerId, "player", "replacement password");
        PlayerCulture restoredCulture = cultures.Resolve("player:3");
        lifecycle.Close(playerId);

        Assert.Multiple(() =>
        {
            Assert.That(registered.Succeeded, Is.True);
            Assert.That(changed.Succeeded, Is.True);
            Assert.That(loggedOut.Succeeded, Is.True);
            Assert.That(loggedIn.Succeeded, Is.True);
            Assert.That(restoredCulture.Name, Is.EqualTo("ru-RU"));
            Assert.That(cultures.Resolve("player:3").Name, Is.EqualTo("en-US"));
            Assert.That(database.Accounts.VerifyPasswordAsync("player", "initial password").AsTask().Result, Is.False);
            Assert.That(database.Accounts.VerifyPasswordAsync("player", "replacement password").AsTask().Result, Is.True);
            Assert.That(sessions.TryGetAccount(playerId, out _), Is.False);
            Assert.That(permissions.HasPermission(playerId, "*"), Is.False);
        });
    }

    [Test]
    public async Task AccountBanIsRejected()
    {
        SqliteVegaDatabase database = await OpenAsync();
        await database.Accounts.CreateAsync(
            "alice", "correct horse battery staple", "default", "Server");
        var bans = new GlobalBanService(database.GlobalBans);
        await bans.CreateAsync(new GlobalBanCreated(
            GlobalBanId.New(),
            new GlobalBanSubject(GlobalBanSubjectKind.Account, "alice"),
            "test ban",
            "Server",
            DateTimeOffset.UtcNow,
            null,
            1));
        var lifecycle = new FakeLifecycle();
        using var permissions = new PlayerSessionAuthorizationService(lifecycle);
        var cultures = new LocalizationService("en-US");
        using var bannedSessions = new PlayerAccountSessionService(
            database.Accounts, database.Authorization, permissions, cultures, lifecycle, bans);

        PlayerAccountSessionResult banned = await bannedSessions.LoginAsync(
            new PlayerId(1), "alice", "correct horse battery staple");

        Assert.Multiple(() =>
        {
            Assert.That(banned.Code, Is.EqualTo("account.banned"));
            Assert.That(bannedSessions.TryGetAccount(new PlayerId(1), out _), Is.False);
        });
    }

    private ValueTask<SqliteVegaDatabase> OpenAsync() => SqliteVegaDatabase.OpenAsync(
        new SqliteVegaDatabaseOptions(Path.Combine(testDirectory, "vega.db")));

    private sealed class FakeLifecycle : IPlayerSessionLifecycle
    {
        public event Action<PlayerSessionOpened>? Opened;
        public event Action<PlayerSessionClosed>? Closed;

        public void Close(PlayerId playerId) => Closed?.Invoke(new PlayerSessionClosed(
            new PlayerSessionId(Guid.NewGuid()),
            playerId,
            "Player",
            DateTimeOffset.UtcNow));

        public void Open(PlayerId playerId) => Opened?.Invoke(new PlayerSessionOpened(
            new PlayerSessionId(Guid.NewGuid()),
            playerId,
            "Player",
            DateTimeOffset.UtcNow));
    }
}
