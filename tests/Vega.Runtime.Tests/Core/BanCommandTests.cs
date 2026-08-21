using NUnit.Framework;
using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Commands;
using Vega.Contracts.Players;
using Vega.Core.Commands.Administration;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class BanCommandTests
{
    [Test]
    public async Task PersistsNameAndIpAndDisconnectsPlayer()
    {
        PlayerSnapshot target = Player(4, "Trouble") with { RemoteEndpoint = "203.0.113.7:7777" };
        var bans = new FakeBans();
        var operations = new FakeOperations();
        var command = new BanCommand(new FakeDirectory([target]), operations, bans);

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
            bans);

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

    private static CommandInvocation Invocation(IReadOnlyList<string> arguments) =>
        new("ban", arguments, new FakePrincipal(), new FakeOutput());

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

        public ValueTask<bool> ApplyCreatedAsync(GlobalBanCreated created, CancellationToken cancellationToken = default)
        {
            Created.Add(created);
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> ApplyRevokedAsync(GlobalBanRevoked revoked, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public IReadOnlyList<GlobalBanCreated> SnapshotActive(DateTimeOffset now) => Created;
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

    private sealed class FakePrincipal : ICommandPrincipal
    {
        public string Name => "Moderator";
        public bool IsServer => true;
        public bool HasPermission(string permission) => true;
    }

    private sealed class FakeOutput : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
