using Vega.Contracts.Worlds;
using Vega.Terraria.Players;

namespace Vega.Terraria.Hooking;

internal sealed partial class NativeGameplayEventHooks
{
    private PlayerStateBaseline[] playerStateBaselines = [];
    private int[] buffScratch = [];
    private long playerObservationTick;

    private readonly record struct PlayerStateBaseline(long Tick, PlayerStateEventSnapshot State, int[]? Buffs);

    private void ResetPlayerStateObservations()
    {
        playerStateBaselines = [];
        buffScratch = [];
        playerObservationTick = 0;
    }

    private void SamplePlayerStates(WorldId world, IWorldGameplayEvents events, TerrariaPlayerBoundary? players)
    {
        bool vitals = events.HasSubscribers<PlayerVitalsChangedEvent>();
        bool death = events.HasSubscribers<PlayerDeathObservedEvent>();
        bool respawn = events.HasSubscribers<PlayerRespawnObservedEvent>();
        bool counters = events.HasSubscribers<PlayerDeathCountersChangedEvent>();
        bool pvp = events.HasSubscribers<PlayerPvpChangedEvent>();
        bool buffs = events.HasSubscribers<PlayerBuffsChangedEvent>();
        if (players is null || !(vitals || death || respawn || counters || pvp || buffs))
        {
            if (playerObservationTick != 0) ResetPlayerStateObservations();
            return;
        }

        int slots = global::Terraria.Main.player.Length;
        if (playerStateBaselines.Length != slots) playerStateBaselines = new PlayerStateBaseline[slots];
        long tick = ++playerObservationTick;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // No independent player scan or allocations on an unchanged tick. Reuse the
        // existing session snapshot and read live fields only on the authoritative thread.
        var online = players.SnapshotOnlinePlayers();
        for (int i = 0; i < online.Count; i++)
        {
            var expected = online[i];
            int slot = expected.Id.Slot;
            if ((uint)slot >= (uint)slots || expected.SessionId is not { } session || expected.WorldId != world ||
                !NativeGameplayReadinessHooks.IsFullyConnected(slot) || !players.TryGetSessionPlayer(expected, out var native)) continue;

            var previous = playerStateBaselines[slot];
            bool consecutive = previous.Tick > 0 && previous.Tick == tick - 1 && previous.State.SessionId == session;
            var before = previous.State;
            var after = new PlayerStateEventSnapshot(world, expected.Id, session, now,
                native.statLife, native.statLifeMax2, native.statMana, native.statManaMax2, native.dead,
                native.hostile, native.team, native.numberOfDeathsPVE, native.numberOfDeathsPVP);
            int[]? oldBuffs = consecutive ? previous.Buffs : null;
            int[]? newBuffs = buffs ? ReadBuffTypes(native.buffType, oldBuffs) : null;
            // Store detached state before notifying plugins; any changes made in a callback
            // are observed on the next completed tick, not recursively in this one.
            playerStateBaselines[slot] = new(tick, after, newBuffs);
            if (!consecutive) continue;

            if (vitals && (before.Life != after.Life || before.MaxLife != after.MaxLife || before.Mana != after.Mana || before.MaxMana != after.MaxMana))
                Publish(new PlayerVitalsChangedEvent(before, after));
            if (death && !before.IsDead && after.IsDead)
            {
                PlayerObservedDeathKind kind = after.DeathsPvp > before.DeathsPvp && after.DeathsPve == before.DeathsPve ? PlayerObservedDeathKind.Pvp :
                    after.DeathsPve > before.DeathsPve && after.DeathsPvp == before.DeathsPvp ? PlayerObservedDeathKind.Pve : PlayerObservedDeathKind.Unknown;
                Publish(new PlayerDeathObservedEvent(before, after, kind));
            }
            if (respawn && before.IsDead && !after.IsDead) Publish(new PlayerRespawnObservedEvent(before, after));
            if (counters && (before.DeathsPve != after.DeathsPve || before.DeathsPvp != after.DeathsPvp))
                Publish(new PlayerDeathCountersChangedEvent(before, after));
            if (pvp && (before.IsHostile != after.IsHostile || before.Team != after.Team)) Publish(new PlayerPvpChangedEvent(before, after));
            if (oldBuffs is not null && newBuffs is not null && !ReferenceEquals(oldBuffs, newBuffs))
                Publish(new PlayerBuffsChangedEvent(after, Array.AsReadOnly(oldBuffs), Array.AsReadOnly(newBuffs)));
        }
    }

    private int[] ReadBuffTypes(int[] nativeTypes, int[]? previous)
    {
        if (buffScratch.Length < nativeTypes.Length) buffScratch = new int[nativeTypes.Length];
        int count = 0;
        // Native buff lists are short. Match membership, independent of the client's
        // slot ordering, and ignore countdown/timer refreshes.
        foreach (int type in nativeTypes)
            if (type > 0) buffScratch[count++] = type;
        Span<int> sorted = buffScratch.AsSpan(0, count);
        sorted.Sort();
        int unique = 0;
        for (int i = 0; i < count; i++)
            if (unique == 0 || sorted[i] != sorted[unique - 1]) sorted[unique++] = sorted[i];
        Span<int> membership = sorted[..unique];
        return previous is not null && membership.SequenceEqual(previous) ? previous : membership.ToArray();
    }
}
