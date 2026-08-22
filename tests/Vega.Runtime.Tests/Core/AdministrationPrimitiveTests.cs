using NUnit.Framework;
using Vega.Contracts.Events;
using Vega.Contracts.Messaging;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Events;
using Vega.Core.Messaging;
using Vega.Core.Players;
using Vega.Core.Worlds;
using Vega.Modules.Mentions;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class AdministrationPrimitiveTests
{
    [Test]
    public void ChatPipelineAppliesOrderedContentBeforePerRecipientMentionRendering()
    {
        PlayerSnapshot sender = Player(1, "Alice");
        PlayerSnapshot bob = Player(2, "Bob");
        var pipeline = new ChatPipeline(
            transforms: [new PrefixTransform("first", -10), new PrefixTransform("second", 10)],
            renderers: [new MentionRenderer()]);

        ChatPipelineResult result = pipeline.Process(new ChatMessageContext(
            WorldId.Primary,
            sender,
            "hello @Bob",
            [sender, bob]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Allowed, Is.True);
            Assert.That(result.RequiresCustomDelivery, Is.True);
            Assert.That(result.Deliveries.Single(delivery => delivery.RecipientId == bob.Id).Content,
                Is.EqualTo("Alice: second first hello [MENTION @Bob]"));
            Assert.That(result.Deliveries.Single(delivery => delivery.RecipientId == sender.Id).Content,
                Is.EqualTo("Alice: second first hello @Bob"));
        });
    }

    [Test]
    public async Task PlayerInspectionRedactsAllConnectionSecretsAtOneBoundary()
    {
        PlayerSnapshot player = Player(1, "Alice") with
        {
            RemoteEndpoint = "203.0.113.1:7777",
            Connection = new ConnectionMetadataSnapshot(
                new ConnectionId(1),
                "203.0.113.1:7777",
                "198.51.100.2:7777",
                ConnectionForwardingProvenance.TrustedForwarder,
                ClientPlatformInfo.Unknown,
                "uuid-secret",
                ConnectionLoginPhase.FullyConnected)
        };
        var service = new PlayerInspectionService(
            new StubPlayers(player),
            new StubInventory(player.Id));

        PlayerInspectionSnapshot? redacted = await service.CaptureAsync(player.Id);
        PlayerInspectionSnapshot? sensitive = await service.CaptureAsync(
            player.Id,
            PlayerInspectionExposure.Sensitive);

        Assert.Multiple(() =>
        {
            Assert.That(redacted!.Player.RemoteEndpoint, Is.Null);
            Assert.That(redacted.Player.Connection!.TransportEndpoint, Is.Null);
            Assert.That(redacted.Player.Connection.ClientUuid, Is.Null);
            Assert.That(sensitive!.Player.Connection!.ClientUuid, Is.EqualTo("uuid-secret"));
        });
    }

    [Test]
    public async Task ChestPolicyCanRejectBeforeTransactionalBackendMutation()
    {
        var identity = new ChestIdentity(WorldId.Primary, 10, 20);
        var backend = new StubChestBackend(new ChestSnapshot(
            identity,
            "test",
            4,
            [new ChestItemSnapshot(0, 1, 1, 0)],
            DateTimeOffset.UtcNow));
        var service = new ChestService(
            backend,
            new AsyncEventPipeline<ChestChanging>([new RejectChestMutation()]),
            new AsyncEventPipeline<ChestChanged>([]));

        ChestMutationResult result = await service.ReplaceAsync(new ChestMutationRequest(
            identity,
            4,
            [],
            "test"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ChestMutationStatus.Rejected));
            Assert.That(result.ErrorCode, Is.EqualTo("test.denied"));
            Assert.That(backend.ReplaceCount, Is.Zero);
        });
    }

    [Test]
    public async Task FileWorldResetStagesReplacementAndPromotesItOnNextStartup()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"reset-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string current = Path.Combine(directory, "world.wld");
        string replacement = Path.Combine(directory, "template.wld");
        await File.WriteAllTextAsync(current, "old");
        await File.WriteAllTextAsync(replacement, "new");

        try
        {
            var logger = new RecordingLogger();
            using var orchestrator = new WorldResetOrchestrator(new FileWorldResetBackend(), [], logger);
            WorldResetResult result = await orchestrator.ExecuteAsync(new WorldResetPlan(
                Guid.NewGuid(),
                WorldId.Primary,
                current,
                replacement,
                "test",
                DateTimeOffset.UtcNow));

            Assert.Multiple(() =>
            {
                Assert.That(result.Succeeded, Is.True);
                Assert.That(File.ReadAllText(current), Is.EqualTo("old"));
                Assert.That(File.Exists(current + ".pending-reset"), Is.True);
            });

            string? backup = FileWorldResetBackend.ApplyPending(current);

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(current), Is.EqualTo("new"));
                Assert.That(backup, Is.Not.Null);
                Assert.That(File.ReadAllText(backup!), Is.EqualTo("old"));
            });
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static PlayerSnapshot Player(int slot, string name) => new(
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

    private sealed record PrefixTransform(string Prefix, int Order) : IChatContentTransform
    {
        public ChatContentTransformResult Transform(ChatMessageContext context, string content) =>
            new($"{Prefix} {content}", true);
    }

    private sealed class StubPlayers(PlayerSnapshot player) : IPlayerDirectory
    {
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => [player];

        public bool TryGet(PlayerId id, out PlayerSnapshot? found)
        {
            found = id == player.Id ? player : null;
            return found is not null;
        }

        public bool TryFindExact(string name, out PlayerSnapshot? found)
        {
            found = string.Equals(name, player.Name, StringComparison.OrdinalIgnoreCase) ? player : null;
            return found is not null;
        }
    }

    private sealed class StubInventory(PlayerId playerId) : IPlayerInventoryService
    {
        public ValueTask<PlayerInventorySnapshot?> CaptureAsync(
            PlayerId requested,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PlayerInventorySnapshot?>(requested == playerId
                ? new PlayerInventorySnapshot(requested, "Alice", 1, [], DateTimeOffset.UtcNow)
                : null);

        public ValueTask<InventoryMutationResult> ReplaceSlotAsync(
            PlayerId playerId,
            long expectedRevision,
            InventorySlotReplacement replacement,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RejectChestMutation : IAsyncEventHandler<ChestChanging>
    {
        public ValueTask HandleAsync(
            EventContext<ChestChanging> context,
            CancellationToken cancellationToken)
        {
            context.Cancel(new EventCancellation("test.denied"));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubChestBackend(ChestSnapshot snapshot) : IChestBackend
    {
        public int ReplaceCount { get; private set; }

        public ValueTask<ChestSnapshot?> CaptureAsync(
            ChestIdentity identity,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ChestSnapshot?>(snapshot);

        public ValueTask<ChestMutationResult> ReplaceAsync(
            ChestMutationRequest request,
            CancellationToken cancellationToken = default)
        {
            ReplaceCount++;
            return ValueTask.FromResult(new ChestMutationResult(ChestMutationStatus.Applied, snapshot, snapshot));
        }
    }

    private sealed class RecordingLogger : IOperationsLogger
    {
        public void Log(
            OperationsLogLevel level,
            string source,
            string message,
            Exception? exception = null,
            string? correlationId = null)
        {
        }
    }
}
