using Terraria.DataStructures;
using Terraria.ID;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Worlds;

public sealed class TerrariaWorldNpcService(
    IGameThreadDispatcher dispatcher,
    WorldId worldId) : IWorldNpcService
{
    private readonly long[] revisions = new long[global::Terraria.Main.maxNPCs];
    private readonly long[] fingerprints = new long[global::Terraria.Main.maxNPCs];
    private readonly bool[] initialized = new bool[global::Terraria.Main.maxNPCs];

    public ValueTask<IReadOnlyList<NpcSnapshot>> SnapshotAsync(
        WorldId requestedWorldId,
        CancellationToken cancellationToken = default) =>
        DispatchAsync<IReadOnlyList<NpcSnapshot>>(
            "world.npcs.snapshot",
            () => Snapshot(requestedWorldId),
            cancellationToken,
            []);

    public ValueTask<NpcSnapshot?> CaptureAsync(
        NpcHandle handle,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(
            "world.npcs.capture",
            () => Capture(handle),
            cancellationToken);

    public ValueTask<NpcOperationResult> SpawnAsync(
        NpcSpawnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return DispatchAsync(
            "world.npcs.spawn",
            () => Spawn(request),
            cancellationToken,
            Unavailable(),
            Cancelled());
    }

    public ValueTask<NpcOperationResult> TeleportAsync(
        NpcTeleportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return DispatchAsync(
            "world.npcs.teleport",
            () => Teleport(request),
            cancellationToken,
            Unavailable(),
            Cancelled());
    }

    public ValueTask<NpcOperationResult> DespawnAsync(
        NpcDespawnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return DispatchAsync(
            "world.npcs.despawn",
            () => Despawn(request),
            cancellationToken,
            Unavailable(),
            Cancelled());
    }

    private IReadOnlyList<NpcSnapshot> Snapshot(WorldId requestedWorldId)
    {
        dispatcher.AssertGameThread();
        if (requestedWorldId != worldId)
        {
            return [];
        }

        var result = new List<NpcSnapshot>();
        for (var slot = 0; slot < global::Terraria.Main.npc.Length; slot++)
        {
            global::Terraria.NPC npc = global::Terraria.Main.npc[slot];
            if (!npc.active)
            {
                ObserveInactive(slot);
                continue;
            }

            result.Add(CreateSnapshot(slot, npc, RefreshRevision(slot, npc)));
        }

        return result.AsReadOnly();
    }

    private NpcSnapshot? Capture(NpcHandle handle)
    {
        dispatcher.AssertGameThread();
        if (handle.WorldId != worldId || !TryGetActive(handle.Slot, out global::Terraria.NPC? npc) || npc is null)
        {
            return null;
        }

        long revision = RefreshRevision(handle.Slot, npc);
        return handle.Type == npc.type && handle.Revision == revision
            ? CreateSnapshot(handle.Slot, npc, revision)
            : null;
    }

    private NpcOperationResult Spawn(NpcSpawnRequest request)
    {
        dispatcher.AssertGameThread();
        if (request.WorldId != worldId ||
            request.Type <= 0 || request.Type >= NPCID.Count ||
            !float.IsFinite(request.Position.X) || !float.IsFinite(request.Position.Y))
        {
            return Invalid("npc.invalid_spawn");
        }

        int slot = global::Terraria.NPC.NewNPC(
            new EntitySource_DebugCommand(),
            (int)MathF.Round(request.Position.X),
            (int)MathF.Round(request.Position.Y),
            request.Type);
        if ((uint)slot >= (uint)global::Terraria.Main.npc.Length)
        {
            return new NpcOperationResult(NpcOperationStatus.Failed, ErrorCode: "npc.spawn_failed");
        }

        global::Terraria.NPC npc = global::Terraria.Main.npc[slot];
        if (request.TargetPlayerSlot is int target &&
            (uint)target < (uint)global::Terraria.Main.maxPlayers)
        {
            npc.target = target;
        }

        npc.netUpdate = true;
        initialized[slot] = false;
        long revision = RefreshRevision(slot, npc);
        if (global::Terraria.Main.netMode == 2)
        {
            global::Terraria.NetMessage.SendData(MessageID.SyncNPC, -1, -1, null, slot);
        }

        return new NpcOperationResult(
            NpcOperationStatus.Applied,
            CreateSnapshot(slot, npc, revision));
    }

    private NpcOperationResult Teleport(NpcTeleportRequest request)
    {
        dispatcher.AssertGameThread();
        if (!float.IsFinite(request.Position.X) || !float.IsFinite(request.Position.Y))
        {
            return Invalid("npc.invalid_position");
        }

        if (!TryExpected(request.Expected, out global::Terraria.NPC? npc, out NpcOperationResult? failure))
        {
            return failure!;
        }

        int slot = request.Expected.Slot;
        npc!.position.X = request.Position.X;
        npc.position.Y = request.Position.Y;
        npc.netUpdate = true;
        fingerprints[slot] = Fingerprint(npc);
        long revision = ++revisions[slot];
        if (global::Terraria.Main.netMode == 2)
        {
            global::Terraria.NetMessage.SendData(MessageID.SyncNPC, -1, -1, null, slot);
        }

        return new NpcOperationResult(
            NpcOperationStatus.Applied,
            CreateSnapshot(slot, npc, revision));
    }

    private NpcOperationResult Despawn(NpcDespawnRequest request)
    {
        dispatcher.AssertGameThread();
        if (!TryExpected(request.Expected, out global::Terraria.NPC? npc, out NpcOperationResult? failure))
        {
            return failure!;
        }

        int slot = request.Expected.Slot;
        NpcSnapshot before = CreateSnapshot(slot, npc!, revisions[slot]);
        npc!.active = false;
        npc.netUpdate = true;
        fingerprints[slot] = Fingerprint(npc);
        revisions[slot]++;
        if (global::Terraria.Main.netMode == 2)
        {
            global::Terraria.NetMessage.SendData(MessageID.SyncNPC, -1, -1, null, slot);
        }

        return new NpcOperationResult(NpcOperationStatus.Applied, before);
    }

    private bool TryExpected(
        NpcHandle expected,
        out global::Terraria.NPC? npc,
        out NpcOperationResult? failure)
    {
        npc = null;
        failure = null;
        if (expected.WorldId != worldId)
        {
            failure = Invalid("npc.wrong_world");
            return false;
        }

        if (!TryGetActive(expected.Slot, out npc) || npc is null)
        {
            failure = new NpcOperationResult(NpcOperationStatus.NotFound, ErrorCode: "npc.not_found");
            return false;
        }

        long revision = RefreshRevision(expected.Slot, npc);
        if (expected.Type != npc.type || expected.Revision != revision)
        {
            failure = new NpcOperationResult(
                NpcOperationStatus.RevisionConflict,
                CreateSnapshot(expected.Slot, npc, revision),
                "npc.revision_conflict");
            return false;
        }

        return true;
    }

    private NpcSnapshot CreateSnapshot(int slot, global::Terraria.NPC npc, long revision) => new(
        new NpcHandle(worldId, slot, npc.type, revision),
        npc.netID,
        global::Terraria.Lang.GetNPCNameValue(npc.netID),
        new PlayerPosition(npc.position.X, npc.position.Y),
        new WorldVelocity(npc.velocity.X, npc.velocity.Y),
        npc.life,
        npc.lifeMax,
        npc.friendly,
        npc.townNPC,
        npc.boss,
        npc.target,
        DateTimeOffset.UtcNow);

    private long RefreshRevision(int slot, global::Terraria.NPC npc)
    {
        long fingerprint = Fingerprint(npc);
        if (!initialized[slot])
        {
            initialized[slot] = true;
            fingerprints[slot] = fingerprint;
            revisions[slot] = checked(revisions[slot] + 1);
        }
        else if (fingerprints[slot] != fingerprint)
        {
            fingerprints[slot] = fingerprint;
            revisions[slot] = checked(revisions[slot] + 1);
        }

        return revisions[slot];
    }

    private void ObserveInactive(int slot)
    {
        if (!initialized[slot])
        {
            return;
        }

        initialized[slot] = false;
        revisions[slot] = checked(revisions[slot] + 1);
        fingerprints[slot] = 0;
    }

    private static long Fingerprint(global::Terraria.NPC npc)
    {
        var hash = new HashCode();
        hash.Add(npc.active);
        hash.Add(npc.type);
        hash.Add(npc.netID);
        hash.Add(npc.life);
        hash.Add(npc.position.X);
        hash.Add(npc.position.Y);
        hash.Add(npc.velocity.X);
        hash.Add(npc.velocity.Y);
        hash.Add(npc.target);
        return hash.ToHashCode();
    }

    private static bool TryGetActive(int slot, out global::Terraria.NPC? npc)
    {
        if ((uint)slot < (uint)global::Terraria.Main.npc.Length &&
            global::Terraria.Main.npc[slot] is { active: true } active)
        {
            npc = active;
            return true;
        }

        npc = null;
        return false;
    }

    private static NpcOperationResult Invalid(string code) =>
        new(NpcOperationStatus.Invalid, ErrorCode: code);

    private static NpcOperationResult Unavailable() =>
        new(NpcOperationStatus.DispatcherUnavailable, ErrorCode: "npc.dispatcher_unavailable");

    private static NpcOperationResult Cancelled() =>
        new(NpcOperationStatus.Cancelled, ErrorCode: "npc.cancelled");

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
