using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Runtime;
using Vega.Contracts.Worlds;

namespace Vega.Operations.Actions;

/// <summary>Optional operator API with separate read and maintenance scopes. It never dispatches arbitrary commands or reads mutable native entities.</summary>
public sealed class LoopbackRestService(
    WorldId worldId,
    IRuntimeLifecycle lifecycle,
    IServerRuntimeStateReader state,
    IPlayerDirectory players,
    INetworkMetricsReader network,
    IServerControl? maintenance = null,
    IRuntimeMetricsReader? runtimeMetrics = null,
    Func<GameThreadQueueSnapshot>? queueMetrics = null,
    IWorldEnvironmentService? environment = null,
    IOperationsLogger? logger = null,
    RestAdministration? administration = null,
    IPluginRestRouter? pluginRest = null) : IDisposable, ICommandHandler
{
    private const int MaximumMutationBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private HttpListener? listener;
    private Task? serveTask;
    private TokenHashes? tokens;
    private readonly CancellationTokenSource stopping = new();
    private int port;
    private int disposed;

    public CommandDescriptor Descriptor { get; } = new("rest", [], ["vega.operations.rest.manage"],
        Syntax: "rest status | listusers | destroytokens");

    public void Start(int listenPort, string expectedTokenSha256, string? maintenanceTokenSha256 = null, string? adminTokenSha256 = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (listener is not null) throw new InvalidOperationException("REST listener already started.");
        if (listenPort is < 1 or > 65535)
            throw new InvalidDataException("REST requires a valid port and SHA-256 token fingerprint.");
        byte[] hash = ParseFingerprint(expectedTokenSha256);
        byte[]? writerHash = maintenanceTokenSha256 is null ? null : ParseFingerprint(maintenanceTokenSha256);
        byte[]? adminHash = adminTokenSha256 is null ? null : ParseFingerprint(adminTokenSha256);
        if (writerHash is not null && ((maintenance is null && environment is null &&
            players is not IPlayerOperations { SupportsSessionBoundKicks: true } &&
            players is not IPlayerMessenger { SupportsSessionBoundMessages: true }) || CryptographicOperations.FixedTimeEquals(hash, writerHash)))
            throw new InvalidDataException("REST maintenance requires a distinct token and an available maintenance service.");
        if (adminHash is not null && (administration is null || CryptographicOperations.FixedTimeEquals(hash, adminHash) ||
            writerHash is not null && CryptographicOperations.FixedTimeEquals(writerHash, adminHash)))
            throw new InvalidDataException("REST administration requires its own distinct token and available administration services.");

        var next = new HttpListener();
        try
        {
            next.Prefixes.Add($"http://127.0.0.1:{listenPort}/");
            next.Start();
        }
        catch { next.Close(); throw; }

        port = listenPort;
        Volatile.Write(ref tokens, new TokenHashes(hash, writerHash, adminHash));
        listener = next;
        serveTask = ServeAsync(next);
    }

    private async Task ServeAsync(HttpListener current)
    {
        // ponytail: single sequential operator lane. Introduce bounded workers if local monitoring throughput warrants it.
        while (current.IsListening)
        {
            HttpListenerContext context;
            try { context = await current.GetContextAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { break; }

            try
            {
                await HandleRequestAsync(context).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // The listener remains available after a local client disappears.
            }
            finally
            {
                try { context.Response.Close(); }
                catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException) { }
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers["Cache-Control"] = "no-store";
        Access access = Authenticate(request.Headers["Authorization"]);
        response.KeepAlive = false;
        if (request.RemoteEndPoint is not { Address: var address } || !IPAddress.IsLoopback(address) || access == Access.None)
        {
            response.StatusCode = 401;
            response.Headers["WWW-Authenticate"] = "Bearer";
            return;
        }
        if (request.Url is not { Query.Length: 0 } url) { response.StatusCode = 400; return; }
        if (url.AbsolutePath.StartsWith("/v1/plugins/", StringComparison.Ordinal))
        {
            if (access != Access.Administration) { response.StatusCode = 403; return; }
            if (pluginRest is null) { response.StatusCode = 404; return; }
            if (!WorldReady()) { response.StatusCode = 503; return; }
            await ApplyPluginOperationAsync(request, response, url.AbsolutePath["/v1/plugins/".Length..]).ConfigureAwait(false);
            return;
        }
        if (url.AbsolutePath.StartsWith("/v1/admin/", StringComparison.Ordinal))
        {
            await HandleAdministrationRouteAsync(request, response, access, url.AbsolutePath["/v1/admin/".Length..]).ConfigureAwait(false);
            return;
        }
        if (request.HttpMethod == "POST" && url.AbsolutePath == "/v1/world/save")
        {
            await HandleWorldSaveAsync(request, response, access).ConfigureAwait(false);
            return;
        }
        if (request.HttpMethod == "POST" && url.AbsolutePath is "/v1/world/time" or "/v1/world/weather")
        {
            if (!CanMaintain(access)) { response.StatusCode = 403; return; }
            if (environment is null) { response.StatusCode = 404; return; }
            await ApplyWorldPatchAsync(request, response, url.AbsolutePath == "/v1/world/time").ConfigureAwait(false);
            return;
        }
        if (request.HttpMethod == "POST" && url.AbsolutePath is "/v1/players/kick" or "/v1/players/message")
        {
            if (!CanMaintain(access)) { response.StatusCode = 403; return; }
            await ApplyPlayerActionAsync(request, response, url.AbsolutePath == "/v1/players/kick").ConfigureAwait(false);
            return;
        }
        if (request.HttpMethod != "GET") { response.StatusCode = 405; return; }
        if (url.AbsolutePath == "/v1/world" && environment is not null)
        {
            await ReadWorldAsync(response, environment).ConfigureAwait(false);
            return;
        }
        object? payload = ReadSnapshotPayload(url.AbsolutePath);
        if (payload is null) { response.StatusCode = 404; return; }
        await WriteJsonAsync(response, payload).ConfigureAwait(false);
    }

    private object? ReadSnapshotPayload(string route) => route switch
    {
        "/v1/status" => new
        {
            world = worldId.Value,
            stage = lifecycle.CurrentStage.ToString(),
            uptimeSeconds = lifecycle.Elapsed.TotalSeconds,
            state = state.Read(),
            online = players.SnapshotOnlinePlayers().Count(player => player.WorldId == worldId)
        },
        "/v1/players" => players.SnapshotOnlinePlayers().Where(player => player.WorldId == worldId)
            .Select(player => new { id = player.Id.Slot, sessionId = player.SessionId?.ToString(), player.Name, player.IsDead, player.Life, player.MaxLife, player.Team,
                platform = player.Platform.Kind.ToString(), platformReported = player.Platform.ReportedCode.HasValue })
            .Take(255).ToArray(),
        "/v1/platforms" => PlayerPlatformStatistics.Snapshot(players.SnapshotOnlinePlayers(), worldId)
            .Select(row => new { platform = row.Platform.ToString(), row.Count }).ToArray(),
        "/v1/network" => network.Read(),
        "/v1/metrics" when runtimeMetrics is not null => ReadMetrics(runtimeMetrics),
        "/v1/scheduler" when queueMetrics is not null => queueMetrics(),
        _ => null
    };

    private async Task HandleAdministrationRouteAsync(HttpListenerRequest request, HttpListenerResponse response, Access access, string route)
    {
        if (access != Access.Administration) { response.StatusCode = 403; return; }
        if (administration is null) { response.StatusCode = 404; return; }
        if (!WorldReady()) { response.StatusCode = 503; return; }
        if (request.HttpMethod == "GET")
        {
            try
            {
                object? page = administration.Read(route);
                if (page is null) response.StatusCode = 404;
                else await WriteJsonAsync(response, page).ConfigureAwait(false);
            }
            catch (Exception) { response.StatusCode = 500; }
        }
        else if (request.HttpMethod == "POST")
        {
            if (!RestAdministration.IsMutationRoute(route)) response.StatusCode = 404;
            else await ApplyAdministrationAsync(request, response, route).ConfigureAwait(false);
        }
        else response.StatusCode = 405;
    }

    private async Task HandleWorldSaveAsync(HttpListenerRequest request, HttpListenerResponse response, Access access)
    {
        if (!CanMaintain(access) || maintenance is null) { response.StatusCode = 403; return; }
        if (request.HasEntityBody) { response.StatusCode = 400; return; }
        if (!WorldReady())
        { response.StatusCode = 503; return; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await maintenance.SaveAsync(deadline.Token).ConfigureAwait(false);
            response.StatusCode = 204;
        }
        catch (OperationCanceledException) { response.StatusCode = 504; }
        catch (Exception) { response.StatusCode = 500; }
        AuditMaintenance("save", response.StatusCode);
    }

    private async Task ReadWorldAsync(HttpListenerResponse response, IWorldEnvironmentService environment)
    {
        if (!WorldReady()) { response.StatusCode = 503; return; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            WorldEnvironmentSnapshot? snapshot = await environment.CaptureAsync(worldId, deadline.Token).ConfigureAwait(false);
            if (snapshot is null || snapshot.WorldId != worldId) response.StatusCode = 503;
            else await WriteJsonAsync(response, WorldView(snapshot)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { response.StatusCode = 504; }
        catch (Exception) { response.StatusCode = 500; }
    }

    private bool WorldReady() => lifecycle.CurrentStage >= RuntimeLifecycleStage.NetworkReady &&
        lifecycle.CurrentStage < RuntimeLifecycleStage.Stopping && state.Read().IsWorldReady;

    private async Task ApplyWorldPatchAsync(HttpListenerRequest request, HttpListenerResponse response, bool time)
    {
        if (!WorldReady()) { response.StatusCode = 503; return; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        WorldEnvironmentPatch? patch;
        try
        {
            byte[]? body = await ReadMutationBodyAsync(request, response, deadline.Token).ConfigureAwait(false);
            if (body is null) return;
            patch = ParseWorldPatch(body, time);
        }
        catch (OperationCanceledException) { response.StatusCode = 504; return; }
        catch (Exception ex) when (ex is IOException or JsonException) { response.StatusCode = 400; return; }
        if (patch is null) { response.StatusCode = 400; return; }
        // A token revoked while the body was arriving must not start a new mutation.
        if (!CanMaintain(Authenticate(request.Headers["Authorization"]))) { response.StatusCode = 401; return; }
        if (!WorldReady()) { response.StatusCode = 503; return; }

        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var result = await environment!.PatchAsync(new(worldId, patch, "rest:maintenance"), deadline.Token).ConfigureAwait(false);
            response.StatusCode = result.Status switch
            {
                WorldEnvironmentMutationStatus.Applied => 204,
                WorldEnvironmentMutationStatus.Invalid => 400,
                WorldEnvironmentMutationStatus.DispatcherUnavailable => 503,
                WorldEnvironmentMutationStatus.Cancelled => 504,
                _ => 500
            };
        }
        catch (OperationCanceledException) { response.StatusCode = 504; }
        catch (Exception) { response.StatusCode = 500; }
        AuditMaintenance(time ? "time" : "weather", response.StatusCode);
    }

    private async Task ApplyPlayerActionAsync(HttpListenerRequest request, HttpListenerResponse response, bool kick)
    {
        if (kick ? players is not IPlayerOperations { SupportsSessionBoundKicks: true }
                 : players is not IPlayerMessenger { SupportsSessionBoundMessages: true })
        { response.StatusCode = 404; return; }
        if (!WorldReady()) { response.StatusCode = 503; return; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        PlayerAction? action;
        try
        {
            byte[]? body = await ReadMutationBodyAsync(request, response, deadline.Token).ConfigureAwait(false);
            if (body is null) return;
            action = ParsePlayerAction(body, kick);
        }
        catch (OperationCanceledException) { response.StatusCode = 504; return; }
        catch (Exception ex) when (ex is IOException or JsonException) { response.StatusCode = 400; return; }
        if (action is null) { response.StatusCode = 400; return; }
        if (!CanMaintain(Authenticate(request.Headers["Authorization"]))) { response.StatusCode = 401; return; }
        if (!WorldReady()) { response.StatusCode = 503; return; }
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (!players.TryGet(action.Id, out var player) || player is null || player.WorldId != worldId || player.SessionId != action.Session)
            { response.StatusCode = 404; }
            else
            {
                PlayerOperationResult result = kick
                    ? await ((IPlayerOperations)players).KickAsync(action.Id, action.Session, action.Text, deadline.Token).ConfigureAwait(false)
                    : await ((IPlayerMessenger)players).SendMessageAsync(player, action.Text, deadline.Token).ConfigureAwait(false);
                response.StatusCode = result.Status switch
                {
                    PlayerOperationStatus.Applied => 204,
                    PlayerOperationStatus.PlayerNotFound => 404,
                    PlayerOperationStatus.DispatcherUnavailable => 503,
                    PlayerOperationStatus.Cancelled => 504,
                    _ => 500
                };
            }
        }
        catch (OperationCanceledException) { response.StatusCode = 504; }
        catch (Exception) { response.StatusCode = 500; }
        AuditMaintenance(kick ? "player.kick" : "player.message", response.StatusCode);
    }

    private async Task ApplyAdministrationAsync(HttpListenerRequest request, HttpListenerResponse response, string route)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            byte[]? body = await ReadMutationBodyAsync(request, response, deadline.Token).ConfigureAwait(false);
            if (body is null) return;
            using JsonDocument payload = JsonDocument.Parse(body);
            if (Authenticate(request.Headers["Authorization"]) != Access.Administration) { response.StatusCode = 401; return; }
            if (!WorldReady()) { response.StatusCode = 503; return; }
            // Sandbox lifecycle operations may take longer than ordinary persistence mutations.
            deadline.CancelAfter(TimeSpan.FromSeconds(route.StartsWith("sandboxes/", StringComparison.Ordinal) ? 120 : 30));
            response.StatusCode = await administration!.WriteAsync(route, payload.RootElement, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { response.StatusCode = 504; }
        catch (Exception ex) when (ex is IOException or JsonException) { response.StatusCode = 400; }
        catch (Exception) { response.StatusCode = 500; }
        AuditMaintenance("admin." + route.Replace('/', '.'), response.StatusCode);
    }

    private async Task ApplyPluginOperationAsync(HttpListenerRequest request, HttpListenerResponse response, string route)
    {
        string[] segments = route.Split('/');
        if (segments.Length != 2 || !segments.All(static segment => segment.Length is > 0 and <= 96 && segment is not ("." or "..") &&
                segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
        { response.StatusCode = 404; return; }
        PluginRestMethod method;
        if (request.HttpMethod == "GET") method = PluginRestMethod.Query;
        else if (request.HttpMethod == "POST") method = PluginRestMethod.Action;
        else { response.StatusCode = 405; return; }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        Task<PluginRestResponse>? pending = null;
        try
        {
            byte[] body = [];
            if (method == PluginRestMethod.Query && request.HasEntityBody) { response.StatusCode = 400; return; }
            if (method == PluginRestMethod.Action)
            {
                byte[]? bytes = await ReadMutationBodyAsync(request, response, deadline.Token).ConfigureAwait(false);
                if (bytes is null) return;
                using JsonDocument json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
                if (json.RootElement.ValueKind != JsonValueKind.Object || !UnambiguousJson(json.RootElement)) { response.StatusCode = 400; return; }
                body = bytes;
            }
            // Read scope and token revocation never grant plugin access, even while a body is arriving.
            if (Authenticate(request.Headers["Authorization"]) != Access.Administration) { response.StatusCode = 401; return; }
            if (!WorldReady()) { response.StatusCode = 503; return; }

            pending = pluginRest!.DispatchAsync(segments[0], segments[1], method, body, deadline.Token).AsTask();
            // Non-cooperative plugin callbacks do not block the sequential REST lane forever.
            // The plugin scope still owns the operation and joins it at retirement.
            PluginRestResponse reply = await pending.WaitAsync(deadline.Token).ConfigureAwait(false);
            response.StatusCode = reply.Status switch
            {
                PluginRestStatus.Success => 200,
                PluginRestStatus.NoContent => 204,
                PluginRestStatus.BadRequest => 400,
                PluginRestStatus.NotFound => 404,
                PluginRestStatus.Conflict => 409,
                PluginRestStatus.Unavailable => 503,
                _ => 500
            };
            if (reply.Status == PluginRestStatus.Success)
            {
                if (reply.Json is not { Length: > 0 and <= 64 * 1024 }) { response.StatusCode = 500; return; }
                response.ContentType = "application/json; charset=utf-8";
                response.ContentLength64 = reply.Json.Length;
                await response.OutputStream.WriteAsync(reply.Json, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { response.StatusCode = 504; }
        catch (Exception ex) when (ex is IOException or JsonException) { response.StatusCode = 400; }
        catch (Exception) { response.StatusCode = 500; }
        finally
        {
            if (pending is { IsCompleted: false }) _ = pending.ContinueWith(static failed => _ = failed.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else if (pending is { IsFaulted: true }) _ = pending.Exception;
            AuditMaintenance("plugin." + segments[0] + "." + segments[1], response.StatusCode);
        }
    }

    private static bool UnambiguousJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
                if (!names.Add(property.Name) || !UnambiguousJson(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in element.EnumerateArray()) if (!UnambiguousJson(item)) return false;
        return true;
    }

    private static async Task<byte[]?> ReadMutationBodyAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken token)
    {
        if (request.ContentLength64 > MaximumMutationBytes) { response.StatusCode = 413; return null; }
        if (!request.HasEntityBody || request.ContentLength64 <= 0) { response.StatusCode = 400; return null; }
        if (!string.Equals(request.ContentType?.Split(';', 2)[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
        { response.StatusCode = 415; return null; }
        byte[] body = new byte[(int)request.ContentLength64];
        await request.InputStream.ReadExactlyAsync(body, token).ConfigureAwait(false);
        return body;
    }

    private sealed record PlayerAction(PlayerId Id, PlayerSessionId Session, string Text);

    private static PlayerAction? ParsePlayerAction(byte[] body, bool kick)
    {
        using JsonDocument json = JsonDocument.Parse(body);
        if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
        int? slot = null;
        Guid? session = null;
        string? text = null;
        foreach (var field in json.RootElement.EnumerateObject())
        {
            switch (field.Name)
            {
                case "slot" when slot is null && field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out int id) && id is >= 0 and < 255:
                    slot = id; break;
                case "sessionId" when session is null && field.Value.ValueKind == JsonValueKind.String && Guid.TryParseExact(field.Value.GetString(), "N", out Guid value) && value != Guid.Empty:
                    session = value; break;
                case "reason" when kick && text is null && field.Value.ValueKind == JsonValueKind.String:
                case "message" when !kick && text is null && field.Value.ValueKind == JsonValueKind.String:
                    text = field.Value.GetString(); break;
                default: return null;
            }
        }
        return slot is { } target && session is { } generation && !string.IsNullOrWhiteSpace(text) &&
            text.Length <= (kick ? 256 : 512) && !text.Any(char.IsControl)
            ? new(new(target), new(generation), text) : null;
    }

    private static WorldEnvironmentPatch? ParseWorldPatch(byte[] body, bool time)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (time)
        {
            if (root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("isDaytime", out var day) || day.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("timeTicks", out var ticks) || ticks.ValueKind != JsonValueKind.Number ||
                !ticks.TryGetDouble(out double value) || !double.IsFinite(value) || value < 0 || value > (day.GetBoolean() ? 54000 : 32400)) return null;
            return new(IsDaytime: day.GetBoolean(), TimeTicks: value);
        }

        bool? raining = null;
        float? intensity = null, wind = null;
        foreach (var field in root.EnumerateObject())
        {
            var value = field.Value;
            switch (field.Name)
            {
                case "raining" when raining is null && value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    raining = value.GetBoolean(); break;
                case "rainIntensity" when intensity is null && value.ValueKind == JsonValueKind.Number &&
                    value.TryGetSingle(out float rain) && float.IsFinite(rain) && rain is >= 0 and <= 1:
                    intensity = rain; break;
                case "windSpeed" when wind is null && value.ValueKind == JsonValueKind.Number &&
                    value.TryGetSingle(out float speed) && float.IsFinite(speed) && speed is >= -1 and <= 1:
                    wind = speed; break;
                default: return null;
            }
        }
        return raining is null && intensity is null && wind is null ? null : new(Raining: raining, RainIntensity: intensity, WindSpeed: wind);
    }

    private static object WorldView(WorldEnvironmentSnapshot world) => new
    {
        world = world.WorldId.Value, world.Name, world.WidthTiles, world.HeightTiles, world.SpawnTileX, world.SpawnTileY,
        world.IsDaytime, world.TimeTicks, world.MoonPhase, world.BloodMoon, world.Eclipse, world.Raining,
        world.RainIntensity, world.WindSpeed, world.Hardmode, world.GameMode, world.CapturedAt
    };

    private static async Task WriteJsonAsync(HttpListenerResponse response, object payload)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private void AuditMaintenance(string action, int status) => logger?.Log(status == 204 ? OperationsLogLevel.Information : OperationsLogLevel.Warning,
        "rest", $"Maintenance world={worldId.Value} action={action} status={status}");

    private static object ReadMetrics(IRuntimeMetricsReader reader)
    {
        var sample = reader.Read();
        return new
        {
            sample.Host.WorkingSetBytes, sample.Host.PrivateMemoryBytes, sample.Host.VirtualMemoryBytes,
            cpuMilliseconds = sample.Host.TotalProcessorTime.TotalMilliseconds,
            sample.ManagedMemoryBytes, sample.GcHeapBytes, sample.GcFragmentedBytes, sample.TotalAllocatedBytes,
            sample.Gen0Collections, sample.Gen1Collections, sample.Gen2Collections, sample.TotalGcPauseMilliseconds,
            sample.AvailableWorkerThreads, sample.MaximumWorkerThreads, sample.SampledAt
        };
    }

    private static byte[] ParseFingerprint(string value)
    {
        if (value.Length != 64) throw new InvalidDataException("REST token fingerprint must contain 64 hexadecimal characters.");
        try { return Convert.FromHexString(value); }
        catch (FormatException) { throw new InvalidDataException("REST token fingerprint must be hexadecimal."); }
    }

    private Access Authenticate(string? authorization)
    {
        TokenHashes? current = Volatile.Read(ref tokens);
        if (current is null || authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || authorization.Length is < 39 or > 519) return Access.None;
        ReadOnlySpan<char> token = authorization.AsSpan(7);
        Span<byte> encoded = stackalloc byte[Encoding.UTF8.GetMaxByteCount(token.Length)];
        int length = Encoding.UTF8.GetBytes(token, encoded);
        Span<byte> input = stackalloc byte[32];
        SHA256.HashData(encoded[..length], input);
        CryptographicOperations.ZeroMemory(encoded[..length]);
        if (current.Administration is { } admin && CryptographicOperations.FixedTimeEquals(admin, input)) return Access.Administration;
        if (current.Maintenance is { } writer && CryptographicOperations.FixedTimeEquals(writer, input)) return Access.Maintenance;
        return CryptographicOperations.FixedTimeEquals(current.Reader, input) ? Access.Read : Access.None;
    }

    public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.WorldId != worldId) return ValueTask.FromResult(CommandExecutionResult.Failed("rest.wrong_world"));
        string action = invocation.Arguments.Count == 0 ? "status" : invocation.Arguments[0].ToLowerInvariant();
        if (invocation.Arguments.Count > 1 || action is not ("status" or "listusers" or "destroytokens"))
            return ValueTask.FromResult(new CommandExecutionResult(CommandExecutionStatus.InvalidUsage, "rest.usage",
                [MessageDescriptor.Error("rest.usage").WithDefaultText("Usage: rest status | listusers | destroytokens") ]));
        if (action == "destroytokens")
        {
            Interlocked.Exchange(ref tokens, null);
            return ValueTask.FromResult(CommandExecutionResult.WithMessages(MessageDescriptor.Info("rest.revoked")
                .WithDefaultText("REST operator tokens revoked until process restart.")));
        }
        bool active = listener?.IsListening == true && serveTask?.IsCompleted == false;
        TokenHashes? current = Volatile.Read(ref tokens);
        int count = !active || current is null ? 0 : 1 + (current.Maintenance is null ? 0 : 1) + (current.Administration is null ? 0 : 1);
        return ValueTask.FromResult(CommandExecutionResult.WithMessages(action == "listusers"
            ? MessageDescriptor.Info("rest.users", count).WithDefaultText("Active configured operator tokens: {0}.")
            : MessageDescriptor.Info("rest.status", active, port, count).WithDefaultText("Loopback REST listening={0}; port={1}; active tokens={2}.")));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Interlocked.Exchange(ref tokens, null);
        stopping.Cancel();
        Interlocked.Exchange(ref listener, null)?.Close();
        // Closing HttpListener aborts outstanding requests and the pending accept.
        if (serveTask is { } task)
        {
            try { task.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { /* Service is shutting down. */ }
        }
        stopping.Dispose();
    }

    private static bool CanMaintain(Access access) => access is Access.Maintenance or Access.Administration;
    private enum Access { None, Read, Maintenance, Administration }
    private sealed record TokenHashes(byte[] Reader, byte[]? Maintenance, byte[]? Administration);
}
