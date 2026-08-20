using NUnit.Framework;
using Vega.Contracts.Accounts;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Cluster;
using Vega.Cluster.Dispatching;
using Vega.Cluster.Observability;
using Vega.Cluster.Scenarios.Administration;
using Vega.Cluster.Scenarios.Authorization;
using Vega.Cluster.Scenarios.Bans;
using Vega.Cluster.Scenarios.Chat;
using Vega.Cluster.Scenarios.Presence;
using Vega.Cluster.Scenarios.Transfer;
using Vega.Cluster.Transport;
using Vega.Contracts.Cluster;
using Vega.Contracts.Cluster.Commands;
using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Cluster.Requests;
using Vega.Contracts.Runtime;

namespace Vega.Runtime.Tests.Cluster;

[TestFixture]
public sealed class ClusterContractTests
{
    [Test]
    public void FactoryCreatesTypedVersionedMetadataAndRejectsMismatches()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-20T12:00:00Z"));
        var serverId = new ServerId("eu-1");
        var correlationId = ClusterCorrelationId.New();
        var message = new ServerPresenceWithdrawn(serverId);
        ClusterEnvelope<ServerPresenceWithdrawn> envelope =
            new ClusterMessageFactory(serverId, time).Create(message, correlationId);

        Assert.Multiple(() =>
        {
            Assert.That(envelope.Metadata.MessageId.Value, Is.Not.EqualTo(Guid.Empty));
            Assert.That(envelope.Metadata.CorrelationId, Is.EqualTo(correlationId));
            Assert.That(envelope.Metadata.SenderServerId, Is.EqualTo(serverId));
            Assert.That(envelope.Metadata.Timestamp, Is.EqualTo(time.GetUtcNow()));
            Assert.That(envelope.Metadata.Schema, Is.EqualTo(message.Schema));
            Assert.That(envelope.Metadata.MessageVersion, Is.EqualTo(message.SchemaVersion));
            Assert.That(message, Is.InstanceOf<IClusterEvent>());
            Assert.That(new DisconnectPlayer("Alice", "test"), Is.InstanceOf<IClusterCommand>());
            Assert.That(new ServerStatusRequest(), Is.InstanceOf<IClusterRequest<ServerStatusResponse>>());
        });

        var mismatched = envelope.Metadata with { MessageVersion = 99 };
        Assert.That(
            () => new ClusterEnvelope<ServerPresenceWithdrawn>(mismatched, message),
            Throws.ArgumentException);
    }

    [Test]
    public async Task StandaloneModeKeepsLocalBanMutationWhenTransportIsUnavailable()
    {
        var replica = new InMemoryGlobalBanReplica();
        var coordinator = new GlobalBanCoordinator(
            replica,
            UnavailableClusterClient.Instance,
            new ClusterMessageFactory(new ServerId("standalone")));

        GlobalBanMutationResult result = await coordinator.CreateAsync(
            new GlobalBanSubject(GlobalBanSubjectKind.PlayerName, "Alice"),
            "test",
            "console");

        Assert.Multiple(() =>
        {
            Assert.That(result.LocalStateChanged, Is.True);
            Assert.That(result.Publication.Status, Is.EqualTo(ClusterDeliveryStatus.Unavailable));
            Assert.That(replica.SnapshotActive(DateTimeOffset.UtcNow), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task FailedMessagesRemainRetryableInsteadOfBecomingDuplicates()
    {
        var audit = new BoundedClusterAuditLog(4);
        var dispatcher = new ClusterMessageDispatcher(new ClusterMessageDeduplicator(), audit);
        dispatcher.RegisterEvent<ServerPresenceWithdrawn>(new ThrowingPresenceHandler());
        ClusterEnvelope<ServerPresenceWithdrawn> envelope = new ClusterMessageFactory(new ServerId("origin"))
            .Create(new ServerPresenceWithdrawn(new ServerId("origin")));

        ClusterDeliveryResult first = await dispatcher.DispatchEventAsync(envelope, CancellationToken.None);
        ClusterDeliveryResult retry = await dispatcher.DispatchEventAsync(envelope, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.EqualTo(ClusterDeliveryStatus.Failed));
            Assert.That(retry.Status, Is.EqualTo(ClusterDeliveryStatus.Failed));
            Assert.That(audit.Snapshot().Select(static item => item.Status),
                Is.EqualTo(new[] { ClusterDeliveryStatus.Failed, ClusterDeliveryStatus.Failed }));
        });
    }

    [Test]
    public async Task StaleLocalBanMutationIsNotPublished()
    {
        var publisher = new CountingClusterPublisher();
        var coordinator = new GlobalBanCoordinator(
            new InMemoryGlobalBanReplica(),
            publisher,
            new ClusterMessageFactory(new ServerId("origin")));
        var banId = GlobalBanId.New();

        GlobalBanMutationResult first = await coordinator.RevokeAsync(banId, "console", 1);
        GlobalBanMutationResult stale = await coordinator.RevokeAsync(banId, "console", 1);

        Assert.Multiple(() =>
        {
            Assert.That(first.LocalStateChanged, Is.True);
            Assert.That(stale.LocalStateChanged, Is.False);
            Assert.That(stale.Publication.Status, Is.EqualTo(ClusterDeliveryStatus.Rejected));
            Assert.That(publisher.PublishCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void TransferRemainsExplicitlyDisabledUntilSimplerScenariosAreProductionValidated()
    {
        var blocked = new PlayerTransferReadinessGate(true, true, false, true);
        var ready = new PlayerTransferReadinessGate(true, true, true, true);

        Assert.Multiple(() =>
        {
            Assert.That(blocked.IsEnabled, Is.False);
            Assert.That(blocked.BlockingReason, Does.Contain("validated in production"));
            Assert.That(ready.IsEnabled, Is.True);
            Assert.That(ready.BlockingReason, Is.Null);
        });
    }

    private sealed class ThrowingPresenceHandler : IClusterEventHandler<ServerPresenceWithdrawn>
    {
        public ValueTask HandleAsync(
            ClusterEnvelope<ServerPresenceWithdrawn> envelope,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("transient");
    }
}

[TestFixture]
public sealed class PresenceClusterTests
{
    [Test]
    public async Task PresenceIsObservableDeduplicatedAndExpires()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-20T12:00:00Z"));
        var audit = new BoundedClusterAuditLog(8);
        var directory = new ServerPresenceDirectory(time);
        var receiver = new ClusterMessageDispatcher(new ClusterMessageDeduplicator(timeProvider: time), audit);
        receiver.RegisterEvent<ServerPresenceAnnounced>(directory);
        receiver.RegisterEvent<ServerPresenceWithdrawn>(directory);

        var bus = new InMemoryClusterBus();
        using InMemoryClusterEndpoint sender = bus.Connect(
            new ServerId("sender"),
            new ClusterMessageDispatcher(new ClusterMessageDeduplicator(timeProvider: time)));
        using InMemoryClusterEndpoint remote = bus.Connect(new ServerId("remote"), receiver);
        var announced = new ServerPresenceAnnounced(
            sender.ServerId,
            "Sender",
            RuntimeLifecycleStage.NetworkReady,
            3,
            time.GetUtcNow() + TimeSpan.FromSeconds(30));
        ClusterEnvelope<ServerPresenceAnnounced> envelope =
            new ClusterMessageFactory(sender.ServerId, time).Create(announced);

        ClusterDeliveryResult first = await sender.PublishAsync(envelope);
        ClusterDeliveryResult duplicate = await sender.PublishAsync(envelope);
        var staleTime = new ManualTimeProvider(time.GetUtcNow() - TimeSpan.FromSeconds(1));
        await sender.PublishAsync(
            new ClusterMessageFactory(sender.ServerId, staleTime)
                .Create(new ServerPresenceWithdrawn(sender.ServerId)));

        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.EqualTo(ClusterDeliveryStatus.Delivered));
            Assert.That(duplicate.Status, Is.EqualTo(ClusterDeliveryStatus.Duplicate));
            Assert.That(directory.SnapshotOnline(), Has.Count.EqualTo(1));
            Assert.That(audit.Snapshot().Select(static item => item.Status),
                Is.EqualTo(new[]
                {
                    ClusterDeliveryStatus.Delivered,
                    ClusterDeliveryStatus.Duplicate,
                    ClusterDeliveryStatus.Delivered
                }));
        });

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.That(directory.SnapshotOnline(), Is.Empty);
    }
}

[TestFixture]
public sealed class ReplicationScenarioTests
{
    [Test]
    public async Task GlobalBanCreateAndRevokeReplicateAndDuplicateDeliveryKicksOnlyOnce()
    {
        var player = Player("Alice", 2);
        var directory = new FakePlayerBoundary([player]);
        var remoteReplica = new InMemoryGlobalBanReplica();
        var remoteDispatcher = new ClusterMessageDispatcher(new ClusterMessageDeduplicator());
        remoteDispatcher.RegisterEvent<GlobalBanCreated>(
            new GlobalBanCreatedHandler(remoteReplica, directory, directory));
        remoteDispatcher.RegisterEvent<GlobalBanRevoked>(new GlobalBanRevokedHandler(remoteReplica));

        var bus = new InMemoryClusterBus();
        using InMemoryClusterEndpoint sender = bus.Connect(
            new ServerId("origin"),
            new ClusterMessageDispatcher(new ClusterMessageDeduplicator()));
        using InMemoryClusterEndpoint remote = bus.Connect(new ServerId("remote"), remoteDispatcher);
        var factory = new ClusterMessageFactory(sender.ServerId);
        var banId = GlobalBanId.New();
        var created = new GlobalBanCreated(
            banId,
            new GlobalBanSubject(GlobalBanSubjectKind.PlayerName, "alice"),
            "abuse",
            "console",
            DateTimeOffset.UtcNow,
            null,
            1);
        ClusterEnvelope<GlobalBanCreated> createEnvelope = factory.Create(created);

        await sender.PublishAsync(createEnvelope);
        ClusterDeliveryResult duplicate = await sender.PublishAsync(createEnvelope);

        Assert.Multiple(() =>
        {
            Assert.That(duplicate.Status, Is.EqualTo(ClusterDeliveryStatus.Duplicate));
            Assert.That(remoteReplica.SnapshotActive(DateTimeOffset.UtcNow), Has.Count.EqualTo(1));
            Assert.That(directory.Kicks, Has.Count.EqualTo(1));
            Assert.That(directory.Kicks[0].PlayerId, Is.EqualTo(player.Id));
        });

        await sender.PublishAsync(factory.Create(new GlobalBanRevoked(
            banId,
            "console",
            DateTimeOffset.UtcNow,
            2)));

        Assert.That(remoteReplica.SnapshotActive(DateTimeOffset.UtcNow), Is.Empty);

        await sender.PublishAsync(factory.Create(new GlobalBanCreated(
            GlobalBanId.New(),
            new GlobalBanSubject(GlobalBanSubjectKind.PlayerName, "Alice"),
            "expired",
            "console",
            DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2),
            DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1),
            1)));
        Assert.That(directory.Kicks, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AuthorizationReplicationIgnoresStaleRevisions()
    {
        var state = new ReplicatedAuthorizationState();
        var dispatcher = new ClusterMessageDispatcher(new ClusterMessageDeduplicator());
        dispatcher.RegisterEvent<AccountGroupChanged>(state);
        dispatcher.RegisterEvent<PermissionAssignmentChanged>(state);
        var bus = new InMemoryClusterBus();
        using InMemoryClusterEndpoint sender = bus.Connect(
            new ServerId("origin"),
            new ClusterMessageDispatcher(new ClusterMessageDeduplicator()));
        using InMemoryClusterEndpoint remote = bus.Connect(new ServerId("remote"), dispatcher);
        var factory = new ClusterMessageFactory(sender.ServerId);
        var accountId = new AccountId(17);

        await sender.PublishAsync(factory.Create(new AccountGroupChanged(accountId, "admin", 2)));
        await sender.PublishAsync(factory.Create(new AccountGroupChanged(accountId, "guest", 1)));
        await sender.PublishAsync(factory.Create(new PermissionAssignmentChanged("admin", "vega.kick", true, 4)));
        await sender.PublishAsync(factory.Create(new PermissionAssignmentChanged("admin", "vega.kick", false, 3)));

        Assert.Multiple(() =>
        {
            Assert.That(state.TryGetAccountGroup(accountId, out string? group), Is.True);
            Assert.That(group, Is.EqualTo("admin"));
            Assert.That(state.IsGranted("admin", "vega.kick"), Is.True);
        });
    }

    [Test]
    public async Task InterServerChatUsesExplicitLocalDeliveryPolicy()
    {
        var boundary = new FakePlayerBoundary([Player("Alice", 1), Player("Bob", 2)]);
        var dispatcher = new ClusterMessageDispatcher(new ClusterMessageDeduplicator());
        dispatcher.RegisterEvent<InterServerChatPublished>(
            new InterServerChatHandler(new AllowInterServerChatPolicy(), boundary, boundary));
        var bus = new InMemoryClusterBus();
        using InMemoryClusterEndpoint sender = bus.Connect(
            new ServerId("lobby"),
            new ClusterMessageDispatcher(new ClusterMessageDeduplicator()));
        using InMemoryClusterEndpoint remote = bus.Connect(new ServerId("survival"), dispatcher);

        await new InterServerChatPublisher(sender, new ClusterMessageFactory(sender.ServerId))
            .PublishAsync("Moderator", "Maintenance soon");

        Assert.Multiple(() =>
        {
            Assert.That(boundary.Messages, Has.Count.EqualTo(2));
            Assert.That(boundary.Messages.All(static item => item.Text == "[lobby] Moderator: Maintenance soon"), Is.True);
        });
    }

    private static PlayerSnapshot Player(string name, int slot) => new(
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
}

[TestFixture]
public sealed class ClusterAdministrationTests
{
    [Test]
    public async Task TargetedCommandAndRequestUseTheirOwnTypedHandlers()
    {
        var boundary = new FakePlayerBoundary([Player("Alice", 3)]);
        var remoteDispatcher = new ClusterMessageDispatcher(new ClusterMessageDeduplicator());
        remoteDispatcher.RegisterCommand<DisconnectPlayer>(new DisconnectPlayerHandler(boundary, boundary));
        var commands = new FakeOperationsCommands();
        remoteDispatcher.RegisterRequest<ExecuteAdministrativeCommand, AdministrativeCommandResponse>(
            new AdministrativeCommandHandler(commands));

        var bus = new InMemoryClusterBus();
        using InMemoryClusterEndpoint sender = bus.Connect(
            new ServerId("control"),
            new ClusterMessageDispatcher(new ClusterMessageDeduplicator()));
        using InMemoryClusterEndpoint remote = bus.Connect(new ServerId("game"), remoteDispatcher);
        var factory = new ClusterMessageFactory(sender.ServerId);

        ClusterDeliveryResult command = await sender.SendAsync(
            remote.ServerId,
            factory.Create(new DisconnectPlayer("Alice", "remote moderation")));
        ClusterEnvelope<ExecuteAdministrativeCommand> requestEnvelope = factory.Create(
            new ExecuteAdministrativeCommand("serverinfo"),
            ClusterCorrelationId.New());
        ClusterRequestResult<AdministrativeCommandResponse> request = await sender
            .RequestAsync<ExecuteAdministrativeCommand, AdministrativeCommandResponse>(
                remote.ServerId,
                requestEnvelope,
                TimeSpan.FromSeconds(1));
        ClusterRequestResult<AdministrativeCommandResponse> duplicate = await sender
            .RequestAsync<ExecuteAdministrativeCommand, AdministrativeCommandResponse>(
                remote.ServerId,
                requestEnvelope,
                TimeSpan.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(command.Status, Is.EqualTo(ClusterDeliveryStatus.Delivered));
            Assert.That(boundary.Kicks.Single().Reason, Is.EqualTo("remote moderation"));
            Assert.That(request.Delivery.Status, Is.EqualTo(ClusterDeliveryStatus.Delivered));
            Assert.That(request.Response?.Message.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(request.Response?.Metadata.SenderServerId, Is.EqualTo(remote.ServerId));
            Assert.That(request.Response?.Metadata.CorrelationId, Is.Not.Null);
            Assert.That(duplicate.Delivery.Status, Is.EqualTo(ClusterDeliveryStatus.Duplicate));
            Assert.That(duplicate.Response?.Message.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(commands.LastCommand, Is.EqualTo("serverinfo"));
            Assert.That(commands.ExecutionCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task AggregatedStatusQueriesEveryOnlineServerAndHandlesUnavailableTargets()
    {
        var bus = new InMemoryClusterBus();
        var controlId = new ServerId("control");
        using InMemoryClusterEndpoint control = bus.Connect(
            controlId,
            new ClusterMessageDispatcher(new ClusterMessageDeduplicator()));
        using InMemoryClusterEndpoint alpha = ConnectStatus(bus, "alpha", 4);
        using InMemoryClusterEndpoint beta = ConnectStatus(bus, "beta", 7);
        var presence = new ServerPresenceDirectory();
        await AddPresence(presence, alpha.ServerId);
        await AddPresence(presence, beta.ServerId);
        await AddPresence(presence, new ServerId("offline"));

        IReadOnlyList<ClusterRequestResult<ServerStatusResponse>> results =
            await new ClusterAdministrationClient(control, new ClusterMessageFactory(controlId))
                .QueryOnlineServersAsync(presence, TimeSpan.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(3));
            Assert.That(results.Count(static result => result.Delivery.IsDelivered), Is.EqualTo(2));
            Assert.That(results.Count(static result => result.Delivery.Status == ClusterDeliveryStatus.Unavailable), Is.EqualTo(1));
            Assert.That(results.Where(static result => result.Response is not null)
                .Sum(static result => result.Response!.Message.OnlinePlayers), Is.EqualTo(11));
        });
    }

    private static InMemoryClusterEndpoint ConnectStatus(InMemoryClusterBus bus, string name, int players)
    {
        var id = new ServerId(name);
        var dispatcher = new ClusterMessageDispatcher(new ClusterMessageDeduplicator());
        dispatcher.RegisterRequest<ServerStatusRequest, ServerStatusResponse>(new FixedStatusHandler(id, players));
        return bus.Connect(id, dispatcher);
    }

    private static ValueTask AddPresence(ServerPresenceDirectory directory, ServerId serverId)
    {
        var message = new ServerPresenceAnnounced(
            serverId,
            serverId.Value,
            RuntimeLifecycleStage.NetworkReady,
            0,
            DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1));
        return directory.HandleAsync(
            new ClusterMessageFactory(serverId).Create(message),
            CancellationToken.None);
    }

    private static PlayerSnapshot Player(string name, int slot) => new(
        new PlayerId(slot), name, false, 100, 100, 20, 20, new PlayerPosition(0, 0), false, 0);

    private sealed class FixedStatusHandler(ServerId serverId, int players) :
        IClusterRequestHandler<ServerStatusRequest, ServerStatusResponse>
    {
        public ValueTask<ServerStatusResponse> HandleAsync(
            ClusterEnvelope<ServerStatusRequest> envelope,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ServerStatusResponse(
                serverId,
                RuntimeLifecycleStage.NetworkReady,
                TimeSpan.FromMinutes(5),
                players,
                $"World-{serverId}"));
    }

    private sealed class FakeOperationsCommands : IOperationsCommandService
    {
        public string? LastCommand { get; private set; }

        public int ExecutionCount { get; private set; }

        public ValueTask<OperationsActionResult> ExecuteAsync(
            string commandText,
            CancellationToken cancellationToken = default)
        {
            LastCommand = commandText;
            ExecutionCount++;
            return ValueTask.FromResult(new OperationsActionResult(
                CommandExecutionStatus.Success,
                null,
                ["done"]));
        }
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset now = now;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan duration) => now += duration;
}

internal sealed class FakePlayerBoundary(IReadOnlyList<PlayerSnapshot> players) :
    IPlayerDirectory,
    IPlayerOperations,
    IPlayerMessenger
{
    public List<(PlayerId PlayerId, string Reason)> Kicks { get; } = [];

    public List<(PlayerId PlayerId, string Text)> Messages { get; } = [];

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

    public ValueTask<PlayerOperationResult> KickAsync(
        PlayerId id,
        string reason,
        CancellationToken cancellationToken = default)
    {
        Kicks.Add((id, reason));
        return ValueTask.FromResult(PlayerOperationResult.Applied());
    }

    public ValueTask<PlayerOperationResult> TeleportAsync(
        PlayerId id,
        PlayerPosition destination,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(PlayerOperationResult.Applied());

    public ValueTask<PlayerOperationResult> SendMessageAsync(
        PlayerId id,
        string message,
        CancellationToken cancellationToken = default)
    {
        Messages.Add((id, message));
        return ValueTask.FromResult(PlayerOperationResult.Applied());
    }
}

internal sealed class CountingClusterPublisher : IClusterPublisher
{
    public int PublishCount { get; private set; }

    public ValueTask<ClusterDeliveryResult> PublishAsync<TEvent>(
        ClusterEnvelope<TEvent> envelope,
        CancellationToken cancellationToken = default)
        where TEvent : IClusterEvent
    {
        PublishCount++;
        return ValueTask.FromResult(ClusterDeliveryResult.Delivered());
    }
}
