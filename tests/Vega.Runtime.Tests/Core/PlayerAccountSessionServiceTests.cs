using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Contracts.Authorization;
using Vega.Contracts.Accounts;
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

    [Test]
    public async Task AccountCommandsAreBoundToTheOriginalPlayerSession()
    {
        SqliteVegaDatabase database = await OpenAsync();
        await database.Accounts.CreateAsync("alice", "correct horse battery staple", "default", "Server");
        var lifecycle = new FakeLifecycle();
        using var permissions = new PlayerSessionAuthorizationService(lifecycle);
        using var sessions = new PlayerAccountSessionService(database.Accounts, database.Authorization, permissions,
            new LocalizationService("en-US"), lifecycle);
        var playerId = new PlayerId(4);
        PlayerSessionId previous = lifecycle.Open(playerId);
        lifecycle.Close(playerId, previous);
        PlayerSessionId current = lifecycle.Open(playerId);
        var stale = await sessions.LoginAsync(playerId, "alice", "correct horse battery staple", expectedSession: previous);
        Assert.That(stale.Code, Is.EqualTo("account.session-changed"));
        var active = await sessions.LoginAsync(playerId, "alice", "correct horse battery staple", expectedSession: current);
        Assert.That(active.Succeeded, Is.True);
        Assert.That(sessions.Logout(playerId, previous).Code, Is.EqualTo("account.session-changed"));
        Assert.That(sessions.TryGetAccount(playerId, out var authenticated), Is.True);
        Assert.That(authenticated!.Name, Is.EqualTo("alice"));
    }

    [Test]
    public async Task AccountBindingChangesOnSameSocketReloginAndRejectsRetiredOperations()
    {
        var database = await OpenAsync();
        await database.Accounts.CreateAsync("alice", "correct horse battery staple", "default", "Server");
        var lifecycle = new FakeLifecycle();
        using var permissions = new PlayerSessionAuthorizationService(lifecycle);
        using var sessions = new PlayerAccountSessionService(database.Accounts, database.Authorization, permissions, new LocalizationService("en-US"), lifecycle);
        var playerId = new PlayerId(4);
        var sessionId = lifecycle.Open(playerId);
        var player = new PlayerSnapshot(playerId, "alice", false, 100, 100, 20, 20, new(0, 0), false, 0) { SessionId = sessionId };
        var established = new List<PlayerAccountBinding>();
        sessions.BindingEstablished += binding =>
        {
            if (sessions.TryGetBinding(player, out var current) && binding == current) established.Add(binding);
        };
        Assert.That((await sessions.LoginAsync(playerId, "alice", "correct horse battery staple", expectedSession: sessionId)).Succeeded, Is.True);
        Assert.That(sessions.TryGetBinding(player, out PlayerAccountBinding before), Is.True);
        Assert.That(established, Is.EqualTo(new[] { before }));
        Assert.That(sessions.TryRunWithBinding(before, () => 17, out int value), Is.True);
        Assert.That(value, Is.EqualTo(17));
        bool closingBindingValid = false;
        sessions.BindingClosing += binding => closingBindingValid = binding == before && sessions.TryRunWithBinding(binding, () => true, out _);
        PlayerAccountBinding? endedBinding = null;
        bool endedBindingRevoked = false;
        sessions.BindingEnded += binding =>
        {
            endedBinding = binding;
            endedBindingRevoked = !sessions.TryGetBinding(player, out _) && !sessions.TryRunWithBinding(binding, () => true, out _);
        };
        Assert.That(sessions.Logout(playerId, sessionId).Succeeded, Is.True);
        Assert.That(closingBindingValid, Is.True);
        Assert.That(endedBinding, Is.EqualTo(before));
        Assert.That(endedBindingRevoked, Is.True);
        Assert.That(sessions.TryGetBinding(player, out _), Is.False);
        Assert.That((await sessions.LoginAsync(playerId, "alice", "correct horse battery staple", expectedSession: sessionId)).Succeeded, Is.True);
        Assert.That(sessions.TryGetBinding(player, out PlayerAccountBinding after), Is.True);
        Assert.That(established, Is.EqualTo(new[] { before, after }));
        Assert.That(after.AccountId, Is.EqualTo(before.AccountId));
        Assert.That(after.AuthenticationId, Is.Not.EqualTo(before.AuthenticationId));
        Assert.That(sessions.TryRunWithBinding(before, () => throw new AssertionException("Retired binding executed."), out int _), Is.False);
        Assert.That(sessions.TryRunWithBinding(after, () => 42, out value), Is.True);
        Assert.That(value, Is.EqualTo(42));
        lifecycle.Close(playerId, sessionId);
        Assert.That(sessions.TryRunWithBinding(after, () => 42, out _), Is.False);
    }

    [Test]
    public async Task TrustedTransferAdmissionIsSingleUseEvenAfterExplicitLogoutAndNormalRelogin()
    {
        var database = await OpenAsync();
        var created = await database.Accounts.CreateAsync("alice", "correct horse battery staple", "default", "Server");
        string accountId = created.After!.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var lifecycle = new FakeLifecycle();
        using var permissions = new PlayerSessionAuthorizationService(lifecycle);
        using var sessions = new PlayerAccountSessionService(database.Accounts, database.Authorization, permissions, new LocalizationService("en-US"), lifecycle);
        var playerId = new PlayerId(4);
        var sessionId = lifecycle.Open(playerId);
        var player = new PlayerSnapshot(playerId, "alice", false, 100, 100, 20, 20, new(0, 0), false, 0) { SessionId = sessionId };
        var transferred = await sessions.EstablishTrustedTransferBindingAsync(player, accountId);
        Assert.That(transferred, Is.Not.Null);
        Assert.That(sessions.TryGetBinding(player, out var current), Is.True);
        Assert.That(current, Is.EqualTo(transferred!.Value));
        Assert.That(sessions.Logout(playerId, sessionId).Succeeded, Is.True);
        Assert.That(await sessions.EstablishTrustedTransferBindingAsync(player, accountId), Is.Null);
        Assert.That(sessions.TryGetAccount(playerId, out _), Is.False);

        Assert.That((await sessions.LoginAsync(playerId, "alice", "correct horse battery staple", expectedSession: sessionId)).Succeeded, Is.True);
        Assert.That(sessions.TryGetBinding(player, out var manual), Is.True);
        Assert.That(manual.AuthenticationId, Is.Not.EqualTo(transferred.Value.AuthenticationId));
        Assert.That(await sessions.EstablishTrustedTransferBindingAsync(player, accountId), Is.Null);
        Assert.That(sessions.TryGetBinding(player, out current), Is.True);
        Assert.That(current, Is.EqualTo(manual));

        lifecycle.Close(playerId, sessionId);
        var replacement = player with { SessionId = lifecycle.Open(playerId) };
        Assert.That(await sessions.EstablishTrustedTransferBindingAsync(replacement, accountId), Is.Not.Null);
    }

    private ValueTask<SqliteVegaDatabase> OpenAsync() => SqliteVegaDatabase.OpenAsync(
        new SqliteVegaDatabaseOptions(Path.Combine(testDirectory, "vega.db")));

    private sealed class FakeLifecycle : IPlayerSessionLifecycle
    {
        public event Action<PlayerSessionOpened>? Opened;
        public event Action<PlayerSessionClosed>? Closed;

        public void Close(PlayerId playerId, PlayerSessionId? sessionId = null) => Closed?.Invoke(new PlayerSessionClosed(
            sessionId ?? PlayerSessionId.New(),
            playerId,
            "Player",
            DateTimeOffset.UtcNow));

        public PlayerSessionId Open(PlayerId playerId)
        {
            PlayerSessionId session = PlayerSessionId.New();
            Opened?.Invoke(new PlayerSessionOpened(session, playerId, "Player", DateTimeOffset.UtcNow));
            return session;
        }
    }
}
