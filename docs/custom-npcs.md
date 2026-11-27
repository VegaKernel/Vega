# Custom NPC actors and shops

Vega adopts the actor-lifetime, movement-intent and immutable-catalog design from
TerraRuntime's `INpcActorOperations`, `NpcActorControlContracts`,
`NpcShopContracts` and `NpcShopPurchaseContracts`. It does **not** adopt the
alternate NPC simulation or inventory authority. The native adapter spawns a
Terraria NPC and owns only the particular actor generation it created.

## Plugin API

```csharp
var shop = new NpcShopCatalog([
    new("torches", 8, 5, BaseValue: 100, CoinPrice: 100),
    new("rope", 965, 20, BaseValue: 250, CoinPrice: 250, BuyOnce: true)
]);
var creation = await context.World.Npcs.CreateActorAsync(
    new NpcActorDefinition(17, "Vega Merchant", Shop: shop),
    new PlayerPosition(800, 800), cancellationToken);
if (creation.Actor is { } merchant)
{
    await merchant.SetIntentAsync(new NpcActorIntent(NpcActorIntentKind.Stop), cancellationToken);
    // Store the lease in the plugin; it is automatically retired on unload.
}
```

`NpcActorDefinition` is reusable; each created lease has a distinct GUID,
native slot, object identity and native generation. It can be renamed,
teleported, assigned an immutable replacement catalog or given a movement
intent (`Vanilla`, `Stop`, horizontal `MoveTo`, or exact-session
`FollowPlayer`). Controlled movement replaces only the native AI call;
Terraria retains gravity, collision and motion integration. It is not a
pathfinding or vertical-navigation implementation. Custom actors use a
vanilla presentation; new sprites, client-local dialogue/buttons and arbitrary
client-side UI types require a compatible client extension. An unmodded
Merchant (`BaseType = 17`) provides the usual native shop button.

Actor leases are tracked by one plugin-owned aggregate. Disposal retires the
exact actor, never a reused NPC slot. The tick sweep is attached only while
actors exist, also handling deferred retirement if the game-thread queue is
full. Actors are retired during native world shutdown before the save. Plugins
should recreate their actors from their own configuration on startup.
Creation requires the authoritative game thread to be identified. Plugins
initializing before Terraria starts can schedule creation with `Tasks.Post`
for execution once the world loop begins; the direct API reports unavailable
rather than blocking startup.

## Conversation, presentation, and purchase

`WorldEvents.OnNpcActorInteracted` reports a confirmed native conversation for
the exact actor and connected player session, with a live player no more than
256 pixels from the NPC. It does not report a shop open.
`PresentShopAsync(player)` checks this conversation, proximity and socket
identity, then sends the bounded catalog through the native `ShopOverride`
packet. Empty slots clear old UI stock. Catalogs are checked against actual
native item defaults on creation and replacement: each slot must have a valid
item/prefix combination and a stack no larger than that item's maximum. An
invalid replacement leaves the active catalog and revision unchanged. The
serialized 40-slot presentation is cached per actor revision, shared on
repeated sends, and invalidated when the catalog changes or stock is consumed;
older in-flight buffers remain immutable. The result `Sent` is not
acknowledgement that the player opened a shop window.

The pinned Terraria 1.4.6.1 `Main.OpenShop` implementation calls client-local
`SetNPCShopIndex` and `Chest.SetupShop`; it does not send an explicit shop-open
request to the server. Packet 104 changes items in the client's currently open
shop. Consequently the server cannot promise automatic UI opening or timing
the override on stock clients. `BaseValue` is the native presentation item
value, which may be affected by native shopping modifiers. Stock client UI
validation remains open.

For explicit server purchases, an offer opts in with `CoinPrice`. Multiplayer
server purchases require native authoritative SSC: the stock client ignores
`SyncEquipment` updates to its own unlocked inventory otherwise. The operation
returns `InventoryAuthorityRequired` before changing coins, items, catalog
revision or stock in that mode. A plugin can
provide a command or its own interaction action and call
`merchant.PurchaseAsync(player, offerId, snapshot.ShopRevision, token)`.
The transaction validates the same current session and conversation, actor and
catalog revision, funds and inventory capacity, prepares every replacement
before mutation, rechecks the input slots and applies them together on the
world thread. It publishes `WorldEvents.OnNpcActorPurchased` only after commit.
Failed preparations leave the inventory untouched. Every changed native slot
is sent directly to the purchasing client, then broadcast to other clients.
`BuyOnce` in this lane
means one globally available offer per actor catalog, with a new revision on
consumption. The purchase lane uses vanilla coins from the main inventory and
coin slots; banks, custom currencies, persistent stock and transaction retry
IDs are not implemented. It is separate from the stock client UI purchase:
do not automatically debit coins again merely because a client changed its
inventory. The native snapshot/commit checks are not a claim of full
authoritative client inventory custody without the existing SSC qualification.

The sample plugin includes `samplemerchant create <pixelX> <pixelY>`, `info`,
`move <pixelX>`, `remove`, `present` and `buy <offer>` (permission
`vega.samples.world`). Its presentation and explicit purchase commands require
the interacting player, not the server console. Native shop flows require a
real stock-client qualification; tests use an isolated normal conversation
and exact-session fixture and assert atomic coin/item commit, sold-out
revisions, and immutable cached catalog presentation. A separate headless
fixture runs the pinned native `MessageBuffer.GetData` client-mode decoder on
the actual generated packet stream. It verifies that closed shops ignore the
packets, an open shop applies item type, stack, prefix, base value and BuyOnce,
and sold-out/unused slots clear old stock. This qualifies the native decoder
only, not the graphical client's shop-opening timing, input, or purchase UI.

An isolated native Windows server smoke loaded the sample plugin, created a
Merchant at (800,800), exported its canonical region and verified the native
NPC's name/type/location, unloaded the collectible plugin and exported again
to verify that the actor was retired. This does not establish stock-client
shop-window or movement presentation behavior. Raw local evidence is in
`artifacts/custom-npc-native-retry-*`.
