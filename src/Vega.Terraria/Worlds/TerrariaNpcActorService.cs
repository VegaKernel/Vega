using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.Localization;
using TerrariaRuntime;
using Vega.Contracts.Accounts;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Terraria.Hooking;
using Vega.Terraria.Networking;
using Vega.Terraria.Players;

namespace Vega.Terraria.Worlds;

/// <summary>Owns plugin actors while leaving normal Terraria NPC state and motion integration native.</summary>
public sealed partial class TerrariaNpcActorService(IGameThreadDispatcher dispatcher, WorldId worldId,
    TerrariaPlayerBoundary? players, IWorldGameplayEvents events, IOperationsLogger logger,
    IPlayerAccountSessionService? accountSessions = null, INpcQuestProgressStore? questStore = null,
    INpcEconomyProvider? economy = null, Func<PlayerSnapshot, bool>? inventoryReadiness = null,
    Func<PlayerSnapshot, PlayerAccountBinding, CancellationToken, ValueTask<bool>>? inventoryCheckpoint = null) : IWorldNpcActorService, IDisposable
{
    private readonly IPlayerAccountSessionService? accounts = accountSessions;
    private readonly INpcQuestProgressStore? persistentQuests = questStore;
    private readonly INpcEconomyProvider? economyProvider = economy;
    private readonly Func<PlayerSnapshot, bool>? sscReady = inventoryReadiness;
    private readonly Func<PlayerSnapshot, PlayerAccountBinding, CancellationToken, ValueTask<bool>>? persistInventory = inventoryCheckpoint;
    private readonly WorldId ownedWorld = worldId;
    private readonly Actor?[] bySlot = new Actor[global::Terraria.Main.maxNPCs];
    private readonly long[] conversationEpoch = new long[global::Terraria.Main.maxPlayers];
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
            !ValidName(definition.Name) || !ValidPosition(position) || !ValidShop(definition.Shop) || !ValidQuests(definition.Quests) ||
            definition.PersistentQuestScope is { } questScope && (!NpcAccountScope.IsValidName(questScope) || accounts is null || persistentQuests is null) ||
            definition.EconomyScope is { } economyScope && (!NpcAccountScope.IsValidName(economyScope) || accounts is null || economyProvider is null) ||
            definition.EconomyProvider is { } externalEconomy && (!externalEconomy.IsValid || definition.EconomyScope is null || economyProvider?.SupportsNamedProviders != true) ||
            definition.EconomyScope is null && UsesEconomy(definition.Shop))
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
        shop.Offers.All(offer => TryCreateOfferPrototype(offer, out _) &&
            (offer.ItemPrice is not { } currency || TryCreateItemPrototype(currency.ItemType, currency.Prefix, out _)));
    private static bool UsesEconomy(NpcShopCatalog? shop) => shop?.Offers.Any(offer => offer.EconomyPrice is not null) == true;

    private static bool ValidQuests(NpcQuestCatalog? quests) => quests is null || quests.Quests.All(quest =>
        TryCreateItemPrototype(quest.Required.ItemType, quest.Required.Prefix, out _) &&
        TryCreateItemPrototype(quest.Reward.ItemType, quest.Reward.Prefix, out var reward) && quest.Reward.Amount <= reward.maxStack);

    private static bool TryCreateOfferPrototype(NpcShopOffer offer, out global::Terraria.Item prototype)
        => TryCreateItemPrototype(offer.ItemType, offer.Prefix, out prototype) && offer.Stack > 0 && offer.Stack <= prototype.maxStack;

    private static bool TryCreateItemPrototype(int itemType, byte prefix, out global::Terraria.Item prototype)
    {
        prototype = new global::Terraria.Item();
        if (itemType <= 0 || itemType >= ItemID.Count || prefix >= PrefixID.Count) return false;
        prototype.SetDefaults(itemType);
        if (prefix != 0) prototype.Prefix(prefix);
        // One presentation slot must describe a real native item, not a stack or prefix that the
        // stock decoder would accept into its UI but could never deliver as specified.
        return prototype.type == itemType && prototype.prefix == prefix && prototype.maxStack > 0;
    }
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
        if (++actor.AiVisits == 60)
        {
            actor.AiVisits = 0;
            if (events.HasSubscribers<NpcActorThinkEvent>())
                PublishSafe(new NpcActorThinkEvent(actor.Id, Snapshot(actor), actor.Intent.Kind), "Actor behavior observer failed.");
            if (!IsCurrent(actor)) { args.ContinueExecution = false; return; }
        }
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
            NpcActorIntentKind.Patrol => PatrolTarget(actor),
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

    private static float PatrolTarget(Actor actor)
    {
        var route = actor.Intent.Waypoints!;
        if (Math.Abs(route[actor.PatrolIndex].X - actor.Native.Center.X) <= actor.Intent.StopDistance)
            actor.PatrolIndex = (actor.PatrolIndex + 1) % route.Count;
        return route[actor.PatrolIndex].X;
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
            int playerSlot = args.Instance.whoAmI;
            if ((uint)playerSlot >= (uint)conversationEpoch.Length || !players.TryGet(new PlayerId(playerSlot), out var player) ||
                player?.SessionId is null) return;
            long epoch = ++conversationEpoch[playerSlot];
            // A native conversation change closes all prior actor menus for that slot,
            // even if the new target is a normal NPC or the conversation is being closed.
            foreach (var previous in bySlot)
                if (previous is not null && previous.Conversations.Remove(player.Id)) previous.Interactions.OnConversation(player);
            if ((uint)slot >= (uint)bySlot.Length || bySlot[slot] is not { } actor || !IsCurrent(actor)) return;
            // Let vanilla process the conversation first, then resolve the exact actor and player again.
            _ = dispatcher.TryPost("npc.actor.interaction", () =>
            {
                if (conversationEpoch[playerSlot] == epoch && IsCurrent(actor) && players.TryGetSessionPlayer(player, out var live) && IsNearConversation(actor, live) &&
                    player.SessionId is { } session)
                {
                    actor.Conversations[player.Id] = session;
                    actor.Interactions.OnConversation(player);
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
        byte[] data = actor.EncodedShop ??= EncodeShop(actor.Shop, actor.SoldOut);
        if (!IsCurrent(actor) || !client.IsActive || !ReferenceEquals(client.Socket, socket) ||
            !players.TryGetSessionPlayer(expected, out live) || live.talkNPC != actor.Slot) return NpcShopPresentationStatus.InvalidSession;
        try
        {
            // Cached encoded shop frames bypass native SendData and its spamCount increment.
            // Native ServerWriteCallBack would debit a counter we never incremented.
            socket.AsyncSend(data, 0, data.Length, static _ => { });
            return NpcShopPresentationStatus.Sent;
        }
        catch (Exception ex)
        {
            logger.Log(OperationsLogLevel.Warning, "npc-actors", "Shop presentation could not be sent.", ex);
            return NpcShopPresentationStatus.Unavailable;
        }
    }

    private static bool IsNearConversation(Actor actor, global::Terraria.Player live) =>
        !live.dead && live.talkNPC == actor.Slot &&
        Microsoft.Xna.Framework.Vector2.DistanceSquared(live.Center, actor.Native.Center) <= 256f * 256f;

    private static bool HasConversation(Actor actor, PlayerSnapshot expected, global::Terraria.Player live) =>
        IsNearConversation(actor, live) &&
        actor.Conversations.TryGetValue(expected.Id, out var conversation) && expected.SessionId == conversation;

    private static bool IsInteracting(Actor actor, PlayerSnapshot expected, global::Terraria.Player live) =>
        actor.Native.townNPC && HasConversation(actor, expected, live);

    private bool TryConversation(Actor actor, PlayerSnapshot expected, out global::Terraria.Player live)
    {
        if (players?.TryGetSessionPlayer(expected, out var current) == true && HasConversation(actor, expected, current))
        { live = current; return true; }
        live = null!;
        return false;
    }

    private bool HasLiveSession(PlayerSnapshot expected) => players?.TryGetSessionPlayer(expected, out _) == true;

    private NpcDialogueResult BeginDialogue(Actor actor, PlayerSnapshot expected)
    {
        if (!IsCurrent(actor)) return new(NpcDialogueStatus.ActorGone);
        return TryConversation(actor, expected, out _) ? actor.Interactions.Begin(expected) : new(NpcDialogueStatus.NotInteracting);
    }

    private NpcDialogueResult ChooseDialogue(Actor actor, PlayerSnapshot expected, long viewRevision, string choiceId)
    {
        if (!IsCurrent(actor)) return new(NpcDialogueStatus.ActorGone);
        if (!TryConversation(actor, expected, out _)) return new(NpcDialogueStatus.NotInteracting);
        var result = actor.Interactions.Choose(expected, viewRevision, choiceId, out var selection);
        if (selection is { } selected)
            PublishSafe(new NpcActorDialogueChoiceEvent(actor.Id, expected, selected.NodeId, selected.Choice.Id, selected.Choice.NextNode), "Actor dialogue observer failed.");
        return result;
    }

    private NpcQuestResult AcceptQuest(Actor actor, PlayerSnapshot expected, string questId, long revision)
    {
        var state = actor.Interactions;
        if (!IsCurrent(actor)) return new(NpcQuestStatus.ActorGone, state.QuestRevision);
        if (!TryConversation(actor, expected, out _)) return new(NpcQuestStatus.NotInteracting, state.QuestRevision);
        var result = state.Accept(expected, questId, revision);
        if (result.Status == NpcQuestStatus.Applied)
            PublishSafe(new NpcActorQuestEvent(actor.Id, expected, questId, NpcQuestState.Accepted), "Actor quest observer failed.");
        return result;
    }

    private NpcQuestResult TurnInQuest(Actor actor, PlayerSnapshot expected, string questId, long revision)
    {
        var state = actor.Interactions;
        if (!IsCurrent(actor)) return new(NpcQuestStatus.ActorGone, state.QuestRevision);
        if (!TryConversation(actor, expected, out var live)) return new(NpcQuestStatus.NotInteracting, state.QuestRevision);
        if (revision != state.QuestRevision) return new(NpcQuestStatus.CatalogChanged, state.QuestRevision);
        if (state.FindQuest(questId) is not { } quest) return new(NpcQuestStatus.UnknownQuest, state.QuestRevision);
        var previous = state.State(expected, questId);
        if (previous != NpcQuestState.Accepted)
            return new(previous == NpcQuestState.Completed ? NpcQuestStatus.AlreadyCompleted : NpcQuestStatus.NotAccepted,
                state.QuestRevision, new(quest, previous));
        var offer = new NpcShopOffer(quest.Id, quest.Reward.ItemType, checked((short)quest.Reward.Amount), 0, quest.Reward.Prefix)
            { ItemPrice = quest.Required };
        var status = Exchange(actor, expected, live, offer,
            () => state.QuestRevision == revision && state.State(expected, questId) == NpcQuestState.Accepted,
            () => state.SetState(expected, questId, NpcQuestState.Completed));
        if (status != NpcShopPurchaseStatus.Committed)
            return new(NpcQuestStatus.InventoryRejected, state.QuestRevision, new(quest, previous), status);
        var result = new NpcQuestResult(NpcQuestStatus.Applied, state.QuestRevision, new(quest, NpcQuestState.Completed));
        PublishSafe(new NpcActorQuestEvent(actor.Id, expected, questId, NpcQuestState.Completed), "Actor quest observer failed.");
        return result;
    }

    private NpcShopPurchaseResult Purchase(Actor actor, PlayerSnapshot expected, string offerId, long revision)
    {
        NpcShopPurchaseResult Failure(NpcShopPurchaseStatus status) => new(status, actor.ShopRevision);
        if (!IsCurrent(actor)) return Failure(NpcShopPurchaseStatus.ActorGone);
        if (actor.Shop is not { } catalog || actor.ShopRevision != revision) return Failure(NpcShopPurchaseStatus.CatalogChanged);
        if (players is null || !players.TryGetSessionPlayer(expected, out var live) || !IsInteracting(actor, expected, live))
            return Failure(NpcShopPurchaseStatus.NotInteracting);
        NpcShopOffer offer = catalog.Offers.FirstOrDefault(o => o.Id == offerId);
        if (offer.Id is null || offer.CoinPrice is null && offer.ItemPrice is null || actor.SoldOut.Contains(offer.Id))
            return Failure(NpcShopPurchaseStatus.OfferUnavailable);
        var status = Exchange(actor, expected, live, offer,
            () => actor.ShopRevision == revision && !actor.SoldOut.Contains(offer.Id),
            () => { if (offer.BuyOnce) { actor.SoldOut.Add(offer.Id); actor.InvalidateShopPresentation(); } });
        if (status != NpcShopPurchaseStatus.Committed) return Failure(status);
        var committed = new NpcShopPurchaseResult(status, actor.ShopRevision, offer.CoinPrice ?? 0, offer.ItemType, offer.Stack)
            { ItemPaid = offer.ItemPrice };
        PublishSafe(new NpcActorPurchasedEvent(actor.Id, expected, offer.Id, committed), "Committed shop purchase observer failed.");
        return committed;
    }

    private NpcShopPurchaseStatus Exchange(Actor actor, PlayerSnapshot expected, global::Terraria.Player live, NpcShopOffer offer,
        Func<bool> stillCurrent, Action committed)
    {
        // The stock multiplayer client ignores SyncEquipment for its own unlocked inventory
        // unless the server advertised SSC. Do not debit or consume stock when the client owns it.
        if (!HasInventoryAuthority(expected))
            return NpcShopPurchaseStatus.InventoryAuthorityRequired;
        if (live.inventory.Length < NpcShopPurchasePlan.Slots) return NpcShopPurchaseStatus.InvalidInventory;
        var original = new NpcShopPurchasePlan.Slot[NpcShopPurchasePlan.Slots];
        for (int i = 0; i < original.Length; i++) original[i] = NpcShopPurchasePlan.Snapshot(live.inventory[i]);

        if (!TryCreateOfferPrototype(offer, out var prototype) || offer.ItemPrice is { } itemCost &&
            !TryCreateItemPrototype(itemCost.ItemType, itemCost.Prefix, out _))
            return NpcShopPurchaseStatus.InvalidOffer;
        var status = NpcShopPurchasePlan.Prepare(original, offer, prototype.maxStack,
            offer.ItemPrice is null ? coinStackLimits ??= NpcShopPurchasePlan.ReadCoinStackLimits() : [], out var planned);
        if (status != NpcShopPurchaseStatus.Committed || planned is null) return status;

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
                return NpcShopPurchaseStatus.InvalidOffer;
            item.stack = target.Stack;
            replacements.Add((i, item));
        }
        if (!IsCurrent(actor) || !HasInventoryAuthority(expected) || players?.TryGetSessionPlayer(expected, out var current) != true ||
            !ReferenceEquals(live, current) || !HasConversation(actor, expected, live) || !stillCurrent()) return NpcShopPurchaseStatus.InventoryChanged;
        for (int i = 0; i < original.Length; i++)
            if (NpcShopPurchasePlan.Snapshot(live.inventory[i]) != original[i]) return NpcShopPurchaseStatus.InventoryChanged;

        foreach (var (slot, item) in replacements) live.inventory[slot] = item;
        committed();
        // Publication is post-commit. A delivery/observer exception must never invite a second debit.
        foreach (var (slot, item) in replacements)
        {
            try
            {
                if (global::Terraria.Main.netMode == 2)
                {
                    global::Terraria.NetMessage.SendData(MessageID.SyncEquipment, expected.Id.Slot, -1, null, expected.Id.Slot, slot, item.prefix);
                    global::Terraria.NetMessage.SendData(MessageID.SyncEquipment, -1, expected.Id.Slot, null, expected.Id.Slot, slot, item.prefix);
                }
            }
            catch (Exception ex) { LogSafe("Committed shop inventory sync failed.", ex); }
        }
        return NpcShopPurchaseStatus.Committed;
    }

    private bool HasInventoryAuthority(PlayerSnapshot expected) => global::Terraria.Main.netMode != 2 ||
        global::Terraria.Main.ServerSideCharacter && (sscReady?.Invoke(expected) ?? true);

    internal static byte[] EncodeShop(NpcShopCatalog catalog, IReadOnlySet<string>? soldOut = null)
    {
        using var stream = new MemoryStream(NpcShopCatalog.MaximumOffers * 14);
        // Native shop override addresses the current local UI. Empty slots clear leftover stock.
        var packet = new NpcShopItem();
        for (byte i = 0; i < NpcShopCatalog.MaximumOffers; i++)
        {
            NpcShopOffer offer = i < catalog.Offers.Count ? catalog.Offers[i] : default;
            if (offer.ItemPrice is not null || offer.EconomyPrice is not null || offer.Id is not null && soldOut?.Contains(offer.Id) == true) offer = default;
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

    private void PublishSafe<T>(T value, string failure) where T : struct
    {
        try { events.Publish(value); }
        catch (Exception ex) { LogSafe(failure, ex); }
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
        public int PatrolIndex;
        public int AiVisits;
        public NpcActorInteractionState Interactions { get; } = new(definition.Dialogue, definition.Quests);
        public NpcShopCatalog? Shop = definition.Shop;
        public long ShopRevision = 1;
        public byte[]? EncodedShop;
        public void InvalidateShopPresentation()
        {
            EncodedShop = null;
            ShopRevision++;
        }
        public Dictionary<PlayerId, PlayerSessionId> Conversations { get; } = new();
        public HashSet<string> SoldOut { get; } = new(StringComparer.Ordinal);
        public bool RetirementRequested => Volatile.Read(ref retirement) != 0;
        public bool IsRetired => RetirementRequested || Volatile.Read(ref owner.disposed);
        public void MarkRetired() => Interlocked.Exchange(ref retirement, 1);
        public ValueTask<NpcActorSnapshot?> CaptureAsync(CancellationToken cancellationToken = default) => owner.Dispatch<NpcActorSnapshot?>(() =>
            owner.IsCurrent(this) ? new NpcActorSnapshot(Id, owner.Snapshot(this), Native.GivenName, Intent.Kind, Shop, ShopRevision)
                { SoldOutOffers = Array.AsReadOnly(SoldOut.ToArray()), Dialogue = Interactions.Dialogue,
                    Quests = Interactions.Quests, QuestRevision = Interactions.QuestRevision,
                    PersistentQuestScope = Definition.PersistentQuestScope, EconomyScope = Definition.EconomyScope, EconomyProvider = Definition.EconomyProvider } : null,
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
            Interactions.InvalidateDialogues();
            owner.Sync(Native, Slot, name: false);
            return true;
        }, false, cancellationToken);
        public ValueTask<bool> SetIntentAsync(NpcActorIntent intent, CancellationToken cancellationToken = default) => owner.Dispatch(() =>
        {
            if (!owner.IsCurrent(this) || intent is null || !intent.IsValid || intent.Follow is { } follow && follow.WorldId != owner.ownedWorld) return false;
            if (intent.Kind == NpcActorIntentKind.Patrol)
            {
                var route = intent.Waypoints!.Take(33).ToArray();
                if (route.Length is < 2 or > 32 || route.Any(position => !ValidPosition(position))) return false;
                intent = intent with { Waypoints = Array.AsReadOnly(route) };
            }
            PatrolIndex = 0;
            Intent = intent;
            Native.netUpdate = true;
            return true;
        }, false, cancellationToken);
        public ValueTask<bool> SetShopAsync(NpcShopCatalog? shop, CancellationToken cancellationToken = default) => owner.Dispatch(() =>
        {
            if (!owner.IsCurrent(this) || !ValidShop(shop) || shop is not null && !Native.townNPC ||
                Definition.EconomyScope is null && UsesEconomy(shop)) return false;
            Shop = shop;
            SoldOut.Clear();
            InvalidateShopPresentation();
            return true;
        }, false, cancellationToken);
        public ValueTask<NpcShopPresentationStatus> PresentShopAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
            owner.Dispatch(() => owner.PresentShop(this, player), NpcShopPresentationStatus.Unavailable, cancellationToken);
        public ValueTask<NpcShopPurchaseResult> PurchaseAsync(PlayerSnapshot player, string offerId, long expectedShopRevision,
            CancellationToken cancellationToken = default) => Definition.EconomyScope is not null
                ? owner.PurchaseWithEconomyAsync(this, player, offerId, expectedShopRevision, null, cancellationToken)
                : owner.Dispatch(() => owner.Purchase(this, player, offerId, expectedShopRevision),
                new NpcShopPurchaseResult(NpcShopPurchaseStatus.Unavailable), cancellationToken);
        public ValueTask<NpcShopPurchaseResult> PurchaseCurrencyAsync(PlayerSnapshot player, string offerId, long expectedShopRevision,
            Guid operationId, CancellationToken cancellationToken = default) => Definition.EconomyScope is not null && operationId != Guid.Empty
                ? owner.PurchaseWithEconomyAsync(this, player, offerId, expectedShopRevision, operationId, cancellationToken)
                : ValueTask.FromResult(new NpcShopPurchaseResult(NpcShopPurchaseStatus.InvalidOffer));
        public ValueTask<bool> SetDialogueAsync(NpcDialogueDefinition? dialogue, CancellationToken cancellationToken = default) => owner.Dispatch(() =>
        {
            if (!owner.IsCurrent(this)) return false;
            Interactions.SetDialogue(dialogue);
            return true;
        }, false, cancellationToken);
        public ValueTask<NpcDialogueResult> BeginDialogueAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
            owner.Dispatch(() => owner.BeginDialogue(this, player), new NpcDialogueResult(NpcDialogueStatus.Unavailable), cancellationToken);
        public ValueTask<NpcDialogueResult> ChooseDialogueAsync(PlayerSnapshot player, long expectedViewRevision, string choiceId,
            CancellationToken cancellationToken = default) => owner.Dispatch(() => owner.ChooseDialogue(this, player, expectedViewRevision, choiceId),
                new NpcDialogueResult(NpcDialogueStatus.Unavailable), cancellationToken);
        public ValueTask<bool> SetQuestsAsync(NpcQuestCatalog? quests, CancellationToken cancellationToken = default) => owner.Dispatch(() =>
        {
            if (!owner.IsCurrent(this) || !ValidQuests(quests)) return false;
            Interactions.SetQuests(quests);
            return true;
        }, false, cancellationToken);
        public ValueTask<IReadOnlyList<NpcQuestView>> GetQuestsAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
            Definition.PersistentQuestScope is not null ? owner.GetPersistentQuestsAsync(this, player, cancellationToken) :
                owner.Dispatch<IReadOnlyList<NpcQuestView>>(() => owner.IsCurrent(this) && owner.TryConversation(this, player, out _)
                ? Interactions.GetQuests(player) : [], [], cancellationToken);
        public ValueTask<NpcQuestResult> AcceptQuestAsync(PlayerSnapshot player, string questId, long expectedCatalogRevision,
            CancellationToken cancellationToken = default) => Definition.PersistentQuestScope is not null
                ? owner.AcceptPersistentQuestAsync(this, player, questId, expectedCatalogRevision, cancellationToken)
                : owner.Dispatch(() => owner.AcceptQuest(this, player, questId, expectedCatalogRevision),
                new NpcQuestResult(NpcQuestStatus.Unavailable), cancellationToken);
        public ValueTask<NpcQuestResult> TurnInQuestAsync(PlayerSnapshot player, string questId, long expectedCatalogRevision,
            CancellationToken cancellationToken = default) => Definition.PersistentQuestScope is not null
                ? owner.TurnInPersistentQuestAsync(this, player, questId, expectedCatalogRevision, cancellationToken)
                : owner.Dispatch(() => owner.TurnInQuest(this, player, questId, expectedCatalogRevision),
                new NpcQuestResult(NpcQuestStatus.Unavailable), cancellationToken);
        public ValueTask<NpcEconomyBalanceResult> GetCurrencyBalanceAsync(PlayerSnapshot player, string currencyId, CancellationToken cancellationToken = default) =>
            owner.CurrencyBalanceAsync(this, player, currencyId, cancellationToken);
        public ValueTask<NpcEconomyBalanceResult> CreditCurrencyAsync(PlayerSnapshot player, NpcEconomyPrice amount, Guid operationId,
            CancellationToken cancellationToken = default) => owner.CreditCurrencyAsync(this, player, amount, operationId, cancellationToken);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref retirement, 1) == 0) owner.Retire(this);
        }
    }
}
