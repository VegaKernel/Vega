# Persistent custom item instances

Vega exposes two distinct item abstractions:

- `plugin.Players.Actions.CustomItems` registers native recipes and delivers physical Terraria items. Native world-item stats are transient.
- `plugin.Players.Actions.CustomItems.Instances` is a durable **virtual item ownership ledger**. It stores immutable, versioned recipe snapshots and an account owner, independently from the native inventory.

Stock inventory, SSC, trade, chests and reconnect do not carry an authoritative unique item ID or arbitrary stats. A virtual instance is therefore not a physical item and does not prove possession of a physical item. Issuing, transferring, finding or revoking one never creates, modifies or removes Terraria inventory/world items. There is no automatic materialization or automatic trade/SSC binding. Plugin code must not interpret two physical items of the same type/prefix as the same instance.

## Issue and read

The ledger is available on a host with the persistent document store, an account session service and a hosted world. Check `Instances.IsAvailable`; recipe registration independently requires native recipe support. Register a single-item recipe once during activation:

```csharp
var items = plugin.Players.Actions.CustomItems;
if (items.IsAvailable && items.Instances.IsAvailable)
{
    items.Register(new CustomItemRecipe("quest-sword", 24)
    {
        WorldStats = new CustomWorldItemStats { Damage = 45, UseTime = 17 }
    });
}
```

Within your authorized gameplay flow, retain a stable, nonempty GUID for each logical award (including retries):

```csharp
var ledger = plugin.Players.Actions.CustomItems.Instances;
var issued = await ledger.IssueAsync(player, awardId, "quest-sword", 1, cancellationToken);
if (issued.Succeeded)
{
    var owned = await ledger.FindOwnedAsync(player, awardId, cancellationToken);
    // Use the immutable virtual definition in your own plugin-managed quest or economy.
}
```

A definition must have `Stack == 1`. The numeric definition version is controlled by the plugin. An existing record cannot be rewritten by later registering a modified recipe or passing a different version. The snapshot is kept even if the recipe is no longer registered after reload. Stats in the virtual definition are descriptive plugin data; the ledger does not apply them to combat. Repeating a successful issue with the same ID, owner and definition returns `AlreadyIssued`; another owner or definition returns `Conflict`. Following a transfer, an issue retry for the original owner returns `Conflict` instead of creating a replacement. A retry after a recipe is removed returns `DefinitionNotFound`; `FindAsync` still reads the persisted record.

The owner is identified by account ID **and account registration generation**, not a name, player slot or IP address. Accounts lacking registration metadata cannot acquire virtual instances. `FindOwnedAsync` verifies the current exact world/session/authentication tenure, including after an asynchronous read. Plugin-wide `FindAsync` and bounded `ListAsync(afterId, limit)` are administrative reads; do not expose them directly to unauthenticated players. The list is ordered by canonical GUID string and includes permanently revoked records.

## Transfer and retire

```csharp
var owned = await ledger.FindOwnedAsync(sender, awardId, cancellationToken);
if (owned is not null)
{
    var result = await ledger.TransferAsync(awardId, owned.Revision, sender, recipient, cancellationToken);
    // Check result.Status before reporting success. A concurrent writer can win.
}
```

The transfer changes the single owner in one database compare-and-swap transaction. Both players must be authenticated in the same hosted world; neither world switches, reconnects, account replacement nor authentication tenure replacement before submission can silently redirect ownership. Plugins authorize their own trade/award policy; the ledger does not implement player consent, payment, or multi-instance barter. `RevokeAsync(id, revision)` is an administrative plugin operation and does not require the account to be online. A revoked GUID cannot be issued again. Conflicting writes must reread the record before retrying.

Authentication is rechecked before the database mutation; authentication revocation and a database commit are not one distributed transaction. If a logout or plugin retirement races a durable commit, reconcile by querying the same GUID and revision after reconnect/reload. Never retry with a fresh GUID after an uncertain issue. No native gameplay action occurs in such a race.

## Storage and lifetime

Records are scoped to the plugin's stable descriptor name and stored using the existing database document service (SQLite, MySQL or PostgreSQL), including across server restarts and plugin reloads. No extra migration or provider-specific schema is required. The reserved `vega.custom-item-instances` collection cannot be modified or read through general `plugin.Data`; use the typed API. Revoked records are retained deliberately to prevent accidental resurrection. Corrupt or unsupported records fail closed rather than replacing ownership. Unavailable or retired activations cannot start further mutations.

On account deletion, the historical record does not automatically transfer to another account, even one reusing its numeric ID. Plugins can administratively revoke these orphan records.
