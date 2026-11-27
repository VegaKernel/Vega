using TerrariaRuntime;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Terraria.Players;

namespace Vega.Terraria.Hooking;

/// <summary>Converts native gameplay notifications to detached, world-local values.</summary>
internal sealed partial class NativeGameplayEventHooks(WorldId world, IWorldGameplayEvents events,
    IGameThreadDispatcher dispatcher, IOperationsLogger logger, TerrariaPlayerBoundary? players = null) : IDisposable
{
    private bool attached;
    private int attachmentEpoch;
    private bool? previousHardmode;
    private Guid? hardmodeWorldFile;
    public void Attach()
    {
        if (attached) return;
        ResetPlayerStateObservations();
        previousHardmode = null;
        Interlocked.Increment(ref attachmentEpoch);
        Hooks.NPC.Create += Creating;
        Hooks.NPC.Killed += Killed;
        Hooks.NPC.StrikeCompleted += StrikeCompleted;
        Hooks.NPC.StrikeCompletedWithSource += StrikeCompletedWithSource;
        Hooks.NPC.Transforming += Transforming;
        Hooks.NPC.DropLoot += Loot;
        Hooks.MessageBuffer.ServerProjectileSynced += ProjectileSynced;
        Hooks.MessageBuffer.ServerProjectileRemoved += ProjectileRemoved;
        Hooks.Main.DedicatedTickCompleted += TickCompleted;
        Hooks.Main.DedicatedWorldSaveCompleted += SaveCompleted;
        AttachPlayerHurtHooks();
        AttachTileChangesHooks();
        Volatile.Write(ref attached, true);
    }

    public void Dispose()
    {
        if (!attached) return;
        Volatile.Write(ref attached, false);
        Interlocked.Increment(ref attachmentEpoch);
        Hooks.NPC.Create -= Creating;
        Hooks.NPC.Killed -= Killed;
        Hooks.NPC.StrikeCompleted -= StrikeCompleted;
        Hooks.NPC.StrikeCompletedWithSource -= StrikeCompletedWithSource;
        Hooks.NPC.Transforming -= Transforming;
        Hooks.NPC.DropLoot -= Loot;
        Hooks.MessageBuffer.ServerProjectileSynced -= ProjectileSynced;
        Hooks.MessageBuffer.ServerProjectileRemoved -= ProjectileRemoved;
        Hooks.Main.DedicatedTickCompleted -= TickCompleted;
        Hooks.Main.DedicatedWorldSaveCompleted -= SaveCompleted;
        DetachPlayerHurtHooks();
        DetachTileChangesHooks();
        ResetPlayerStateObservations();
        previousHardmode = null;
    }

    private bool Observed<T>() where T : struct => dispatcher.IsGameThread && events.HasSubscribers<T>();

    private void TickCompleted(TimeSpan duration)
    {
        if (!Volatile.Read(ref attached) || !dispatcher.IsGameThread) return;
        try { SamplePlayerStates(world, events, players); }
        catch (Exception ex) { logger.Log(OperationsLogLevel.Error, "world-events", "Player state observation failed.", ex); }
        if (events.HasSubscribers<WorldHardmodeChangedEvent>())
        {
            Guid? file = global::Terraria.Main.ActiveWorldFileData?.UniqueId;
            bool hardmode = global::Terraria.Main.hardMode;
            bool? previous = previousHardmode;
            previousHardmode = hardmode;
            if (file == hardmodeWorldFile && previous.HasValue && previous.Value != hardmode)
                Publish(new WorldHardmodeChangedEvent(world, file, previous.Value, hardmode));
            hardmodeWorldFile = file;
        }
        else previousHardmode = null;
        if (events.HasSubscribers<WorldTickCompletedEvent>()) Publish(new WorldTickCompletedEvent(world, duration));
    }

    private void SaveCompleted(bool success, Exception? error)
    {
        // Native autosave can finish on a worker. Never call plugin code there,
        // and never retain the native exception object across the thread boundary.
        if (!Volatile.Read(ref attached) || !events.HasSubscribers<WorldSaveCompletedEvent>()) return;
        int epoch = Volatile.Read(ref attachmentEpoch);
        var value = new WorldSaveCompletedEvent(world, success, error?.GetType().Name);
        if (dispatcher.IsGameThread)
        {
            Publish(value);
            return;
        }
        try
        {
            if (!dispatcher.TryPost("world.save.completed", () =>
            {
                if (Volatile.Read(ref attached) && Volatile.Read(ref attachmentEpoch) == epoch &&
                    dispatcher.IsGameThread && events.HasSubscribers<WorldSaveCompletedEvent>())
                    Publish(value);
            }))
                logger.Log(OperationsLogLevel.Warning, "world-events", "World save notification dropped: game-thread queue unavailable.");
        }
        catch (Exception ex)
        {
            logger.Log(OperationsLogLevel.Error, "world-events", "World save notification dispatch failed.", ex);
        }
    }

    private void Creating(object? sender, Hooks.NPC.CreateEventArgs args)
    {
        if (Observed<NpcCreatingEvent>()) Publish(new NpcCreatingEvent(world, args.Slot, args.Generation, args.Type, new(args.X, args.Y)));
    }

    private void Killed(object? sender, Hooks.NPC.KilledEventArgs args)
    {
        if (Observed<NpcKilledEvent>()) Publish(new NpcKilledEvent(Snapshot(args.Npc)));
    }

    private void StrikeCompleted(global::Terraria.NPC npc, int damage, int requestedDamage, bool critical)
    {
        if (Volatile.Read(ref attached) && Observed<NpcStrikeCompletedEvent>())
            Publish(new NpcStrikeCompletedEvent(Snapshot(npc), requestedDamage, damage, critical));
    }

    private void StrikeCompletedWithSource(global::Terraria.NPC npc, int damage, int requestedDamage, bool critical, global::Terraria.Entity? source)
    {
        if (!Volatile.Read(ref attached) || !Observed<NpcStrikeCompletedWithSourceEvent>()) return;
        Publish(new NpcStrikeCompletedWithSourceEvent(new(Snapshot(npc), requestedDamage, damage, critical), SnapshotSource(source)));
    }

    private NpcStrikeSourceSnapshot SnapshotSource(global::Terraria.Entity? source)
    {
        if (source is global::Terraria.Player player)
        {
            if (players is not null && players.TryGet(new PlayerId(player.whoAmI), out var snapshot) &&
                snapshot is { SessionId: { } session } && snapshot.WorldId == world &&
                players.TryGetSessionPlayer(snapshot, out var live) && ReferenceEquals(live, player))
                return new(NpcStrikeSourceKind.Player, snapshot.Id, session);
            return new(NpcStrikeSourceKind.Player);
        }
        if (source is global::Terraria.Projectile projectile)
            return new(NpcStrikeSourceKind.Projectile, Projectile: Snapshot(projectile));
        return new(source is null ? NpcStrikeSourceKind.Unknown : NpcStrikeSourceKind.Other);
    }

    private void Transforming(object? sender, Hooks.NPC.TransformingEventArgs args)
    {
        if (args.Result == HookResult.Cancel || !dispatcher.IsGameThread) return;
        bool hasPolicy = events.HasPolicies<NpcTransformingEvent>();
        bool hasObservers = events.HasSubscribers<NpcTransformingEvent>();
        if (!hasPolicy && !hasObservers) return;
        var value = new NpcTransformingEvent(Snapshot(args.Npc), args.NewType);
        if (hasPolicy && !Allows(value)) { args.Result = HookResult.Cancel; return; }
        if (hasObservers) Publish(value);
    }

    private void Loot(object? sender, Hooks.NPC.DropLootEventArgs args)
    {
        if (args.Event == HookEvent.Before)
        {
            if (args.Result != HookResult.Cancel && dispatcher.IsGameThread && events.HasPolicies<NpcLootCreatingEvent>() &&
                !Allows(new NpcLootCreatingEvent(Snapshot(args.Npc), args.Type, args.Stack, args.Pfix, new(args.X, args.Y))))
            {
                // Native Item.NewItem uses the out-of-range slot for an unsuccessful allocation.
                // The legacy hook's default ItemIndex is zero, which could alias a live item.
                args.ItemIndex = global::Terraria.Main.maxItems;
                args.Result = HookResult.Cancel;
            }
            return;
        }
        if (args.Event != HookEvent.After || !Observed<NpcLootDroppedEvent>()) return;
        int slot = args.ItemIndex;
        if ((uint)slot >= (uint)Math.Min(global::Terraria.Main.maxItems, global::Terraria.Main.item.Length) ||
            global::Terraria.Main.item[slot] is not { active: true } item) return;
        Publish(new NpcLootDroppedEvent(Snapshot(args.Npc), slot, item.type, item.stack, item.prefix));
    }

    private void ProjectileSynced(int sender, global::Terraria.Projectile projectile)
    {
        if (Observed<ProjectileSynchronizedEvent>()) Publish(new ProjectileSynchronizedEvent(new(sender), Snapshot(projectile)));
    }

    private void ProjectileRemoved(int sender, global::Terraria.Projectile projectile)
    {
        if (Observed<ProjectileRemovedEvent>()) Publish(new ProjectileRemovedEvent(new(sender), Snapshot(projectile)));
    }

    private NpcEventSnapshot Snapshot(global::Terraria.NPC npc) => new(world, npc.whoAmI, npc.generation,
        npc.type, new(npc.position.X, npc.position.Y), npc.life, npc.friendly, npc.boss);

    private ProjectileEventSnapshot Snapshot(global::Terraria.Projectile projectile) => new(world, projectile.whoAmI,
        projectile.key, projectile.type, projectile.owner, new(projectile.position.X, projectile.position.Y));

    private void Publish<T>(T value) where T : struct
    {
        try { events.Publish(value); }
        catch (Exception ex) { logger.Log(OperationsLogLevel.Error, "world-events", "World event publication failed.", ex); }
    }

    private bool Allows<T>(T value) where T : struct
    {
        try { return events.Allows(value); }
        catch (Exception ex)
        {
            logger.Log(OperationsLogLevel.Error, "world-events", "World policy evaluation failed.", ex);
            return true;
        }
    }
}
