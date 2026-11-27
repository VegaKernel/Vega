using NUnit.Framework;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Commands;
using Vega.Contracts.Events;
using Vega.Contracts.Localization;
using Vega.Contracts.Messaging;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Events;
using Vega.Core.Commands;
using Vega.Core.Localization;
using Vega.Core.Messaging;
using Vega.Core.Players;
using Vega.Core.Worlds;
using Vega.Modules.Mentions;
using Vega.Modules.Administration;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class AdministrationPrimitiveTests
{
    [TestCase("attack now", "attack now", false)]
    [TestCase("@bob, @BOB!", "[MENTION @bob], [MENTION @BOB]!", true)]
    [TestCase("x@Bob @Bobby @Bob_ @Bob", "x@Bob @Bobby @Bob_ [MENTION @Bob]", true)]
    [TestCase("@B @", "@B @", false)]
    public void MentionRenderingPreservesBoundariesAndCase(string input, string expected, bool changed)
    {
        PlayerSnapshot bob = Player(2, "Bob");
        var context = new ChatMessageContext(WorldId.Primary, bob, input, [bob]);
        var result = new MentionRenderer().Render(context, bob, input);
        Assert.That(result, Is.EqualTo(new ChatRecipientRenderResult(expected, changed)));
        if (!changed) Assert.That(result.Content, Is.SameAs(input));
    }

    [Test]
    public void PublicChatObservationExcludesCommandsRejectedMessagesAndMissingSessions()
    {
        PlayerSnapshot sender = Player(1, "Alice") with { SessionId = PlayerSessionId.New() };
        PlayerSnapshot bob = Player(2, "Bob");
        var pipeline = new ChatPipeline(transforms: [new PrefixTransform("prefix", 0)], renderers: [new MentionRenderer()]);
        var observed = new List<PublicChatAcceptedObservation>();
        pipeline.Accepted += _ => throw new InvalidOperationException("fixture fault");
        pipeline.Accepted += observed.Add;
        var context = new ChatMessageContext(WorldId.Primary, sender, "hello @Bob", [sender, bob]);
        ChatPipelineResult result = pipeline.Process(context);
        Assert.That(observed, Is.Empty); // native acceptance is a separate host action
        pipeline.ObserveAccepted(context, result with { Allowed = false });
        pipeline.ObserveAccepted(context with { Sender = sender with { SessionId = null } }, result);
        pipeline.ObserveAccepted(context with { WorldId = new("other") }, result);
        foreach (string command in new[] { "/login secret", ".login secret", "  /unknown", "." })
        {
            ChatMessageContext commandContext = context with { Content = command };
            pipeline.ObserveAccepted(commandContext, pipeline.Process(commandContext));
        }
        pipeline.ObserveAccepted(context, result);
        Assert.That(observed, Is.EqualTo(new[] { new PublicChatAcceptedObservation(WorldId.Primary, sender.Id,
            sender.SessionId!.Value, "prefix hello @Bob", 2, true) }));
    }

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
    public void ChatGroupPresentationPreservesAudienceAndPerRecipientRendering()
    {
        PlayerSnapshot sender = Player(1, "Alice");
        PlayerSnapshot bob = Player(2, "Bob");
        int resolutions = 0;
        var pipeline = new ChatPipeline(renderers: [new MentionRenderer()], presentation: _ =>
        {
            resolutions++;
            return new Vega.Contracts.Authorization.GroupChatPresentation("[B] ", "!", "12AB34");
        });
        var result = pipeline.Process(new ChatMessageContext(WorldId.Primary, sender, "hello @Bob", [sender, bob]));
        Assert.That(resolutions, Is.EqualTo(1));
        Assert.That(result.RequiresCustomDelivery, Is.True);
        Assert.That(result.Deliveries.Select(d => d.RecipientId), Is.EqualTo(new[] { sender.Id, bob.Id }));
        Assert.That(result.Deliveries[0].Content, Is.EqualTo("[B] Alice!: hello @Bob"));
        Assert.That(result.Deliveries[1].Content, Is.EqualTo("[B] Alice!: hello [MENTION @Bob]"));
        Assert.That(result.Deliveries.All(d => d.Color == new ChatColor(0x12, 0xab, 0x34)), Is.True);
    }

    [Test]
    public void ChatPipelineReusesUnchangedTextAcrossRecipients()
    {
        PlayerSnapshot sender = Player(1, "Alice");
        PlayerSnapshot bob = Player(2, "Bob");
        PlayerSnapshot carol = Player(3, "Carol");
        var pipeline = new ChatPipeline(renderers: [new MentionRenderer()]);

        ChatPipelineResult result = pipeline.Process(new ChatMessageContext(
            WorldId.Primary, sender, "hello @Bob", [sender, bob, carol]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Deliveries[0].Content, Is.EqualTo("Alice: hello @Bob"));
            Assert.That(result.Deliveries[1].Content, Is.EqualTo("Alice: hello [MENTION @Bob]"));
            Assert.That(result.Deliveries[2].Content, Is.SameAs(result.Deliveries[0].Content));
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
    public async Task PlayerInspectionDoesNotCombineRetiredSessionWithReplacementInventory()
    {
        var alice = Player(1, "Alice") with { SessionId = PlayerSessionId.New() };
        var replacement = Player(1, "Bob") with { SessionId = PlayerSessionId.New() };
        var directory = new StubPlayers(alice);
        var service = new PlayerInspectionService(directory, new StubInventory(alice.Id, () => directory.Current = replacement));
        Assert.That(await service.CaptureAsync(alice), Is.Null);
        Assert.That(await service.CaptureAsync(alice), Is.Null);
    }

    [TestCase(ClientPlatformKind.Pc, 6, "Платформа: PC")]
    [TestCase(ClientPlatformKind.Switch, 5, "Платформа: Nintendo Switch")]
    [TestCase(ClientPlatformKind.GameCenter, 7, "Платформа: Game Center")]
    [TestCase(ClientPlatformKind.Unknown, -1, "Платформа: Неизвестно")]
    [TestCase(ClientPlatformKind.Unrecognized, 99, "Платформа: Не распознана")]
    public async Task UserInfoChatAliasDisplaysCurrentLocalizedPlatformWithoutSensitivePermission(
        ClientPlatformKind platform, int reportedCode, string expected)
    {
        var alice = Player(1, "Alice") with
        {
            SessionId = PlayerSessionId.New(),
            RemoteEndpoint = "203.0.113.1:7777",
            Connection = new(new(1), "203.0.113.1:7777", "203.0.113.1:7777",
                ConnectionForwardingProvenance.Direct, ClientPlatformInfo.Unknown, "uuid-secret", ConnectionLoginPhase.FullyConnected)
        };
        var current = new ClientPlatformInfo(platform, reportedCode < 0 ? null : (byte)reportedCode);
        var directory = new StubPlayers(alice with
        {
            Platform = current,
            Connection = alice.Connection! with { Platform = current }
        });
        var dispatcher = new CommandDispatcher([new UserInfoCommand(directory)]);
        var localization = new LocalizationService("en-US");
        using var catalog = UserInfoCommand.RegisterRussianCatalog(localization);
        var invocation = new CommandInvocation("ui", ["Alice"], new InspectPrincipal(), new SilentOutput());
        foreach (string command in new[] { "ui", "userinfo" })
        {
            var result = await dispatcher.DispatchAsync(invocation with { Name = command });
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(result.Messages, Has.Count.EqualTo(1));
            Assert.That(localization.Format(result.Messages!.Single(), new PlayerCulture("ru-RU")), Is.EqualTo(expected));
        }
    }

    [TestCase("203.0.113.1:7777", "203.0.113.1")]
    [TestCase("[2001:db8::1]:7777", "2001:db8::1")]
    public async Task UserInfoUsesTShockCardSelectorsAndExactAuthenticatedGroup(string endpoint, string ip)
    {
        var alice = Player(1, "Alice") with
        {
            SessionId = PlayerSessionId.New(), RemoteEndpoint = endpoint, Platform = new(ClientPlatformKind.Pc, 6),
            Connection = new(new(1), endpoint, endpoint, ConnectionForwardingProvenance.Direct,
                new(ClientPlatformKind.Pc, 6), "12345678-middle-abcdefgh", ConnectionLoginPhase.FullyConnected)
        };
        var directory = new StubPlayers(alice) { Additional = [
            Player(2, "Alice2") with { SessionId = PlayerSessionId.New() },
            Player(3, "OtherWorld") with { SessionId = PlayerSessionId.New(), WorldId = new("arena") }
        ] };
        var accountSessions = new StubAccountSession(alice);
        var dispatcher = new CommandDispatcher([new UserInfoCommand(directory, accountSessions, new StubRoleLeases())]);
        var localization = new LocalizationService("en-US");
        var invocation = new CommandInvocation("ui", ["tsi:1"], new InspectPrincipal(sensitive: true), new SilentOutput());
        foreach (string selector in new[] { "1", "tsi:1", "tsn:Alice" })
        {
            var result = await dispatcher.DispatchAsync(invocation with { Arguments = [selector] });
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(result.Messages!.Select(message => localization.Format(message, new PlayerCulture("en-US"))), Is.EqualTo(new[]
            {
                $"IP Address: {ip}.", "Short UUID: 12345678…abcdefgh", " -> Logged-in as: alice; in group temporary.", "Platform: PC"
            }));
        }
        var redacted = await dispatcher.DispatchAsync(invocation with { Principal = new InspectPrincipal(), Arguments = ["tsn:Alice"] });
        Assert.That(redacted.Messages!.Select(message => message.Key), Is.EqualTo(new[] { "command.userinfo.account", "command.userinfo.platform" }));
        Assert.That((await dispatcher.DispatchAsync(invocation with { Arguments = ["Ali"] })).ErrorCode, Is.EqualTo("command.userinfo.multiple_matches"));
        Assert.That((await dispatcher.DispatchAsync(invocation with { Arguments = ["OtherWorld"] })).ErrorCode, Is.EqualTo("command.userinfo.invalid_player"));
        Assert.That((await dispatcher.DispatchAsync(invocation with { Arguments = [] })).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        accountSessions.Authenticated = false;
        var guest = await dispatcher.DispatchAsync(invocation with { Arguments = ["tsn:Alice"] });
        Assert.That(guest.Messages!.Select(message => message.Key), Does.Not.Contain("command.userinfo.account"));
    }

    [Test]
    public async Task UserInfoDoesNotExposeReplacementSlotAfterTargetLookup()
    {
        var alice = Player(1, "Alice") with { SessionId = PlayerSessionId.New(), RemoteEndpoint = "203.0.113.1" };
        var replacement = Player(1, "Bob") with { SessionId = PlayerSessionId.New(), RemoteEndpoint = "192.0.2.10" };
        var directory = new StubPlayers(alice);
        directory.DuringSnapshot = () => directory.Current = replacement;
        var dispatcher = new CommandDispatcher([new UserInfoCommand(directory)]);
        var result = await dispatcher.DispatchAsync(new CommandInvocation("ui", ["tsi:1"], new InspectPrincipal(sensitive: true), new SilentOutput()));
        Assert.That(result.ErrorCode, Is.EqualTo("command.userinfo.invalid_player"));
        Assert.That(result.Messages!.Select(message => message.Key), Is.EqualTo(new[] { "command.userinfo.invalid_player" }));
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
        public PlayerSnapshot Current { get; set; } = player;
        public PlayerSnapshot[] Additional { get; init; } = [];
        public Action? DuringSnapshot { get; set; }
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers()
        {
            PlayerSnapshot[] snapshot = [Current, .. Additional];
            DuringSnapshot?.Invoke();
            return snapshot;
        }

        public bool TryGet(PlayerId id, out PlayerSnapshot? found)
        {
            found = id == Current.Id ? Current : Additional.FirstOrDefault(player => player.Id == id);
            return found is not null;
        }

        public bool TryFindExact(string name, out PlayerSnapshot? found)
        {
            found = string.Equals(name, Current.Name, StringComparison.OrdinalIgnoreCase) ? Current : null;
            return found is not null;
        }
    }

    private sealed class InspectPrincipal(bool sensitive = false) : ICommandPrincipal
    {
        public string Name => "Inspector";
        public bool IsServer => false;
        public bool HasPermission(string permission) => permission == "vega.players.inspect" || sensitive && permission == "vega.players.sensitive";
    }

    private sealed class StubAccountSession(PlayerSnapshot player) : IPlayerAccountSessionService
    {
        private readonly PlayerAccountBinding binding = new(player.Id, player.SessionId!.Value, new(42), Guid.NewGuid());
        private readonly AccountSnapshot account = new(new(42), "alice", "default", null);
        public bool Authenticated { get; set; } = true;
        public bool TryGetAccount(PlayerId id, out AccountSnapshot? value) => (value = Authenticated && id == binding.PlayerId ? account : null) is not null;
        public bool TryGetBinding(PlayerSnapshot snapshot, out PlayerAccountBinding value)
        {
            value = binding;
            return Authenticated && snapshot.Id == binding.PlayerId && snapshot.SessionId == binding.SessionId;
        }
        public bool TryRunWithBinding<T>(PlayerAccountBinding expected, Func<T> operation, out T result)
        {
            if (Authenticated && binding == expected) { result = operation(); return true; }
            result = default!;
            return false;
        }
        public ValueTask<bool> SetCultureAsync(PlayerId playerId, string? culture, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubRoleLeases : IRoleLeaseService
    {
        public RoleLeaseSnapshot? FindActive(AccountId accountId, DateTimeOffset now) => new(Guid.NewGuid(), accountId, "temporary", "server", now.AddMinutes(-1), now.AddHours(1), 1, false);
        public IReadOnlyList<RoleLeaseSnapshot> SnapshotActive(DateTimeOffset now) => throw new NotSupportedException();
        public ValueTask<RoleLeaseMutationResult> GrantAsync(AccountId id, string group, DateTimeOffset issued, DateTimeOffset expires, string actor, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask<RoleLeaseMutationResult> RevokeAsync(Guid id, long revision, string actor, CancellationToken token = default) => throw new NotSupportedException();
    }

    private sealed class SilentOutput : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class StubInventory(PlayerId playerId, Action? duringCapture = null) : IPlayerInventoryService
    {
        public ValueTask<PlayerInventorySnapshot?> CaptureAsync(
            PlayerId requested,
            CancellationToken cancellationToken = default)
        {
            duringCapture?.Invoke();
            return ValueTask.FromResult<PlayerInventorySnapshot?>(requested == playerId
                ? new PlayerInventorySnapshot(requested, "Alice", 1, [], DateTimeOffset.UtcNow)
                : null);
        }

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
