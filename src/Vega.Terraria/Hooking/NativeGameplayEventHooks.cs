using TerrariaRuntime;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Hooking;

/// <summary>Converts native gameplay notifications to detached, world-local values.</summary>
internal sealed class NativeGameplayEventHooks(WorldId world, IWorldGameplayEvents events,
    IGameThreadDispatcher dispatcher, IOperationsLogger logger) : IDisposable
{
    private bool attached;
    private int attachmentEpoch;
    public void Attach()
    {
        if (attached) return;
        Interlocked.Increment(ref attachmentEpoch);
        Hooks.NPC.Create += Creating;
        Hooks.NPC.Killed += Killed;
        Hooks.NPC.StrikeCompleted += StrikeCompleted;
        Hooks.NPC.Transforming += Transforming;
        Hooks.NPC.DropLoot += Loot;
        Hooks.MessageBuffer.ServerProjectileSynced += ProjectileSynced;
        Hooks.MessageBuffer.ServerProjectileRemoved += ProjectileRemoved;
        Hooks.Main.DedicatedTickCompleted += TickCompleted;
        Hooks.Main.DedicatedWorldSaveCompleted += SaveCompleted;
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
        Hooks.NPC.Transforming -= Transforming;
        Hooks.NPC.DropLoot -= Loot;
        Hooks.MessageBuffer.ServerProjectileSynced -= ProjectileSynced;
        Hooks.MessageBuffer.ServerProjectileRemoved -= ProjectileRemoved;
        Hooks.Main.DedicatedTickCompleted -= TickCompleted;
        Hooks.Main.DedicatedWorldSaveCompleted -= SaveCompleted;
    }

    private bool Observed<T>() where T : struct => dispatcher.IsGameThread && events.HasSubscribers<T>();

    private void TickCompleted(TimeSpan duration)
    {
        if (Volatile.Read(ref attached) && events.HasSubscribers<WorldTickCompletedEvent>() && dispatcher.IsGameThread)
            Publish(new WorldTickCompletedEvent(world, duration));
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
