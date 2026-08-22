using System.Collections.Concurrent;
using Terraria.ID;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Worlds;

public sealed class TerrariaChestBackend(IGameThreadDispatcher dispatcher) : IChestBackend
{
    private readonly ConcurrentDictionary<ChestIdentity, VersionState> versions = new();

    public ValueTask<ChestSnapshot?> CaptureAsync(
        ChestIdentity identity,
        CancellationToken cancellationToken = default) =>
        DispatchAsync("chest.capture", () => Capture(identity), cancellationToken);

    public ValueTask<ChestMutationResult> ReplaceAsync(
        ChestMutationRequest request,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(
            "chest.replace",
            () => Replace(request),
            cancellationToken,
            new ChestMutationResult(ChestMutationStatus.Failed, ErrorCode: "chest.dispatcher_unavailable"),
            new ChestMutationResult(ChestMutationStatus.Cancelled, ErrorCode: "chest.cancelled"));

    private ChestSnapshot? Capture(ChestIdentity identity)
    {
        dispatcher.AssertGameThread();
        if (identity.WorldId != WorldId.Primary || !TryFind(identity, out _, out global::Terraria.Chest? chest) || chest is null)
        {
            return null;
        }

        long fingerprint = Fingerprint(chest);
        VersionState state = versions.AddOrUpdate(
            identity,
            static (_, value) => new VersionState(1, value),
            static (_, current, value) => current.Fingerprint == value
                ? current
                : new VersionState(checked(current.Revision + 1), value),
            fingerprint);
        return Snapshot(identity, chest, state.Revision);
    }

    private ChestMutationResult Replace(ChestMutationRequest request)
    {
        dispatcher.AssertGameThread();
        ChestSnapshot? before = Capture(request.Identity);
        if (before is null || !TryFind(request.Identity, out int chestIndex, out global::Terraria.Chest? chest) || chest is null)
        {
            return new ChestMutationResult(ChestMutationStatus.NotFound, ErrorCode: "chest.not_found");
        }

        if (before.Revision != request.ExpectedRevision)
        {
            return new ChestMutationResult(
                ChestMutationStatus.RevisionConflict,
                before,
                ErrorCode: "chest.revision_conflict");
        }

        if (request.Items.Count > chest.item.Length ||
            request.Items.Any(item =>
                (uint)item.Slot >= (uint)chest.item.Length ||
                item.ItemType < 0 || item.ItemType >= ItemID.Count || item.Stack < 0) ||
            request.Items.Select(static item => item.Slot).Distinct().Count() != request.Items.Count)
        {
            return new ChestMutationResult(
                ChestMutationStatus.Invalid,
                before,
                ErrorCode: "chest.invalid_items");
        }

        foreach (global::Terraria.Item item in chest.item)
        {
            item.TurnToAir();
        }

        foreach (ChestItemSnapshot replacement in request.Items)
        {
            global::Terraria.Item item = chest.item[replacement.Slot];
            item.SetDefaults(replacement.ItemType);
            item.stack = replacement.ItemType == 0
                ? 0
                : Math.Clamp(replacement.Stack, 1, Math.Max(1, item.maxStack));
            if (replacement.Prefix != 0)
            {
                item.Prefix(replacement.Prefix);
            }
        }

        if (request.Name is not null)
        {
            chest.name = request.Name.Trim();
        }

        VersionState state = versions.AddOrUpdate(
            request.Identity,
            static (_, value) => new VersionState(1, value),
            static (_, current, value) => new VersionState(checked(current.Revision + 1), value),
            Fingerprint(chest));
        for (var slot = 0; slot < chest.item.Length; slot++)
        {
            global::Terraria.NetMessage.SendData(32, -1, -1, null, chestIndex, slot);
        }

        return new ChestMutationResult(
            ChestMutationStatus.Applied,
            before,
            Snapshot(request.Identity, chest, state.Revision));
    }

    private static ChestSnapshot Snapshot(
        ChestIdentity identity,
        global::Terraria.Chest chest,
        long revision) =>
        new(
            identity,
            chest.name ?? string.Empty,
            revision,
            Array.AsReadOnly(chest.item
                .Select((item, slot) => new ChestItemSnapshot(slot, item.type, item.stack, item.prefix))
                .ToArray()),
            DateTimeOffset.UtcNow);

    private static bool TryFind(
        ChestIdentity identity,
        out int index,
        out global::Terraria.Chest? chest)
    {
        index = global::Terraria.Chest.FindChest(identity.X, identity.Y);
        chest = (uint)index < (uint)global::Terraria.Main.chest.Length
            ? global::Terraria.Main.chest[index]
            : null;
        return chest is not null;
    }

    private static long Fingerprint(global::Terraria.Chest chest)
    {
        var hash = new HashCode();
        hash.Add(chest.name, StringComparer.Ordinal);
        foreach (global::Terraria.Item item in chest.item)
        {
            hash.Add(item.type);
            hash.Add(item.stack);
            hash.Add(item.prefix);
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
                () => completion.TrySetResult(cancellationToken.IsCancellationRequested ? cancelled! : action())))
        {
            return ValueTask.FromResult(unavailable!);
        }

        return new ValueTask<T>(completion.Task);
    }

    private sealed record VersionState(long Revision, long Fingerprint);
}
