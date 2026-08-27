using NUnit.Framework;
using Vega.Contracts.Accounts;
using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Commands;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Core.Bans;
using Vega.Core.Commands.Administration;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class BanCommandTests
{
    [Test]
    public async Task PersistsAccountUuidAndIpAndDisconnectsPlayer()
    {
        PlayerSnapshot target = Player(4, "Trouble") with
        {
            RemoteEndpoint = "198.51.100.9:7777",
            Connection = new ConnectionMetadataSnapshot(
                new ConnectionId(4),
                "203.0.113.7:7777",
                "203.0.113.7:7777",
                ConnectionForwardingProvenance.Direct,
                ClientPlatformInfo.Unknown,
                "client-uuid-4",
                ConnectionLoginPhase.FullyConnected)
        };
        var bans = new FakeBans();
        var operations = new FakeOperations();
        var sessions = new FakeAccountSessions(
            target.Id,
            new AccountSnapshot(new AccountId(12), "TroubleAccount", "default", DateTimeOffset.UtcNow));
        var command = new BanCommand(
            new FakeDirectory([target]),
            operations,
            new GlobalBanService(bans),
            accountSessions: sessions);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(["tsi:4", "griefing", "spawn"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(bans.Created.Select(static ban => ban.Subject.Kind),
                Is.EquivalentTo(new[]
                {
                    GlobalBanSubjectKind.Account,
                    GlobalBanSubjectKind.Uuid,
                    GlobalBanSubjectKind.IpAddress
                }));
            Assert.That(bans.Created.Single(ban => ban.Subject.Kind == GlobalBanSubjectKind.Account).Subject.Value,
                Is.EqualTo("TroubleAccount"));
            Assert.That(bans.Created.Single(ban => ban.Subject.Kind == GlobalBanSubjectKind.Uuid).Subject.Value,
                Is.EqualTo("client-uuid-4"));
            Assert.That(bans.Created.Single(ban => ban.Subject.Kind == GlobalBanSubjectKind.IpAddress).Subject.Value,
                Is.EqualTo("203.0.113.7"));
            Assert.That(bans.Created.Any(ban => ban.Subject.Kind == GlobalBanSubjectKind.PlayerName), Is.False);
            Assert.That(operations.KickedPlayer, Is.EqualTo(target.Id));
            Assert.That(operations.KickReason, Is.EqualTo("Banned: griefing spawn"));
        });
    }

    [Test]
    public async Task FallsBackToPlayerNameWhenNoStableIdentityIsAvailable()
    {
        PlayerSnapshot target = Player(4, "Trouble");
        var bans = new FakeBans();
        var command = new BanCommand(
            new FakeDirectory([target]),
            new FakeOperations(),
            new GlobalBanService(bans),
            accountSessions: new FakeAccountSessions(target.Id, null));

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(["Trouble"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(bans.Created, Has.Count.EqualTo(1));
            Assert.That(bans.Created[0].Subject,
                Is.EqualTo(new GlobalBanSubject(GlobalBanSubjectKind.PlayerName, "Trouble")));
        });
    }

    [Test]
    public async Task OfflineAccountBanUsesLatestUuidAndIpEvidence()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        var account = new AccountSnapshot(new AccountId(12), "TroubleAccount", "default", now);
        var bans = new FakeBans();
        var command = new BanCommand(
            new FakeDirectory([]),
            new FakeOperations(),
            new GlobalBanService(bans),
            accounts: new FakeAccounts(account),
            accountIdentities: new FakeIdentityEvidence(
            [
                new(account.Id, account.Name, AccountIdentityEvidenceKind.ClientUuid,
                    "old-device", now.AddDays(-3), now.AddDays(-3), 1),
                new(account.Id, account.Name, AccountIdentityEvidenceKind.ClientUuid,
                    "current-device", now.AddDays(-2), now.AddDays(-1), 2),
                new(account.Id, account.Name, AccountIdentityEvidenceKind.IpAddress,
                    "192.0.2.44", now.AddDays(-2), now.AddDays(-1), 2)
            ]));

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(["TroubleAccount", "offline", "abuse"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(bans.Created.Select(static ban => ban.Subject), Is.EquivalentTo(new[]
            {
                new GlobalBanSubject(GlobalBanSubjectKind.Account, "TroubleAccount"),
                new GlobalBanSubject(GlobalBanSubjectKind.Uuid, "current-device"),
                new GlobalBanSubject(GlobalBanSubjectKind.IpAddress, "192.0.2.44")
            }));
            Assert.That(bans.Created.All(static ban => ban.Reason == "offline abuse"), Is.True);
        });
    }

    [Test]
    public async Task RejectsAmbiguousPrefixWithoutPersistingAnything()
    {
        var bans = new FakeBans();
        var command = new BanCommand(
            new FakeDirectory([Player(1, "Alice"), Player(2, "Alina")]),
            new FakeOperations(),
            new GlobalBanService(bans));

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(["Ali"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Failed));
            Assert.That(result.ErrorCode, Is.EqualTo("command.ban.multiple-matches"));
            Assert.That(bans.Created, Is.Empty);
        });
    }

    [Test]
    public async Task CreatesListsAndRevokesTypedExpiringBan()
    {
        var replica = new FakeBans();
        var command = new BanCommand(
            new FakeDirectory([]),
            new FakeOperations(),
            new GlobalBanService(replica));

        CommandExecutionResult created = await command.ExecuteAsync(
            Invocation(["add", "cidr", "203.0.113.77/24", "7d", "proxy", "range"]),
            CancellationToken.None);
        GlobalBanCreated stored = replica.Created.Single();
        CommandExecutionResult listed = await command.ExecuteAsync(
            Invocation(["list"]),
            CancellationToken.None);
        CommandExecutionResult revoked = await command.ExecuteAsync(
            Invocation("unban", [stored.BanId.Value.ToString("D")]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(created.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(stored.Subject, Is.EqualTo(
                new GlobalBanSubject(GlobalBanSubjectKind.Network, "203.0.113.0/24")));
            Assert.That(stored.ExpiresAt, Is.Not.Null);
            Assert.That(stored.Reason, Is.EqualTo("proxy range"));
            Assert.That(listed.Messages, Has.Count.EqualTo(1));
            Assert.That(revoked.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(replica.Revoked.Single().Revision, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task RedactsSensitiveBanSubjectsWithoutDedicatedPermission()
    {
        var replica = new FakeBans();
        var service = new GlobalBanService(replica);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await service.CreateAsync(new GlobalBanCreated(
            GlobalBanId.New(),
            new GlobalBanSubject(GlobalBanSubjectKind.IpAddress, "203.0.113.8"),
            "test",
            "system",
            now,
            null,
            1));
        var command = new BanCommand(new FakeDirectory([]), new FakeOperations(), service);

        CommandExecutionResult result = await command.ExecuteAsync(
            new CommandInvocation(
                "ban",
                ["list"],
                new FakePrincipal(["vega.admin.ban"]),
                new FakeOutput()),
            CancellationToken.None);

        Assert.That(result.Messages!.Single().Arguments[1], Is.EqualTo("IpAddress:<redacted>"));
    }

    private static CommandInvocation Invocation(IReadOnlyList<string> arguments) =>
        Invocation("ban", arguments);

    private static CommandInvocation Invocation(string name, IReadOnlyList<string> arguments) =>
        new(name, arguments, new FakePrincipal(), new FakeOutput());

    private static PlayerSnapshot Player(int slot, string name) =>
        new(
            new PlayerId(slot),
            name,
            false,
            100,
            100,
            20,
            20,
            new PlayerPosition(0, 0),
            false,
            0);

    private sealed class FakeBans : IGlobalBanReplica
    {
        public List<GlobalBanCreated> Created { get; } = [];
        public List<GlobalBanRevoked> Revoked { get; } = [];

        public ValueTask<bool> ApplyCreatedAsync(GlobalBanCreated created, CancellationToken cancellationToken = default)
        {
            Created.Add(created);
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> ApplyRevokedAsync(GlobalBanRevoked revoked, CancellationToken cancellationToken = default)
        {
            Revoked.Add(revoked);
            return ValueTask.FromResult(true);
        }

        public IReadOnlyList<GlobalBanCreated> SnapshotActive(DateTimeOffset now) => Created
            .Where(created => created.ExpiresAt is null || created.ExpiresAt > now)
            .Where(created => Revoked.All(revoked => revoked.BanId != created.BanId))
            .ToArray();
    }

    private sealed class FakeDirectory(IReadOnlyList<PlayerSnapshot> players) : IPlayerDirectory
    {
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => players;

        public bool TryGet(PlayerId id, out PlayerSnapshot? player)
        {
            player = players.FirstOrDefault(candidate => candidate.Id == id);
            return player is not null;
        }

        public bool TryFindExact(string name, out PlayerSnapshot? player)
        {
            player = players.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            return player is not null;
        }
    }

    private sealed class FakeOperations : IPlayerOperations
    {
        public PlayerId? KickedPlayer { get; private set; }
        public string? KickReason { get; private set; }

        public ValueTask<PlayerOperationResult> KickAsync(
            PlayerId id,
            string reason,
            CancellationToken cancellationToken = default)
        {
            KickedPlayer = id;
            KickReason = reason;
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }

        public ValueTask<PlayerOperationResult> TeleportAsync(
            PlayerId id,
            PlayerPosition destination,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());
    }

    private sealed class FakeAccountSessions(PlayerId playerId, AccountSnapshot? account)
        : IPlayerAccountSessionService
    {
        public bool TryGetAccount(PlayerId candidate, out AccountSnapshot? snapshot)
        {
            snapshot = candidate == playerId ? account : null;
            return snapshot is not null;
        }

        public ValueTask<bool> SetCultureAsync(
            PlayerId candidate,
            string? culture,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);
    }

    private sealed class FakeAccounts(AccountSnapshot account) : IAccountService
    {
        public IReadOnlyList<AccountSnapshot> SnapshotAccounts() => [account];

        public ValueTask<AccountSnapshot?> FindByNameAsync(
            string name,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AccountSnapshot?>(
                string.Equals(name, account.Name, StringComparison.OrdinalIgnoreCase) ? account : null);

        public ValueTask<AccountMutationResult> CreateAsync(string name, string password, string groupName, string actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<AccountMutationResult> DeleteAsync(string name, long expectedRevision, string actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<AccountMutationResult> SetGroupAsync(string name, string groupName, long expectedRevision,
            string actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<AccountMutationResult> SetCultureAsync(string name, string? culture, long expectedRevision,
            string actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<AccountMutationResult> ChangePasswordAsync(string name, string password, long expectedRevision,
            string actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<AccountMutationResult> RecordLoginAsync(string name, DateTimeOffset seenAt, long expectedRevision,
            string actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> VerifyPasswordAsync(string name, string password,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeIdentityEvidence(IReadOnlyList<AccountIdentityEvidenceSnapshot> evidence)
        : IAccountIdentityEvidenceService
    {
        public IReadOnlyList<AccountIdentityEvidenceSnapshot> SnapshotEvidence() => evidence;
        public IReadOnlyList<PossibleAccountLinkSnapshot> FindPossibleAccounts(AccountId accountId) => [];
        public ValueTask RecordAsync(AccountId accountId, string accountName, string? clientUuid, string? ipAddress,
            DateTimeOffset seenAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakePrincipal(IReadOnlyCollection<string>? permissions = null) : ICommandPrincipal
    {
        public string Name => "Moderator";
        public bool IsServer => true;
        public bool HasPermission(string permission) =>
            permissions is null || permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FakeOutput : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
