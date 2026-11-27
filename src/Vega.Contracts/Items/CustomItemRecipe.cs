namespace Vega.Contracts.Items;

using Vega.Contracts.Players;

/// <summary>A named native item recipe. Optional world stats use the stock native world-item tweak capability.</summary>
public sealed record CustomItemRecipe(string Name, int ItemType, int Stack = 1, int Prefix = 0)
{
    /// <summary>Transient world-item stats. Stock inventory, SSC, trade and reconnect do not persist them.</summary>
    public CustomWorldItemStats? WorldStats { get; init; }
}

public readonly record struct ItemColor(byte Red, byte Green, byte Blue);

/// <summary>Supported native world-item stats only. No projectile/ammunition identity or geometry replacement.</summary>
public sealed record CustomWorldItemStats
{
    public ItemColor? Color { get; init; }
    public ushort? Damage { get; init; }
    public float? Knockback { get; init; }
    public ushort? UseAnimation { get; init; }
    public ushort? UseTime { get; init; }
    public float? ShootSpeed { get; init; }
    public float? Scale { get; init; }

    public bool HasGameplayOverrides => Damage is not null || Knockback is not null || UseAnimation is not null ||
        UseTime is not null || ShootSpeed is not null || Scale is not null;

    public bool IsValid => (Color is not null || HasGameplayOverrides) && Damage is null or <= (ushort)short.MaxValue &&
        UseAnimation is null or >= 1 and <= 3600 && UseTime is null or >= 1 and <= 3600 &&
        FiniteRange(Knockback, 0, 100) && FiniteRange(ShootSpeed, 0, 100) && FiniteRange(Scale, 0.25f, 4);

    private static bool FiniteRange(float? value, float min, float max) => value is null ||
        float.IsFinite(value.Value) && value.Value >= min && value.Value <= max;
}

/// <summary>Host-owned native delivery of a world item with its supported stats; requires exact player identity.</summary>
public interface IPlayerCustomWorldItemDelivery
{
    ValueTask<PlayerOperationResult> GiveWorldItemAsync(PlayerSnapshot player, int itemType, int stack, int prefix,
        CustomWorldItemStats stats, CancellationToken cancellationToken = default);
}
