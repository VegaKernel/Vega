using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Runtime;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Core.Operations;
using Vega.Operations;
using Vega.Operations.Actions;
using Vega.Operations.Metrics;

namespace Vega.Runtime.Tests.Operations;

[TestFixture]
public sealed class LoopbackRestServiceTests
{
    [Test]
    public async Task LoopbackStatusAndPlayersRequireTokenAndRevocationTakesEffect()
    {
        var world = new WorldId("arena");
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        using var service = new LoopbackRestService(world, new RuntimeLifecycleTracker(Stopwatch.GetTimestamp()), new State(),
            new Players([Player(1, "Alice", world), Player(2, "Other", WorldId.Primary)]), new NetworkMetricsCounter(),
            runtimeMetrics: new SampledRuntimeMetricsReader(new ProcessHostMetricsReader()),
            queueMetrics: () => new GameThreadQueueSnapshot(3, 4096, 1, 128, 2));
        service.Start(port, fingerprint);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(5) };
        using (var denied = await http.GetAsync("v1/status")) Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using (var denied = await http.GetAsync("v1/metrics")) Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using (var status = await http.GetAsync("v1/status")) Assert.That(status.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using (var sample = JsonDocument.Parse(await http.GetStringAsync("v1/metrics")))
        {
            Assert.That(sample.RootElement.GetProperty("privateMemoryBytes").GetInt64(), Is.GreaterThan(0));
            Assert.That(sample.RootElement.GetProperty("totalGcPauseMilliseconds").GetDouble(), Is.GreaterThanOrEqualTo(0));
            Assert.That(sample.RootElement.TryGetProperty("machineName", out _), Is.False);
        }
        using (var queue = JsonDocument.Parse(await http.GetStringAsync("v1/scheduler")))
        {
            Assert.That(queue.RootElement.GetProperty("pending").GetInt32(), Is.EqualTo(3));
            Assert.That(queue.RootElement.GetProperty("capacity").GetInt32(), Is.EqualTo(4096));
        }
        using (var players = JsonDocument.Parse(await http.GetStringAsync("v1/players")))
        {
            Assert.That(players.RootElement.GetArrayLength(), Is.EqualTo(1));
            Assert.That(players.RootElement[0].GetProperty("name").GetString(), Is.EqualTo("Alice"));
            Assert.That(players.RootElement[0].TryGetProperty("remoteEndpoint", out _), Is.False);
        }
        using (var method = await http.PostAsync("v1/status", new StringContent(""))) Assert.That(method.StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
        var dispatcher = new CommandDispatcher([service]);
        var invocation = new CommandInvocation("rest", ["destroytokens"], new Principal(false), new Output()) { WorldId = world };
        Assert.That((await dispatcher.DispatchAsync(invocation)).Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
        Assert.That((await dispatcher.DispatchAsync(invocation with { Principal = new Principal(true) })).Status, Is.EqualTo(CommandExecutionStatus.Success));
        using (var revoked = await http.GetAsync("v1/status")) Assert.That(revoked.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task SaveRequiresSeparateMaintenanceTokenAndReadyWorld()
    {
        string reader = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string writer = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        static string Fingerprint(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var lifetime = new RuntimeLifecycleTracker(Stopwatch.GetTimestamp());
        var control = new Maintenance();
        using var service = new LoopbackRestService(WorldId.Primary, lifetime, new State(), new Players([]), new NetworkMetricsCounter(), control);
        Assert.Throws<InvalidDataException>(() => service.Start(port, Fingerprint(reader), Fingerprint(reader)));
        service.Start(port, Fingerprint(reader), Fingerprint(writer));
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reader);
        using (var denied = await http.PostAsync("v1/world/save", null)) Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", writer);
        using (var notReady = await http.PostAsync("v1/world/save", null)) Assert.That(notReady.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(control.Saves, Is.Zero);
        for (int i = 1; i <= (int)RuntimeLifecycleStage.NetworkReady; i++) lifetime.Advance((RuntimeLifecycleStage)i);
        using (var saved = await http.PostAsync("v1/world/save", null)) Assert.That(saved.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(control.Saves, Is.EqualTo(1));
        var invocation = new CommandInvocation("rest", ["destroytokens"], new Principal(true), new Output());
        await service.ExecuteAsync(invocation, default);
        using (var revoked = await http.PostAsync("v1/world/save", null)) Assert.That(revoked.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(control.Saves, Is.EqualTo(1));
    }

    private sealed class Maintenance : IServerControl
    {
        public int Saves { get; private set; }
        public ValueTask SaveAsync(CancellationToken cancellationToken = default) { Saves++; return ValueTask.CompletedTask; }
        public ValueTask ShutdownAsync(string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static PlayerSnapshot Player(int slot, string name, WorldId world) =>
        new(new(slot), name, false, 100, 100, 20, 20, new(0, 0), false, 0) { WorldId = world, RemoteEndpoint = "192.0.2.10" };

    private sealed class State : IServerRuntimeStateReader
    {
        public ServerRuntimeStateSnapshot Read() => new("Arena", 8, 7777, true, 60, DateTimeOffset.UtcNow);
    }

    private sealed class Players(PlayerSnapshot[] values) : IPlayerDirectory
    {
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => values;
        public bool TryGet(PlayerId id, out PlayerSnapshot? player) => (player = values.FirstOrDefault(p => p.Id == id)) is not null;
        public bool TryFindExact(string name, out PlayerSnapshot? player) => (player = values.FirstOrDefault(p => p.Name == name)) is not null;
    }

    private sealed class Principal(bool allowed) : ICommandPrincipal
    {
        public string Name => "test";
        public bool IsServer => false;
        public bool HasPermission(string permission) => allowed;
    }

    private sealed class Output : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
