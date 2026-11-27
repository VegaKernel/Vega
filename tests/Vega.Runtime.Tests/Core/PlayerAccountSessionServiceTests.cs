using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Contracts.Authorization;
using Vega.Contracts.Accounts;
using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Localization;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Authorization;
using Vega.Core.Bans;
using Vega.Core.Commands;
using Vega.Core.Localization;
using Vega.Core.Modules;
using Vega.Persistence.Sqlite;
using Vega.PluginSdk;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class PlayerAccountSessionServiceTests
{
    private string testDirectory = null!;

    [Test]
    public async Task IdentitySdkObservesCommittedChangesAndAllowsOnlyConfiguredGroupAssignments()
    {
        var db = await OpenAsync();
        var vip = await db.Authorization.CreateGroupAsync("vip", "default", "Server");
        await db.Authorization.SetPermissionAsync("vip", "fixture.vip", PermissionEffect.Grant, vip.After!.Revision, "Server");
        var created = await db.Accounts.CreateAsync("alice", "fixture password", "default", "Server");
        var lifecycle = new FakeLifecycle();
        using var permissions = new PlayerSessionAuthorizationService(lifecycle);
        using var sessions = new PlayerAccountSessionService(db.Accounts, db.Authorization, permissions, new LocalizationService("en-US"), lifecycle, roleLeases: db.RoleLeases);
        var playerId = new PlayerId(4);
        var sessionId = lifecycle.Open(playerId);
        Assert.That((await sessions.LoginAsync(playerId, "alice", "fixture password", expectedSession: sessionId)).Succeeded, Is.True);
        var dispatcher = new QueuedIdentityDispatcher();
        var scope = new HotModuleRegistrationScope(new CommandDispatcher([]), new IdentityLogger(), "Plugin.Donations", Path.GetTempPath(), gameThread: dispatcher,
            accounts: db.Accounts, groups: db.Authorization, roleLeases: db.RoleLeases, identityAssignmentGroups: ["default", "vip"]);
        var identity = scope.Identity;
        var accountEvents = new List<PluginAccountChanged>();
        var groupEvents = new List<PluginGroupChanged>();
        var leases = new List<RoleLeaseChange>();
        identity.OnAccountChanged(accountEvents.Add);
        identity.OnGroupChanged(groupEvents.Add);
        identity.OnTemporaryGroupChanged(leases.Add);
        var account = created.After!.Id;
        Assert.That((await identity.FindAccountByIdAsync(account))?.Name, Is.EqualTo("alice"));
        Assert.That((await identity.AssignGroupAsync(account, "administrator")).Code, Is.EqualTo("plugin.identity.group-denied"));
        Assert.That((await identity.AssignGroupAsync(account, "vip")).Succeeded, Is.True);
        Assert.That(permissions.HasPermission(playerId, "fixture.vip"), Is.True, "online permission changes must not wait for re-login");
        Assert.That(accountEvents, Is.Empty, "SDK observers must not run inside the database writer");
        Assert.That((await identity.GrantTemporaryGroupAsync(account, "default", DateTimeOffset.UtcNow.AddHours(1))).Succeeded, Is.True);
        Assert.That(permissions.HasPermission(playerId, "fixture.vip"), Is.False);
        Assert.That((await identity.RevokeOwnTemporaryGroupAsync(account)).Succeeded, Is.True);
        Assert.That(permissions.HasPermission(playerId, "fixture.vip"), Is.True);
        await db.RoleLeases.GrantAsync(account, "default", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), "Server");
        Assert.That((await identity.RevokeOwnTemporaryGroupAsync(account)).Code, Is.EqualTo("plugin.identity.group-denied"));
        Assert.That((await identity.GrantTemporaryGroupAsync(account, "vip", DateTimeOffset.UtcNow.AddHours(1))).Code, Is.EqualTo("plugin.identity.group-denied"));
        await db.Authorization.CreateGroupAsync("extra", "default", "Server");
        dispatcher.Drain();
        Assert.That(accountEvents.Single().After!.GroupName, Is.EqualTo("vip"));
        Assert.That(accountEvents.Single().Actor, Is.EqualTo("plugin:Plugin.Donations"));
        Assert.That(groupEvents.Single().After!.Name, Is.EqualTo("extra"));
        Assert.That(leases, Has.Count.EqualTo(3));
        await db.Authorization.CreateGroupAsync("later", "default", "Server");
        await scope.DisposeAsync();
        dispatcher.Drain();
        Assert.That(groupEvents, Has.Count.EqualTo(1), "retired subscriptions and queued callbacks must not execute");
    }

    private sealed class QueuedIdentityDispatcher : Vega.Contracts.Threading.IGameThreadDispatcher
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> pending = new();
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => false;
        public void AssertGameThread() => throw new NotSupportedException();
        public bool TryPost(string name, Action action) { pending.Enqueue(action); return true; }
        public bool TrySchedule(string name, Func<bool> step) => throw new NotSupportedException();
        public void Drain() { while (pending.TryDequeue(out var action)) action(); }
    }

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
    public async Task PluginIdentityQueriesUseMinimalAccountProjectionAndExactBindingPermissions()
    {
        var database = await OpenAsync();
        var parent = await database.Authorization.CreateGroupAsync("builders", "default", "Server");
        await database.Authorization.SetPermissionAsync("builders", "world.build", PermissionEffect.Grant, parent.After!.Revision, "Server");
        var child = await database.Authorization.CreateGroupAsync("members", "builders", "Server");
        await database.Authorization.SetPermissionAsync("members", "world.denied", PermissionEffect.Deny, child.After!.Revision, "Server");
        var created = await database.Accounts.CreateAsync("alice", "fixture password", "members", "Server");
        var lifecycle = new FakeLifecycle();
        using var permissions = new PlayerSessionAuthorizationService(lifecycle);
        using var sessions = new PlayerAccountSessionService(database.Accounts, database.Authorization, permissions,
            new LocalizationService("en-US"), lifecycle);
        var playerId = new PlayerId(4);
        var world = new WorldId("identity");
        var player = new PlayerSnapshot(playerId, "Alice", false, 100, 100, 20, 20, new(0, 0), false, 0)
        { WorldId = world, SessionId = lifecycle.Open(playerId) };
        var directory = new IdentityDirectory { Current = player };
        await using var scope = new HotModuleRegistrationScope(new CommandDispatcher([]), new IdentityLogger(), "Plugin.Identity",
            Path.GetTempPath(), playerDirectory: directory, activityWorldId: world, authentication: sessions,
            accounts: database.Accounts, groups: database.Authorization, playerPermissions: permissions);
        var identity = scope.Identity;
        Assert.That(identity.AccountsAvailable && identity.GroupsAvailable && identity.AuthenticatedPermissionsAvailable, Is.True);
        var projected = new PluginAccountIdentity(created.After!.Id, "alice", "members");
        Assert.That(await identity.FindAccountAsync("ALICE"), Is.EqualTo(projected));
        Assert.That(await identity.FindAccountAsync("nonexistent"), Is.Null);
        Assert.That(await identity.GroupHasPermissionAsync("members", "world.build"), Is.True);
        Assert.That(await identity.GroupHasPermissionAsync("members", "world.denied"), Is.False);
        var groups = await identity.SnapshotGroupsAsync();
        Assert.That(groups.Any(group => group.Name == "members" && group.DeniedPermissions.Contains("WORLD.DENIED")), Is.True);
        Assert.That((await identity.FindGroupAsync("builders"))!.Permissions.Contains("WORLD.BUILD"), Is.True);
        Assert.That(identity.TryGetAuthenticatedAccount(player, out _), Is.False);
        Assert.That((await sessions.LoginAsync(playerId, "alice", "fixture password", expectedSession: player.SessionId)).Succeeded, Is.True);
        Assert.That(identity.TryGetAuthenticatedAccount(player, out var current), Is.True);
        Assert.That(current, Is.EqualTo(projected));
        Assert.That(identity.TryHasAuthenticatedPermission(player, "world.build", out bool allowed) && allowed, Is.True);
        Assert.That(identity.TryHasAuthenticatedPermission(player, "world.denied", out bool denied), Is.True);
        Assert.That(denied, Is.False);
        Assert.That(identity.TryHasAuthenticatedPermission(player with { WorldId = new("foreign") }, "world.build", out _), Is.False);
        var next = player with { SessionId = lifecycle.Open(playerId) };
        directory.Current = next;
        Assert.That(identity.TryGetAuthenticatedAccount(player, out _), Is.False);
        Assert.That(identity.TryHasAuthenticatedPermission(player, "world.build", out _), Is.False);
        Assert.That(identity.TryGetAuthenticatedAccount(next, out _), Is.False);
        await scope.DisposeAsync();
        Assert.CatchAsync<OperationCanceledException>(async () => await identity.FindAccountAsync("alice"));
        Assert.That(identity.TryGetAuthenticatedAccount(next, out _), Is.False);
    }

    private sealed class IdentityDirectory : IPlayerDirectory
    {
        public PlayerSnapshot? Current { get; set; }
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => Current is { } value ? [value] : [];
        public bool TryGet(PlayerId id, out PlayerSnapshot? player) => (player = Current?.Id == id ? Current : null) is not null;
        public bool TryFindExact(string name, out PlayerSnapshot? player) => (player = Current?.Name == name ? Current : null) is not null;
    }

    private sealed class IdentityLogger : IOperationsLogger
    {
        public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null, string? correlationId = null) { }
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
    public async Task AuthenticationObservationSeparatesLogoutDisconnectAndReplacement()
    {
        var database = await OpenAsync();
        await database.Accounts.CreateAsync("alice", "fixture password", "default", "Server");
        var lifecycle = new FakeLifecycle();
        using var permissions = new PlayerSessionAuthorizationService(lifecycle);
        using var sessions = new PlayerAccountSessionService(database.Accounts, database.Authorization, permissions,
            new LocalizationService("en-US"), lifecycle);
        var player = new PlayerId(4);
        var established = new List<PlayerAccountBinding>();
        var ended = new List<PlayerAuthenticationEnded>();
        var explicitEnds = new List<PlayerAccountBinding>();
        sessions.BindingEstablished += established.Add;
        sessions.AuthenticationEnded += _ => throw new InvalidOperationException("fixture fault");
        sessions.AuthenticationEnded += ended.Add;
        sessions.BindingEnded += explicitEnds.Add;
        var first = lifecycle.Open(player);
        Assert.That((await sessions.LoginAsync(player, "alice", "fixture password", expectedSession: first)).Succeeded, Is.True);
        Assert.That(sessions.Logout(player, first).Succeeded, Is.True);
        Assert.That((await sessions.LoginAsync(player, "alice", "fixture password", expectedSession: first)).Succeeded, Is.True);
        var second = lifecycle.Open(player);
        lifecycle.Close(player, first); // delayed old close must not revoke a new session
        Assert.That((await sessions.LoginAsync(player, "alice", "fixture password", expectedSession: second)).Succeeded, Is.True);
        lifecycle.Close(player, first);
        Assert.That(ended, Has.Count.EqualTo(2));
        lifecycle.Close(player, second);
        lifecycle.Close(player, second);
        Assert.That(ended, Is.EqualTo(new[]
        {
            new PlayerAuthenticationEnded(established[0], PlayerAuthenticationEndReason.Logout),
            new PlayerAuthenticationEnded(established[1], PlayerAuthenticationEndReason.SessionReplaced),
            new PlayerAuthenticationEnded(established[2], PlayerAuthenticationEndReason.Disconnected)
        }));
        Assert.That(explicitEnds, Is.EqualTo(new[] { established[0] }));
        Assert.That(established.Select(binding => binding.AuthenticationId).Distinct().Count(), Is.EqualTo(3));
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
