using Terraria.ID;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;

namespace Vega.Terraria.Players;

public sealed class TerrariaPlayerInventoryService(IGameThreadDispatcher dispatcher) : IPlayerInventoryService
{
    // Terraria 1.4.5.7 / protocol 325 has 255 player slots. Do not read Main.maxPlayers here:
    // this service is composed before Terraria's save path and Main static initialization are ready.
    private const int PlayerCapacity = 255;
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
        DispatchAsync(
            "inventory.replace-slot",
            () => ReplaceSlot(playerId, expectedRevision, replacement),
            cancellationToken,
            new InventoryMutationResult(
                InventoryMutationStatus.DispatcherUnavailable,
                ErrorCode: "inventory.dispatcher_unavailable"),
            new InventoryMutationResult(
                InventoryMutationStatus.Cancelled,
                ErrorCode: "inventory.cancelled"));

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
            return new InventoryMutationResult(
                InventoryMutationStatus.PlayerNotFound,
                ErrorCode: "player.not_found");
        }

        if ((uint)replacement.Slot >= (uint)player.inventory.Length ||
            replacement.ItemType < 0 || replacement.ItemType >= ItemID.Count ||
            replacement.Stack < 0)
        {
            return new InventoryMutationResult(
                InventoryMutationStatus.InvalidItem,
                ErrorCode: "inventory.invalid_item");
        }

        long currentRevision = RefreshRevision(playerId, player);
        if (currentRevision != expectedRevision)
        {
            return new InventoryMutationResult(
                InventoryMutationStatus.RevisionConflict,
                CreateSnapshot(playerId, player, currentRevision),
                "inventory.revision_conflict");
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
        fingerprints[playerId.Slot] = Fingerprint(player);
        long revision = ++revisions[playerId.Slot];
        global::Terraria.NetMessage.SendData(
            5,
            -1,
            -1,
            null,
            playerId.Slot,
            replacement.Slot,
            item.prefix);
        return new InventoryMutationResult(
            InventoryMutationStatus.Applied,
            CreateSnapshot(playerId, player, revision));
    }

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
            revisions[slot]++;
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
