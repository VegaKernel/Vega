using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Runtime;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Core.Bans;
using Vega.Core.Operations;
using Vega.Operations;
using Vega.Operations.Actions;
using Vega.Operations.Metrics;
using Vega.Persistence.Sqlite;
using Vega.Sandboxes;

namespace Vega.Runtime.Tests.Operations;

[TestFixture]
public sealed class LoopbackRestServiceTests
{
    [Test]
    public async Task ConfirmedSandboxRestUsesCuratedTemplatesAndRejectsStaleOrExternalDestruction()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-rest-sandbox", Guid.NewGuid().ToString("N"));
        try
        {
            string templates = Path.Combine(root, "rest-templates");
            Directory.CreateDirectory(templates);
            string source = Path.Combine(templates, "base.wld"), replacement = Path.Combine(templates, "replacement.wld");
            File.WriteAllBytes(source, [1, 2, 3]); File.WriteAllBytes(replacement, [4, 5, 6]);
            var database = await SqliteVegaDatabase.OpenAsync(new SqliteVegaDatabaseOptions(Path.Combine(root, "vega.db")));
            var logger = new RecordingLogger();
            using var sandboxes = new SandboxControlService(root, SandboxOptions.Default, logger);
            var admin = new RestAdministration(WorldId.Primary, database.Accounts, database.Authorization, database.Regions,
                new GlobalBanService(database.GlobalBans), database.Mutes, sandboxes, sandboxes, templates);
            var lifetime = new RuntimeLifecycleTracker(Stopwatch.GetTimestamp());
            for (int stage = 1; stage <= (int)RuntimeLifecycleStage.NetworkReady; stage++) lifetime.Advance((RuntimeLifecycleStage)stage);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            string reader = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), administrator = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            static string Fingerprint(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            using var service = new LoopbackRestService(WorldId.Primary, lifetime, new State(), new Players([]), new NetworkMetricsCounter(),
                logger: logger, administration: admin);
            service.Start(port, Fingerprint(reader), adminTokenSha256: Fingerprint(administrator));
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", administrator);
            async Task<HttpStatusCode> Post(string route, object payload)
            {
                using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var response = await http.PostAsync("v1/admin/sandboxes/" + route, content);
                return response.StatusCode;
            }
            object Create(string template, string persistence = "persistent", string sscMode = "copyOnEnter") =>
                new { worldId = "arena", name = "Arena", template, persistence, capacity = 8, sscMode };
            using (var catalog = JsonDocument.Parse(await http.GetStringAsync("v1/admin/sandboxTemplates")))
            {
                Assert.That(catalog.RootElement.GetProperty("items").GetArrayLength(), Is.EqualTo(2));
                Assert.That(catalog.RootElement.ToString(), Does.Not.Contain(root));
            }
            Assert.That(await Post("create", Create("../base.wld")), Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(await Post("create", Create(source)), Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(await Post("create", Create("missing.wld")), Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(await Post("create", Create("base.wld", "external")), Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(await Post("create", Create("base.wld")), Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(await Post("create", Create("base.wld")), Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(await Post("create", Create("base.wld", sscMode: "isolated")), Is.EqualTo(HttpStatusCode.Conflict));
            var first = sandboxes.Snapshot().Single().Descriptor;
            Assert.That(File.ReadAllBytes(first.InstanceWorldPath), Is.EqualTo(new byte[] { 1, 2, 3 }));
            File.WriteAllBytes(first.InstanceWorldPath, [9, 9, 9]);
            object Reset(bool confirm, long revision, DateTimeOffset createdAt) => new
            { worldId = "arena", revision, createdAt, confirm, template = "replacement.wld" };
            Assert.That(await Post("reset", Reset(false, first.Revision, first.CreatedAt)), Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(await Post("reset", Reset(true, first.Revision + 1, first.CreatedAt)), Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await Post("reset", Reset(true, first.Revision, first.CreatedAt.AddSeconds(-1))), Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(File.ReadAllBytes(first.InstanceWorldPath), Is.EqualTo(new byte[] { 9, 9, 9 }));
            Assert.That(await Post("reset", Reset(true, first.Revision, first.CreatedAt)), Is.EqualTo(HttpStatusCode.NoContent));
            var second = sandboxes.Snapshot().Single().Descriptor;
            Assert.That(second.Revision, Is.EqualTo(first.Revision + 1));
            Assert.That(File.ReadAllBytes(second.InstanceWorldPath), Is.EqualTo(new byte[] { 4, 5, 6 }));
            object Destroy(SandboxDescriptor descriptor, bool confirm) => new
            { worldId = descriptor.WorldId.Value, revision = descriptor.Revision, createdAt = descriptor.CreatedAt, confirm };
            Assert.That(await Post("destroy", Destroy(second, false)), Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(await Post("destroy", Destroy(first, true)), Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await Post("destroy", Destroy(second, true)), Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(File.Exists(second.InstanceWorldPath), Is.False);
            Assert.That(File.ReadAllBytes(source), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(File.ReadAllBytes(replacement), Is.EqualTo(new byte[] { 4, 5, 6 }));
            Assert.That(await Post("create", Create("base.wld")), Is.EqualTo(HttpStatusCode.NoContent));
            var recreated = sandboxes.Snapshot().Single().Descriptor;
            Assert.That(recreated.Revision, Is.EqualTo(first.Revision));
            Assert.That(await Post("destroy", Destroy(first, true)), Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await Post("destroy", Destroy(recreated, true)), Is.EqualTo(HttpStatusCode.NoContent));
            var external = await sandboxes.CreateAsync(new("external", "External", source, SandboxPersistenceMode.External, 8,
                new(SandboxAccessMode.Public), null, SandboxSscMode.Disabled, new HashSet<string>()));
            Assert.That(external.IsSuccess, Is.True, external.Message);
            Assert.That(await Post("destroy", Destroy(external.Sandbox!.Descriptor, true)), Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(File.Exists(source), Is.True);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task AdministrativeRestUsesRealPersistenceAndWorldScopedTypedMutations()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-rest-admin", Guid.NewGuid().ToString("N"));
        try
        {
            var database = await SqliteVegaDatabase.OpenAsync(new SqliteVegaDatabaseOptions(Path.Combine(root, "vega.db")));
            var world = new WorldId("rest-arena");
            var bans = new GlobalBanService(database.GlobalBans);
            using var sandbox = new SandboxProbe();
            var admin = new RestAdministration(world, database.Accounts, database.Authorization, database.Regions,
                bans, database.Mutes, sandbox, sandbox);
            var lifetime = new RuntimeLifecycleTracker(Stopwatch.GetTimestamp());
            var logger = new RecordingLogger();
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            string reader = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), writer = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                administrator = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            static string Fingerprint(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            using var service = new LoopbackRestService(world, lifetime, new State(), new Players([]), new NetworkMetricsCounter(),
                maintenance: new Maintenance(), logger: logger, administration: admin);
            Assert.Throws<InvalidDataException>(() => service.Start(port, Fingerprint(reader), Fingerprint(writer), Fingerprint(writer)));
            service.Start(port, Fingerprint(reader), Fingerprint(writer), Fingerprint(administrator));
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(10) };
            async Task<HttpStatusCode> Post(string route, string payload)
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await http.PostAsync("v1/admin/" + route, content);
                return response.StatusCode;
            }
            async Task<JsonDocument> Read(string route) => JsonDocument.Parse(await http.GetStringAsync("v1/admin/" + route));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reader);
            using (var denied = await http.GetAsync("v1/admin/accounts")) Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(await Post("groups/create", "{}"), Is.EqualTo(HttpStatusCode.Forbidden));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", writer);
            using (var denied = await http.GetAsync("v1/admin/accounts")) Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(await Post("groups/create", "{}"), Is.EqualTo(HttpStatusCode.Forbidden));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", administrator);
            using (var pending = await http.GetAsync("v1/admin/accounts")) Assert.That(pending.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            for (int stage = 1; stage <= (int)RuntimeLifecycleStage.NetworkReady; stage++) lifetime.Advance((RuntimeLifecycleStage)stage);
            Assert.That(await Post("groups/create", "{\"name\":\"builders\",\"parent\":\"default\"}"), Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(await Post("groups/permission", "{\"name\":\"builders\",\"revision\":1,\"permission\":\"vega.build\",\"effect\":\"grant\"}"), Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(await Post("groups/permission", "{\"name\":\"builders\",\"revision\":1,\"permission\":\"vega.build\",\"effect\":\"deny\"}"), Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(database.Authorization.HasPermission("builders", "vega.build"), Is.True);
            Assert.That(await Post("accounts/create", "{\"name\":\"rest-user\",\"password\":\"fixture-password\",\"group\":\"builders\"}"), Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(await database.Accounts.VerifyPasswordAsync("rest-user", "fixture-password"), Is.True);
            using (var page = await Read("accounts/0"))
            {
                Assert.That(page.RootElement.GetProperty("items")[0].GetProperty("name").GetString(), Is.EqualTo("rest-user"));
                Assert.That(page.RootElement.ToString(), Does.Not.Contain("password"));
                Assert.That(page.RootElement.GetProperty("nextPage").ValueKind, Is.EqualTo(JsonValueKind.Null));
            }
            foreach (string bad in new[] { "{\"name\":\"rest-user\",\"revision\":\"1\",\"group\":\"default\"}",
                "{\"name\":\"rest-user\",\"revision\":1,\"group\":\"default\",\"actor\":\"forged\"}",
                "{\"name\":\"rest-user\",\"revision\":1,\"revision\":2,\"group\":\"default\"}" })
                Assert.That(await Post("accounts/group", bad), Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(await Post("accounts/group", "{\"name\":\"rest-user\",\"revision\":1,\"group\":\"default\"}"), Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(await Post("accounts/group", "{\"name\":\"rest-user\",\"revision\":1,\"group\":\"builders\"}"), Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await Post("regions/create", "{\"name\":\"rest-zone\",\"x\":5,\"y\":6,\"width\":10,\"height\":12,\"priority\":0,\"owner\":null}"), Is.EqualTo(HttpStatusCode.NoContent));
            await database.Regions.CreateAsync(WorldId.Primary, "other-zone", 1, 1, 2, 2, 0, null, "test");
            using (var page = await Read("regions"))
            {
                Assert.That(page.RootElement.GetProperty("items").GetArrayLength(), Is.EqualTo(1));
                Assert.That(page.RootElement.GetProperty("items")[0].GetProperty("world").GetString(), Is.EqualTo(world.Value));
            }
            Assert.That(await Post("regions/resize", "{\"name\":\"rest-zone\",\"revision\":1,\"x\":8,\"y\":6,\"width\":12,\"height\":14}"), Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(await Post("regions/delete", "{\"name\":\"other-zone\",\"revision\":1}"), Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(await Post("bans/add", "{\"kind\":\"name\",\"value\":\"fixture-name\",\"reason\":\"fixture moderation\",\"durationSeconds\":60}"), Is.EqualTo(HttpStatusCode.NoContent));
            Guid banId = bans.SnapshotActive(DateTimeOffset.UtcNow).Single().Id.Value;
            Assert.That(await Post("bans/revoke", $$"""{"id":"{{banId}}","revision":0}"""), Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await Post("bans/revoke", $$"""{"id":"{{banId}}","revision":1}"""), Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(bans.SnapshotActive(DateTimeOffset.UtcNow), Is.Empty);
            Assert.That(await Post("mutes/add", "{\"kind\":\"account\",\"value\":\"rest-user\",\"reason\":\"fixture\",\"durationSeconds\":60}"), Is.EqualTo(HttpStatusCode.NoContent));
            Guid muteId = database.Mutes.SnapshotActive(DateTimeOffset.UtcNow).Single().Id.Value;
            Assert.That(await Post("mutes/revoke", $$"""{"id":"{{muteId}}","revision":1}"""), Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(database.Mutes.SnapshotActive(DateTimeOffset.UtcNow), Is.Empty);
            using (var page = await Read("sandboxes")) Assert.That(page.RootElement.ToString(), Does.Not.Contain("private-path"));
            Assert.That(await Post("sandboxes/start", "{\"worldId\":\"arena\"}"), Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(await Post("sandboxes/stop", "{\"worldId\":\"primary\"}"), Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(sandbox.Actions, Is.EqualTo(new[] { "start:arena" }));
            Assert.That(logger.Entries.Any(e => e.Contains("action=admin.accounts.create status=204")), Is.True);
            Assert.That(logger.Entries.Any(e => e.Contains("fixture-password") || e.Contains("rest-user")), Is.False);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

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

    private sealed class SandboxProbe : ISandboxControlService
    {
        public List<string> Actions { get; } = [];
        public IReadOnlyList<SandboxStatus> Snapshot() => [new(new(new("arena"), "Arena", "private-path", "private-path",
            SandboxPersistenceMode.Persistent, 8, new(SandboxAccessMode.Public), null, SandboxSscMode.Isolated,
            new HashSet<string>(), 7778, 1, DateTimeOffset.UtcNow, "test"), SandboxLifecycleState.Stopped, null, null, null, null, null,
            0, 60, new(0, 0, 0), 0, null, false)];
        public ValueTask<SandboxOperationResult> StartAsync(WorldId worldId, CancellationToken cancellationToken = default)
        { Actions.Add("start:" + worldId.Value); return ValueTask.FromResult(new SandboxOperationResult(SandboxOperationStatus.Applied)); }
        public ValueTask<SandboxOperationResult> StopAsync(WorldId worldId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SandboxOperationResult> RestartAsync(WorldId worldId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SandboxOperationResult> CreateAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SandboxOperationResult> ResetAsync(WorldId worldId, string? replacementSource = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SandboxOperationResult> DestroyAsync(WorldId worldId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
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
