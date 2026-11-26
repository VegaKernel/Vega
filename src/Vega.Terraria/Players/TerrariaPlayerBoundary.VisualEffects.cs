using System.Diagnostics;
using Microsoft.Xna.Framework;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.GameContent.Creative;
using Vega.Contracts.Accounts;
using Vega.Contracts.Players;

namespace Vega.Terraria.Players;

public sealed partial class TerrariaPlayerBoundary : IPlayerVisualOperations, IPlayerGodModeOperations
{
    private readonly HashSet<PlayerSessionId> activeAnnoyEffects = [];
    private readonly Dictionary<int, PlayerSessionId> adminGodMode = [];

    public async ValueTask<(PlayerOperationResult Result, bool Enabled)> SetGodModeAsync(PlayerSnapshot player, bool? enabled,
        CancellationToken cancellationToken = default)
    {
        bool next = false;
        var result = await DispatchAsync("player.godmode", () =>
        {
            if (!TryGetSessionPlayer(player, out var live) || player.SessionId is not { } session) return PlayerOperationResult.NotFound();
            if (live.ghost) return PlayerOperationResult.Failed("godmode.ghost");
            var power = CreativePowerManager.Instance.GetPower<CreativePowers.GodmodePower>();
            next = enabled ?? !power.IsEnabledForPlayer(live.whoAmI);
            if (next) adminGodMode[live.whoAmI] = session;
            else adminGodMode.Remove(live.whoAmI);
            power.SetEnabledState(live.whoAmI, next);
            return PlayerOperationResult.Applied();
        }, "godmode.failed", cancellationToken).ConfigureAwait(false);
        return (result, next);
    }

    // Native GodmodePower uses player indices. Retire only flags that these commands enabled;
    // never let an administrative grant survive the original connection.
    private void ClearAdminGodMode(int slot, PlayerSessionId session)
    {
        if (!adminGodMode.TryGetValue(slot, out var owner) || owner != session) return;
        try { CreativePowerManager.Instance.GetPower<CreativePowers.GodmodePower>().SetEnabledState(slot, false); }
        catch (Exception ex) { Console.Error.WriteLine($"Vega godmode cleanup failed: {ex}"); }
        finally { adminGodMode.Remove(slot); }
    }

    private void ClearStaleAdminGodMode(int slot, PlayerSessionId session)
    {
        if (adminGodMode.TryGetValue(slot, out var owner) && owner != session) ClearAdminGodMode(slot, owner);
    }

    public ValueTask<PlayerOperationResult> RocketAsync(PlayerSnapshot player, IPlayerAccountSessionService accounts,
        CancellationToken cancellationToken = default) => DispatchAsync("player.rocket", () =>
        {
            if (!TryGetSessionPlayer(player, out var live) || live.dead || live.ghost) return PlayerOperationResult.NotFound();
            if (!global::Terraria.Main.ServerSideCharacter) return PlayerOperationResult.Failed("rocket.native_ssc_required");
            if (!accounts.TryGetBinding(player, out var binding)) return PlayerOperationResult.Failed("rocket.authentication_required");
            return accounts.TryRunWithBinding(binding, () =>
            {
                if (!TryGetSessionPlayer(player, out var current) || current.dead || current.ghost) return PlayerOperationResult.NotFound();
                current.velocity.Y = -50f;
                global::Terraria.NetMessage.SendData(MessageID.PlayerControls, number: current.whoAmI);
                return PlayerOperationResult.Applied();
            }, out PlayerOperationResult result) ? result : PlayerOperationResult.Failed("rocket.authentication_changed");
        }, "rocket.failed", cancellationToken);

    public ValueTask<PlayerOperationResult> AnnoyAsync(PlayerSnapshot player, int seconds, CancellationToken cancellationToken = default) =>
        DispatchAsync("player.annoy", () =>
        {
            if (seconds is < 1 or > 30) return PlayerOperationResult.Failed("annoy.duration");
            if (!TryGetSessionPlayer(player, out _) || player.SessionId is not { } session) return PlayerOperationResult.NotFound();
            if (!activeAnnoyEffects.Add(session)) return PlayerOperationResult.Failed("annoy.already_active");
            long started = Stopwatch.GetTimestamp();
            long nextSound = started;
            // One cooperative step per server tick; no new threads, no slot-only targeting, bounded rate and duration.
            bool scheduled = dispatcher.TrySchedule("player.annoy.effect", () =>
            {
                bool done = true;
                try
                {
                    long now = Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(started, now).TotalSeconds >= seconds || !TryGetSessionPlayer(player, out var live)) return true;
                    if (now >= nextSound)
                    {
                        // Vanilla MiscDataSync action 2 plays the Item1 sound at the specified player on the client.
                        global::Terraria.NetMessage.SendData(MessageID.MiscDataSync, live.whoAmI, -1, null, live.whoAmI, 2f);
                        nextSound = now + Stopwatch.Frequency / 4;
                    }
                    done = false;
                    return false;
                }
                finally { if (done) activeAnnoyEffects.Remove(session); }
            });
            if (scheduled) return PlayerOperationResult.Applied();
            activeAnnoyEffects.Remove(session);
            return PlayerOperationResult.DispatcherUnavailable();
        }, "annoy.failed", cancellationToken);

    public ValueTask<PlayerOperationResult> FireworkAsync(PlayerSnapshot player, FireworkStyle style, CancellationToken cancellationToken = default) =>
        DispatchAsync("player.firework", () =>
        {
            if (!TryGetSessionPlayer(player, out var live) || live.dead || live.ghost) return PlayerOperationResult.NotFound();
            int type = style switch
            {
                FireworkStyle.Red => ProjectileID.RocketFireworkRed, FireworkStyle.Green => ProjectileID.RocketFireworkGreen,
                FireworkStyle.Blue => ProjectileID.RocketFireworkBlue, FireworkStyle.Yellow => ProjectileID.RocketFireworkYellow,
                FireworkStyle.Star => ProjectileID.RocketFireworksBoxRed, FireworkStyle.Spiral => ProjectileID.RocketFireworksBoxGreen,
                FireworkStyle.Rings => ProjectileID.RocketFireworksBoxBlue, FireworkStyle.Flower => ProjectileID.RocketFireworksBoxYellow,
                _ => -1
            };
            if (type < 0) return PlayerOperationResult.Failed("firework.invalid_style");
            bool free = false;
            for (int i = 0; i < global::Terraria.Main.maxProjectiles; i++)
                if (!global::Terraria.Main.projectile[i].active) { free = true; break; }
            if (!free) return PlayerOperationResult.Failed("firework.no_free_slot");
            // Use the vanilla cosmetic projectile AI (zero damage) and normal lifetime/synchronization.
            int slot = global::Terraria.Projectile.NewProjectile(new EntitySource_DebugCommand(), live.Center - new Vector2(0f, 64f),
                new Vector2(0f, -8f), type, 0, 0f);
            return (uint)slot < (uint)global::Terraria.Main.maxProjectiles && global::Terraria.Main.projectile[slot].active
                ? PlayerOperationResult.Applied() : PlayerOperationResult.Failed("firework.no_free_slot");
        }, "firework.failed", cancellationToken);
}
