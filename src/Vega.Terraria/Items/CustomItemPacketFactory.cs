using Multiplicity.Packets;
using Vega.Contracts.Items;

namespace Vega.Terraria.Items;

public static class CustomItemPacketFactory
{
	public static IReadOnlyList<TerrariaPacket> Create(
		short itemIndex,
		byte playerSlot,
		float positionX,
		float positionY,
		CustomItemDefinition definition)
	{
		ArgumentNullException.ThrowIfNull(definition);
		Validate(definition);

		CustomItemOverride item = definition.Override;
		return
		[
			new UpdateItemDrop
			{
				ItemIndex = itemIndex,
				PositionX = positionX,
				PositionY = positionY,
				Stack = item.Stack ?? 1,
				Prefix = item.Prefix ?? 0,
				ItemNetId = definition.ItemType
			},
			CreateTweak(itemIndex, item),
			new ItemOwner { ItemId = itemIndex, PlayerId = playerSlot }
		];
	}

	public static byte[] Serialize(TerrariaPacket packet)
	{
		ArgumentNullException.ThrowIfNull(packet);
		using var stream = new MemoryStream(packet.GetLength() + 3);
		packet.ToStream(stream);
		return stream.ToArray();
	}

	private static TweakItem CreateTweak(short itemIndex, CustomItemOverride item)
	{
		var flags1 = TweakItemFlags1.None;
		var flags2 = TweakItemFlags2.None;

		if (item.Color is not null) flags1 |= TweakItemFlags1.PackedColorValue;
		if (item.Damage is not null) flags1 |= TweakItemFlags1.Damage;
		if (item.Knockback is not null) flags1 |= TweakItemFlags1.Knockback;
		if (item.UseAnimation is not null) flags1 |= TweakItemFlags1.UseAnimation;
		if (item.UseTime is not null) flags1 |= TweakItemFlags1.UseTime;
		if (item.ShootProjectileType is not null) flags1 |= TweakItemFlags1.Shoot;
		if (item.ShootSpeed is not null) flags1 |= TweakItemFlags1.ShootSpeed;
		if (item.Scale is not null) flags2 |= TweakItemFlags2.Scale;
		if (item.AmmoType is not null) flags2 |= TweakItemFlags2.Ammo;
		if (item.UseAmmoType is not null) flags2 |= TweakItemFlags2.UseAmmo;
		if (item.NotAmmo is not null) flags2 |= TweakItemFlags2.NotAmmo;
		if (flags2 != TweakItemFlags2.None) flags1 |= TweakItemFlags1.SecondaryFlags;

		return new TweakItem
		{
			ItemIndex = itemIndex,
			Flags1 = flags1,
			PackedColorValue = PackColor(item.Color),
			Damage = item.Damage ?? 0,
			Knockback = item.Knockback ?? 0,
			UseAnimation = item.UseAnimation ?? 0,
			UseTime = item.UseTime ?? 0,
			Shoot = item.ShootProjectileType ?? 0,
			ShootSpeed = item.ShootSpeed ?? 0,
			Flags2 = flags2,
			Scale = item.Scale ?? 0,
			Ammo = item.AmmoType ?? 0,
			UseAmmo = item.UseAmmoType ?? 0,
			NotAmmo = item.NotAmmo ?? false
		};
	}

	private static uint PackColor(ItemColor? color) => color is ItemColor value
		? 0xff000000u | value.Red | (uint)value.Green << 8 | (uint)value.Blue << 16
		: 0;

	private static void Validate(CustomItemDefinition definition)
	{
		if (definition.Override.Width is not null || definition.Override.Height is not null)
			throw new NotSupportedException("Terraria protocol 329 does not transmit custom item width or height.");
		if (string.IsNullOrWhiteSpace(definition.Name))
			throw new ArgumentException("A custom item name is required.", nameof(definition));
		if (definition.ItemType <= 0)
			throw new ArgumentOutOfRangeException(nameof(definition), "Item type must be positive.");
		if (definition.Override.Stack is <= 0)
			throw new ArgumentOutOfRangeException(nameof(definition), "Stack must be positive when supplied.");
		if (definition.Override.Knockback is float knockback && !float.IsFinite(knockback))
			throw new ArgumentOutOfRangeException(nameof(definition), "Knockback must be finite.");
		if (definition.Override.ShootSpeed is float shootSpeed && !float.IsFinite(shootSpeed))
			throw new ArgumentOutOfRangeException(nameof(definition), "Shoot speed must be finite.");
		if (definition.Override.Scale is float scale && (!float.IsFinite(scale) || scale <= 0))
			throw new ArgumentOutOfRangeException(nameof(definition), "Scale must be finite and positive.");
	}
}
