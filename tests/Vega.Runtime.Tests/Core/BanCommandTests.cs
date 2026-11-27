using NUnit.Framework;
using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Commands;
using Vega.Contracts.Players;
using Vega.Contracts.Networking;
using Vega.Contracts.Worlds;
using Vega.Core.Bans;
using Vega.Core.Commands.Administration;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class BanCommandTests
{
    [Test]
    public async Task LegacyBanDetailsAndDelUseExistingRevisionedBanService()
    {
        var replica = new FakeBans();
        var command = new BanCommand(new FakeDirectory([]), new FakeOperations(), new GlobalBanService(replica));
        Assert.That((await command.ExecuteAsync(Invocation(["help"]), default)).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await command.ExecuteAsync(Invocation(["identifiers"]), default)).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await command.ExecuteAsync(Invocation(["examples"]), default)).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await command.ExecuteAsync(Invocation(["add", "name", "Example", "permanent"]), default)).Status, Is.EqualTo(CommandExecutionStatus.Success));
        string id = replica.Created.Single().BanId.Value.ToString("D");
        Assert.That((await command.ExecuteAsync(Invocation(["details", id]), default)).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await command.ExecuteAsync(Invocation(["del", id]), default)).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await command.ExecuteAsync(Invocation(["details", id]), default)).Status, Is.EqualTo(CommandExecutionStatus.Failed));
    }

    [Test]
    public async Task PersistsNameAndIpAndDisconnectsPlayer()
    {
        PlayerSnapshot target = Player(4, "Trouble") with { RemoteEndpoint = "203.0.113.7:7777" };
        var bans = new FakeBans();
        var operations = new FakeOperations();
        var command = new BanCommand(
            new FakeDirectory([target]),
            operations,
            new GlobalBanService(bans));

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(["tsi:4", "griefing", "spawn"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(bans.Created.Select(static ban => ban.Subject.Kind),
                Is.EquivalentTo(new[] { GlobalBanSubjectKind.PlayerName, GlobalBanSubjectKind.IpAddress }));
            Assert.That(bans.Created.Single(ban => ban.Subject.Kind == GlobalBanSubjectKind.PlayerName).Subject.Value,
                Is.EqualTo("Trouble"));
            Assert.That(bans.Created.Single(ban => ban.Subject.Kind == GlobalBanSubjectKind.IpAddress).Subject.Value,
                Is.EqualTo("203.0.113.7"));
            Assert.That(operations.KickedPlayer, Is.EqualTo(target.Id));
            Assert.That(operations.KickReason, Is.EqualTo("Banned: griefing spawn"));
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
    public async Task OnlineBanUsesEffectiveAddressAndOriginalWorldSession()
    {
        var world = new WorldId("arena");
        var target = Player(4, "Target") with
        {
            WorldId = world, SessionId = PlayerSessionId.New(), RemoteEndpoint = "127.0.0.1:1234",
            Connection = new(new ConnectionId(4), "127.0.0.1:1234", "192.0.2.25", ConnectionForwardingProvenance.TrustedForwarder,
                ClientPlatformInfo.Unknown, null, ConnectionLoginPhase.FullyConnected)
        };
        var other = target with { WorldId = WorldId.Primary, SessionId = PlayerSessionId.New() };
        var replica = new FakeBans();
        var operations = new FakeOperations();
        var command = new BanCommand(new FakeDirectory([other, target]), operations, new GlobalBanService(replica));
        var result = await command.ExecuteAsync(Invocation(["tsi:4"]) with { WorldId = world }, default);
        Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(replica.Created.Single(b => b.Subject.Kind == GlobalBanSubjectKind.IpAddress).Subject.Value, Is.EqualTo("192.0.2.25"));
        Assert.That(operations.KickedSnapshot, Is.EqualTo(target));
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
        public PlayerSnapshot? KickedSnapshot { get; private set; }

        public ValueTask<PlayerOperationResult> KickAsync(PlayerSnapshot player, string reason, CancellationToken cancellationToken = default)
        {
            KickedSnapshot = player;
            return KickAsync(player.Id, reason, cancellationToken);
        }

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
