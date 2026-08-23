using NUnit.Framework;
using Vega.Contracts.Accounts;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;
using Vega.Sandboxes;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class SandboxSscSessionCoordinatorTests
{
    [Test]
    public async Task TrustedTransferRestoresIdentityAppliesTargetStateAndPersistsOnClose()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-sandbox-ssc-session", Guid.NewGuid().ToString("N"));
        string storePath = Path.Combine(root, "ssc.json");
        var worldId = new WorldId("arena");
        var playerId = new PlayerId(7);
        var sessionId = PlayerSessionId.New();
        try
        {
            var store = new SandboxSscStore(storePath);
            SandboxSscWriteResult seeded = store.Save(
                "42", worldId, SandboxSscMode.Isolated, "target-v1", 0, DateTimeOffset.UtcNow);
            Assert.That(seeded.IsSuccess, Is.True);

            var directory = new FakePlayers(Player(playerId, sessionId, "42", worldId));
            var lifecycle = new FakeLifecycle();
            var accounts = new FakeAccounts();
            var operations = new FakeOperations();
            var characters = new FakeCharacters();
            using var coordinator = new SandboxSscSessionCoordinator(
                directory,
                lifecycle,
                accounts,
                operations,
                store,
                characters,
                worldId,
                SandboxSscMode.Isolated,
                new RecordingLogger());

            await EventuallyAsync(() => characters.AppliedPayload == "target-v1" && accounts.RestoredAccountId == "42");
            Assert.That(operations.KickCount, Is.Zero);

            characters.CapturePayload = "target-v2";
            directory.Players = [];
            lifecycle.Close(sessionId, playerId);
            await EventuallyAsync(() => store.Read("42", worldId, SandboxSscMode.Isolated)?.Payload == "target-v2");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task MissingTargetStateFailsClosed()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-sandbox-ssc-session-missing", Guid.NewGuid().ToString("N"));
        var worldId = new WorldId("arena");
        var playerId = new PlayerId(3);
        var sessionId = PlayerSessionId.New();
        try
        {
            var directory = new FakePlayers(Player(playerId, sessionId, "42", worldId));
            var lifecycle = new FakeLifecycle();
            var operations = new FakeOperations();
            using var coordinator = new SandboxSscSessionCoordinator(
                directory,
                lifecycle,
                new FakeAccounts(),
                operations,
                new SandboxSscStore(Path.Combine(root, "ssc.json")),
                new FakeCharacters(),
                worldId,
                SandboxSscMode.Isolated,
                new RecordingLogger());

            await EventuallyAsync(() => operations.KickCount == 1);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static PlayerSnapshot Player(PlayerId id, PlayerSessionId sessionId, string accountId, WorldId worldId) =>
        new(id, "alice", false, 100, 100, 20, 20, new PlayerPosition(0, 0), false, 0)
        {
            SessionId = sessionId,
            WorldId = worldId,
            Connection = new ConnectionMetadataSnapshot(
                new ConnectionId(id.Slot),
                "127.0.0.1:5000",
                "203.0.113.10:5000",
                ConnectionForwardingProvenance.TrustedForwarder,
                ClientPlatformInfo.Unknown,
                "00000000-0000-0000-0000-000000000001",
                ConnectionLoginPhase.WorldRequested)
            {
                TrustedTransferAccountId = accountId
            }
        };

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline) Assert.Fail("Condition was not reached before timeout.");
            await Task.Delay(25);
        }
    }

    private sealed class FakePlayers(params PlayerSnapshot[] players) : IPlayerDirectory
    {
        public IReadOnlyList<PlayerSnapshot> Players { get; set; } = players;
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => Players;
        public bool TryGet(PlayerId id, out PlayerSnapshot? player)
        {
            player = Players.FirstOrDefault(candidate => candidate.Id == id);
            return player is not null;
        }
        public bool TryFindExact(string name, out PlayerSnapshot? player)
        {
            player = Players.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            return player is not null;
        }
    }

    private sealed class FakeLifecycle : IPlayerSessionLifecycle
    {
        public event Action<PlayerSessionOpened>? Opened;
        public event Action<PlayerSessionClosed>? Closed;
        public void Close(PlayerSessionId sessionId, PlayerId playerId) =>
            Closed?.Invoke(new PlayerSessionClosed(sessionId, playerId, "alice", DateTimeOffset.UtcNow));
        public void Open(PlayerSessionId sessionId, PlayerId playerId) =>
            Opened?.Invoke(new PlayerSessionOpened(sessionId, playerId, "alice", DateTimeOffset.UtcNow));
    }

    private sealed class FakeAccounts : IPlayerAccountSessionService
    {
        public string? RestoredAccountId { get; private set; }
        public bool TryGetAccount(PlayerId playerId, out AccountSnapshot? account) { account = null; return false; }
        public ValueTask<bool> SetCultureAsync(PlayerId playerId, string? culture, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
        public ValueTask<bool> EstablishTrustedTransferAsync(PlayerId playerId, string accountId, CancellationToken cancellationToken = default)
        {
            RestoredAccountId = accountId;
            return ValueTask.FromResult(true);
        }
    }

    private sealed class FakeOperations : IPlayerOperations
    {
        public int KickCount { get; private set; }
        public ValueTask<PlayerOperationResult> KickAsync(PlayerId id, string reason, CancellationToken cancellationToken = default)
        {
            KickCount++;
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }
        public ValueTask<PlayerOperationResult> TeleportAsync(PlayerId id, PlayerPosition destination, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());
    }

    private sealed class FakeCharacters : ISandboxSscCharacterAdapter
    {
        public string CapturePayload { get; set; } = "captured";
        public string? AppliedPayload { get; private set; }
        public ValueTask<SandboxSscCharacterCaptureResult> CaptureAsync(PlayerId playerId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SandboxSscCharacterCaptureResult(true, CapturePayload));
        public ValueTask<SandboxSscCharacterApplyResult> ApplyAsync(PlayerId playerId, string payload, CancellationToken cancellationToken = default)
        {
            AppliedPayload = payload;
            return ValueTask.FromResult(new SandboxSscCharacterApplyResult(true));
        }
    }

    private sealed class RecordingLogger : IOperationsLogger
    {
        public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null, string? correlationId = null) { }
    }
}
