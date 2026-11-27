using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Core.Scenes;

public sealed partial class SceneRuntime : IVirtualChestGuiAuthority
{
    private const int ManagedMouseSlot = 58;
    private const int MaximumGuiIndicesPerSession = 32;
    private static readonly TimeSpan GuiPairLifetime = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan GuiIdleLifetime = TimeSpan.FromMinutes(2);
    private readonly Dictionary<(WorldId World, PlayerId Player), ManagedChestPlayer> managedChestPlayers = [];
    private int managedChestCount;
    public bool HasManagedChestPlayers => Volatile.Read(ref managedChestCount) != 0;

    private sealed class ManagedChestPlayer(PlayerSessionId session, IVirtualChestGuiHost host)
    {
        public PlayerSessionId Session { get; } = session;
        public IVirtualChestGuiHost Host { get; } = host;
        public HashSet<int> Corrections { get; } = [];
        public Dictionary<short, ChestIdentity> AssignedIndices { get; } = [];
        public PlayerInventorySnapshot? Inventory;
        public ManagedChestGui? Gui;
        public ManagedDisplaySession? Display;
    }

    private sealed class ManagedChestGui(SceneVirtualChestOpenRequest open, short index, DateTimeOffset now)
    {
        public SceneVirtualChestOpenRequest Open = open;
        public short Index { get; } = index;
        public DateTimeOffset LastActivity = now;
        public DateTimeOffset? PendingAt;
        public ChestItemSnapshot? ChestProposal;
        public InventorySlotReplacement? InventoryProposal;
        public SceneVirtualChestGuiTransferIntent? Ready;
        public bool Delivered, Claimed, Closing;
    }

    public bool IsManagedChestPlayer(PlayerSnapshot player)
    {
        if (Volatile.Read(ref managedChestCount) == 0) return false;
        lock (sync) return TryGetManagedChestPlayer(player, out _);
    }

    public bool IsManagedChestSlot(WorldId world, PlayerId player)
    {
        if (!HasManagedChestPlayers) return false;
        lock (sync) return managedChestPlayers.ContainsKey((world, player));
    }

    public bool CanUseManagedGuiIndex(PlayerSnapshot player, short index, ChestIdentity chest)
    {
        lock (sync)
        {
            if (!IsCurrentObserver(player)) return false;
            if (!managedChestPlayers.TryGetValue((player.WorldId, player.Id), out var state)) return true;
            return state.Gui is null && state.Display is null && state.Session == player.SessionId &&
                (state.AssignedIndices.TryGetValue(index, out var previous) ? previous == chest : state.AssignedIndices.Count < MaximumGuiIndicesPerSession);
        }
    }

    public bool CanPresentManagedGui(PlayerSnapshot player, ChestIdentity chest)
    {
        lock (sync)
        {
            if (!IsCurrentObserver(player)) return false;
            if (!managedChestPlayers.TryGetValue((player.WorldId, player.Id), out var state)) return true;
            return state.Gui is null && state.Display is null && state.Session == player.SessionId &&
                (state.AssignedIndices.Count < MaximumGuiIndicesPerSession || state.AssignedIndices.ContainsValue(chest));
        }
    }

    private bool TryGetManagedChestPlayer(PlayerSnapshot player, out ManagedChestPlayer state)
    {
        if (IsCurrentObserver(player) && managedChestPlayers.TryGetValue((player.WorldId, player.Id), out var found) && found.Session == player.SessionId)
        { state = found; return true; }
        state = null!;
        return false;
    }

    public bool TryBeginManagedGui(PlayerSnapshot player, SceneVirtualChestOpenRequest open, short index,
        PlayerInventorySnapshot inventory, IVirtualChestGuiHost host)
    {
        if (index < 0 || !InventoryMatchesSession(player, inventory)) return false;
        lock (sync)
        {
            var identity = open.Chest.Identity;
            if (!TryGetInteractionObserver(player, identity.X, identity.Y, out var observer) || observer != open.Observer ||
                player.WorldId != identity.WorldId || !WithinObjectReach(player, identity.X, identity.Y) ||
                DateTimeOffset.UtcNow - open.RequestedAt > VirtualInteractionLifetime || !CurrentGuiChest(player, open, out _)) return false;
            var key = (player.WorldId, player.Id);
            if (!managedChestPlayers.TryGetValue(key, out var managed))
            {
                managedChestPlayers[key] = managed = new ManagedChestPlayer(observer.Session, host);
                Interlocked.Increment(ref managedChestCount);
            }
            if (managed.Session != observer.Session || !ReferenceEquals(managed.Host, host) || managed.Gui is not null || managed.Display is not null ||
                (managed.AssignedIndices.TryGetValue(index, out var previous) ? previous != identity : managed.AssignedIndices.Count >= MaximumGuiIndicesPerSession)) return false;
            managed.AssignedIndices[index] = identity;
            managed.Inventory = inventory;
            managed.Gui = new(open, index, DateTimeOffset.UtcNow);
            return true;
        }
    }

    private static bool InventoryMatchesSession(PlayerSnapshot player, PlayerInventorySnapshot inventory) =>
        player.SessionId is not null && inventory.PlayerId == player.Id && inventory.SessionId == player.SessionId && inventory.WorldId == player.WorldId;

    private bool CurrentGuiChest(PlayerSnapshot player, SceneVirtualChestOpenRequest open, out SceneVirtualChestSnapshot current)
    {
        current = default;
        var identity = open.Chest.Identity;
        if (player.IsDead || !WithinObjectReach(player, identity.X, identity.Y) ||
            !TryResolveVirtualChestLocked(identity.WorldId, open.Observer, identity.X, identity.Y, out var resolved) ||
            resolved is not { } found || found.SceneId != open.Chest.SceneId || found.OverlayId != open.Chest.OverlayId ||
            found.Identity != identity || !ReferenceEquals(found.Definition, open.Chest.Definition)) return false;
        current = found;
        return true;
    }

    public bool ObserveManagedInventory(PlayerSnapshot player, InventorySlotReplacement proposed, DateTimeOffset now)
    {
        lock (sync)
        {
            if (!TryGetManagedChestPlayer(player, out var state)) return false;
            if (state.Display is { } display) { ObserveManagedDisplayInventory(state,display,proposed,now); return true; }
            if (state.Gui is not { Closing: false } gui || state.Inventory is not { } inventory ||
                !TryInventorySlot(inventory, proposed.Slot, out var before))
            {
                AddCorrection(state, proposed.Slot);
                if (state.Gui is { } current) current.Closing = true;
                return true;
            }
            if (before.ItemType == proposed.ItemType && before.Stack == proposed.Stack && before.Prefix == proposed.Prefix && before.Favorited == proposed.Favorited)
                return true; // periodic unchanged slot reports do not start a transaction
            if (gui.InventoryProposal is { } repeated && repeated == proposed) return true;
            if (!ValidStack(proposed.ItemType, proposed.Stack, proposed.Prefix) || gui.Ready is not null ||
                gui.InventoryProposal is not null)
            {
                AddCorrection(state, proposed.Slot);
                gui.Closing = true;
                return true;
            }
            gui.InventoryProposal = proposed;
            gui.PendingAt ??= now;
            gui.LastActivity = now;
            TryPairManagedGui(state, gui, now);
            return true;
        }
    }

    public bool ObserveManagedChest(PlayerSnapshot player, short index, ChestItemSnapshot proposed, DateTimeOffset now)
    {
        lock (sync)
        {
            if (!TryGetManagedChestPlayer(player, out var state)) return false;
            if (state.Display is { } display) { display.Cancelled = true; return true; }
            if (state.Gui is not { Closing: false } gui) return true;
            if (gui.Index != index || (uint)proposed.Slot >= SceneVirtualChestDefinition.SlotCount ||
                !ValidStack(proposed.ItemType, proposed.Stack, proposed.Prefix)) { gui.Closing = true; return true; }
            var before = ChestSlot(gui.Open.Chest.Definition, proposed.Slot);
            if (before == proposed) return true;
            if (gui.ChestProposal is { } repeated && repeated == proposed) return true;
            if (gui.Ready is not null || gui.ChestProposal is not null) { gui.Closing = true; return true; }
            gui.ChestProposal = proposed;
            gui.PendingAt ??= now;
            gui.LastActivity = now;
            TryPairManagedGui(state, gui, now);
            return true;
        }
    }

    public bool ObserveManagedChestOpen(PlayerSnapshot player, short index, int x, int y)
    {
        lock (sync)
        {
            if (!TryGetManagedChestPlayer(player, out var state)) return false;
            if (state.Display is { } display) display.Cancelled = true;
            if (state.Gui is { } gui && (index != gui.Index || x != gui.Open.Chest.Identity.X || y != gui.Open.Chest.Identity.Y)) gui.Closing = true;
            return true;
        }
    }

    public bool RequestManagedGuiClose(PlayerSnapshot player)
    {
        lock (sync)
        {
            if (!TryGetManagedChestPlayer(player, out var state)) return false;
            bool changed = false;
            if (state.Gui is { } gui) { gui.Closing = true; changed = true; }
            if (state.Display is { } display) { display.Cancelled = true; changed = true; }
            return changed;
        }
    }

    public bool RequestManagedGuiClose(PlayerSnapshot player, SceneId scene, OverlayId overlay, VirtualChestId chest)
    {
        lock (sync)
        {
            if (!TryGetManagedChestPlayer(player, out var state) || state.Gui is not { } gui || gui.Open.Chest.SceneId != scene ||
                gui.Open.Chest.OverlayId != overlay || gui.Open.Chest.Definition.Id != chest) return false;
            gui.Closing = true;
            return true;
        }
    }

    private static void AddCorrection(ManagedChestPlayer state, int slot)
    {
        // Native inventory sanity checks run first, but retain an independent bounded correction set.
        if (slot >= 0 && slot <= short.MaxValue && state.Corrections.Count < 512) state.Corrections.Add(slot);
    }

    private static bool TryInventorySlot(PlayerInventorySnapshot inventory, int slot, out InventoryItemSnapshot item)
    {
        item = default;
        if ((uint)slot >= (uint)inventory.Items.Count || slot is >= 50 and not ManagedMouseSlot || inventory.Items[slot].Slot != slot) return false;
        item = inventory.Items[slot];
        return true;
    }

    private static ChestItemSnapshot ChestSlot(SceneVirtualChestDefinition chest, int slot) =>
        chest.Items.FirstOrDefault(item => item.Slot == slot) is { ItemType: > 0 } item ? item : new(slot, 0, 0, 0);

    private static bool ValidStack(int type, int stack, byte prefix) => type is >= 0 and <= ushort.MaxValue && stack is >= 0 and <= 9999 &&
        (type == 0 ? stack == 0 && prefix == 0 : stack > 0);

    private static void TryPairManagedGui(ManagedChestPlayer state, ManagedChestGui gui, DateTimeOffset now)
    {
        if (gui.ChestProposal is not { } chest || gui.InventoryProposal is not { } inventory || state.Inventory is not { } snapshot) return;
        if (now - gui.PendingAt!.Value > GuiPairLifetime || !TryInventorySlot(snapshot, inventory.Slot, out var beforeInventory) ||
            !TryPlanBalancedChestMovement(ChestSlot(gui.Open.Chest.Definition, chest.Slot), chest, beforeInventory, inventory, out var direction, out int amount))
        { gui.Closing = true; return; }
        var transfer = new SceneVirtualChestTransferRequest(gui.Open with { RequestedAt = now }, snapshot.Revision, chest.Slot, inventory.Slot, amount, direction);
        gui.Ready = new(Guid.NewGuid(), transfer);
        gui.PendingAt = now;
    }

    // Conservative stock-GUI slice: one source slot and one compatible destination slot;
    // no swaps, sorting, splitting across multiple locations, or favorite flag changes.
    internal static bool TryPlanBalancedChestMovement(ChestItemSnapshot beforeChest, ChestItemSnapshot afterChest,
        InventoryItemSnapshot beforeInventory, InventorySlotReplacement afterInventory, out VirtualChestTransferDirection direction, out int amount)
    {
        direction = default;
        amount = 0;
        if (beforeChest.Slot != afterChest.Slot || beforeInventory.Slot != afterInventory.Slot || beforeInventory.Favorited != afterInventory.Favorited ||
            !ValidStack(beforeChest.ItemType, beforeChest.Stack, beforeChest.Prefix) || !ValidStack(afterChest.ItemType, afterChest.Stack, afterChest.Prefix) ||
            !ValidStack(beforeInventory.ItemType, beforeInventory.Stack, beforeInventory.Prefix) || !ValidStack(afterInventory.ItemType, afterInventory.Stack, afterInventory.Prefix)) return false;
        int delta = beforeChest.Stack - afterChest.Stack;
        if (delta == 0 || (long)afterInventory.Stack - beforeInventory.Stack != delta) return false;
        int type = delta > 0 ? beforeChest.ItemType : beforeInventory.ItemType;
        byte prefix = delta > 0 ? beforeChest.Prefix : beforeInventory.Prefix;
        if (type == 0 || delta < 0 && beforeInventory.Favorited ||
            !Compatible(beforeChest.ItemType, beforeChest.Prefix, type, prefix) || !Compatible(afterChest.ItemType, afterChest.Prefix, type, prefix) ||
            !Compatible(beforeInventory.ItemType, beforeInventory.Prefix, type, prefix) || !Compatible(afterInventory.ItemType, afterInventory.Prefix, type, prefix)) return false;
        direction = delta > 0 ? VirtualChestTransferDirection.Withdraw : VirtualChestTransferDirection.Deposit;
        amount = Math.Abs(delta);
        return true;
    }

    private static bool Compatible(int type, byte prefix, int expectedType, byte expectedPrefix) => type == 0 || type == expectedType && prefix == expectedPrefix;

    public bool TryTakeManagedGuiTransfer(SceneId scene, OverlayId overlay, VirtualChestId chest, out SceneVirtualChestGuiTransferIntent? intent)
    {
        lock (sync)
        {
            foreach (var state in managedChestPlayers.Values)
                if (state.Gui is { Closing: false, Delivered: false, Ready: { } ready } gui && ready.Transfer.Open.Chest.SceneId == scene &&
                    ready.Transfer.Open.Chest.OverlayId == overlay && ready.Transfer.Open.Chest.Definition.Id == chest &&
                    DateTimeOffset.UtcNow - ready.Transfer.Open.RequestedAt <= VirtualInteractionLifetime)
                { gui.Delivered = true; intent = ready; return true; }
            intent = null;
            return false;
        }
    }

    public bool TryClaimManagedGuiTransfer(SceneVirtualChestGuiTransferIntent intent, out SceneVirtualChestTransferRequest transfer)
    {
        transfer = default;
        var open = intent.Transfer.Open;
        lock (sync)
        {
            if (!managedChestPlayers.TryGetValue((open.Chest.Identity.WorldId, open.Observer.Player), out var state) || state.Session != open.Observer.Session ||
                state.Gui is not { Closing: false, Delivered: true, Claimed: false, Ready: { } ready } gui || ready != intent ||
                DateTimeOffset.UtcNow - ready.Transfer.Open.RequestedAt > VirtualInteractionLifetime) return false;
            gui.Claimed = true;
            transfer = ready.Transfer;
            return true;
        }
    }

    public bool RejectManagedGuiTransfer(SceneVirtualChestGuiTransferIntent intent)
    {
        var open = intent.Transfer.Open;
        lock (sync)
        {
            if (!managedChestPlayers.TryGetValue((open.Chest.Identity.WorldId, open.Observer.Player), out var state) || state.Session != open.Observer.Session ||
                state.Gui is not { Closing: false, Delivered: true, Claimed: false, Ready: { } ready } gui || ready != intent) return false;
            gui.Closing = true;
            return true;
        }
    }

    public bool CompleteManagedGuiTransfer(SceneVirtualChestGuiTransferIntent intent, SceneVirtualChestTransferResult result,
        PlayerInventorySnapshot? inventory, out short index, out SceneVirtualChestSnapshot? chest)
    {
        index = -1; chest = null;
        var open = intent.Transfer.Open;
        lock (sync)
        {
            if (!managedChestPlayers.TryGetValue((open.Chest.Identity.WorldId, open.Observer.Player), out var state) || state.Session != open.Observer.Session ||
                state.Gui is not { Claimed: true, Ready: { } ready } gui || ready != intent) return false;
            if (result.Status != VirtualChestTransferStatus.Applied || result.Chest is not { } updated || inventory is null ||
                inventory.Revision != result.InventoryRevision || inventory.SessionId != state.Session || inventory.PlayerId != open.Observer.Player || inventory.WorldId != open.Chest.Identity.WorldId)
            { gui.Closing = true; return false; }
            state.Inventory = inventory;
            gui.Open = open with { Chest = open.Chest with { Definition = updated }, RequestedAt = DateTimeOffset.UtcNow };
            gui.LastActivity = DateTimeOffset.UtcNow;
            gui.PendingAt = null;
            gui.InventoryProposal = null;
            gui.ChestProposal = null;
            gui.Ready = null;
            gui.Delivered = gui.Claimed = false;
            index = gui.Index;
            chest = gui.Open.Chest;
            return !gui.Closing;
        }
    }

    public void TickManagedChestGuis(IPlayerDirectory players, DateTimeOffset now)
    {
        if (Volatile.Read(ref managedChestCount) == 0) return;
        List<(IVirtualChestGuiHost Host, PlayerSnapshot Player, short Index, ChestIdentity? Closed, int[] Corrections)>? work = null;
        List<SceneRenderRequest>? displayRedraws = null;
        lock (sync)
        {
            foreach (var pair in managedChestPlayers)
            {
                var state = pair.Value;
                if (state.Gui is null && state.Display is null && state.Corrections.Count == 0) continue;
                if (!players.TryGet(pair.Key.Player, out var player) || player is null || player.WorldId != pair.Key.World || player.SessionId != state.Session) continue;
                ChestIdentity? closed = null;
                short index = -1;
                if (TryExpireManagedDisplay(state,player,now,out var displayRedraw) && displayRedraw is { } request)
                    (displayRedraws ??= []).Add(request);
                if (state.Gui is { } gui && (gui.Closing || !CurrentGuiChest(player, gui.Open, out _) || now - gui.LastActivity > GuiIdleLifetime ||
                    gui.PendingAt is { } pending && now - pending > (gui.Ready is null ? GuiPairLifetime : VirtualInteractionLifetime)))
                {
                    closed = gui.Open.Chest.Identity;
                    index = gui.Index;
                    state.Gui = null;
                    for (int slot = 0; slot <= ManagedMouseSlot; slot++) AddCorrection(state, slot);
                }
                if (closed is null && state.Corrections.Count == 0) continue;
                (work ??= []).Add((state.Host, player, index, closed, state.Corrections.ToArray()));
                state.Corrections.Clear();
            }
        }
        if (work is not null)
            foreach (var entry in work)
                entry.Host.SynchronizeManagedGui(entry.Player, entry.Index, entry.Closed, entry.Corrections);
        if (displayRedraws is not null && sink is not null)
            foreach (var request in displayRedraws) sink.TryInvalidate(request);
    }

    private void RemoveManagedChestPlayer(WorldId world, PlayerId player)
    {
        if (managedChestPlayers.Remove((world, player))) Interlocked.Decrement(ref managedChestCount);
    }
}
