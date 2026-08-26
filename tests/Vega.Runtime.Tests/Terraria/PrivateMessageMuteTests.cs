using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Moderation;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Terraria.Commands;

namespace Vega.Runtime.Tests.Terraria;

[TestFixture]
public sealed class PrivateMessageMuteTests
{
    [Test]
    public async Task WhisperChecksStableUuidAndEffectiveIpIdentity()
    {
        PlayerSnapshot caller = Player(1, "Alice") with
        {
            SessionId = PlayerSessionId.New(),
            RemoteEndpoint = "198.51.100.8:7777",
            Connection = new ConnectionMetadataSnapshot(
                new ConnectionId(1),
                "198.51.100.8:7777",
                "203.0.113.44:7777",
                ConnectionForwardingProvenance.TrustedForwarder,
                ClientPlatformInfo.Unknown,
                "alice-device",
                ConnectionLoginPhase.FullyConnected)
        };
        PlayerSnapshot target = Player(2, "Bob") with { SessionId = PlayerSessionId.New() };
        var mutes = new CapturingMuteService();
        var lifecycle = new FakeLifecycle();
        using var state = new PrivateMessageSessionState(lifecycle);
        var command = new WhisperCommand(
            new FakeDirectory([caller, target]),
            new FakeMessenger(),
            mutes,
            state);

        CommandExecutionResult result = await command.ExecuteAsync(
            new CommandInvocation(
                "whisper",
                ["Bob", "hello"],
                new FakePlayerPrincipal(caller.Id, caller.Name, "AliceAccount"),
                new FakeOutput()),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Failed));
            Assert.That(result.ErrorCode, Is.EqualTo("command.whisper.muted"));
            Assert.That(mutes.Identity, Is.EqualTo(new MuteIdentity(
                "Alice", "AliceAccount", "alice-device", "203.0.113.44")));
        });
    }

    [TestCase("[2001:db8::44]:7777")]
    [TestCase("[::ffff:203.0.113.44]:7777")]
    public void MuteIdentityRejectsIpv6ConnectionEndpoints(string endpoint)
    {
        PlayerSnapshot player = Player(1, "Alice") with { RemoteEndpoint = endpoint };

        MuteIdentity identity = WhisperCommand.BuildMuteIdentity(player, "AliceAccount");

        Assert.That(identity.IpAddress, Is.Null);
    }

    private static PlayerSnapshot Player(int slot, string name) => new(
        new PlayerId(slot), name, false, 100, 100, 20, 20,
        new PlayerPosition(0, 0), false, 0);

    private sealed class CapturingMuteService : IMuteService
    {
        public MuteIdentity? Identity { get; private set; }
        public IReadOnlyList<MuteSnapshot> SnapshotActive(DateTimeOffset now) => [];
        public MuteSnapshot? FindActive(string playerName, string? accountName, DateTimeOffset now) =>
            throw new AssertionException("The legacy name/account lookup must not be used.");
        public MuteSnapshot? FindActive(MuteIdentity identity, DateTimeOffset now)
        {
            Identity = identity;
            return new MuteSnapshot(
                MuteId.New(), MuteSubjectKind.Account, "AliceAccount", "test", "Server",
                now, now.AddHours(1), 1);
        }
        public ValueTask<MuteMutationResult> CreateAsync(MuteSnapshot mute,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<MuteMutationResult> RevokeAsync(MuteId muteId, long expectedRevision, string actor,
            DateTimeOffset revokedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
                candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return player is not null;
        }
    }

    private sealed class FakeMessenger : IPlayerMessenger
    {
        public ValueTask<PlayerOperationResult> SendMessageAsync(PlayerId id, string message,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());
    }

    private sealed class FakeLifecycle : IPlayerSessionLifecycle
    {
        public event Action<PlayerSessionOpened>? Opened
        {
            add { }
            remove { }
        }
        public event Action<PlayerSessionClosed>? Closed
        {
            add { }
            remove { }
        }
    }

    private sealed class FakePlayerPrincipal(PlayerId playerId, string playerName, string? accountName)
        : IPlayerCommandPrincipal
    {
        public PlayerId PlayerId => playerId;
        public string PlayerName => playerName;
        public string? AccountName => accountName;
        public string CultureSubjectId => playerId.ToString();
        public string Name => playerName;
        public bool IsServer => false;
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
