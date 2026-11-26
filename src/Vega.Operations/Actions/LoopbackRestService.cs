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
    Func<GameThreadQueueSnapshot>? queueMetrics = null) : IDisposable, ICommandHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private HttpListener? listener;
    private Task? serveTask;
    private TokenHashes? tokens;
    private readonly CancellationTokenSource stopping = new();
    private int port;
    private int disposed;

    public CommandDescriptor Descriptor { get; } = new("rest", [], ["vega.operations.rest.manage"],
        Syntax: "rest status | listusers | destroytokens");

    public void Start(int listenPort, string expectedTokenSha256, string? maintenanceTokenSha256 = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (listener is not null) throw new InvalidOperationException("REST listener already started.");
        if (listenPort is < 1 or > 65535)
            throw new InvalidDataException("REST requires a valid port and SHA-256 token fingerprint.");
        byte[] hash = ParseFingerprint(expectedTokenSha256);
        byte[]? writerHash = maintenanceTokenSha256 is null ? null : ParseFingerprint(maintenanceTokenSha256);
        if (writerHash is not null && (maintenance is null || CryptographicOperations.FixedTimeEquals(hash, writerHash)))
            throw new InvalidDataException("REST maintenance requires a distinct token and an available maintenance service.");

        var next = new HttpListener();
        try
        {
            next.Prefixes.Add($"http://127.0.0.1:{listenPort}/");
            next.Start();
        }
        catch { next.Close(); throw; }

        port = listenPort;
        Volatile.Write(ref tokens, new TokenHashes(hash, writerHash));
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
                var request = context.Request;
                var response = context.Response;
                response.Headers["Cache-Control"] = "no-store";
                Access access = Authenticate(request.Headers["Authorization"]);
                response.KeepAlive = false;
                if (request.RemoteEndPoint is not { Address: var address } || !IPAddress.IsLoopback(address) || access == Access.None)
                {
                    response.StatusCode = 401;
                    response.Headers["WWW-Authenticate"] = "Bearer";
                    continue;
                }
                if (request.Url is not { Query.Length: 0 } url) { response.StatusCode = 400; continue; }
                if (request.HttpMethod == "POST" && url.AbsolutePath == "/v1/world/save")
                {
                    if (access != Access.Maintenance || maintenance is null) { response.StatusCode = 403; continue; }
                    if (request.HasEntityBody) { response.StatusCode = 400; continue; }
                    if (lifecycle.CurrentStage < RuntimeLifecycleStage.NetworkReady || lifecycle.CurrentStage >= RuntimeLifecycleStage.Stopping || !state.Read().IsWorldReady)
                    { response.StatusCode = 503; continue; }
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(30));
                    try
                    {
                        await maintenance.SaveAsync(deadline.Token).ConfigureAwait(false);
                        response.StatusCode = 204;
                    }
                    catch (OperationCanceledException) { response.StatusCode = 504; }
                    catch (Exception) { response.StatusCode = 500; }
                    continue;
                }
                if (request.HttpMethod != "GET") { response.StatusCode = 405; continue; }
                object? payload = url.AbsolutePath switch
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
                        .Select(player => new { id = player.Id.Slot, player.Name, player.IsDead, player.Life, player.MaxLife, player.Team })
                        .Take(255).ToArray(),
                    "/v1/network" => network.Read(),
                    "/v1/metrics" when runtimeMetrics is not null => ReadMetrics(runtimeMetrics),
                    "/v1/scheduler" when queueMetrics is not null => queueMetrics(),
                    _ => null
                };
                if (payload is null) { response.StatusCode = 404; continue; }
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
                response.ContentType = "application/json; charset=utf-8";
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
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
        byte[] input = SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..]));
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
        int count = !active || current is null ? 0 : current.Maintenance is null ? 1 : 2;
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

    private enum Access { None, Read, Maintenance }
    private sealed record TokenHashes(byte[] Reader, byte[]? Maintenance);
}
