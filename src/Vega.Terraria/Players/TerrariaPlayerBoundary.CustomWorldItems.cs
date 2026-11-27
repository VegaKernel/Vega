using Multiplicity.Packets;
using Terraria.ID;
using Vega.Contracts.Items;
using Vega.Contracts.Players;

namespace Vega.Terraria.Players;

public sealed partial class TerrariaPlayerBoundary : IPlayerCustomWorldItemDelivery
{
    public ValueTask<PlayerOperationResult> GiveWorldItemAsync(PlayerSnapshot player, int itemType, int stack, int prefix,
        CustomWorldItemStats stats, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stats);
        return GiveItemAsync(player, itemType.ToString(System.Globalization.CultureInfo.InvariantCulture), stack, prefix,
            false, cancellationToken, stats);
    }

    private static bool SendCustomWorldItem(global::Terraria.WorldItem worldItem, int slot, int recipient, CustomWorldItemStats stats)
    {
        try
        {
            var (primary, secondary) = NativeWorldItemStats.Apply(worldItem.inner, stats);
            // The stock native serializer reads the actual authoritative item. Do not construct
            // packets or write sockets directly; all native transport rules remain in force.
            if (global::Terraria.NetMessage.TrySendData(MessageID.SyncItem, recipient, -1, null, slot, 1) &&
                global::Terraria.NetMessage.TrySendData(MessageID.ItemTweaker, recipient, -1, null, slot, (byte)primary, (byte)secondary) &&
                global::Terraria.NetMessage.TrySendData(MessageID.ItemOwner, recipient, -1, null, slot)) return true;
            worldItem.TurnToAirAndSync();
            return false;
        }
        catch
        {
            // A partially issued delivery must not leave an allocated, stat-modified world item behind.
            worldItem.TurnToAirAndSync();
            throw;
        }
    }
}

/// <summary>Native-supported transient stats on an existing item type; not a permanent inventory identity.</summary>
internal static class NativeWorldItemStats
{
    internal static bool CanApply(global::Terraria.Item item, int stack, CustomWorldItemStats stats) =>
        stats.IsValid && (!stats.HasGameplayOverrides || (stack == 1 && item.maxStack == 1)) &&
        (stats.Damage is null && stats.Knockback is null || item.damage > 0) &&
        (stats.UseAnimation is null || item.useAnimation > 0) && (stats.UseTime is null || item.useTime > 0) &&
        (stats.ShootSpeed is null || item.shoot > 0);

    internal static (TweakItemFlags1 Primary, TweakItemFlags2 Secondary) Apply(global::Terraria.Item item, CustomWorldItemStats stats)
    {
        if (!CanApply(item, item.stack, stats)) throw new ArgumentException("Unsupported world-item stat override.", nameof(stats));
        var primary = TweakItemFlags1.None;
        var secondary = TweakItemFlags2.None;
        if (stats.Color is { } color) { item.color = new(color.Red, color.Green, color.Blue); primary |= TweakItemFlags1.PackedColorValue; }
        if (stats.Damage is { } damage) { item.damage = damage; primary |= TweakItemFlags1.Damage; }
        if (stats.Knockback is { } knockback) { item.knockBack = knockback; primary |= TweakItemFlags1.Knockback; }
        if (stats.UseAnimation is { } animation) { item.useAnimation = animation; primary |= TweakItemFlags1.UseAnimation; }
        if (stats.UseTime is { } time) { item.useTime = time; primary |= TweakItemFlags1.UseTime; }
        if (stats.ShootSpeed is { } speed) { item.shootSpeed = speed; primary |= TweakItemFlags1.ShootSpeed; }
        if (stats.Scale is { } scale) { item.scale = scale; secondary |= TweakItemFlags2.Scale; }
        if (secondary != TweakItemFlags2.None) primary |= TweakItemFlags1.SecondaryFlags;
        return (primary, secondary);
    }
}
