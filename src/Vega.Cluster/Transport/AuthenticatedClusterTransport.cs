using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vega.Cluster.Dispatching;
using Vega.Cluster.Scenarios.Presence;
using Vega.Contracts.Cluster;
using Vega.Contracts.Cluster.Events;

namespace Vega.Cluster.Transport;

/// <summary>Optional bounded peer-to-peer notification transport. The listener is always local; use HTTPS for remote peers.</summary>
public sealed class AuthenticatedClusterTransport : IClusterNotificationSender, IClusterServerDirectory, IDisposable
{
    public const string NotificationPath = "/v1/cluster/notifications";
    private const int MaximumWireBytes = 32 * 1024;
    private const int MaximumPeers = 32;
    private const string SenderHeader = "X-Vega-Cluster-Sender";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };
    private sealed class Peer(ServerId id, Uri endpoint, string sendToken, byte[] receiveTokenHash)
    {
        public ServerId Id { get; } = id;
        public Uri Endpoint { get; } = endpoint;
        public string SendToken { get; } = sendToken;
        public byte[] ReceiveTokenHash { get; } = receiveTokenHash;
    }
    private sealed record WireFrame<T>(Guid MessageId, long Timestamp, string Sender, string Schema,
        int MessageVersion, T Message);

    private readonly Dictionary<ServerId, Peer> peers;
    private readonly ClusterMessageDispatcher dispatcher;
    private readonly ClusterMessageFactory messages;
    private readonly TimeProvider clock;
    private readonly ServerPresenceDirectory presence;
    private readonly ClusterServerRole role;
    private readonly string displayName;
    private readonly HttpListener listener;
    private readonly HttpClient client;
    private readonly SemaphoreSlim inbound = new(8);
    private readonly SemaphoreSlim outbound = new(MaximumPeers);
    private readonly object activeSync = new();
    private readonly HashSet<Task> active = [];
    private readonly CancellationTokenSource shutdown;
    private readonly CancellationToken shutdownToken;
    private readonly Task acceptLoop;
    private int disposed;

    public AuthenticatedClusterTransport(ClusterTransportOptions options, ClusterMessageDispatcher dispatcher,
        Func<string, string?>? resolveSecret = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        clock = timeProvider ?? TimeProvider.System;
        if (!PluginClusterNotification.ValidName(options.ServerId, 96) || options.ListenPort is < 1024 or > 65535 ||
            !Enum.IsDefined(options.Role) ||
            options.Peers is not { Length: > 0 and <= MaximumPeers })
            throw new ArgumentException("Invalid cluster server ID, listen port or peer count.", nameof(options));
        LocalServerId = new ServerId(options.ServerId);
        role = options.Role;
        displayName = options.DisplayName ?? options.ServerId;
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 128 || displayName.Any(char.IsControl))
            throw new ArgumentException("Invalid cluster display name.", nameof(options));
        messages = new(LocalServerId, clock);
        peers = [];
        resolveSecret ??= Environment.GetEnvironmentVariable;
        var tokenOwners = new Dictionary<string, ServerId>(StringComparer.Ordinal);
        foreach (var configured in options.Peers)
        {
            if (configured is null || !PluginClusterNotification.ValidName(configured.ServerId, 96) || !Enum.IsDefined(configured.Role) ||
                !Uri.TryCreate(configured.Endpoint, UriKind.Absolute, out Uri? endpoint) || endpoint.UserInfo.Length != 0 ||
                endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.AbsolutePath != NotificationPath ||
                !(endpoint.Scheme == "https" || (endpoint.Scheme == "http" && IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address))))
                throw new ArgumentException("Cluster peer must have an explicit ID and HTTPS (or literal loopback HTTP) notification endpoint.", nameof(options));
            var id = new ServerId(configured.ServerId);
            if (id == LocalServerId) throw new ArgumentException("A cluster peer cannot be the local server.", nameof(options));
            string send = ReadSecret(configured.SendTokenEnvironmentVariable, resolveSecret);
            byte[] receive = HashToken(ReadSecret(configured.ReceiveTokenEnvironmentVariable, resolveSecret));
            // A peer must never know any other peer's credential in either direction.
            foreach (string hash in new[] { Convert.ToHexString(receive), Convert.ToHexString(HashToken(send)) })
            {
                if (tokenOwners.TryGetValue(hash, out var owner) && owner != id)
                    throw new ArgumentException("Cluster peer credentials must not be shared across peers.", nameof(options));
                tokenOwners[hash] = id;
            }
            if (!peers.TryAdd(id, new(id, endpoint, send, receive))) throw new ArgumentException("Duplicate cluster peer ID.", nameof(options));
        }
        presence = new ServerPresenceDirectory(clock, options.Peers.ToDictionary(peer => new ServerId(peer.ServerId), peer => peer.Role));
        // Only host-owned presence events are admitted in addition to typed plugin notifications.
        dispatcher.RegisterEvent<ServerPresenceAnnounced>(presence);
        dispatcher.RegisterEvent<ServerPresenceWithdrawn>(presence);
        Peers = Array.AsReadOnly(peers.Keys.OrderBy(id => id.Value, StringComparer.Ordinal).ToArray());
        listener = new HttpListener();
        client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
        shutdown = new CancellationTokenSource();
        shutdownToken = shutdown.Token;
        listener.Prefixes.Add($"http://127.0.0.1:{options.ListenPort}/");
        try { listener.Start(); }
        catch { listener.Close(); client.Dispose(); shutdown.Dispose(); throw; }
        acceptLoop = AcceptAsync();
    }

    public bool IsAvailable => Volatile.Read(ref disposed) == 0 && listener.IsListening;
    public ServerId LocalServerId { get; }
    public IReadOnlyList<ServerId> Peers { get; }
    public IReadOnlyList<ClusterServerStatus> SnapshotServers() => IsAvailable ? presence.SnapshotServers() : [];
    public ClusterServerStatus? SelectRoute(ClusterServerRole role) => IsAvailable ? presence.SelectRoute(role) : null;

    /// <summary>Host-only periodic aggregate presence, never writable through the plugin SDK.</summary>
    public ValueTask<IReadOnlyList<ClusterPeerDelivery>> AnnouncePresenceAsync(ClusterPresenceSample sample, CancellationToken token = default)
    {
        var announcement = new ServerPresenceAnnounced(LocalServerId, displayName, sample.Stage,
            sample.OnlinePlayers, clock.GetUtcNow() + TimeSpan.FromSeconds(20))
        {
            Role = role,
            MaximumPlayers = sample.MaximumPlayers,
            IsWorldReady = sample.IsWorldReady
        };
        return BroadcastFrameAsync(Serialize(announcement), token);
    }

    public ValueTask<IReadOnlyList<ClusterPeerDelivery>> WithdrawPresenceAsync(CancellationToken token = default) =>
        BroadcastFrameAsync(Serialize(new ServerPresenceWithdrawn(LocalServerId)), token);

    public ValueTask<ClusterDeliveryResult> SendAsync(ServerId target, PluginClusterNotification notification, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!notification.IsValid) return ValueTask.FromResult(new ClusterDeliveryResult(ClusterDeliveryStatus.Rejected, "Invalid notification."));
        if (!peers.TryGetValue(target, out var peer)) return ValueTask.FromResult(ClusterDeliveryResult.Unavailable("Unknown cluster peer."));
        return SendFrameAsync(peer, Serialize(notification), token);
    }

    public async ValueTask<IReadOnlyList<ClusterPeerDelivery>> BroadcastAsync(PluginClusterNotification notification, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!notification.IsValid) return Array.AsReadOnly(Peers.Select(peer => new ClusterPeerDelivery(peer,
            new ClusterDeliveryResult(ClusterDeliveryStatus.Rejected, "Invalid notification."))).ToArray());
        return await BroadcastFrameAsync(Serialize(notification), token).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<ClusterPeerDelivery>> BroadcastFrameAsync(byte[] frame, CancellationToken token)
    {
        return Array.AsReadOnly(await Task.WhenAll(Peers.Select(async id => new ClusterPeerDelivery(id,
            await SendFrameAsync(peers[id], frame, token).ConfigureAwait(false)))).ConfigureAwait(false));
    }

    private byte[] Serialize<T>(T message) where T : IClusterEvent
    {
        var envelope = messages.Create(message);
        return JsonSerializer.SerializeToUtf8Bytes(new WireFrame<T>(envelope.Metadata.MessageId.Value,
            envelope.Metadata.Timestamp.ToUnixTimeMilliseconds(), LocalServerId.Value, envelope.Metadata.Schema,
            envelope.Metadata.MessageVersion, envelope.Message), Json);
    }

    private async ValueTask<ClusterDeliveryResult> SendFrameAsync(Peer peer, byte[] frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsAvailable || !outbound.Wait(0)) return ClusterDeliveryResult.Unavailable("Cluster transport unavailable or busy.");
        try
        {
            if (frame.Length > MaximumWireBytes) return new(ClusterDeliveryStatus.Rejected, "Notification exceeds wire limit.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdownToken);
            linked.CancelAfter(Deadline);
            using var request = new HttpRequestMessage(HttpMethod.Post, peer.Endpoint);
            request.Headers.TryAddWithoutValidation(SenderHeader, LocalServerId.Value);
            request.Headers.Authorization = new("Bearer", peer.SendToken);
            request.Content = new ByteArrayContent(frame);
            request.Content.Headers.ContentType = new("application/json");
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            return (int)response.StatusCode switch
            {
                204 => ClusterDeliveryResult.Delivered(),
                208 => ClusterDeliveryResult.Duplicate(),
                400 or 401 or 403 or 404 or 413 => new(ClusterDeliveryStatus.Rejected, "Peer rejected the notification."),
                408 or 504 => new(ClusterDeliveryStatus.TimedOut, "Peer notification timed out."),
                503 => ClusterDeliveryResult.Unavailable("Peer notification service is busy or unavailable."),
                _ => new(ClusterDeliveryStatus.Failed, "Peer notification failed.")
            };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return shutdownToken.IsCancellationRequested ? ClusterDeliveryResult.Unavailable() : new(ClusterDeliveryStatus.TimedOut); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ObjectDisposedException)
        { return ClusterDeliveryResult.Unavailable("Peer connection unavailable."); }
        finally { outbound.Release(); }
    }

    private async Task AcceptAsync()
    {
        while (!shutdownToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            if (!inbound.Wait(0)) { Close(context.Response, 503); continue; }
            // Bounded by the admission semaphore. Every admitted context is closed by HandleAsync.
            Task work = HandleAsync(context);
            lock (activeSync) active.Add(work);
            _ = work.ContinueWith(done => { lock (activeSync) active.Remove(done); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        int status = 500;
        try
        {
            HttpListenerRequest request = context.Request;
            if (request.RemoteEndPoint is not { Address: { } source } || !IPAddress.IsLoopback(source) ||
                request.Headers[SenderHeader] is not { } senderId || !PluginClusterNotification.ValidName(senderId, 96) ||
                !peers.TryGetValue(new ServerId(senderId), out var peer) || !Authenticate(request.Headers["Authorization"], peer))
            { status = 401; return; }
            if (request.HttpMethod != "POST" || request.Url is not { AbsolutePath: NotificationPath, Query.Length: 0 })
            { status = 404; return; }
            if (request.ContentLength64 is <= 0 or > MaximumWireBytes) { status = 413; return; }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            linked.CancelAfter(Deadline);
            byte[] body = new byte[(int)request.ContentLength64];
            // A peer that stalls mid-body cannot retain a worker indefinitely, even if the stream ignores cancellation.
            Task read = request.InputStream.ReadExactlyAsync(body, linked.Token).AsTask();
            try { await read.WaitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                status = 504;
                Close(context.Response, status);
                try { request.InputStream.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException or HttpListenerException) { }
                try { await read.ConfigureAwait(false); } catch { /* Closed the connection; do not abandon an admitted read. */ }
                return;
            }
            WireFrame<JsonElement>? wire = JsonSerializer.Deserialize<WireFrame<JsonElement>>(body, Json);
            if (wire is null || wire.Sender != senderId || wire.MessageId == Guid.Empty || wire.Message.ValueKind != JsonValueKind.Object ||
                wire.MessageVersion != 1)
            { status = 400; return; }
            DateTimeOffset timestamp;
            try { timestamp = DateTimeOffset.FromUnixTimeMilliseconds(wire.Timestamp); }
            catch (ArgumentOutOfRangeException) { status = 400; return; }
            if ((clock.GetUtcNow() - timestamp).Duration() > MaximumClockSkew) { status = 400; return; }
            var metadata = new ClusterMessageMetadata(new(wire.MessageId), null, peer.Id, timestamp, wire.Schema, wire.MessageVersion);
            // The plugin registry runs handlers through its own tracked, cancellation-aware activation scope. A
            // non-cooperative callback still holds one inbound lane until it finishes; do not admit unbounded work.
            Task<ClusterDeliveryResult> dispatch = DispatchFrameAsync(metadata, wire.Message, linked.Token).AsTask();
            try
            {
                ClusterDeliveryResult delivery = await dispatch.WaitAsync(linked.Token).ConfigureAwait(false);
                status = delivery.Status switch
                {
                    ClusterDeliveryStatus.Delivered => 204,
                    ClusterDeliveryStatus.Duplicate => 208,
                    ClusterDeliveryStatus.Rejected => 400,
                    ClusterDeliveryStatus.Unavailable => 503,
                    ClusterDeliveryStatus.TimedOut => 504,
                    _ => 500
                };
            }
            catch (OperationCanceledException)
            {
                status = 504;
                Close(context.Response, status);
                try { await dispatch.ConfigureAwait(false); } catch { /* Already reported timeout. */ }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException or NotSupportedException) { status = 400; }
        catch (OperationCanceledException) { status = 504; }
        catch (Exception) { status = 500; }
        finally { Close(context.Response, status); inbound.Release(); }
    }

    private ValueTask<ClusterDeliveryResult> DispatchFrameAsync(ClusterMessageMetadata metadata, JsonElement payload, CancellationToken token)
    {
        // Explicit wire family allowlist. Never deserialize remote commands, requests, or arbitrary event names.
        switch (metadata.Schema)
        {
            case "vega.cluster.plugin.notification":
                if (payload.Deserialize<PluginClusterNotification>(Json) is { IsValid: true } plugin)
                    return dispatcher.DispatchEventAsync(new ClusterEnvelope<PluginClusterNotification>(metadata, plugin), token);
                break;
            case "vega.cluster.presence.announced":
                if (payload.Deserialize<ServerPresenceAnnounced>(Json) is { } announced && announced.ServerId == metadata.SenderServerId)
                    return dispatcher.DispatchEventAsync(new ClusterEnvelope<ServerPresenceAnnounced>(metadata, announced), token);
                break;
            case "vega.cluster.presence.withdrawn":
                if (payload.Deserialize<ServerPresenceWithdrawn>(Json) is { } withdrawn && withdrawn.ServerId == metadata.SenderServerId)
                    return dispatcher.DispatchEventAsync(new ClusterEnvelope<ServerPresenceWithdrawn>(metadata, withdrawn), token);
                break;
        }
        return ValueTask.FromResult(new ClusterDeliveryResult(ClusterDeliveryStatus.Rejected, "Invalid notification family or payload."));
    }

    private static void Close(HttpListenerResponse response, int status)
    {
        try { response.StatusCode = status; response.ContentLength64 = 0; response.KeepAlive = false; response.Close(); }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException or IOException) { }
    }

    private static bool Authenticate(string? header, Peer peer)
    {
        const string bearer = "Bearer ";
        if (header is null || !header.StartsWith(bearer, StringComparison.Ordinal) || header.Length > 256) return false;
        string token = header[bearer.Length..];
        return ValidToken(token) && CryptographicOperations.FixedTimeEquals(HashToken(token), peer.ReceiveTokenHash);
    }

    private static string ReadSecret(string name, Func<string, string?> resolver)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128) throw new ArgumentException("Invalid cluster token environment-variable name.");
        string? token = resolver(name);
        if (!ValidToken(token)) throw new ArgumentException("Cluster peer token must be a base64url-style secret of 32–128 characters.");
        return token!;
    }

    private static bool ValidToken(string? token) => token is { Length: >= 32 and <= 128 } &&
        token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { shutdown.Cancel(); } catch (AggregateException) { /* A hostile callback must not prevent listener retirement. */ }
        listener.Close();
        client.Dispose();
        try { acceptLoop.Wait(Deadline); } catch (AggregateException) { }
        Task[] pending;
        lock (activeSync) pending = active.ToArray();
        try { Task.WhenAll(pending).Wait(Deadline); } catch (AggregateException) { }
        shutdown.Dispose();
    }
}
