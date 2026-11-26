using Terraria.ID;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;

namespace Vega.Terraria.Players;

public sealed class TerrariaPlayerInventoryService(IGameThreadDispatcher dispatcher, TerrariaPlayerBoundary players) : IPlayerInventoryService
{
    private readonly long[] revisions = new long[global::Terraria.Main.maxPlayers];
    private readonly long[] fingerprints = new long[global::Terraria.Main.maxPlayers];
    private readonly bool[] initialized = new bool[global::Terraria.Main.maxPlayers];
    private readonly PlayerSessionId?[] generations = new PlayerSessionId?[global::Terraria.Main.maxPlayers];

    public ValueTask<PlayerInventorySnapshot?> CaptureAsync(
        PlayerId playerId,
        CancellationToken cancellationToken = default) => players.TryGet(playerId, out var player) && player is not null
        ? CaptureAsync(player, cancellationToken) : ValueTask.FromResult<PlayerInventorySnapshot?>(null);

    public ValueTask<PlayerInventorySnapshot?> CaptureAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
        DispatchAsync(
            "inventory.capture",
            () => Capture(player),
            cancellationToken);

    public ValueTask<InventoryMutationResult> ReplaceSlotAsync(
        PlayerId playerId,
        long expectedRevision,
        InventorySlotReplacement replacement,
        CancellationToken cancellationToken = default) => players.TryGet(playerId, out var player) && player is not null
        ? ReplaceSlotAsync(player, expectedRevision, replacement, cancellationToken)
        : ValueTask.FromResult(new InventoryMutationResult(InventoryMutationStatus.PlayerNotFound, ErrorCode: "player.not_found"));

    public ValueTask<InventoryMutationResult> ReplaceSlotAsync(PlayerSnapshot player, long expectedRevision, InventorySlotReplacement replacement,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(
            "inventory.replace-slot",
            () => ReplaceSlot(player, expectedRevision, replacement),
            cancellationToken,
            new InventoryMutationResult(
                InventoryMutationStatus.DispatcherUnavailable,
                ErrorCode: "inventory.dispatcher_unavailable"),
            new InventoryMutationResult(
                InventoryMutationStatus.Cancelled,
                ErrorCode: "inventory.cancelled"));

    private PlayerInventorySnapshot? Capture(PlayerSnapshot expected)
    {
        dispatcher.AssertGameThread();
        if (!players.TryGetSessionPlayer(expected, out var player))
        {
            return null;
        }

        return CreateSnapshot(expected, player, RefreshRevision(expected, player));
    }

    private InventoryMutationResult ReplaceSlot(
        PlayerSnapshot expected,
        long expectedRevision,
        InventorySlotReplacement replacement)
    {
        dispatcher.AssertGameThread();
        if (!players.TryGetSessionPlayer(expected, out var player))
        {
            return new InventoryMutationResult(
                InventoryMutationStatus.PlayerNotFound,
                ErrorCode: "player.not_found");
        }

        if ((uint)replacement.Slot >= (uint)player.inventory.Length ||
            replacement.ItemType < 0 || replacement.ItemType >= ItemID.Count ||
            replacement.Stack < 0 || replacement.Prefix >= PrefixID.Count)
        {
            return new InventoryMutationResult(
                InventoryMutationStatus.InvalidItem,
                ErrorCode: "inventory.invalid_item");
        }

        long currentRevision = RefreshRevision(expected, player);
        if (currentRevision != expectedRevision)
        {
            return new InventoryMutationResult(
                InventoryMutationStatus.RevisionConflict,
                CreateSnapshot(expected, player, currentRevision),
                "inventory.revision_conflict");
        }

        var item = new global::Terraria.Item();
        item.SetDefaults(replacement.ItemType);
        if (replacement.ItemType == 0 ? replacement.Stack != 0 || replacement.Prefix != 0 :
            replacement.Stack < 1 || replacement.Stack > item.maxStack || replacement.Prefix != 0 && !item.Prefix(replacement.Prefix))
        {
            return new InventoryMutationResult(InventoryMutationStatus.InvalidItem, ErrorCode: "inventory.invalid_stack_or_prefix");
        }
        item.stack = replacement.Stack;
        item.favorited = replacement.Favorited;
        player.inventory[replacement.Slot] = item;
        fingerprints[expected.Id.Slot] = Fingerprint(player);
        long revision = ++revisions[expected.Id.Slot];
        global::Terraria.NetMessage.SendData(
            5,
            -1,
            -1,
            null,
            expected.Id.Slot,
            replacement.Slot,
            item.prefix);
        return new InventoryMutationResult(
            InventoryMutationStatus.Applied,
            CreateSnapshot(expected, player, revision));
    }

    private PlayerInventorySnapshot CreateSnapshot(
        PlayerSnapshot expected,
        global::Terraria.Player player,
        long revision) =>
        new(
            expected.Id,
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
            DateTimeOffset.UtcNow) { SessionId = expected.SessionId, WorldId = expected.WorldId };

    private long RefreshRevision(PlayerSnapshot expected, global::Terraria.Player player)
    {
        long fingerprint = Fingerprint(player);
        int slot = expected.Id.Slot;
        if (!initialized[slot])
        {
            initialized[slot] = true;
            fingerprints[slot] = fingerprint;
            generations[slot] = expected.SessionId;
            revisions[slot] = 1;
        }
        else if (generations[slot] != expected.SessionId || fingerprints[slot] != fingerprint)
        {
            fingerprints[slot] = fingerprint;
            generations[slot] = expected.SessionId;
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
                () =>
                {
                    try { completion.TrySetResult(cancellationToken.IsCancellationRequested ? cancelled! : action()); }
                    catch (Exception ex) { completion.TrySetException(ex); }
                }))
        {
            return ValueTask.FromResult(unavailable!);
        }

        return new ValueTask<T>(completion.Task.WaitAsync(cancellationToken));
    }
}
