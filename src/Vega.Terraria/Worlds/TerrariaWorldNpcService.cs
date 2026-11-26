using Terraria.DataStructures;
using Terraria.ID;
using Terraria.Localization;
using Multiplicity.Packets;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Terraria.Players;

namespace Vega.Terraria.Worlds;

public sealed class TerrariaWorldNpcService(
    IGameThreadDispatcher dispatcher,
    WorldId worldId,
    TerrariaPlayerBoundary? players = null) : IWorldNpcService, IWorldNpcCatalog
{
    private readonly long[] revisions = new long[global::Terraria.Main.maxNPCs];
    private readonly long[] fingerprints = new long[global::Terraria.Main.maxNPCs];
    private readonly bool[] initialized = new bool[global::Terraria.Main.maxNPCs];
    private NpcTypeDefinition[]? catalog;

    public ValueTask<IReadOnlyList<NpcTypeDefinition>> FindTypesAsync(string query, CancellationToken cancellationToken = default) =>
        DispatchAsync<IReadOnlyList<NpcTypeDefinition>>("world.npcs.catalog", () => FindTypes(query), cancellationToken, []);

    private IReadOnlyList<NpcTypeDefinition> FindTypes(string query)
    {
        dispatcher.AssertGameThread();
        if (string.IsNullOrWhiteSpace(query)) return [];
        catalog ??= Enumerable.Range(1, NPCID.Count - 1).Select(id => new NpcTypeDefinition(id,
            global::Terraria.Lang.GetNPCNameValue(id), IsBossType(id))).ToArray();
        if (int.TryParse(query, out int id)) return catalog.Where(npc => npc.Type == id).ToArray();
        var exact = catalog.Where(npc => string.Equals(npc.Name, query, StringComparison.OrdinalIgnoreCase)).ToArray();
        return exact.Length != 0 ? exact : catalog.Where(npc => npc.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

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
        // Legacy SDK calls may supply only the target slot. Capture its current session before queueing.
        if (request.TargetPlayer is null && request.TargetPlayerSlot is int targetSlot)
        {
            if (targetSlot < 0 || players is null || !players.TryGet(new PlayerId(targetSlot), out var target) || target?.SessionId is null)
                return ValueTask.FromResult(Invalid("npc.target_unavailable"));
            request = request with { TargetPlayer = target };
        }
        return DispatchAsync(
            "world.npcs.spawn",
            () => Spawn(request),
            cancellationToken,
            Unavailable(),
            Cancelled());
    }

    public ValueTask<NpcBatchOperationResult> ButcherAsync(NpcButcherRequest request, CancellationToken cancellationToken = default) =>
        DispatchAsync("world.npcs.butcher", () => Butcher(request), cancellationToken,
            new(NpcOperationStatus.DispatcherUnavailable, ErrorCode: "npc.dispatcher_unavailable"),
            new(NpcOperationStatus.Cancelled, ErrorCode: "npc.cancelled"));

    public ValueTask<NpcBatchOperationResult> RenameAsync(NpcRenameRequest request, CancellationToken cancellationToken = default) =>
        DispatchAsync("world.npcs.rename", () => Rename(request), cancellationToken,
            new(NpcOperationStatus.DispatcherUnavailable, ErrorCode: "npc.dispatcher_unavailable"),
            new(NpcOperationStatus.Cancelled, ErrorCode: "npc.cancelled"));

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
        for (var slot = 0; slot < Math.Min(revisions.Length, global::Terraria.Main.npc.Length); slot++)
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
            !float.IsFinite(request.Position.X) || !float.IsFinite(request.Position.Y) ||
            request.Position.X < 0 || request.Position.X >= global::Terraria.Main.rightWorld ||
            request.Position.Y < 0 || request.Position.Y >= global::Terraria.Main.bottomWorld)
        {
            return Invalid("npc.invalid_spawn");
        }

        global::Terraria.Player? targetPlayer = null;
        if (request.TargetPlayer is { } expected)
        {
            if (request.TargetPlayerSlot is int requestedSlot && requestedSlot != expected.Id.Slot ||
                players is null || !players.TryGetSessionPlayer(expected, out targetPlayer) || targetPlayer.dead)
                return Invalid("npc.target_session_changed");
        }

        if (request.Boss)
        {
            if (targetPlayer is null || !IsBossType(request.Type)) return Invalid("npc.invalid_boss");
            return SpawnBoss(request.Type, targetPlayer);
        }

        int slot = global::Terraria.NPC.NewNPC(
            new EntitySource_DebugCommand(),
            (int)MathF.Round(request.Position.X),
            (int)MathF.Round(request.Position.Y),
            request.Type);
        if ((uint)slot >= (uint)Math.Min(global::Terraria.Main.maxNPCs, global::Terraria.Main.npc.Length) || !global::Terraria.Main.npc[slot].active)
        {
            return new NpcOperationResult(NpcOperationStatus.Failed, ErrorCode: "npc.spawn_failed");
        }

        global::Terraria.NPC npc = global::Terraria.Main.npc[slot];
        if (targetPlayer is not null)
        {
            npc.target = targetPlayer.whoAmI;
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

    private NpcOperationResult SpawnBoss(int type, global::Terraria.Player player)
    {
        // Detect which native slot was created, rather than claiming success when the native summon declines.
        int count = Math.Min(revisions.Length, global::Terraria.Main.npc.Length);
        var before = global::Terraria.Main.npc.Take(count).Select(n => (n.active, n.generation)).ToArray();
        if (type == NPCID.WallofFlesh)
        {
            if (global::Terraria.Main.wofNPCIndex != -1 || player.position.Y / 16f < global::Terraria.Main.maxTilesY - 205)
                return Invalid("npc.wall_of_flesh_unavailable");
            global::Terraria.NPC.SpawnWOF(player.position);
        }
        else global::Terraria.NPC.SpawnOnPlayer(player.whoAmI, type);

        for (int slot = 0; slot < count; slot++)
        {
            var npc = global::Terraria.Main.npc[slot];
            if (!npc.active || npc.type != type || before[slot] == (npc.active, npc.generation)) continue;
            initialized[slot] = false;
            return new(NpcOperationStatus.Applied, CreateSnapshot(slot, npc, RefreshRevision(slot, npc)));
        }
        return new(NpcOperationStatus.Failed, ErrorCode: "npc.boss_spawn_declined");
    }

    private NpcBatchOperationResult Butcher(NpcButcherRequest request)
    {
        dispatcher.AssertGameThread();
        if (request.WorldId != worldId || request.Type is int type && (type <= 0 || type >= NPCID.Count))
            return new(NpcOperationStatus.Invalid, ErrorCode: "npc.invalid_selection");

        // Snapshot the original generations: a native death can spawn additional NPCs.
        var selected = global::Terraria.Main.npc.Take(revisions.Length)
            .Select((npc, slot) => (npc, slot)).Where(entry => entry.npc.active &&
                (request.Type is null || entry.npc.netID == request.Type))
            .Select(entry => (entry.slot, entry.npc.generation)).ToArray();
        int defeated = 0;
        foreach (var (slot, generation) in selected)
        {
            if (!TryGetActive(slot, out var npc) || npc is null || npc.generation != generation || npc.townNPC || npc.netID == NPCID.TargetDummy || npc.dontTakeDamage)
                continue;
            // Do not trigger hardmode conversion through an administrative cleanup command.
            if (!global::Terraria.Main.hardMode && (npc.type is NPCID.WallofFlesh or NPCID.WallofFleshEye)) continue;
            bool boss = npc.boss || IsBossType(npc.type) || IsBossPart(npc.type) ||
                TryGetActive(npc.realLife, out var head) && head is not null && (head.boss || IsBossType(head.type) || IsBossPart(head.type));
            if (boss && (request.Type is null || !request.IncludeBosses)) continue;

            int damage = (int)Math.Clamp((long)npc.life + Math.Max(0, npc.defense) + 1, 1, int.MaxValue);
            npc.StrikeNPCNoInteraction(damage, 0, 0);
            bool sameGeneration = global::Terraria.Main.npc[slot].generation == generation;
            if (sameGeneration && global::Terraria.Main.netMode == 2)
            {
                global::Terraria.NetMessage.SendData(MessageID.DamageNPC, -1, -1, NetworkText.Empty, slot, Math.Min(damage, short.MaxValue), 0f, 0f);
                global::Terraria.NetMessage.SendData(MessageID.SyncNPC, -1, -1, null, slot);
            }
            if (!sameGeneration || !npc.active || npc.life <= 0) defeated++;
        }
        return new(NpcOperationStatus.Applied, defeated);
    }

    private NpcBatchOperationResult Rename(NpcRenameRequest request)
    {
        dispatcher.AssertGameThread();
        if (request.WorldId != worldId || request.Type <= 0 || request.Type >= NPCID.Count ||
            string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200 || request.Name.Any(char.IsControl))
            return new(NpcOperationStatus.Invalid, ErrorCode: "npc.invalid_rename");
        int renamed = 0;
        for (int slot = 0; slot < Math.Min(revisions.Length, global::Terraria.Main.npc.Length); slot++)
        {
            if (!TryGetActive(slot, out var npc) || npc is null || !npc.townNPC || npc.netID != request.Type) continue;
            npc.GivenName = request.Name;
            if (global::Terraria.Main.netMode == 2)
                global::Terraria.NetMessage.SendData((int)PacketTypes.UpdateNPCName, -1, -1, NetworkText.FromLiteral(request.Name), slot);
            renamed++;
        }
        return renamed > 0 ? new(NpcOperationStatus.Applied, renamed) : new(NpcOperationStatus.NotFound, ErrorCode: "npc.town_npc_not_found");
    }

    private static bool IsBossType(int type) => !IsBossPart(type) &&
        (type is NPCID.EaterofWorldsHead or NPCID.MoonLordCore or NPCID.WallofFlesh ||
         ContentSamples.NpcsByNetId.TryGetValue(type, out var sample) && sample.boss);

    // Several multipart bosses have independently active segments without a realLife link.
    // They must not be accepted as summon roots or be removed by a default butcher.
    private static bool IsBossPart(int type) => type is
        NPCID.EaterofWorldsBody or NPCID.EaterofWorldsTail or NPCID.TheDestroyerBody or NPCID.TheDestroyerTail or
        NPCID.SkeletronHand or NPCID.PrimeCannon or NPCID.PrimeLaser or NPCID.PrimeSaw or NPCID.PrimeVice or
        NPCID.WallofFleshEye or NPCID.TheHungry or NPCID.TheHungryII or
        NPCID.GolemHead or NPCID.GolemHeadFree or NPCID.GolemFistLeft or NPCID.GolemFistRight or
        NPCID.PlanterasHook or NPCID.PlanterasTentacle or NPCID.Creeper or
        NPCID.MoonLordHead or NPCID.MoonLordHand or NPCID.MoonLordFreeEye or NPCID.MoonLordLeechBlob or
        NPCID.MartianSaucerTurret or NPCID.MartianSaucerCannon or NPCID.MartianSaucerCore or NPCID.PirateShipCannon;

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
        hash.Add(npc.generation);
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
        if ((uint)slot < (uint)Math.Min(global::Terraria.Main.maxNPCs, global::Terraria.Main.npc.Length) &&
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
                () =>
                {
                    try { completion.TrySetResult(cancellationToken.IsCancellationRequested ? cancelled! : action()); }
                    catch (Exception ex) { completion.TrySetException(ex); }
                }))
        {
            return ValueTask.FromResult(unavailable!);
        }

        return new ValueTask<T>(completion.Task);
    }
}
