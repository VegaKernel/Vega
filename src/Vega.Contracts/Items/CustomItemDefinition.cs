using Vega.Contracts.Players;

namespace Vega.Contracts.Items;

public readonly record struct ItemColor(byte Red, byte Green, byte Blue);

public sealed record CustomItemOverride
{
	public byte? Prefix { get; init; }
	public short? Stack { get; init; }
	public ItemColor? Color { get; init; }
	public ushort? Damage { get; init; }
	public float? Knockback { get; init; }
	public ushort? UseAnimation { get; init; }
	public ushort? UseTime { get; init; }
	public short? ShootProjectileType { get; init; }
	public float? ShootSpeed { get; init; }
	public short? Width { get; init; }
	public short? Height { get; init; }
	public float? Scale { get; init; }
	public short? AmmoType { get; init; }
	public short? UseAmmoType { get; init; }
	public bool? NotAmmo { get; init; }
}

public sealed record CustomItemDefinition(
	string Name,
	short ItemType,
	CustomItemOverride Override);

public interface ICustomItemDelivery
{
	ValueTask<PlayerOperationResult> DeliverAsync(
		PlayerId playerId,
		CustomItemDefinition item,
		CancellationToken cancellationToken = default);
}
