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
    public async Task PlayerMaintenanceRequiresExactWorldSessionAndNeverFallsBackToSlotOnlyMutation()
    {
        var world = new WorldId("moderated");
        var session = PlayerSessionId.New();
        var player = Player(4, "Alice", world) with { SessionId = session };
        var boundary = new PlayerBoundary([player, Player(5, "OtherWorld", WorldId.Primary) with { SessionId = session }]);
        string reader = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string writer = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        static string Fingerprint(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var lifetime = new RuntimeLifecycleTracker(Stopwatch.GetTimestamp());
        var logger = new RecordingLogger();
        using var service = new LoopbackRestService(world, lifetime, new State(), boundary, new NetworkMetricsCounter(), logger: logger);
        service.Start(port, Fingerprint(reader), Fingerprint(writer));
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(5) };
        async Task<HttpStatusCode> Post(string path, string json)
        {
            using var body = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync("v1/players/" + path, body);
            return response.StatusCode;
        }
        string message = $$"""{"slot":4,"sessionId":"{{session}}","message":"test notice"}""";
        string kick = $$"""{"slot":4,"sessionId":"{{session}}","reason":"maintenance notice"}""";
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reader);
        Assert.That(await Post("kick", kick), Is.EqualTo(HttpStatusCode.Forbidden));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", writer);
        Assert.That(await Post("kick", kick), Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        for (int stage = 1; stage <= (int)RuntimeLifecycleStage.NetworkReady; stage++) lifetime.Advance((RuntimeLifecycleStage)stage);
        using (var view = JsonDocument.Parse(await http.GetStringAsync("v1/players")))
        {
            Assert.That(view.RootElement.GetArrayLength(), Is.EqualTo(1));
            Assert.That(view.RootElement[0].GetProperty("sessionId").GetString(), Is.EqualTo(session.ToString()));
            Assert.That(view.RootElement[0].TryGetProperty("remoteEndpoint", out _), Is.False);
        }
        foreach (string bad in new[] { "{}", kick.Replace("4,", "255,"), kick.Replace("4,", "4,\"slot\":4,"),
                     kick.Replace("maintenance notice", "\\u000a"), kick.Replace("reason", "message"), new string(' ', 4097) })
            Assert.That(await Post("kick", bad), Is.EqualTo(bad.Length > 4096 ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.BadRequest));
        Assert.That(await Post("kick", kick.Replace("4,", "5,")), Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await Post("message", message.Replace(session.ToString(), PlayerSessionId.New().ToString())), Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(boundary.Calls, Is.Empty);
        Assert.That(await Post("message", message), Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await Post("kick", kick), Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(boundary.Calls, Is.EqualTo(new[] { $"message:4:{session}:test notice", $"kick:4:{session}:maintenance notice" }));
        // The native boundary revalidates again on the game thread, after our read.
        boundary.Result = PlayerOperationResult.NotFound();
        Assert.That(await Post("message", message), Is.EqualTo(HttpStatusCode.NotFound));
        boundary.Result = PlayerOperationResult.DispatcherUnavailable();
        Assert.That(await Post("kick", kick), Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        boundary.Result = PlayerOperationResult.Cancelled();
        Assert.That(await Post("kick", kick), Is.EqualTo(HttpStatusCode.GatewayTimeout));
        Assert.That(logger.Entries.Any(entry => entry.Contains("action=player.kick status=204")), Is.True);
        Assert.That(logger.Entries.Any(entry => entry.Contains("maintenance notice") || entry.Contains(session.ToString())), Is.False);
    }

    [Test]
    public async Task WorldRestReadsAreScopedAndMaintenanceChangesUseTypedWorldBoundary()
    {
        var world = new WorldId("rest-world");
        string reader = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string writer = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        static string Fingerprint(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var lifetime = new RuntimeLifecycleTracker(Stopwatch.GetTimestamp());
        var environment = new RecordingEnvironment(world);
        var logger = new RecordingLogger();
        using var service = new LoopbackRestService(world, lifetime, new State(), new Players([]), new NetworkMetricsCounter(),
            environment: environment, logger: logger);
        service.Start(port, Fingerprint(reader), Fingerprint(writer));
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(5) };
        async Task<HttpStatusCode> Post(string path, string json, string mediaType = "application/json")
        {
            using var body = new StringContent(json, Encoding.UTF8, mediaType);
            using var response = await http.PostAsync(path, body);
            return response.StatusCode;
        }

        using (var denied = await http.GetAsync("v1/world")) Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reader);
        using (var unavailable = await http.GetAsync("v1/world")) Assert.That(unavailable.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(await Post("v1/world/time", "{\"isDaytime\":true,\"timeTicks\":123}"), Is.EqualTo(HttpStatusCode.Forbidden));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", writer);
        Assert.That(await Post("v1/world/time", "{\"isDaytime\":true,\"timeTicks\":123}"), Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(environment.Requests, Is.Empty);
        for (int i = 1; i <= (int)RuntimeLifecycleStage.NetworkReady; i++) lifetime.Advance((RuntimeLifecycleStage)i);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reader);
        using (var snapshot = JsonDocument.Parse(await http.GetStringAsync("v1/world")))
        {
            Assert.That(snapshot.RootElement.GetProperty("world").GetString(), Is.EqualTo(world.Value));
            Assert.That(snapshot.RootElement.GetProperty("widthTiles").GetInt32(), Is.EqualTo(4200));
            Assert.That(snapshot.RootElement.TryGetProperty("seed", out _), Is.False);
            Assert.That(snapshot.RootElement.TryGetProperty("fileIdentity", out _), Is.False);
        }
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", writer);
        foreach (string bad in new[] { "{}", "{\"isDaytime\":false,\"timeTicks\":54000}",
                     "{\"isDaytime\":true,\"timeTicks\":0,\"world\":\"other\"}", "{\"isDaytime\":true,\"timeTicks\":\"noon\"}" })
            Assert.That(await Post("v1/world/time", bad), Is.EqualTo(HttpStatusCode.BadRequest));
        foreach (string bad in new[] { "{}", "{\"raining\":true,\"raining\":false}", "{\"windSpeed\":2}", "{\"rainIntensity\":-0.1}" })
            Assert.That(await Post("v1/world/weather", bad), Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await Post("v1/world/time", new string(' ', 4097)), Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        Assert.That(await Post("v1/world/time", "{\"isDaytime\":true,\"timeTicks\":0}", "text/plain"), Is.EqualTo(HttpStatusCode.UnsupportedMediaType));
        Assert.That(environment.Requests, Is.Empty);
        Assert.That(await Post("v1/world/time", "{\"isDaytime\":false,\"timeTicks\":3600}"), Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await Post("v1/world/weather", "{\"raining\":true,\"rainIntensity\":0.75,\"windSpeed\":-0.5}"), Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(environment.Requests, Is.EqualTo(new[]
        {
            new WorldEnvironmentMutationRequest(world, new(IsDaytime: false, TimeTicks: 3600), "rest:maintenance"),
            new WorldEnvironmentMutationRequest(world, new(Raining: true, RainIntensity: 0.75f, WindSpeed: -0.5f), "rest:maintenance")
        }));
        environment.Status = WorldEnvironmentMutationStatus.DispatcherUnavailable;
        Assert.That(await Post("v1/world/weather", "{\"raining\":false}"), Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(logger.Entries, Has.Count.EqualTo(3));
        Assert.That(logger.Entries.Last(), Does.Contain("status=503"));
        Assert.That(logger.Entries.All(entry => !entry.Contains(writer, StringComparison.Ordinal)), Is.True);
        await service.ExecuteAsync(new CommandInvocation("rest", ["destroytokens"], new Principal(true), new Output()) { WorldId = world }, default);
        Assert.That(await Post("v1/world/weather", "{\"raining\":false}"), Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(environment.Requests, Has.Count.EqualTo(3));
    }

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

    private sealed class RecordingEnvironment(WorldId world) : IWorldEnvironmentService
    {
        public List<WorldEnvironmentMutationRequest> Requests { get; } = [];
        public WorldEnvironmentMutationStatus Status { get; set; } = WorldEnvironmentMutationStatus.Applied;
        public ValueTask<WorldEnvironmentSnapshot?> CaptureAsync(WorldId requestedWorld, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<WorldEnvironmentSnapshot?>(requestedWorld == world
                ? new WorldEnvironmentSnapshot(world, 4200, 1200, 100, 100, true, 1200, 0, false, false, false, 0, 0, false, DateTimeOffset.UtcNow)
                    { Name = "Test World", Seed = 12345, FileIdentity = Guid.NewGuid() }
                : null);
        public ValueTask<WorldEnvironmentMutationResult> PatchAsync(WorldEnvironmentMutationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new WorldEnvironmentMutationResult(Status));
        }
    }

    private sealed class RecordingLogger : IOperationsLogger
    {
        public List<string> Entries { get; } = [];
        public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null, string? correlationId = null) => Entries.Add(text);
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

    private sealed class PlayerBoundary(PlayerSnapshot[] values) : IPlayerDirectory, IPlayerOperations, IPlayerMessenger
    {
        public bool SupportsSessionBoundKicks => true;
        public bool SupportsSessionBoundMessages => true;
        public List<string> Calls { get; } = [];
        public PlayerOperationResult Result { get; set; } = PlayerOperationResult.Applied();
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => values;
        public bool TryGet(PlayerId id, out PlayerSnapshot? player) => (player = values.FirstOrDefault(p => p.Id == id)) is not null;
        public bool TryFindExact(string name, out PlayerSnapshot? player) => (player = values.FirstOrDefault(p => p.Name == name)) is not null;
        public ValueTask<PlayerOperationResult> KickAsync(PlayerId id, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PlayerOperationResult> KickAsync(PlayerId id, PlayerSessionId sessionId, string reason, CancellationToken cancellationToken = default)
        { Calls.Add($"kick:{id.Slot}:{sessionId}:{reason}"); return ValueTask.FromResult(Result); }
        public ValueTask<PlayerOperationResult> SendMessageAsync(PlayerId id, string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PlayerOperationResult> SendMessageAsync(PlayerSnapshot player, string message, CancellationToken cancellationToken = default)
        { Calls.Add($"message:{player.Id.Slot}:{player.SessionId}:{message}"); return ValueTask.FromResult(Result); }
        public ValueTask<PlayerOperationResult> TeleportAsync(PlayerId id, PlayerPosition destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
