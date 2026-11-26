using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.Localization;
using TerrariaRuntime;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Terraria.Hooking;
using Vega.Terraria.Networking;
using Vega.Terraria.Players;

namespace Vega.Terraria.Worlds;

/// <summary>Owns plugin actors while leaving normal Terraria NPC state and motion integration native.</summary>
public sealed class TerrariaNpcActorService(IGameThreadDispatcher dispatcher, WorldId worldId,
    TerrariaPlayerBoundary? players, IWorldGameplayEvents events, IOperationsLogger logger) : IWorldNpcActorService, IDisposable
{
    private readonly WorldId ownedWorld = worldId;
    private readonly Actor?[] bySlot = new Actor[global::Terraria.Main.maxNPCs];
    private int count;
    private bool attached;
    private bool disposed;
    private int[]? coinStackLimits;
    internal Func<int, int, int, int> SpawnNative { get; init; } = static (x, y, type) =>
        global::Terraria.NPC.NewNPC(new EntitySource_DebugCommand(), x, y, type);

    public ValueTask<NpcActorCreationResult> CreateAsync(WorldId requestedWorld, NpcActorDefinition definition, PlayerPosition position,
        CancellationToken cancellationToken = default) => Dispatch(() => Create(requestedWorld, definition, position),
            new NpcActorCreationResult(NpcOperationStatus.DispatcherUnavailable), cancellationToken);

    private NpcActorCreationResult Create(WorldId requestedWorld, NpcActorDefinition definition, PlayerPosition position)
    {
        dispatcher.AssertGameThread();
        if (disposed || requestedWorld != ownedWorld) return new(NpcOperationStatus.DispatcherUnavailable);
        if (definition is null || definition.BaseType <= 0 || definition.BaseType >= NPCID.Count ||
            !ValidName(definition.Name) || !ValidPosition(position) || !ValidShop(definition.Shop))
            return new(NpcOperationStatus.Invalid, ErrorCode: "npc.actor_invalid_definition");

        Span<bool> occupied = stackalloc bool[bySlot.Length];
        for (int i = 0; i < Math.Min(bySlot.Length, global::Terraria.Main.npc.Length); i++)
            occupied[i] = global::Terraria.Main.npc[i] is { active: true };
        int slot = SpawnNative((int)MathF.Round(position.X), (int)MathF.Round(position.Y), definition.BaseType);
        if ((uint)slot >= (uint)Math.Min(bySlot.Length, global::Terraria.Main.npc.Length) ||
            occupied[slot] || global::Terraria.Main.npc[slot] is not { active: true } npc)
            return new(NpcOperationStatus.Failed, ErrorCode: "npc.actor_spawn_failed");
        if (bySlot[slot] is { } previous) Remove(previous, despawn: false);
        if (npc.type != definition.BaseType)
        {
            npc.active = false;
            Sync(npc, slot, name: false);
            return new(NpcOperationStatus.Failed, ErrorCode: "npc.actor_spawn_type_changed");
        }
        if (definition.Shop is not null && !npc.townNPC)
        {
            npc.active = false;
            Sync(npc, slot, name: false);
            return new(NpcOperationStatus.Invalid, ErrorCode: "npc.actor_shop_requires_town_npc");
        }

        npc.position = new(position.X, position.Y);
        npc.GivenName = definition.Name;
        npc.friendly = definition.Friendly;
        npc.immortal = definition.Immortal;
        npc.dontTakeDamage = definition.Immortal;
        var actor = new Actor(this, npc, slot, definition);
        bySlot[slot] = actor;
        count++;
        Attach();
        Sync(npc, slot, name: true);
        return new(NpcOperationStatus.Applied, actor);
    }

    private static bool ValidShop(NpcShopCatalog? shop) => shop is null ||
        shop.Offers.All(offer => offer.ItemType < ItemID.Count && offer.Prefix < PrefixID.Count);
    private static bool ValidName(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 200 && !name.Any(char.IsControl);
    private static bool ValidPosition(PlayerPosition position) => float.IsFinite(position.X) && float.IsFinite(position.Y) &&
        position.X >= 0 && position.Y >= 0 && position.X < global::Terraria.Main.rightWorld && position.Y < global::Terraria.Main.bottomWorld;

    private bool IsCurrent(Actor actor) => !disposed && !actor.RetirementRequested && ReferenceEquals(bySlot[actor.Slot], actor) &&
        NativeMatches(actor);

    private static bool NativeMatches(Actor actor) => (uint)actor.Slot < (uint)global::Terraria.Main.npc.Length &&
        ReferenceEquals(global::Terraria.Main.npc[actor.Slot], actor.Native) && actor.Native.active &&
        actor.Native.generation == actor.Generation && actor.Native.type == actor.Type;

    private void Attach()
    {
        if (attached) return;
        HookEvents.Terraria.NPC.AI += OnAi;
        HookEvents.Terraria.Main.Update += OnTick;
        global::Terraria.Main.OnTickForThirdPartySoftwareOnly += Sweep;
        Hooks.MessageBuffer.GetData += OnReceive;
        Hooks.Main.DedicatedWorldStopping += StopWorld;
        attached = true;
    }

    private void Detach()
    {
        if (!attached) return;
        HookEvents.Terraria.NPC.AI -= OnAi;
        HookEvents.Terraria.Main.Update -= OnTick;
        global::Terraria.Main.OnTickForThirdPartySoftwareOnly -= Sweep;
        Hooks.MessageBuffer.GetData -= OnReceive;
        Hooks.Main.DedicatedWorldStopping -= StopWorld;
        attached = false;
    }

    private void OnTick(global::Terraria.Main instance, HookEvents.Terraria.Main.UpdateEventArgs args) => Sweep();

    private void Sweep()
    {
        if (!dispatcher.IsGameThread) return;
        foreach (Actor? actor in bySlot)
            if (actor is not null && (actor.RetirementRequested || !NativeMatches(actor))) Remove(actor, actor.RetirementRequested);
    }

    private void StopWorld()
    {
        if (!dispatcher.IsGameThread) return;
        foreach (Actor? actor in bySlot) if (actor is not null) Remove(actor, despawn: true);
    }

    private void Remove(Actor actor, bool despawn)
    {
        if (!ReferenceEquals(bySlot[actor.Slot], actor)) return;
        bySlot[actor.Slot] = null;
        count--;
        actor.MarkRetired();
        if (despawn && NativeMatches(actor))
        {
            actor.Native.active = false;
            Sync(actor.Native, actor.Slot, name: false);
        }
        if (count == 0) Detach();
    }

    private void Retire(Actor actor)
    {
        if (disposed) return;
        // Retire flag also survives dispatcher saturation; the attached world-tick sweep owns cleanup.
        if (dispatcher.IsGameThread) Remove(actor, despawn: true);
        else _ = dispatcher.TryPost("npc.actor.retire", () => Remove(actor, despawn: true));
    }

    private void OnAi(global::Terraria.NPC npc, HookEvents.Terraria.NPC.AIEventArgs args)
    {
        if (!dispatcher.IsGameThread || (uint)npc.whoAmI >= (uint)bySlot.Length || bySlot[npc.whoAmI] is not { } actor ||
            !IsCurrent(actor) || !ReferenceEquals(npc, actor.Native)) return;
        if (actor.Intent.Kind == NpcActorIntentKind.Vanilla) return;
        npc.friendly = actor.Definition.Friendly;
        npc.immortal = actor.Definition.Immortal;
        npc.dontTakeDamage = actor.Definition.Immortal;
        args.ContinueExecution = false;
        float? target = actor.Intent.Kind switch
        {
            NpcActorIntentKind.MoveTo => actor.Intent.Target.X,
            NpcActorIntentKind.FollowPlayer when actor.Intent.Follow is { } expected && players?.TryGetSessionPlayer(expected, out var live) == true
                && live is { dead: false } => live.Center.X,
            _ => null
        };
        float previous = npc.velocity.X;
        float dx = target is float x ? x - npc.Center.X : 0;
        float desired = Math.Abs(dx) <= actor.Intent.StopDistance ? 0 : MathF.CopySign(actor.Intent.Speed, dx);
        npc.velocity.X = desired == 0 && actor.Intent.Kind == NpcActorIntentKind.Stop ? 0
            : previous + Math.Clamp(desired - previous, -actor.Intent.Acceleration, actor.Intent.Acceleration);
        if (desired != 0)
        {
            npc.direction = desired < 0 ? -1 : 1;
            npc.spriteDirection = npc.direction;
        }
        if (npc.velocity.X != previous) npc.netUpdate = true;
    }

    private void OnReceive(object? sender, Hooks.MessageBuffer.GetDataEventArgs args)
    {
        if (args.Result == HookResult.Cancel || args.PacketId != (byte)PacketTypes.NpcTalk || !dispatcher.IsGameThread || players is null ||
            !MultiplicityPacketProcessing.TryParseInboundPayload(args.PacketId,
                args.Instance.readBuffer, args.ReadOffset, args.GetPayloadLength(), out PacketView packet)) return;
        try
        {
            var talk = packet.AsNpcTalkView();
            int slot = talk.NpcTalkTarget;
            if ((uint)slot >= (uint)bySlot.Length || bySlot[slot] is not { } actor || !IsCurrent(actor) ||
                !players.TryGet(new PlayerId(args.Instance.whoAmI), out var player) || player?.SessionId is null) return;
            // Let vanilla process the conversation first, then resolve the exact actor and player again.
            _ = dispatcher.TryPost("npc.actor.interaction", () =>
            {
                if (IsCurrent(actor) && players.TryGetSessionPlayer(player, out var live) && live.talkNPC == actor.Slot &&
                    player.SessionId is { } session)
                {
                    actor.Conversations[player.Id] = session;
                    if (events.HasSubscribers<NpcActorInteractedEvent>()) events.Publish(new NpcActorInteractedEvent(actor.Id, Snapshot(actor), player));
                }
            });
        }
        catch (Exception ex) { logger.Log(OperationsLogLevel.Warning, "npc-actors", "NPC interaction observation failed.", ex); }
    }

    private NpcShopPresentationStatus PresentShop(Actor actor, PlayerSnapshot expected)
    {
        if (!IsCurrent(actor)) return NpcShopPresentationStatus.ActorGone;
        if (actor.Shop is null) return NpcShopPresentationStatus.NoCatalog;
        if (expected.WorldId != ownedWorld || players?.TryGetSessionPlayer(expected, out var live) != true || live.dead)
            return NpcShopPresentationStatus.InvalidSession;
        if (!IsInteracting(actor, expected, live)) return NpcShopPresentationStatus.NotInteracting;
        if ((uint)expected.Id.Slot >= (uint)global::Terraria.Netplay.Clients.Length ||
            global::Terraria.Netplay.Clients[expected.Id.Slot] is not { IsActive: true, Socket: { } socket } client)
            return NpcShopPresentationStatus.InvalidSession;
        byte[] data = EncodeShop(actor.Shop, actor.SoldOut);
        if (!IsCurrent(actor) || !client.IsActive || !ReferenceEquals(client.Socket, socket) ||
            !players.TryGetSessionPlayer(expected, out live) || live.talkNPC != actor.Slot) return NpcShopPresentationStatus.InvalidSession;
        try
        {
            socket.AsyncSend(data, 0, data.Length, client.ServerWriteCallBack);
            return NpcShopPresentationStatus.Sent;
        }
        catch (Exception ex)
        {
            logger.Log(OperationsLogLevel.Warning, "npc-actors", "Shop presentation could not be sent.", ex);
            return NpcShopPresentationStatus.Unavailable;
        }
    }

    private static bool IsInteracting(Actor actor, PlayerSnapshot expected, global::Terraria.Player live) =>
        !live.dead && live.talkNPC == actor.Slot && actor.Native.townNPC &&
        actor.Conversations.TryGetValue(expected.Id, out var conversation) && expected.SessionId == conversation &&
        Microsoft.Xna.Framework.Vector2.DistanceSquared(live.Center, actor.Native.Center) <= 256f * 256f;

    private NpcShopPurchaseResult Purchase(Actor actor, PlayerSnapshot expected, string offerId, long revision)
    {
        NpcShopPurchaseResult Failure(NpcShopPurchaseStatus status) => new(status, actor.ShopRevision);
        if (!IsCurrent(actor)) return Failure(NpcShopPurchaseStatus.ActorGone);
        if (actor.Shop is not { } catalog || actor.ShopRevision != revision) return Failure(NpcShopPurchaseStatus.CatalogChanged);
        if (players is null || !players.TryGetSessionPlayer(expected, out var live) || !IsInteracting(actor, expected, live))
            return Failure(NpcShopPurchaseStatus.NotInteracting);
        NpcShopOffer offer = catalog.Offers.FirstOrDefault(o => o.Id == offerId);
        if (offer.Id is null || offer.CoinPrice is not long price || actor.SoldOut.Contains(offer.Id)) return Failure(NpcShopPurchaseStatus.OfferUnavailable);
        if (live.inventory.Length < NpcShopPurchasePlan.Slots) return Failure(NpcShopPurchaseStatus.InvalidInventory);
        var original = new NpcShopPurchasePlan.Slot[NpcShopPurchasePlan.Slots];
        for (int i = 0; i < original.Length; i++) original[i] = NpcShopPurchasePlan.Snapshot(live.inventory[i]);

        var prototype = new global::Terraria.Item();
        prototype.SetDefaults(offer.ItemType);
        if (offer.Prefix != 0) prototype.Prefix(offer.Prefix);
        if (prototype.type != offer.ItemType || prototype.prefix != offer.Prefix || prototype.maxStack <= 0)
            return Failure(NpcShopPurchaseStatus.InvalidOffer);
        var status = NpcShopPurchasePlan.Prepare(original, offer, prototype.maxStack,
            coinStackLimits ??= NpcShopPurchasePlan.ReadCoinStackLimits(), out var planned);
        if (status != NpcShopPurchaseStatus.Committed || planned is null) return Failure(status);

        // Prepare every replacement before committing any native slot. Preserve extra item fields
        // on an existing stack; newly created stacks use normal Terraria item defaults/prefixes.
        var replacements = new List<(int Slot, global::Terraria.Item Item)>();
        for (int i = 0; i < original.Length; i++)
        {
            if (original[i] == planned[i]) continue;
            var target = planned[i];
            global::Terraria.Item item;
            if (target.Type == original[i].Type && target.Prefix == original[i].Prefix) item = live.inventory[i].Clone();
            else
            {
                item = new global::Terraria.Item();
                item.SetDefaults(target.Type);
                if (target.Prefix != 0) item.Prefix(target.Prefix);
            }
            if (item.type != target.Type || item.prefix != target.Prefix || target.Stack > item.maxStack)
                return Failure(NpcShopPurchaseStatus.InvalidOffer);
            item.stack = target.Stack;
            replacements.Add((i, item));
        }
        if (!IsCurrent(actor) || actor.ShopRevision != revision || !players.TryGetSessionPlayer(expected, out var current) ||
            !ReferenceEquals(live, current) || !IsInteracting(actor, expected, live)) return Failure(NpcShopPurchaseStatus.InventoryChanged);
        for (int i = 0; i < original.Length; i++)
            if (NpcShopPurchasePlan.Snapshot(live.inventory[i]) != original[i]) return Failure(NpcShopPurchaseStatus.InventoryChanged);

        foreach (var (slot, item) in replacements) live.inventory[slot] = item;
        if (offer.BuyOnce) { actor.SoldOut.Add(offer.Id); actor.ShopRevision++; }
        var committed = new NpcShopPurchaseResult(NpcShopPurchaseStatus.Committed, actor.ShopRevision, price, offer.ItemType, offer.Stack);
        // Publication is post-commit. A delivery/observer exception must never invite a second debit.
        foreach (var (slot, item) in replacements)
        {
            try
            {
                if (global::Terraria.Main.netMode == 2) global::Terraria.NetMessage.SendData(5, -1, -1, null, expected.Id.Slot, slot, item.prefix);
            }
            catch (Exception ex) { LogSafe("Committed shop inventory sync failed.", ex); }
        }
        try { events.Publish(new NpcActorPurchasedEvent(actor.Id, expected, offer.Id, committed)); }
        catch (Exception ex) { LogSafe("Committed shop purchase observer failed.", ex); }
        return committed;
    }

    internal static byte[] EncodeShop(NpcShopCatalog catalog, IReadOnlySet<string>? soldOut = null)
    {
        using var stream = new MemoryStream(NpcShopCatalog.MaximumOffers * 14);
        // Native shop override addresses the current local UI. Empty slots clear leftover stock.
        var packet = new NpcShopItem();
        for (byte i = 0; i < NpcShopCatalog.MaximumOffers; i++)
        {
            NpcShopOffer offer = i < catalog.Offers.Count ? catalog.Offers[i] : default;
            if (offer.Id is not null && soldOut?.Contains(offer.Id) == true) offer = default;
            packet.Slot = i;
            packet.ItemType = offer.ItemType;
            packet.Stack = offer.Stack;
            packet.Prefix = offer.Prefix;
            packet.Value = offer.BaseValue;
            packet.Flags = offer.BuyOnce ? NpcShopItemFlags.BuyOnce : NpcShopItemFlags.None;
            packet.ToStream(stream);
        }
        return stream.ToArray();
    }

    private void Sync(global::Terraria.NPC npc, int slot, bool name)
    {
        npc.netUpdate = true;
        if (global::Terraria.Main.netMode != 2) return;
        try
        {
            global::Terraria.NetMessage.SendData(MessageID.SyncNPC, -1, -1, null, slot);
            if (name && npc.townNPC) global::Terraria.NetMessage.SendData((int)PacketTypes.UpdateNPCName, -1, -1,
                NetworkText.FromLiteral(npc.GivenName), slot);
        }
        catch (Exception ex) { LogSafe("Native actor synchronization failed.", ex); }
    }

    private void LogSafe(string message, Exception error)
    {
        try { logger.Log(OperationsLogLevel.Warning, "npc-actors", message, error); }
        catch { /* A reporting failure must not change an already committed native operation result. */ }
    }

    private NpcEventSnapshot Snapshot(Actor actor) => new(ownedWorld, actor.Slot, actor.Generation, actor.Type,
        new(actor.Native.position.X, actor.Native.position.Y), actor.Native.life, actor.Native.friendly, actor.Native.boss);

    private ValueTask<T> Dispatch<T>(Func<T> work, T unavailable, CancellationToken token)
    {
        if (token.IsCancellationRequested) return ValueTask.FromCanceled<T>(token);
        if (disposed || !dispatcher.IsGameThreadKnown) return ValueTask.FromResult(unavailable);
        if (dispatcher.IsGameThread) return ValueTask.FromResult(work());
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryPost("npc.actor", () =>
        {
            if (token.IsCancellationRequested) completion.TrySetCanceled(token);
            else { try { completion.TrySetResult(work()); } catch (Exception ex) { completion.TrySetException(ex); } }
        })) return ValueTask.FromResult(unavailable);
        return new(completion.Task);
    }

    public void Dispose()
    {
        if (disposed) return;
        if (dispatcher.IsGameThread) StopWorld();
        disposed = true;
        Detach();
        Array.Clear(bySlot);
    }

    private sealed class Actor(TerrariaNpcActorService owner, global::Terraria.NPC native, int slot, NpcActorDefinition definition) : INpcActor
    {
        private int retirement;
        public NpcActorId Id { get; } = new(Guid.NewGuid());
        public global::Terraria.NPC Native { get; } = native;
        public int Slot { get; } = slot;
        public int Generation { get; } = native.generation;
        public int Type { get; } = native.type;
        public NpcActorDefinition Definition { get; } = definition;
        public NpcActorIntent Intent = new(definition.Stationary ? NpcActorIntentKind.Stop : NpcActorIntentKind.Vanilla);
        public NpcShopCatalog? Shop = definition.Shop;
        public long ShopRevision = 1;
        public Dictionary<PlayerId, PlayerSessionId> Conversations { get; } = new();
        public HashSet<string> SoldOut { get; } = new(StringComparer.Ordinal);
        public bool RetirementRequested => Volatile.Read(ref retirement) != 0;
        public bool IsRetired => RetirementRequested || Volatile.Read(ref owner.disposed);
        public void MarkRetired() => Interlocked.Exchange(ref retirement, 1);
        public ValueTask<NpcActorSnapshot?> CaptureAsync(CancellationToken cancellationToken = default) => owner.Dispatch<NpcActorSnapshot?>(() =>
            owner.IsCurrent(this) ? new NpcActorSnapshot(Id, owner.Snapshot(this), Native.GivenName, Intent.Kind, Shop, ShopRevision)
                { SoldOutOffers = Array.AsReadOnly(SoldOut.ToArray()) } : null,
            null, cancellationToken);
        public ValueTask<bool> RenameAsync(string name, CancellationToken cancellationToken = default) => owner.Dispatch(() =>
        {
            if (!owner.IsCurrent(this) || !ValidName(name)) return false;
            Native.GivenName = name;
            owner.Sync(Native, Slot, name: true);
            return true;
        }, false, cancellationToken);
        public ValueTask<bool> TeleportAsync(PlayerPosition position, CancellationToken cancellationToken = default) => owner.Dispatch(() =>
        {
            if (!owner.IsCurrent(this) || !ValidPosition(position)) return false;
            Native.position = new(position.X, position.Y);
            Native.velocity = default;
            Conversations.Clear();
            owner.Sync(Native, Slot, name: false);
            return true;
        }, false, cancellationToken);
        public ValueTask<bool> SetIntentAsync(NpcActorIntent intent, CancellationToken cancellationToken = default) => owner.Dispatch(() =>
        {
            if (!owner.IsCurrent(this) || intent is null || !intent.IsValid || intent.Follow is { } follow && follow.WorldId != owner.ownedWorld) return false;
            Intent = intent;
            Native.netUpdate = true;
            return true;
        }, false, cancellationToken);
        public ValueTask<bool> SetShopAsync(NpcShopCatalog? shop, CancellationToken cancellationToken = default) => owner.Dispatch(() =>
        {
            if (!owner.IsCurrent(this) || !ValidShop(shop) || shop is not null && !Native.townNPC) return false;
            Shop = shop;
            SoldOut.Clear();
            ShopRevision++;
            return true;
        }, false, cancellationToken);
        public ValueTask<NpcShopPresentationStatus> PresentShopAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
            owner.Dispatch(() => owner.PresentShop(this, player), NpcShopPresentationStatus.Unavailable, cancellationToken);
        public ValueTask<NpcShopPurchaseResult> PurchaseAsync(PlayerSnapshot player, string offerId, long expectedShopRevision,
            CancellationToken cancellationToken = default) => owner.Dispatch(() => owner.Purchase(this, player, offerId, expectedShopRevision),
                new NpcShopPurchaseResult(NpcShopPurchaseStatus.Unavailable), cancellationToken);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref retirement, 1) == 0) owner.Retire(this);
        }
    }
}
