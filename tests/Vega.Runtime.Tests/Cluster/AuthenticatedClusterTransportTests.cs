using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using NUnit.Framework;
using Vega.Cluster.Dispatching;
using Vega.Cluster.Scenarios.Presence;
using Vega.Cluster.Transport;
using Vega.Cluster;
using Vega.Contracts.Cluster;
using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Operations;
using Vega.Contracts.Runtime;
using Vega.Core.Commands;
using Vega.Core.Modules;
using Vega.PluginSdk;

namespace Vega.Runtime.Tests.Cluster;

[TestFixture, NonParallelizable]
public sealed class AuthenticatedClusterTransportTests
{
    [Test]
    public async Task HostPresenceCrossesRealPeerListenersAndRoutesOnlyReadyCapacity()
    {
        using var pair = new PeerPair(ClusterServerRole.Lobby, ClusterServerRole.Game);
        await using var lobby = Scope("Lobby", pair.FirstRegistry, pair.First);
        Assert.That(lobby.Cluster.SnapshotServers(), Is.Empty);
        Assert.That(lobby.Cluster.SelectRoute(ClusterServerRole.Game), Is.Null);
        var ready = new ClusterPresenceSample(RuntimeLifecycleStage.NetworkReady, 3, 8, true);
        Assert.That((await pair.Second.AnnouncePresenceAsync(ready)).Single().Delivery.IsDelivered, Is.True);
        var suggestion = lobby.Cluster.SelectRoute(ClusterServerRole.Game);
        Assert.That(suggestion?.ServerId, Is.EqualTo(new ServerId("b")));
        Assert.That(suggestion?.OnlinePlayers, Is.EqualTo(3));
        Assert.That(suggestion?.MaximumPlayers, Is.EqualTo(8));
        Assert.That(lobby.Cluster.SelectRoute(ClusterServerRole.Lobby), Is.Null);
        Assert.That((await pair.Second.AnnouncePresenceAsync(ready with { OnlinePlayers = 8 })).Single().Delivery.IsDelivered, Is.True);
        Assert.That(lobby.Cluster.SelectRoute(ClusterServerRole.Game), Is.Null);
        Assert.That((await pair.Second.AnnouncePresenceAsync(ready with { IsWorldReady = false })).Single().Delivery.IsDelivered, Is.True);
        Assert.That(lobby.Cluster.SelectRoute(ClusterServerRole.Game), Is.Null);
        Assert.That((await pair.Second.WithdrawPresenceAsync()).Single().Delivery.IsDelivered, Is.True);
        Assert.That(lobby.Cluster.SnapshotServers(), Is.Empty);
    }

    [Test]
    public async Task PresenceHeartbeatPublishesAndWithdrawsWithinHostLifetime()
    {
        using var pair = new PeerPair(ClusterServerRole.Lobby, ClusterServerRole.Game);
        using (var heartbeat = new ClusterPresenceHeartbeat(pair.Second,
                   () => new(RuntimeLifecycleStage.FullyWarmed, 1, 10, true), new Logger()))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (pair.First.SelectRoute(ClusterServerRole.Game) is null)
                await Task.Delay(20, deadline.Token);
            Assert.That(pair.First.SnapshotServers(), Has.Count.EqualTo(1));
        }
        Assert.That(pair.First.SnapshotServers(), Is.Empty);
    }

    [Test]
    public async Task DirectoryUsesConfiguredRolesLeastUtilizationAndWithdrawalWatermarks()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-11-27T12:00:00Z"));
        var alpha = new ServerId("alpha");
        var beta = new ServerId("beta");
        var directory = new ServerPresenceDirectory(clock, new Dictionary<ServerId, ClusterServerRole>
        {
            [alpha] = ClusterServerRole.Game, [beta] = ClusterServerRole.Game
        });
        ClusterEnvelope<ServerPresenceAnnounced> Announcement(ServerId id, int count, int capacity,
            ClusterServerRole role = ClusterServerRole.Game, DateTimeOffset? expiry = null) =>
            new ClusterMessageFactory(id, clock).Create(new ServerPresenceAnnounced(id, id.Value,
                RuntimeLifecycleStage.NetworkReady, count, expiry ?? clock.GetUtcNow().AddSeconds(20))
            { Role = role, MaximumPlayers = capacity, IsWorldReady = true });

        await directory.HandleAsync(Announcement(alpha, 4, 20), default);
        await directory.HandleAsync(Announcement(beta, 1, 2), default);
        Assert.That(directory.SelectRoute(ClusterServerRole.Game)?.ServerId, Is.EqualTo(alpha));
        clock.Advance(TimeSpan.FromSeconds(1));
        await directory.HandleAsync(Announcement(alpha, 0, 20, ClusterServerRole.Lobby), default);
        Assert.That(directory.SelectRoute(ClusterServerRole.Game)?.ServerId, Is.EqualTo(beta));
        Assert.That(directory.SnapshotServers().Single(s => s.ServerId == alpha).Role, Is.EqualTo(ClusterServerRole.Unspecified));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await directory.HandleAsync(Announcement(new("unknown"), 0, 10), default));
        Assert.ThrowsAsync<ArgumentException>(async () => await directory.HandleAsync(Announcement(beta, -1, 10), default));
        Assert.ThrowsAsync<ArgumentException>(async () => await directory.HandleAsync(Announcement(beta, 0, 10, expiry: clock.GetUtcNow().AddMinutes(10)), default));
        var stale = Announcement(beta, 0, 10);
        clock.Advance(TimeSpan.FromSeconds(1));
        await directory.HandleAsync(new ClusterMessageFactory(beta, clock).Create(new ServerPresenceWithdrawn(beta)), default);
        await directory.HandleAsync(stale, default);
        Assert.That(directory.SelectRoute(ClusterServerRole.Game), Is.Null);
        clock.Advance(TimeSpan.FromSeconds(25));
        Assert.That(directory.SnapshotServers(), Is.Empty);
    }

    [Test]
    public async Task TypedNotificationsCrossRealPeerListenersAndRemainPluginScoped()
    {
        using var pair = new PeerPair();
        await using var sending = Scope("Lobby", pair.FirstRegistry, pair.First);
        var receiving = Scope("lobby", pair.SecondRegistry, pair.Second);
        var observed = new List<PluginClusterMessage<int>>();
        receiving.Cluster.Subscribe("presence", 1, TypeInfo<int>(), (message, _) => { observed.Add(message); return ValueTask.CompletedTask; });
        Assert.That(sending.Cluster.IsAvailable, Is.True);
        Assert.That(sending.Cluster.LocalServerId, Is.EqualTo(new ServerId("a")));
        Assert.That(sending.Cluster.Peers, Is.EqualTo(new[] { new ServerId("b") }));
        Assert.That((await sending.Cluster.SendAsync(new("b"), "presence", 1, 42, TypeInfo<int>())).IsDelivered, Is.True);
        Assert.That(observed, Has.Count.EqualTo(1));
        Assert.That(observed[0].Sender, Is.EqualTo(new ServerId("a")));
        Assert.That(observed[0].Payload, Is.EqualTo(42));
        Assert.That(observed[0].MessageId.Value, Is.Not.EqualTo(Guid.Empty));

        var broadcast = await sending.Cluster.BroadcastAsync("PRESENCE", 1, 43, TypeInfo<int>());
        Assert.That(broadcast.Single().Delivery.IsDelivered, Is.True);
        Assert.That(observed.Select(m => m.Payload), Is.EqualTo(new[] { 42, 43 }));
        Assert.That((await sending.Cluster.SendAsync(new("b"), "presence", 2, 99, TypeInfo<int>())).IsDelivered, Is.False);
        await using var differentPlugin = Scope("Other", pair.FirstRegistry, pair.First);
        Assert.That((await differentPlugin.Cluster.SendAsync(new("b"), "presence", 1, 99, TypeInfo<int>())).IsDelivered, Is.False);
        Assert.That(observed, Has.Count.EqualTo(2));
        Assert.That((await sending.Cluster.SendAsync(new("missing"), "presence", 1, 99, TypeInfo<int>())).Status, Is.EqualTo(ClusterDeliveryStatus.Unavailable));
        Assert.ThrowsAsync<ArgumentException>(async () => await sending.Cluster.SendAsync(new("b"), "presence", 1,
            new string('x', PluginClusterNotification.MaximumPayloadBytes), TypeInfo<string>()));

        await receiving.DisposeAsync();
        Assert.That((await sending.Cluster.SendAsync(new("b"), "presence", 1, 99, TypeInfo<int>())).IsDelivered, Is.False);
        await using var replacement = Scope("Lobby", pair.SecondRegistry, pair.Second);
        replacement.Cluster.Subscribe("presence", 1, TypeInfo<int>(), (message, _) => { observed.Add(message); return ValueTask.CompletedTask; });
        Assert.That((await sending.Cluster.SendAsync(new("b"), "presence", 1, 44, TypeInfo<int>())).IsDelivered, Is.True);
        Assert.That(observed.Select(m => m.Payload), Is.EqualTo(new[] { 42, 43, 44 }));
    }

    [Test]
    public async Task AuthenticationSchemaTimestampAndDuplicateChecksPrecedeDelivery()
    {
        using var pair = new PeerPair();
        await using var receiver = Scope("Lobby", pair.SecondRegistry, pair.Second);
        int delivered = 0;
        receiver.Cluster.Subscribe("presence", 1, TypeInfo<int>(), (_, _) => { delivered++; return ValueTask.CompletedTask; });
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        Guid id = Guid.NewGuid();
        var valid = new PluginClusterNotification("Lobby", "presence", 1, JsonSerializer.SerializeToUtf8Bytes(42));
        byte[] Frame(string sender, DateTimeOffset time, string schema) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageId = id,
            timestamp = time.ToUnixTimeMilliseconds(),
            sender,
            schema,
            messageVersion = 1,
            message = valid
        });
        async Task<HttpStatusCode> Post(string sender, string token, byte[] body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, pair.SecondEndpoint);
            request.Headers.Add("X-Vega-Cluster-Sender", sender);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new ByteArrayContent(body);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }
        byte[] frame = Frame("a", DateTimeOffset.UtcNow, valid.Schema);
        Assert.That(await Post("a", TestSecret(), frame), Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await Post("unknown", pair.FirstSend, frame), Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await Post("a", pair.FirstSend, Frame("other", DateTimeOffset.UtcNow, valid.Schema)), Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await Post("a", pair.FirstSend, Frame("a", DateTimeOffset.UtcNow.AddMinutes(-5), valid.Schema)), Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await Post("a", pair.FirstSend, Frame("a", DateTimeOffset.UtcNow, "unknown.event")), Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await Post("a", pair.FirstSend, [0]), Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await Post("a", pair.FirstSend, new byte[33 * 1024]), Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        Assert.That(delivered, Is.Zero);
        Assert.That(await Post("a", pair.FirstSend, frame), Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((int)await Post("a", pair.FirstSend, frame), Is.EqualTo(208));
        Assert.That(delivered, Is.EqualTo(1));
    }

    [Test]
    public async Task RetiredPluginCancelsAndDrainsPendingNotificationAndFaultsStayLocal()
    {
        using var pair = new PeerPair();
        await using var sending = Scope("Lobby", pair.FirstRegistry, pair.First);
        var receiving = Scope("Lobby", pair.SecondRegistry, pair.Second);
        receiving.Cluster.Subscribe("fault", 1, TypeInfo<int>(), (_, _) => throw new InvalidOperationException("fixture"));
        Assert.That((await sending.Cluster.SendAsync(new("b"), "fault", 1, 1, TypeInfo<int>())).IsDelivered, Is.False);
        Assert.That(receiving.Faults.Count, Is.EqualTo(1));
        Assert.That(sending.Faults.Count, Is.Zero);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiving.Cluster.Subscribe("wait", 1, TypeInfo<int>(), async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { ended.TrySetResult(); }
        });
        var pending = sending.Cluster.SendAsync(new("b"), "wait", 1, 1, TypeInfo<int>()).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await receiving.DisposeAsync();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That((await pending.WaitAsync(TimeSpan.FromSeconds(3))).IsDelivered, Is.False);
    }

    [Test]
    public void PeerConfigurationRejectsSharedCredentialsAndNonTlsRemoteEndpoints()
    {
        string token = TestSecret();
        var dispatcher = new ClusterMessageDispatcher(new ClusterMessageDeduplicator());
        var remote = new ClusterTransportOptions("a", FreePort(), [new("b", "http://remote.invalid/v1/cluster/notifications", "send", "receive")]);
        Assert.Throws<ArgumentException>(() => new AuthenticatedClusterTransport(remote, dispatcher, _ => token));
        var duplicated = new ClusterTransportOptions("a", FreePort(), [
            new("b", "https://peer-b.invalid/v1/cluster/notifications", "one", "two"),
            new("c", "https://peer-c.invalid/v1/cluster/notifications", "three", "four")]);
        Assert.Throws<ArgumentException>(() => new AuthenticatedClusterTransport(duplicated, dispatcher, _ => token));
    }

    [Test]
    public void DeduplicationIsBoundedAndScopedToAuthenticatedSender()
    {
        var dedup = new ClusterMessageDeduplicator(capacity: 2);
        var id = ClusterMessageId.New();
        Assert.That(dedup.TryAccept(new("a"), id), Is.True);
        Assert.That(dedup.TryAccept(new("a"), id), Is.False);
        Assert.That(dedup.TryAccept(new("b"), id), Is.True);
        Assert.That(dedup.TryAccept(new("c"), id), Is.True);
        Assert.That(dedup.TryAccept(new("a"), id), Is.True);
    }

    private static HotModuleRegistrationScope Scope(string name, PluginClusterRegistry registry, IClusterNotificationSender transport) =>
        new(new CommandDispatcher([]), new Logger(), name, Path.GetTempPath(), pluginClusterRegistry: registry, clusterSender: transport);
    private static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)JsonSerializerOptions.Default.GetTypeInfo(typeof(T));
    private static string TestSecret() => RandomNumberGenerator.GetHexString(64);
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private sealed class PeerPair : IDisposable
    {
        public readonly PluginClusterRegistry FirstRegistry = new();
        public readonly PluginClusterRegistry SecondRegistry = new();
        public readonly string FirstSend = TestSecret();
        public readonly AuthenticatedClusterTransport First;
        public readonly AuthenticatedClusterTransport Second;
        public readonly string SecondEndpoint;
        public PeerPair(ClusterServerRole firstRole = ClusterServerRole.Unspecified, ClusterServerRole secondRole = ClusterServerRole.Unspecified)
        {
            int portA = FreePort(), portB;
            do { portB = FreePort(); } while (portA == portB);
            string secondSend = TestSecret();
            SecondEndpoint = $"http://127.0.0.1:{portB}{AuthenticatedClusterTransport.NotificationPath}";
            First = Start(FirstRegistry, new("a", portA, [new("b", SecondEndpoint, "send", "receive") { Role = secondRole }]) { Role = firstRole }, FirstSend, secondSend);
            try { Second = Start(SecondRegistry, new("b", portB, [new("a", $"http://127.0.0.1:{portA}{AuthenticatedClusterTransport.NotificationPath}", "send", "receive") { Role = firstRole }]) { Role = secondRole }, secondSend, FirstSend); }
            catch { First.Dispose(); throw; }
        }
        private static AuthenticatedClusterTransport Start(PluginClusterRegistry registry, ClusterTransportOptions options, string send, string receive)
        {
            var dispatcher = new ClusterMessageDispatcher(new ClusterMessageDeduplicator());
            dispatcher.RegisterEvent(registry);
            return new AuthenticatedClusterTransport(options, dispatcher, name => name == "send" ? send : receive);
        }
        public void Dispose() { Second.Dispose(); First.Dispose(); }
    }

    private sealed class Logger : IOperationsLogger
    {
        public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null, string? correlationId = null) { }
    }

    private sealed class TestClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset now = initial;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
