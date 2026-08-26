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
            Assert.That(mute.Subjects, Has.Count.EqualTo(1));
            Assert.That(mute.Subjects[0], Is.EqualTo(new MuteSubject(MuteIdentityKind.Account, "Alice")));
            Assert.That(mute.Reason, Is.EqualTo("chat abuse"));
            Assert.That(mute.ExpiresAt, Is.EqualTo(now.AddMinutes(90)));
            Assert.That(listed.Messages, Has.Count.EqualTo(1));
            Assert.That(revoked.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(database.Mutes.SnapshotActive(now), Is.Empty);
        });
    }

    [Test]
    public async Task StableSubjectsMatchOneLogicalMuteByAccountUuidOrIp()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-23T12:00:00Z");
        SqliteVegaDatabase database = await SqliteVegaDatabase.OpenAsync(
            new SqliteVegaDatabaseOptions(Path.Combine(directory, "vega.db")));
        var mute = new MuteSnapshot(
            MuteId.New(),
            MuteSubjectKind.Account,
            "Alice",
            "chat abuse",
            "Server",
            now,
            now.AddHours(1),
            1)
        {
            Subjects =
            [
                new(MuteIdentityKind.Account, "Alice"),
                new(MuteIdentityKind.PlayerName, "AliceCharacter"),
                new(MuteIdentityKind.ClientUuid, "terraria-opaque-UUID-token"),
                new(MuteIdentityKind.IpAddress, "::ffff:192.0.2.44")
            ]
        };

        MuteMutationResult created = await database.Mutes.CreateAsync(mute);
        MuteSnapshot? byAccount = database.Mutes.FindActive(new MuteIdentity("Other", "alice"), now);
        MuteSnapshot? byUuid = database.Mutes.FindActive(
            new MuteIdentity("Renamed", ClientUuid: "terraria-opaque-UUID-token"), now);
        MuteSnapshot? byIp = database.Mutes.FindActive(
            new MuteIdentity("Renamed", IpAddress: "192.0.2.44"), now);
        MuteSnapshot persisted = database.Mutes.SnapshotActive(now).Single();

        Assert.Multiple(() =>
        {
            Assert.That(created.Succeeded, Is.True);
            Assert.That(byAccount?.Id, Is.EqualTo(mute.Id));
            Assert.That(byUuid?.Id, Is.EqualTo(mute.Id));
            Assert.That(byIp?.Id, Is.EqualTo(mute.Id));
            Assert.That(persisted.Subjects, Has.Count.EqualTo(4));
            Assert.That(persisted.Subjects.Single(subject => subject.Kind == MuteIdentityKind.IpAddress).Value,
                Is.EqualTo("192.0.2.44"));
        });
    }

    [Test]
    public async Task OfflineAccountMuteUsesLatestIdentityEvidenceAndCanBeRevokedByTarget()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-23T12:00:00Z");
        SqliteVegaDatabase database = await SqliteVegaDatabase.OpenAsync(
            new SqliteVegaDatabaseOptions(Path.Combine(directory, "vega.db")));
        AccountSnapshot account = (await database.Accounts.CreateAsync(
            "Alice", "correct horse battery staple", "default", "Server")).After!;
        await database.AccountIdentities.RecordAsync(
            account.Id, account.Name, "old-device", "192.0.2.10", now.AddDays(-2));
        await database.AccountIdentities.RecordAsync(
            account.Id, account.Name, "current-device", "192.0.2.44", now.AddDays(-1));
        var command = new MuteCommand(
            EmptyPlayers.Instance,
            EmptyAccountSessions.Instance,
            database.Mutes,
            new FixedTimeProvider(now),
            database.Accounts,
            database.AccountIdentities);

        CommandExecutionResult created = await ExecuteAsync(
            command, "mute", "add", "Alice", "90m", "chat", "abuse");
        MuteSnapshot mute = database.Mutes.SnapshotActive(now).Single();
        CommandExecutionResult revoked = await ExecuteAsync(command, "unmute", "Alice");

        Assert.Multiple(() =>
        {
            Assert.That(created.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(mute.SubjectKind, Is.EqualTo(MuteSubjectKind.Account));
            Assert.That(mute.SubjectValue, Is.EqualTo(account.Name));
            Assert.That(mute.Subjects, Is.EquivalentTo(new[]
            {
                new MuteSubject(MuteIdentityKind.Account, account.Name),
                new MuteSubject(MuteIdentityKind.ClientUuid, "current-device"),
                new MuteSubject(MuteIdentityKind.IpAddress, "192.0.2.44")
            }));
            Assert.That(revoked.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(database.Mutes.SnapshotActive(now), Is.Empty);
        });
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
