# Inter-plugin services and persistent documents

Two ordinary SDK capabilities are designed for ports of WalletService, NPCShop, RequestManager, session statistics and other TShock plugins that previously depended on a shared in-process singleton or direct access to the server's SQL connection.

## Versioned services

Use `plugin.Services.Register` to implement a named request/reply endpoint. The provider identity is taken from the plugin's module descriptor; consumers specify it explicitly. The wire contract is JSON with an explicit integer version. The provider and consumer can use independent C# DTO types with the same JSON shape, so their collectible load contexts do not need to share a custom DLL.

```csharp
using System.Text.Json.Serialization;
using Vega.PluginSdk;

public record BalanceRequest(int AccountId);
public record BalanceReply(long Amount);

[JsonSerializable(typeof(BalanceRequest))]
[JsonSerializable(typeof(BalanceReply))]
internal partial class WalletJson : JsonSerializerContext;

// Provider (descriptor name: WalletService), inside Configure:
plugin.Services.Register("balance", 1, WalletJson.Default.BalanceRequest,
    WalletJson.Default.BalanceReply, async (request, token) =>
    {
        var record = await plugin.Data.ReadAsync("wallets", request.AccountId.ToString(),
            WalletJson.Default.BalanceReply, token);
        return record?.Value ?? new BalanceReply(0);
    });

// Consumer (e.g. NPCShop), inside a command or RunIo callback:
var result = await plugin.Services.CallAsync(new("WalletService", "balance", 1),
    new BalanceRequest(accountId), WalletJson.Default.BalanceRequest,
    WalletJson.Default.BalanceReply, token);
if (!result.Succeeded) { /* provider unavailable or call failed */ }
```

- `Snapshot()` lists active addresses only. Callers resolve the current activation on each call, not a stale delegate.
- Do not assume a provider is started ahead of the consumer. A missing service/version, a retired provider or a full provider work queue returns `Unavailable`. A provider handler fault or incompatible reply returns `Failed`; faults are recorded against the provider. Caller cancellation is propagated.
- Source-generated `JsonTypeInfo<T>` is passed explicitly, avoiding reflection caches that can hold collectible plugin types. Use an explicit wire version for incompatible request/response changes. No exception, provider object or delegate crosses into the consumer.
- Each request and response is limited to 64 KiB of UTF-8 JSON. Calls run in host-tracked background work and are canceled and joined during activation retirement. Handlers must honor their token and use the existing dispatched world/player APIs instead of mutating native state directly. Endpoints are process-local; cluster-wide transport is not implied.
- Other installed plugins may call a published endpoint. A provider must enforce its own business rules and authorization semantics. This is typed capability integration, not an authorization grant or a sandbox for arbitrary plugin code.

## Persistent documents

`plugin.Storage` remains the file-based JSON mechanism. `plugin.Data` is backed by the server's configured SQLite, MySQL or PostgreSQL database. It is automatically isolated by the plugin's descriptor name. There is no raw SQL or database connection in the ordinary SDK. Changing the descriptor name changes the owning namespace; keep it stable across releases. Data is shared with the same plugin in processes connected to the same Vega database; include world identity in the collection/key if the records should be world-local.

Documents support typed JSON, revision compare-and-swap, paginated collection enumeration and atomic batches (up to 32 mutations across collections). This supports atomic transfers, for example:

```csharp
var from = await plugin.Data.ReadAsync("wallets", "account-1", WalletJson.Default.BalanceReply, token);
var to = await plugin.Data.ReadAsync("wallets", "account-2", WalletJson.Default.BalanceReply, token);
if (from?.Value is not { } source || to?.Value is not { } destination || source.Amount < 10)
    return;

bool transferred = await plugin.Data.CommitAsync([
    PluginDataExtensions.Set("wallets", "account-1", from.Revision,
        new BalanceReply(source.Amount - 10), WalletJson.Default.BalanceReply),
    PluginDataExtensions.Set("wallets", "account-2", to.Revision,
        new BalanceReply(checked(destination.Amount + 10)), WalletJson.Default.BalanceReply)
], token);
// false = concurrent conflict; read both again and recheck the policy before retrying.
```

- Revision `0` means create only if absent; positive revisions require an exact match. `PluginDataExtensions.Delete` removes a document only at the given revision. Deleting/recreating never reuses the previous revision. Tombstones retain only key and revision; payloads are removed.
- Failed revision checks roll back the entire batch. Database/validation errors are exceptions, not conflicts. No distributed transaction with game-world mutations is implied.
- Collection and document names accept case-insensitive ASCII letters/digits plus `.`, `_`, `-`, `:` (80/160 characters respectively). Names are normalized to lowercase. Maximum document size is 48 KiB of UTF-8 JSON. `ListAsync` yields at most 100 records per page, with an exclusive `afterKey` cursor; continue with the last key from the prior page.
- The host admits up to 64 simultaneous service/database operations per activation. Work is asynchronous and tied to activation/caller cancellation; a full local queue raises `InvalidOperationException`, allowing a caller to apply backpressure. Use `RunIo` or an asynchronous command callback, not synchronous waits on the game thread.
- Schema migration v5 creates `vega_plugin_documents` in the selected database. The records participate in ordinary database backup/restore. Legacy plugin schemas are not imported automatically; a port must explicitly map its old records into documents (or retain its own independently managed database).
