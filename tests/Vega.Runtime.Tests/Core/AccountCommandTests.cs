using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Commands;
using Vega.Core.Commands;
using Vega.Modules.Administration;
using Vega.Persistence.Sqlite;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class AccountCommandTests
{
    private string directory = null!;

    [SetUp]
    public void SetUp() => directory = Path.Combine(
        Path.GetTempPath(), "Vega.AccountCommand.Tests", Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Test]
    public async Task RunningDatabaseSeesRemoteGroupPermissionChanges()
    {
        var options = new SqliteVegaDatabaseOptions(Path.Combine(directory, "shared.db"));
        var primary = SqliteVegaDatabase.Create(options);
        var backend = SqliteVegaDatabase.Create(options);
        await primary.InitializeAsync();
        await backend.InitializeAsync();
        Assert.That(backend.Authorization.TryGetGroup("remote", out _), Is.False);
        var created = await primary.Authorization.CreateGroupAsync("remote", "default", "server");
        var granted = await primary.Authorization.SetPermissionAsync("remote", "audit.view", PermissionEffect.Grant, created.After!.Revision, "server");
        Assert.That(granted.Succeeded, Is.True);
        Assert.That(() => backend.Authorization.HasPermission("remote", "audit.view"), Is.True.After(3000, 50));
        var revoked = await primary.Authorization.SetPermissionAsync("remote", "audit.view", null, granted.After!.Revision, "server");
        Assert.That(revoked.Succeeded, Is.True);
        Assert.That(() => backend.Authorization.HasPermission("remote", "audit.view"), Is.False.After(3000, 50));
    }

    [Test]
    public async Task RunningDatabaseSeesRemoteLeaseAndRegionChanges()
    {
        var options = new SqliteVegaDatabaseOptions(Path.Combine(directory, "shared.db"));
        var primary = await SqliteVegaDatabase.OpenAsync(options);
        var backend = await SqliteVegaDatabase.OpenAsync(options);
        AccountId account = (await primary.Accounts.CreateAsync("Alice", "correct horse battery staple", "default", "server")).After!.Id;
        var now = DateTimeOffset.UtcNow;
        Assert.That(backend.RoleLeases.FindActive(account, now), Is.Null);
        Assert.That(backend.Regions.FindTopRegion(10, 10), Is.Null);
        var lease = await primary.RoleLeases.GrantAsync(account, "default", now, now.AddHours(1), "server");
        await primary.Regions.CreateAsync(Vega.Contracts.Worlds.WorldId.Primary, "Arena", 5, 5, 20, 20, 1, null, "server");
        Assert.That(() => backend.RoleLeases.FindActive(account, now), Is.Not.Null.After(3000, 50));
        Assert.That(() => backend.Regions.FindTopRegion(10, 10), Is.Not.Null.After(3000, 50));
        await primary.RoleLeases.RevokeAsync(lease.After!.Id, lease.After.Revision, "server");
        Assert.That(() => backend.RoleLeases.FindActive(account, now), Is.Null.After(3000, 50));
    }

    [Test]
    public async Task LegacyUserAddDelAndHelpReuseExistingAccountAuthority()
    {
        var database = await SqliteVegaDatabase.OpenAsync(new SqliteVegaDatabaseOptions(Path.Combine(directory, "vega.db")));
        var command = new AccountCommand(database.Accounts, database.Authorization);
        var dispatcher = new CommandDispatcher([command]);
        var create = new CommandInvocation("user", ["add", "Alice", "correct horse battery staple", "default"], ServerPrincipal.Instance, NullOutput.Instance);
        Assert.That((await dispatcher.DispatchAsync(create)).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(await database.Accounts.FindByNameAsync("Alice"), Is.Not.Null);
        Assert.That((await dispatcher.DispatchAsync(create with { Arguments = ["help"] })).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await dispatcher.DispatchAsync(create with { Arguments = ["del", "Alice"], Principal = new InspectorPrincipal() })).Status,
            Is.EqualTo(CommandExecutionStatus.Forbidden));
        Assert.That((await dispatcher.DispatchAsync(create with { Arguments = ["del", "Alice"] })).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(await database.Accounts.FindByNameAsync("Alice"), Is.Null);
    }

    [Test]
    public async Task TemporaryGroupCommandsGrantListAndRevokePersistentLeaseFromModule()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-23T12:00:00Z");
        var time = new FixedTimeProvider(now);
        SqliteVegaDatabase database = await SqliteVegaDatabase.OpenAsync(
            new SqliteVegaDatabaseOptions(Path.Combine(directory, "vega.db")));
        await database.Authorization.CreateGroupAsync("moderator", "default", "Server");
        AccountMutationResult account = await database.Accounts.CreateAsync(
            "Alice", "correct horse battery staple", "default", "Server");
        var command = new AccountCommand(
            database.Accounts, database.Authorization, database.RoleLeases, time);

        CommandExecutionResult granted = await ExecuteAsync(command, "temp", "Alice", "moderator", "90m");
        RoleLeaseSnapshot? lease = database.RoleLeases.FindActive(account.After!.Id, now);
        CommandExecutionResult listed = await ExecuteAsync(command, "temp-list");
        CommandExecutionResult revoked = await ExecuteAsync(command, "temp-clear", "Alice");

        Assert.Multiple(() =>
        {
            Assert.That(granted.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(lease!.GroupName, Is.EqualTo("moderator"));
            Assert.That(lease.ExpiresAt, Is.EqualTo(now.AddMinutes(90)));
            Assert.That(listed.Messages, Has.Count.EqualTo(1));
            Assert.That(revoked.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(database.RoleLeases.FindActive(account.After.Id, now), Is.Null);
        });
    }

    [Test]
    public async Task CompatibilityCommandsReuseAccountInspectionAndRoleLeaseAuthority()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-23T12:00:00Z");
        SqliteVegaDatabase database = await SqliteVegaDatabase.OpenAsync(new SqliteVegaDatabaseOptions(Path.Combine(directory, "vega.db")));
        await database.Authorization.CreateGroupAsync("moderator", "default", "Server");
        var created = await database.Accounts.CreateAsync("Alice", "correct horse battery staple", "default", "Server");
        Assert.That(created.After!.RegisteredUtc, Is.Not.Null);
        var recorded = await database.Accounts.RecordLoginWithConnectionAsync("Alice", now, created.After.Revision, "Alice", "192.0.2.12", "0123456789abcdefghij");
        Assert.That(recorded.After?.LastKnownIp, Is.EqualTo("192.0.2.12"));
        Assert.That((await database.Accounts.FindByNameAsync("Alice"))?.LastKnownUuid, Is.EqualTo("0123456789abcdefghij"));
        var account = new AccountCommand(database.Accounts, database.Authorization, database.RoleLeases, new FixedTimeProvider(now));
        var dispatcher = new CommandDispatcher([new AccountInfoCommand(account), new TempGroupCommand(account)]);
        var info = new CommandInvocation("ai", ["Alice"], new InspectorPrincipal(), NullOutput.Instance);
        Assert.That((await dispatcher.DispatchAsync(info)).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await dispatcher.DispatchAsync(info with { Name = "tempgroup", Arguments = ["Alice", "moderator", "1h"] })).Status,
            Is.EqualTo(CommandExecutionStatus.Forbidden));
        var grant = info with { Name = "tempgroup", Arguments = ["Alice", "moderator", "1h"], Principal = ServerPrincipal.Instance };
        Assert.That((await dispatcher.DispatchAsync(grant)).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(database.RoleLeases.FindActive(created.After!.Id, now)?.GroupName, Is.EqualTo("moderator"));
        Assert.That((await dispatcher.DispatchAsync(grant with { Arguments = ["Alice", "clear"] })).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(database.RoleLeases.FindActive(created.After.Id, now), Is.Null);
        string id = created.After.Id.Value.ToString();
        Assert.That((await dispatcher.DispatchAsync(info with { Name = "ai-id", Arguments = [id] })).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await dispatcher.DispatchAsync(grant with { Name = "tg", Arguments = ["add", id, "moderator"] })).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(database.RoleLeases.FindActive(created.After.Id, now)?.ExpiresAt, Is.EqualTo(DateTimeOffset.MaxValue));
        Assert.That((await dispatcher.DispatchAsync(grant with { Name = "tg", Arguments = ["info", id] })).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await dispatcher.DispatchAsync(grant with { Name = "tg", Arguments = ["del", id] })).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(database.RoleLeases.FindActive(created.After.Id, now), Is.Null);
        Assert.That((await dispatcher.DispatchAsync(grant with { Name = "tg", Arguments = ["add", id, "moderator", "10h-30m+2m"] })).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(database.RoleLeases.FindActive(created.After.Id, now)?.ExpiresAt, Is.EqualTo(now.AddMinutes(572)));
    }

    [Test]
    public async Task GroupOptionsRestrictPermanentAndTemporaryAssignmentsEvenWithDelegationPermission()
    {
        var database = await SqliteVegaDatabase.OpenAsync(new SqliteVegaDatabaseOptions(Path.Combine(directory, "vega.db")));
        await database.Authorization.CreateGroupAsync("moderator", "default", "Server");
        await database.Authorization.CreateGroupAsync("vip", "default", "Server");
        await database.Accounts.CreateAsync("Op", "correct horse battery staple", "moderator", "Server");
        await database.Accounts.CreateAsync("Alice", "correct horse battery staple", "default", "Server");
        var command = new AccountCommand(database.Accounts, database.Authorization, database.RoleLeases,
            groupAssignments: new Dictionary<string, HashSet<string>> { ["moderator"] = ["default"] });
        var invocation = new CommandInvocation("account", ["group", "Alice", "vip"], new DelegatingPrincipal(), NullOutput.Instance);
        Assert.That((await command.ExecuteAsync(invocation, default)).ErrorCode, Is.EqualTo("command.account.delegation-denied"));
        Assert.That((await command.ExecuteAsync(invocation with { Arguments = ["temp", "Alice", "vip", "1h"] }, default)).ErrorCode, Is.EqualTo("command.account.delegation-denied"));
        Assert.That((await command.ExecuteAsync(invocation with { Arguments = ["temp", "Alice", "default", "1h"] }, default)).Status, Is.EqualTo(CommandExecutionStatus.Success));
    }

    private sealed class DelegatingPrincipal : IPlayerCommandPrincipal
    {
        public string Name => "Op";
        public bool IsServer => false;
        public bool HasPermission(string permission) => true;
        public string CultureSubjectId => "Op";
        public Vega.Contracts.Players.PlayerId PlayerId => new(0);
        public string PlayerName => "Op";
        public string? AccountName => "Op";
    }

    private static ValueTask<CommandExecutionResult> ExecuteAsync(
        AccountCommand command,
        params string[] arguments) => command.ExecuteAsync(
        new CommandInvocation("account", arguments, ServerPrincipal.Instance, NullOutput.Instance),
        CancellationToken.None);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ServerPrincipal : ICommandPrincipal
    {
        public static ServerPrincipal Instance { get; } = new();
        public string Name => "Server";
        public bool IsServer => true;
        public bool HasPermission(string permission) => true;
    }

    private sealed class InspectorPrincipal : ICommandPrincipal
    {
        public string Name => "Inspector";
        public bool IsServer => false;
        public bool HasPermission(string permission) => permission == "vega.accounts.inspect";
    }

    private sealed class NullOutput : ICommandOutput
    {
        public static NullOutput Instance { get; } = new();
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
