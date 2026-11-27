# Plugin REST operations

Plugins can expose typed, host-owned query/action endpoints inside the existing loopback operator REST service. No extra listener is created. Both read and action routes require the **administrator** bearer token (`VEGA_REST_ADMIN_TOKEN_SHA256`); the read and maintenance tokens do not grant plugin access. The existing loopback binding, authentication, world-readiness gate and token revocation remain in force.

## Register operations

For a plugin descriptor named `MyPlugin`:

```csharp
using System.Text.Json.Serialization;
using Vega.Contracts.Operations;
using Vega.PluginSdk;

public record StatisticsReply(long Sessions);
public record ResetRequest(long ExpectedRevision);

[JsonSerializable(typeof(StatisticsReply))]
[JsonSerializable(typeof(ResetRequest))]
internal partial class OperationsJson : JsonSerializerContext;

// During Configure:
if (plugin.Rest.IsAvailable)
{
    plugin.Rest.RegisterQuery("statistics", OperationsJson.Default.StatisticsReply,
        async token => new(PluginRestStatus.Success, await ReadStatisticsAsync(token)));
    plugin.Rest.RegisterAction("reset-statistics", OperationsJson.Default.ResetRequest,
        OperationsJson.Default.StatisticsReply, async (request, token) =>
        {
            var result = await TryResetStatisticsAsync(request.ExpectedRevision, token);
            return result is null ? new(PluginRestStatus.Conflict) : new(PluginRestStatus.Success, result);
        });
}
```

With the server's standard local REST listener configured, the operator calls:

- `GET /v1/plugins/MyPlugin/statistics` with no body or URL query string.
- `POST /v1/plugins/MyPlugin/reset-statistics` with `Content-Type: application/json` and a fixed-length JSON object, for example `{"ExpectedRevision":12}`.

The path includes the plugin's own descriptor name, never a caller-supplied owner. Descriptor and operation names are case-insensitive ASCII letters, digits, `.`, `_`, `-`, up to 96 characters; a name cannot be `.` or `..`. Routes cannot override native Vega endpoints, another plugin's namespace or the authentication policy. The same path may have a separate GET and POST operation. A duplicate registration for the same method and operation is rejected.

| Typed status | HTTP |
| --- | --- |
| `Success` | 200 with typed JSON |
| `NoContent` | 204 |
| `BadRequest` | 400 |
| `NotFound` | 404 |
| `Conflict` | 409 |
| `Unavailable` | 503 |
| `Failed` | 500 |

## Boundaries and lifecycle

- The ordinary SDK receives only deserialized request DTOs and a cancellation token. It supplies typed reply DTOs, not HTTP context, authentication tokens, headers, sockets, arbitrary response headers or server command impersonation.
- Request JSON is limited to 4 KiB, depth 16 and a single object without duplicate properties. Query parameters and non-JSON action bodies are rejected. Response JSON is limited to 64 KiB. Handler exceptions or over-limit replies return a generic failure and are recorded against the plugin fault guard.
- Callback execution happens outside the game thread; use existing SDK dispatch APIs to manipulate game state. REST waits at most five seconds for body, operation and response. Cancellation and unload are host-managed. A timed-out callback that ignores cancellation is still tracked by the owning activation and must finish before that activation can be unloaded. A timeout is **not evidence** that a durable action was rolled back; use revisions and idempotency IDs on mutations.
- Routes are bound to activation lifetime, detached after draining, and may be registered afresh by hot reload. Stopping the plugin makes its endpoints unavailable; removing it leaves 404. A retired plugin cannot retain an independently listening endpoint.
- `IsAvailable` indicates the host supports route registration. It does not enable the listener or create credentials. The listener continues to be opt-in via Vega's existing REST environment configuration, and is not started by managed sandbox backends.
- The plugin is responsible for its business validation and data minimization. Use `RegisterQuery` for observation rather than state mutation, and `RegisterAction` for changes requiring explicit validation. Audit records contain route and status, not request contents or the bearer token.
