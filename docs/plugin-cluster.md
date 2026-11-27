# Plugin cluster notifications

Vega can exchange typed plugin notifications between independently running servers. This transport is optional, explicitly configured, and separate from Terraria player routing and the local administrative REST API. It does not expose remote commands, arbitrary requests, account mutation, bans or whitelist operations. Existing persistent ban/whitelist authority remains unchanged. Sandbox child processes do not inherit the primary process's cluster listener or identity.

## Server configuration

Create `config/cluster.json` in each participating server installation. Absence of this file keeps standalone behavior. Give each server a distinct, stable ID and local listener port. For example, node `lobby` on one machine can point at node `survival`:

```json
{
  "serverId": "lobby",
  "listenPort": 8765,
  "role": "lobby",
  "displayName": "Lobby",
  "peers": [
    {
      "serverId": "survival",
      "role": "game",
      "endpoint": "https://survival.example.net/v1/cluster/notifications",
      "sendTokenEnvironmentVariable": "VEGA_LOBBY_TO_SURVIVAL",
      "receiveTokenEnvironmentVariable": "VEGA_SURVIVAL_TO_LOBBY"
    }
  ]
}
```

On `survival`, configure its local role as `game` and `lobby` as a peer with role `lobby` and the inverse environment-variable roles. Supply high-entropy base64url-style secrets (32–128 characters) as process environment variables; do not put raw tokens in configuration files. Each peer pair must use secrets distinct from every other pair. Distinct secrets per direction are recommended. The recipient checks the peer's configured token and binds the notification sender to that authenticated peer.

Roles are optional for existing configurations (`unspecified` is the default). A status observation becomes eligible for routing only if the sender's advertised role matches the recipient's explicit peer configuration. No peer can enable itself as a routing destination by merely announcing a different role.

The listener binds only to `127.0.0.1`. For two independent deployments on one trusted host, use literal loopback HTTP endpoints with different ports. For separate machines, route the exact notification path through an operator-managed HTTPS reverse proxy to the local listener, with ordinary valid TLS certificates; the proxy must preserve headers and `Content-Length` and must not log authorization tokens. Vega rejects non-loopback HTTP endpoints and does not follow redirects. Clients never supply raw URLs or tokens through the plugin facade.

Configuration limits: at most 32 explicit peers, 16 KiB typed payload, 32 KiB outer wire body, eight concurrent inbound operations, 32 concurrent outbound operations and a five-second request deadline. Remote payload and metadata are verified before dispatch. Timestamps outside a one-minute clock-skew window are rejected, so synchronize clocks. Duplicate IDs are bounded and sender-scoped. Startup fails if an explicitly configured listener cannot bind or a peer configuration is invalid.

## Plugin API

Each server loads the same logical plugin name. Declare the protocol DTO in the plugin and use a `JsonSerializerContext` or compatible `JsonTypeInfo<T>`. For example:

```csharp
public sealed record LobbyStatus(string DisplayName, int Players);

[JsonSerializable(typeof(LobbyStatus))]
internal partial class LobbyJsonContext : JsonSerializerContext { }

// Owned by the plugin activation; callbacks may overlap.
var cache = new ConcurrentDictionary<ServerId, LobbyStatus>();

// During activation:
if (plugin.Cluster.IsAvailable)
{
    plugin.Cluster.Subscribe("status", 1, LobbyJsonContext.Default.LobbyStatus,
        (message, token) =>
        {
            // message.Sender is assigned by the authenticated transport, not the DTO.
            // This callback runs outside the game thread. Native state changes need the
            // existing game-thread-aware SDK and current session validation.
            cache[message.Sender] = message.Payload;
            return ValueTask.CompletedTask;
        });
}

// Send or broadcast an immutable snapshot with the same topic/version:
var result = await plugin.Cluster.SendAsync(new ServerId("survival"), "status", 1,
    new LobbyStatus("Lobby", 20), LobbyJsonContext.Default.LobbyStatus, cancellationToken);

var receipts = await plugin.Cluster.BroadcastAsync("status", 1,
    new LobbyStatus("Lobby", 20), LobbyJsonContext.Default.LobbyStatus, cancellationToken);
```

`LocalServerId` and `Peers` expose only configured IDs, not addresses, secrets or a liveness guarantee. A topic/version is scoped to the plugin's declared name (case-insensitive); it is not a global route. The receiving handler and DTO decoder are registered in the current activation and retired automatically, with pending operations tracked and cancellation propagated. Callback failures count against the receiving plugin's fault boundary, not the sender. The SDK never executes packet data as commands.

## Server discovery and advisory routing

The runtime publishes an aggregate presence heartbeat to configured peers every five seconds, with a 20-second lease. Status contains the server's declared display name, lifecycle stage, world readiness, number of connected players and configured maximum. The heartbeat is host-owned, and the typed plugin event namespace cannot update it. Withdrawals on clean shutdown and bounded expiry remove unavailable peers; delayed announcements cannot override a newer withdrawal. No account identifiers, player lists, actual network endpoints, or credentials appear in the presence API.

```csharp
// Read the latest authenticated observations. Configured peers that have not
// reported recently are absent from this snapshot.
foreach (var server in plugin.Cluster.SnapshotServers())
    plugin.Log.Info($"{server.DisplayName}: {server.OnlinePlayers}/{server.MaximumPlayers} ({server.Role})");

// The lowest observed utilization among ready Game peers with free capacity.
// Equal utilization is ordered by player count and stable server ID.
var suggestion = plugin.Cluster.SelectRoute(ClusterServerRole.Game);
if (suggestion is not null)
    plugin.Log.Info($"Suggested game server: {suggestion.ServerId}");
```

`SelectRoute` is advisory. A candidate must have an explicitly confirmed role, a ready world, a network-ready lifecycle state (not stopping), a positive known maximum and observed spare capacity. The result does not allocate a slot, authorize a user, grant admission, disconnect a source session or initiate a transfer. It is useful for lobby menus and route decisions; the application must re-check availability at the actual admission boundary.

The existing sandbox transfer admission and SSC coordination remain local to the managing deployment. Independent deployments cannot safely consume each other's local admission tickets or assume the same account/SSC ownership, ban state or authorization. Cross-deployment player handoff still requires an authenticated destination reservation and a shared or transactional identity/character handoff protocol; this transport deliberately does not enable remote player-moving commands.

## Delivery semantics

This is a bounded, direct event transport, not a durable cluster queue or automatic peer configuration service. A successful result means the receiving dispatcher acknowledged the callback; an unknown or retired subscription is not reported as successful. Duplicate acknowledgement means that the bounded receiver deduplication cache has already accepted that message ID. Broadcast returns a receipt for each configured peer and excludes the local server. There is no automatic retry after a timeout or unavailable result, and an admitted callback may have completed just before a timeout. For operations that require durable exactly-once business effects, carry an application operation ID in your DTO and use the durable data SDK's compare-and-set transactions. Use the host presence directory for server observations, rather than treating the configured peer list as an online list.

The original `InMemoryClusterBus` remains useful for isolation and scenario tests. This optional transport intentionally makes only typed plugin notifications and host presence announcement/withdrawal available across processes; existing experimental request/command/transfer scenarios are not remotely enabled by this configuration.
