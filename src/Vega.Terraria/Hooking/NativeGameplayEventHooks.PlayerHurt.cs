using Microsoft.Xna.Framework;
using Terraria.DataStructures;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using NativePlayerHooks = On.Terraria.Player;
using NativeProjectileHooks = On.Terraria.Projectile;

namespace Vega.Terraria.Hooking;

internal sealed partial class NativeGameplayEventHooks
{
    // Game-thread-only call context, scoped to the native call and restored even when native code throws.
    private HurtOrigin hurtOrigin;
    private bool hurtAttached;
    private bool meleeAttached;
    private bool projectileAttached;
    internal bool PlayerHurtObservationAvailable => hurtAttached && meleeAttached && projectileAttached;
    private readonly record struct HurtOrigin(PlayerSnapshot? Melee, ProjectileEventSnapshot? Projectile);

    private void AttachPlayerHurtHooks()
    {
        if (players is null) return;
        try
        {
            NativePlayerHooks.Hurt += HurtCompleted;
            hurtAttached = true;
            NativePlayerHooks.ItemCheck_MeleeHitPVP += ObserveMeleePvpCall;
            meleeAttached = true;
            NativeProjectileHooks.Damage_PVP += ObserveProjectilePvpCall;
            projectileAttached = true;
        }
        catch (Exception ex)
        {
            DetachPlayerHurtHooks();
            logger.Log(OperationsLogLevel.Warning, "world-events", "Native player hurt observation is unavailable; native combat remains unchanged.", ex);
        }
    }

    private void DetachPlayerHurtHooks()
    {
        if (projectileAttached) RemoveHurtHook(() => NativeProjectileHooks.Damage_PVP -= ObserveProjectilePvpCall, ref projectileAttached);
        if (meleeAttached) RemoveHurtHook(() => NativePlayerHooks.ItemCheck_MeleeHitPVP -= ObserveMeleePvpCall, ref meleeAttached);
        if (hurtAttached) RemoveHurtHook(() => NativePlayerHooks.Hurt -= HurtCompleted, ref hurtAttached);
        hurtOrigin = default;
    }

    private void RemoveHurtHook(Action remove, ref bool installed)
    {
        try { remove(); installed = false; }
        catch (Exception ex) { logger.Log(OperationsLogLevel.Warning, "world-events", "Unable to detach native hurt hook.", ex); }
    }

    private bool IsHurtObserved() => Volatile.Read(ref attached) && Observed<PlayerHurtCompletedEvent>();

    private PlayerSnapshot? ExactPlayer(global::Terraria.Player player) =>
        players is not null && players.TryGet(new PlayerId(player.whoAmI), out var snapshot) &&
        snapshot is { SessionId: not null } && snapshot.WorldId == world &&
        players.TryGetSessionPlayer(snapshot, out var live) && ReferenceEquals(live, player) ? snapshot : null;

    internal void ObserveMeleePvpCall(NativePlayerHooks.orig_ItemCheck_MeleeHitPVP orig, global::Terraria.Player player,
        global::Terraria.Item item, Rectangle hitbox, int damage, float knockback)
    {
        if (!IsHurtObserved()) { orig(player, item, hitbox, damage, knockback); return; }
        HurtOrigin prior = hurtOrigin;
        try
        {
            hurtOrigin = default;
            try { hurtOrigin = new(ExactPlayer(player), null); }
            catch (Exception ex) { logger.Log(OperationsLogLevel.Warning, "world-events", "Native melee context unavailable.", ex); }
            orig(player, item, hitbox, damage, knockback);
        }
        finally { hurtOrigin = prior; }
    }

    internal void ObserveProjectilePvpCall(NativeProjectileHooks.orig_Damage_PVP orig, global::Terraria.Projectile projectile,
        Rectangle hitbox, float knockback)
    {
        if (!IsHurtObserved()) { orig(projectile, hitbox, knockback); return; }
        HurtOrigin prior = hurtOrigin;
        try
        {
            hurtOrigin = default;
            try
            {
                int slot = projectile.whoAmI;
                hurtOrigin = projectile.active && (uint)slot < (uint)global::Terraria.Main.projectile.Length &&
                    ReferenceEquals(global::Terraria.Main.projectile[slot], projectile)
                    ? new(null, Snapshot(projectile)) : default;
            }
            catch (Exception ex) { logger.Log(OperationsLogLevel.Warning, "world-events", "Native projectile context unavailable.", ex); }
            orig(projectile, hitbox, knockback);
        }
        finally { hurtOrigin = prior; }
    }

    internal double HurtCompleted(NativePlayerHooks.orig_Hurt orig, global::Terraria.Player victim,
        PlayerDeathReason reason, int damage, int direction, bool pvp, bool quiet, bool critical, int cooldown, bool dodgeable)
    {
        if (!IsHurtObserved()) return orig(victim, reason, damage, direction, pvp, quiet, critical, cooldown, dodgeable);
        PlayerSnapshot? before = null;
        try { before = ExactPlayer(victim); }
        catch (Exception ex) { logger.Log(OperationsLogLevel.Warning, "world-events", "Native hurt victim unavailable.", ex); }
        HurtOrigin origin = hurtOrigin;
        int life = victim.statLife;
        double result = orig(victim, reason, damage, direction, pvp, quiet, critical, cooldown, dodgeable);
        if (before is not { SessionId: { } session } || !double.IsFinite(result) || result <= 0) return result;
        try
        {
            // A native call may close or replace a slot; no stale victim/attacker session may be published.
            if (ExactPlayer(victim)?.SessionId != session || !IsHurtObserved()) return result;
            PlayerId? attacker = null;
            PlayerSessionId? attackerSession = null;
            ProjectileEventSnapshot? projectile = null;
            var kind = PlayerHurtSourceKind.Unverified;
            if (pvp && origin.Melee is { SessionId: { } sourceSession } melee && melee.Id != before.Id &&
                reason._sourcePlayerIndex == melee.Id.Slot && players!.TryGetSessionPlayer(melee, out _))
            {
                kind = PlayerHurtSourceKind.NativeMeleePlayer;
                attacker = melee.Id;
                attackerSession = sourceSession;
            }
            else if (pvp && origin.Projectile is { } shot && reason._sourceProjectileLocalIndex == shot.Slot &&
                reason._sourceProjectileType == shot.Type)
            {
                kind = PlayerHurtSourceKind.NativeProjectile;
                projectile = shot;
            }
            HurtOrigin prior = hurtOrigin;
            // A plugin may perform a separate hit while observing this one. Never lend it the native caller context.
            hurtOrigin = default;
            try
            {
                Publish(new PlayerHurtCompletedEvent(world, before.Id, session, pvp, critical, damage, result,
                    life, victim.statLife, kind, attacker, attackerSession, projectile,
                    reason._sourcePlayerIndex >= 0 ? reason._sourcePlayerIndex : null));
            }
            finally { hurtOrigin = prior; }
        }
        catch (Exception ex) { logger.Log(OperationsLogLevel.Error, "world-events", "Native player hurt observation failed.", ex); }
        return result;
    }
}
