using Terraria.ID;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;

namespace Vega.Terraria.Players;

public sealed class TerrariaPlayerInventoryService(IGameThreadDispatcher dispatcher) : IPlayerInventoryGrantService
{
    // Terraria 1.4.5.8 / protocol 326 has 255 player slots. Do not read Main.maxPlayers here:
    // this service is composed before Terraria's save path and Main static initialization are ready.
    private const int PlayerCapacity = 255;
    private const int MainInventorySlotCount = 50;
    private readonly long[] revisions = new long[PlayerCapacity];
    private readonly long[] fingerprints = new long[PlayerCapacity];
    private readonly bool[] initialized = new bool[PlayerCapacity];

    public ValueTask<PlayerInventorySnapshot?> CaptureAsync(
        PlayerId playerId,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(
            "inventory.capture",
            () => Capture(playerId),
            cancellationToken);

    public ValueTask<InventoryMutationResult> ReplaceSlotAsync(
        PlayerId playerId,
        long expectedRevision,
        InventorySlotReplacement replacement,
        CancellationToken cancellationToken = default) =>
        DispatchMutationAsync(
            "inventory.replace-slot",
            () => ReplaceSlot(playerId, expectedRevision, replacement),
            cancellationToken);

    public ValueTask<InventoryMutationResult> GrantAsync(
        PlayerId playerId,
        long expectedRevision,
        InventoryGrantRequest request,
        CancellationToken cancellationToken = default) =>
        DispatchMutationAsync(
            "inventory.grant",
            () => Grant(playerId, expectedRevision, request),
            cancellationToken);

    private PlayerInventorySnapshot? Capture(PlayerId playerId)
    {
        dispatcher.AssertGameThread();
        if (!TryGetPlayer(playerId, out global::Terraria.Player? player) || player is null)
        {
            return null;
        }

        return CreateSnapshot(playerId, player, RefreshRevision(playerId, player));
    }

    private InventoryMutationResult ReplaceSlot(
        PlayerId playerId,
        long expectedRevision,
        InventorySlotReplacement replacement)
    {
        dispatcher.AssertGameThread();
        if (!TryGetPlayer(playerId, out global::Terraria.Player? player) || player is null)
        {
            return PlayerNotFound();
        }

        if ((uint)replacement.Slot >= (uint)player.inventory.Length ||
            replacement.ItemType < 0 || replacement.ItemType >= ItemID.Count ||
            replacement.Stack < 0 || replacement.Prefix >= PrefixID.Count)
        {
            return InvalidItem();
        }

        long currentRevision = RefreshRevision(playerId, player);
        if (currentRevision != expectedRevision)
        {
            return RevisionConflict(playerId, player, currentRevision);
        }

        global::Terraria.Item item = player.inventory[replacement.Slot];
        item.SetDefaults(replacement.ItemType);
        item.stack = replacement.ItemType == 0
            ? 0
            : Math.Clamp(replacement.Stack, 1, Math.Max(1, item.maxStack));
        if (replacement.Prefix != 0)
        {
            item.Prefix(replacement.Prefix);
        }

        item.favorited = replacement.Favorited;
        SyncSlot(playerId, replacement.Slot, item);
        return Applied(playerId, player);
    }

    private InventoryMutationResult Grant(
        PlayerId playerId,
        long expectedRevision,
        InventoryGrantRequest request)
    {
        dispatcher.AssertGameThread();
        if (!TryGetPlayer(playerId, out global::Terraria.Player? player) || player is null)
        {
            return PlayerNotFound();
        }

        if (request.ItemType <= 0 || request.ItemType >= ItemID.Count ||
            request.Stack <= 0 || request.Prefix >= PrefixID.Count)
        {
            return InvalidItem();
        }

        long currentRevision = RefreshRevision(playerId, player);
        if (currentRevision != expectedRevision)
        {
            return RevisionConflict(playerId, player, currentRevision);
        }

        var prototype = new global::Terraria.Item();
        prototype.SetDefaults(request.ItemType);
        if (request.Prefix != 0)
        {
            prototype.Prefix(request.Prefix);
        }

        int maxStack = Math.Max(1, prototype.maxStack);
        int slotCount = Math.Min(MainInventorySlotCount, player.inventory.Length);
        long capacity = 0;
        for (int slot = 0; slot < slotCount; slot++)
        {
            global::Terraria.Item item = player.inventory[slot];
            if (item.type == request.ItemType && item.prefix == prototype.prefix && item.stack > 0)
            {
                capacity += Math.Max(0, Math.Max(1, item.maxStack) - item.stack);
            }
            else if (item.type == ItemID.None || item.stack <= 0)
            {
                capacity += maxStack;
            }
        }

        if (capacity < request.Stack)
        {
            return new InventoryMutationResult(
                InventoryMutationStatus.InventoryFull,
                CreateSnapshot(playerId, player, currentRevision),
                "inventory.full");
        }

        int remaining = request.Stack;
        for (int slot = 0; slot < slotCount && remaining > 0; slot++)
        {
            global::Terraria.Item item = player.inventory[slot];
            if (item.type != request.ItemType || item.prefix != prototype.prefix || item.stack <= 0)
            {
                continue;
            }

            int free = Math.Max(0, Math.Max(1, item.maxStack) - item.stack);
            if (free == 0)
            {
                continue;
            }

            int added = Math.Min(free, remaining);
            item.stack += added;
            remaining -= added;
            SyncSlot(playerId, slot, item);
        }

        for (int slot = 0; slot < slotCount && remaining > 0; slot++)
        {
            global::Terraria.Item item = player.inventory[slot];
            if (item.type != ItemID.None && item.stack > 0)
            {
                continue;
            }

            int added = Math.Min(maxStack, remaining);
            item.SetDefaults(request.ItemType);
            if (request.Prefix != 0)
            {
                item.Prefix(request.Prefix);
            }

            item.stack = added;
            item.favorited = false;
            remaining -= added;
            SyncSlot(playerId, slot, item);
        }

        if (remaining != 0)
        {
            // Capacity is calculated before mutation, so reaching this branch means Terraria changed a slot
            // synchronously during the grant. Fail loudly rather than claiming a complete delivery.
            return new InventoryMutationResult(
                InventoryMutationStatus.Failed,
                CreateSnapshot(playerId, player, RefreshRevision(playerId, player)),
                "inventory.grant_incomplete");
        }

        return Applied(playerId, player);
    }

    private InventoryMutationResult Applied(PlayerId playerId, global::Terraria.Player player)
    {
        fingerprints[playerId.Slot] = Fingerprint(player);
        long revision = checked(++revisions[playerId.Slot]);
        return new InventoryMutationResult(
            InventoryMutationStatus.Applied,
            CreateSnapshot(playerId, player, revision));
    }

    private InventoryMutationResult RevisionConflict(
        PlayerId playerId,
        global::Terraria.Player player,
        long revision) =>
        new(
            InventoryMutationStatus.RevisionConflict,
            CreateSnapshot(playerId, player, revision),
            "inventory.revision_conflict");

    private static InventoryMutationResult PlayerNotFound() =>
        new(InventoryMutationStatus.PlayerNotFound, ErrorCode: "player.not_found");

    private static InventoryMutationResult InvalidItem() =>
        new(InventoryMutationStatus.InvalidItem, ErrorCode: "inventory.invalid_item");

    private static void SyncSlot(PlayerId playerId, int slot, global::Terraria.Item item) =>
        global::Terraria.NetMessage.SendData(
            5,
            -1,
            -1,
            null,
            playerId.Slot,
            slot,
            item.prefix);

    private PlayerInventorySnapshot CreateSnapshot(
        PlayerId playerId,
        global::Terraria.Player player,
        long revision) =>
        new(
            playerId,
            player.name,
            revision,
            Array.AsReadOnly(player.inventory
                .Select((item, slot) => new InventoryItemSnapshot(
                    slot,
                    item.type,
                    item.stack,
                    item.prefix,
                    item.favorited,
                    item.Name))
                .ToArray()),
            DateTimeOffset.UtcNow);

    private long RefreshRevision(PlayerId playerId, global::Terraria.Player player)
    {
        long fingerprint = Fingerprint(player);
        int slot = playerId.Slot;
        if (!initialized[slot])
        {
            initialized[slot] = true;
            fingerprints[slot] = fingerprint;
            revisions[slot] = 1;
        }
        else if (fingerprints[slot] != fingerprint)
        {
            fingerprints[slot] = fingerprint;
            revisions[slot] = checked(revisions[slot] + 1);
        }

        return revisions[slot];
    }

    private static long Fingerprint(global::Terraria.Player player)
    {
        var hash = new HashCode();
        foreach (global::Terraria.Item item in player.inventory)
        {
            hash.Add(item.type);
            hash.Add(item.stack);
            hash.Add(item.prefix);
            hash.Add(item.favorited);
        }

        return hash.ToHashCode();
    }

    private static bool TryGetPlayer(
        PlayerId playerId,
        out global::Terraria.Player? player)
    {
        if ((uint)playerId.Slot < (uint)global::Terraria.Main.player.Length &&
            global::Terraria.Main.player[playerId.Slot] is { active: true } active)
        {
            player = active;
            return true;
        }

        player = null;
        return false;
    }

    private ValueTask<InventoryMutationResult> DispatchMutationAsync(
        string operation,
        Func<InventoryMutationResult> action,
        CancellationToken cancellationToken) =>
        DispatchAsync(
            operation,
            action,
            cancellationToken,
            new InventoryMutationResult(
                InventoryMutationStatus.DispatcherUnavailable,
                ErrorCode: "inventory.dispatcher_unavailable"),
            new InventoryMutationResult(
                InventoryMutationStatus.Cancelled,
                ErrorCode: "inventory.cancelled"));

    private ValueTask<T> DispatchAsync<T>(
        string operation,
        Func<T> action,
        CancellationToken cancellationToken,
        T? unavailable = default,
        T? cancelled = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(cancelled!);
        }

        if (!dispatcher.IsGameThreadKnown)
        {
            return ValueTask.FromResult(unavailable!);
        }

        if (dispatcher.IsGameThread)
        {
            return ValueTask.FromResult(action());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryPost(
                operation,
                () => completion.TrySetResult(cancellationToken.IsCancellationRequested ? cancelled! : action())))
        {
            return ValueTask.FromResult(unavailable!);
        }

        return new ValueTask<T>(completion.Task);
    }
}
