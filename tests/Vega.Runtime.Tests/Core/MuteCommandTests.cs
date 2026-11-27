using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Contracts.Accounts;
using Vega.Contracts.Commands;
using Vega.Contracts.Moderation;
using Vega.Contracts.Players;
using Vega.Core.Commands.Administration;
using Vega.Persistence.Sqlite;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class MuteCommandTests
{
    private string directory = null!;

    [SetUp]
    public void SetUp() => directory = Path.Combine(
        Path.GetTempPath(), "Vega.MuteCommand.Tests", Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Test]
    public async Task TypedMuteCommandCreatesListsAndRevokesPersistentMute()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-23T12:00:00Z");
        SqliteVegaDatabase database = await SqliteVegaDatabase.OpenAsync(
            new SqliteVegaDatabaseOptions(Path.Combine(directory, "vega.db")));
        var command = new MuteCommand(
            EmptyPlayers.Instance,
            EmptyAccountSessions.Instance,
            database.Mutes,
            new FixedTimeProvider(now));

        CommandExecutionResult created = await ExecuteAsync(
            command, "mute", "add", "account", "Alice", "90m", "chat", "abuse");
        MuteSnapshot mute = database.Mutes.SnapshotActive(now).Single();
        CommandExecutionResult listed = await ExecuteAsync(command, "mute", "list");
        CommandExecutionResult revoked = await ExecuteAsync(command, "unmute", mute.Id.ToString());

        Assert.Multiple(() =>
        {
            Assert.That(created.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(mute.SubjectKind, Is.EqualTo(MuteSubjectKind.Account));
            Assert.That(mute.SubjectValue, Is.EqualTo("Alice"));
            Assert.That(mute.Reason, Is.EqualTo("chat abuse"));
            Assert.That(mute.ExpiresAt, Is.EqualTo(now.AddMinutes(90)));
            Assert.That(listed.Messages, Has.Count.EqualTo(1));
            Assert.That(revoked.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(database.Mutes.SnapshotActive(now), Is.Empty);
        });
    }

    [Test]
    public async Task ExtendedBansOfflineAccountMuteSurvivesRestartAndMatchesStoredIdentities()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var options = new SqliteVegaDatabaseOptions(Path.Combine(directory, "vega.db"));
        var database = await SqliteVegaDatabase.OpenAsync(options);
        var created = await database.Accounts.CreateAsync("Alice", "fixture-password", "default", "Server");
        await database.Accounts.RecordLoginWithConnectionAsync("Alice", now, created.After!.Revision, "Alice", "192.0.2.15", "fixture-uuid");
        var command = new MuteCommand(EmptyPlayers.Instance, EmptyAccountSessions.Instance, database.Mutes, new FixedTimeProvider(now), database.Accounts);
        Assert.That((await ExecuteAsync(command, "mute", "add", "Alice", "0", "fixture reason")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        var reopened = await SqliteVegaDatabase.OpenAsync(options);
        Assert.That(reopened.Mutes.FindActive("other", null, "192.0.2.15", null, now)?.ExpiresAt, Is.EqualTo(DateTimeOffset.MaxValue));
        Assert.That(reopened.Mutes.FindActive("other", null, null, "fixture-uuid", now), Is.Not.Null);
        command = new MuteCommand(EmptyPlayers.Instance, EmptyAccountSessions.Instance, reopened.Mutes, new FixedTimeProvider(now), reopened.Accounts);
        Assert.That((await ExecuteAsync(command, "mute", "del", "Alice")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(reopened.Mutes.FindActive("other", null, "192.0.2.15", "fixture-uuid", now), Is.Null);
    }

    [Test]
    public async Task ExtendedBansDelegatedMuteCannotTargetAHigherPermissionTier()
    {
        var database = await SqliteVegaDatabase.OpenAsync(new SqliteVegaDatabaseOptions(Path.Combine(directory, "vega.db")));
        var group = await database.Authorization.CreateGroupAsync("protected", "default", "Server");
        await database.Authorization.SetPermissionAsync("protected", "extendedbans.mute.10", Vega.Contracts.Authorization.PermissionEffect.Grant, group.After!.Revision, "Server");
        await database.Accounts.CreateAsync("Alice", "fixture-password", "protected", "Server");
        var command = new MuteCommand(EmptyPlayers.Instance, EmptyAccountSessions.Instance, database.Mutes, accounts: database.Accounts, groups: database.Authorization);
        var invocation = new CommandInvocation("mute", ["add", "Alice", "1h", "test"], new LimitedMutePrincipal(), NullOutput.Instance);
        Assert.That((await command.ExecuteAsync(invocation, default)).ErrorCode, Is.EqualTo("command.mute.authority"));
        Assert.That(database.Mutes.SnapshotActive(DateTimeOffset.UtcNow), Is.Empty);
    }

    private sealed class LimitedMutePrincipal : ICommandPrincipal
    {
        public string Name => "Moderator";
        public bool IsServer => false;
        public bool HasPermission(string permission) => permission == "extendedbans.mute";
    }

    private static ValueTask<CommandExecutionResult> ExecuteAsync(
        MuteCommand command,
        string name,
        params string[] arguments) => command.ExecuteAsync(
        new CommandInvocation(name, arguments, ServerPrincipal.Instance, NullOutput.Instance),
        CancellationToken.None);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class EmptyPlayers : IPlayerDirectory
    {
        public static EmptyPlayers Instance { get; } = new();
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => [];
        public bool TryGet(PlayerId id, out PlayerSnapshot? player) { player = null; return false; }
        public bool TryFindExact(string name, out PlayerSnapshot? player) { player = null; return false; }
    }

    private sealed class EmptyAccountSessions : IPlayerAccountSessionService
    {
        public static EmptyAccountSessions Instance { get; } = new();
        public bool TryGetAccount(PlayerId playerId, out AccountSnapshot? account) { account = null; return false; }
        public ValueTask<bool> SetCultureAsync(PlayerId playerId, string? culture, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    }

    private sealed class ServerPrincipal : ICommandPrincipal
    {
        public static ServerPrincipal Instance { get; } = new();
        public string Name => "Server";
        public bool IsServer => true;
        public bool HasPermission(string permission) => true;
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
