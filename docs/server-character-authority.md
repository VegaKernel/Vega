# Server-authoritative character and item security

This document records the security model for SSC/survival worlds. The goal is not to reproduce TShock SSC packet-for-packet. The goal is to make the server authoritative for character state while preserving legitimate Terraria mechanics.

## Authority and storage are separate concerns

Character authority and character storage scope must not be conflated.

- `Client` authority: the client owns character state, suitable for ordinary bring-your-own-character worlds.
- `Server` authority: Vega owns persistent character state and validates every client-authored mutation before it becomes authoritative.

A server-authoritative character can use different scopes:

- account/shared scope for a normal survival server;
- world scope for an isolated survival sandbox;
- copy-on-enter scope for event/sandbox worlds that seed from another character but do not write back to it.

The existing sandbox modes (`Disabled`, `Inherit`, `CopyOnEnter`, `Isolated`) already provide most storage mechanics. Internally, future configuration should make the authority decision explicit instead of inferring it from storage scope.

## Existing SSC state

`TerrariaSandboxSscCharacterAdapter` already captures/restores inventory, armor, dyes, banks, trash, loadouts, life/mana, spawn, appearance and permanent character flags. The revisioned sandbox store prevents stale state from overwriting newer persisted state.

Persistence alone is not anti-cheat. A hacked client can still submit a formally valid `PlayerSlot` update unless the mutation path is authorized.

## Decompiled client/server trust findings

Readable comparison source:

`JonataOliveiraa/Terraria1.4.5@0459d7b61e35f15354a40fb9d1422d84e9bb4f2c`

This source currently exposes protocol-325 behavior and is used for semantic/control-flow analysis only. See `protocol-baseline.md` for the 1.4.5.8 protocol-number caveat.

### PlayerSlot

On a dedicated server `MessageBuffer` rewrites the claimed player id to the sending connection, then constructs an `Item` from client supplied type/stack/prefix and assigns it to the target player slot. That prevents editing another player's slot but does not prove where the item came from.

Therefore range checks such as "known item id" and "stack <= maxStack" are necessary memory-safety checks, not item-origin validation.

### ItemDrop / item index 400

For packet 21/90, item index `400` is the client request path for creating a new world item. The dedicated server calls `Item.NewItem(new EntitySource_Sync(), ...)` using client supplied type, stack, prefix, position and velocity.

It is tempting to treat every index-400 request as "the player dropped an inventory item". That is incorrect. Decompiled client code uses `Item.RequestNewItem` for multiple legitimate mechanics, including:

- manual player drop;
- inventory overflow;
- Torch God reward;
- digestion/item effects;
- paint-scraper/tile-break products;
- extractinator output;
- Lucky Coin effects;
- other client-evaluated vanilla item sources.

The wire packet does not carry the original `IEntitySource`. A secure server cannot recover provenance by guessing from packet 21 alone.

### ProjectileNew

The dedicated server accepts client-authored projectile position, velocity, type, damage, original damage, knockback and AI after checking that the projectile is not a hostile server projectile and that the packed projectile key belongs to the sender. Vanilla then writes those values directly to the authoritative projectile object.

Vega therefore must validate more than finite coordinates.

Current PR #39 hardening now provides:

1. generic payload invariants for finite AI/knockback and non-negative damage/original damage;
2. sender ownership and projectile slot/type bounds;
3. the Cattiva build-bypass guard;
4. a conservative direct-held-item envelope for non-ammo shots, derived from the server's actual `HeldItem`, `GetWeaponDamage()` and `shootSpeed`;
5. deliberate fall-through for secondary/minion/sentry/ammo-transformed projectiles until their creation rules are modeled, avoiding global magic-number false positives.

## Required authoritative inventory model

A server-authoritative world should maintain a revisioned inventory shadow. Client packets are mutation proposals, not state declarations.

A positive inventory delta must consume a short-lived server-issued credit/provenance record. A negative delta must correspond to a recognized operation or a server-authorized debit.

Suggested transaction shape:

```text
InventoryMutation
  player/account
  world
  inventory revision
  source kind
  source entity/slot when applicable
  item type
  prefix
  amount
  issued tick
  expiry tick
```

Authoritative sources include:

- a world item reserved/picked up by the player;
- chest withdrawal/deposit;
- a validated crafting recipe transaction;
- a validated shop purchase/sale transaction;
- server/admin/plugin grants;
- server-owned NPC/boss/event rewards;
- death/drop transactions;
- sandbox transfer/apply operations.

The server commits the mutation, increments the inventory revision, then synchronizes the resulting slots to the client.

## World-item creation policy

For manual dropping in a server-authoritative world:

1. identify the authoritative source slot/item;
2. debit the authoritative inventory first;
3. create the world item server-side;
4. record the world item's server provenance;
5. sync both inventory and world item;
6. reject/re-sync any client packet that attempts to recreate the same debit.

A naked client request to manufacture a world item must never itself establish provenance.

Because packet 21 index 400 represents several legitimate client-side vanilla mechanics, the long-term solution is to migrate those mechanics to explicit server-side validators/handlers rather than applying a blanket "must exist in inventory" rule that would break vanilla gameplay.

## Projectile authority roadmap

The direct-held-item envelope is intentionally the first safe subset. Extend it by source class:

- ammo weapons: side-effect-free server ammo selection and an allowed projectile/damage/speed envelope;
- minions/sentries: validate ownership, summon state and server-known summon damage;
- secondary projectiles: validate against a recently authorized parent projectile/action;
- accessory/on-hit projectiles: validate against server-known equipment/buff state and triggering hit;
- special weapon branches: encode the decompiled vanilla transformation rather than widening a global limit.

High-confidence violations should reject before native mutation, re-sync relevant state and emit a structured security incident. Repeated violations may kick; automatic bans should be reserved for high-confidence exploit evidence.

## Failure policy

Do not immediately ban on a single inventory revision mismatch. Terraria networking, latency and future vanilla changes can create benign disagreements. Preferred response:

1. reject the mutation;
2. restore/resync authoritative state;
3. log the exact claimed versus authoritative delta;
4. increment a session incident counter;
5. kick on repeated/high-confidence manipulation;
6. optionally ban only under explicit operator policy.
