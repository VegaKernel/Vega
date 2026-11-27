using System.Collections.Frozen;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Regions;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Regions;

/// <summary>One world-local membership tracker shared by all plugins. Never reads native mutable player state.</summary>
public sealed class RegionObservations : IDisposable
{
    private readonly IRegionService regions;
    private readonly IPlayerDirectory players;
    private readonly IWorldGameplayEvents events;
    private readonly WorldId world;
    private readonly IGameThreadDispatcher dispatcher;
    private readonly IOperationsLogger logger;
    private readonly IDisposable ticks;
    private readonly IRegionChangeObservations? changes;
    private readonly IPlayerSessionLifecycle? lifecycle;
    private readonly Dictionary<PlayerSessionId, MembershipState> tracked = [];
    private readonly List<PlayerSessionId> missing = [];
    private readonly List<RegionSnapshot> hits = [];
    private IReadOnlyList<RegionSnapshot>? cachedSource;
    private RegionSnapshot[] detached = [];
    private int stamp;
    private bool disposed;

    public RegionObservations(IRegionService regions, IPlayerDirectory players, IWorldGameplayEvents events,
        WorldId world, IGameThreadDispatcher dispatcher, IOperationsLogger logger)
    {
        this.regions = regions;
        this.players = players;
        this.events = events;
        this.world = world;
        this.dispatcher = dispatcher;
        this.logger = logger;
        ticks = events.Subscribe<WorldTickCompletedEvent>(ObserveTick);
        changes = regions as IRegionChangeObservations;
        if (changes is not null) changes.Changed += ObserveChange;
        lifecycle = players as IPlayerSessionLifecycle;
        if (lifecycle is not null) lifecycle.Closed += ObserveClose;
    }

    private void ObserveChange(RegionChangedEvent change)
    {
        if (Volatile.Read(ref disposed) || change.WorldId != world || !events.HasSubscribers<RegionChangedEvent>()) return;
        // Database mutations can commit off the game thread. Never call plugin code on a database/command stack.
        if (!dispatcher.TryPost("region.changed", () =>
        {
            if (!Volatile.Read(ref disposed)) events.Publish(change);
        })) logger.Log(OperationsLogLevel.Warning, "region-observations", "Region change observation dropped: game-thread queue unavailable.");
    }

    private void ObserveTick(WorldTickCompletedEvent tick)
    {
        if (Volatile.Read(ref disposed) || tick.WorldId != world) return;
        if (!events.HasSubscribers<RegionEnteredEvent>() && !events.HasSubscribers<RegionLeftEvent>())
        {
            tracked.Clear();
            cachedSource = null;
            detached = [];
            return;
        }

        // Cached repository snapshot is reused until a committed mutation; unchanged tile positions
        // do not scan regions. ponytail: moving players scan O(region count) per tile crossing;
        // if large moving-player/region workloads warrant it, replace with a shared spatial index.
        IReadOnlyList<RegionSnapshot> snapshot = regions.SnapshotRegions(world);
        if (!ReferenceEquals(snapshot, cachedSource))
        {
            detached = snapshot.Where(region => region.WorldId == world).Select(region => region with
            {
                Members = region.Members.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
                Groups = region.Groups.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
                Flags = region.Flags.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)
            }).ToArray();
            cachedSource = snapshot;
        }
        int currentStamp = unchecked(++stamp);
        foreach (PlayerSnapshot player in players.SnapshotOnlinePlayers())
        {
            if (player.WorldId != world || player.SessionId is not { } session ||
                player.Connection?.LoginPhase is { } phase && phase != ConnectionLoginPhase.FullyConnected ||
                !float.IsFinite(player.Position.X) || !float.IsFinite(player.Position.Y) ||
                player.Position.X < 0 || player.Position.Y < 0 ||
                player.Position.X / 16d > int.MaxValue || player.Position.Y / 16d > int.MaxValue) continue;

            int x = (int)Math.Floor(player.Position.X / 16d), y = (int)Math.Floor(player.Position.Y / 16d);
            tracked.TryGetValue(session, out MembershipState? before);
            if (before is not null && before.Player != player.Id)
            {
                LeaveSession(session);
                before = null;
            }
            if (before is not null && before.Player == player.Id && before.X == x && before.Y == y && ReferenceEquals(before.Source, snapshot))
            {
                before.Stamp = currentStamp;
                continue;
            }

            hits.Clear();
            foreach (RegionSnapshot region in detached)
                if (region.Contains(x, y)) hits.Add(region);

            if (before is not null)
                foreach (RegionSnapshot region in before.Regions)
                    if (!ContainsRegion(hits, region.Id))
                        events.Publish(new RegionLeftEvent(new(world, player.Id, session, x, y, region)));
            foreach (RegionSnapshot region in hits)
                if (before is null || !ContainsRegion(before.Regions, region.Id))
                    events.Publish(new RegionEnteredEvent(new(world, player.Id, session, x, y, region)));
            var next = before ?? new MembershipState(player.Id);
            next.X = x;
            next.Y = y;
            next.Stamp = currentStamp;
            next.Source = snapshot;
            next.Regions.Clear();
            next.Regions.AddRange(hits);
            if (before is null) tracked[session] = next;
        }

        foreach (var (session, membership) in tracked)
            if (membership.Stamp != currentStamp) missing.Add(session);
        foreach (PlayerSessionId session in missing) LeaveSession(session);
        missing.Clear();
        hits.Clear();
    }

    private static bool ContainsRegion(List<RegionSnapshot> regions, int id)
    {
        foreach (RegionSnapshot region in regions) if (region.Id == id) return true;
        return false;
    }

    private void ObserveClose(PlayerSessionClosed closed)
    {
        if (Volatile.Read(ref disposed)) return;
        dispatcher.TryPost("region.session-closed", () =>
        {
            if (!Volatile.Read(ref disposed)) LeaveSession(closed.SessionId);
        });
    }

    private void LeaveSession(PlayerSessionId session)
    {
        if (!tracked.Remove(session, out MembershipState? state)) return;
        foreach (RegionSnapshot region in state.Regions)
            events.Publish(new RegionLeftEvent(new(world, state.Player, session, state.X, state.Y, region)));
    }

    public void Dispose()
    {
        Volatile.Write(ref disposed, true);
        ticks.Dispose();
        if (changes is not null) changes.Changed -= ObserveChange;
        if (lifecycle is not null) lifecycle.Closed -= ObserveClose;
    }

    private sealed class MembershipState(PlayerId player)
    {
        public PlayerId Player { get; } = player;
        public int X { get; set; }
        public int Y { get; set; }
        public int Stamp { get; set; }
        public IReadOnlyList<RegionSnapshot>? Source { get; set; }
        public List<RegionSnapshot> Regions { get; } = [];
    }
}
