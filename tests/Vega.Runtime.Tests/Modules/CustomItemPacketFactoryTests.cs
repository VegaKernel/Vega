using Multiplicity.Packets;
using NUnit.Framework;
using Vega.Contracts.Items;
using Vega.Terraria.Items;

namespace Vega.Runtime.Tests.Modules;

[TestFixture]
public sealed class CustomItemPacketFactoryTests
{
	[Test]
	public void Protocol329DoesNotTransmitRemovedDimensionOverrides()
	{
		var definition = new CustomItemDefinition("dimensions", 39, new CustomItemOverride { Width = 20 });
		Assert.Throws<NotSupportedException>(() => CustomItemPacketFactory.Create(12, 7, 100, 200, definition));
	}
	[Test]
	public void TypedPartialOverrideUsesMultiplicityPacketsAndOnlySetsSuppliedFlags()
	{
		var definition = new CustomItemDefinition(
			"incident-bow",
			39,
			new CustomItemOverride
			{
				Damage = 77,
				ShootSpeed = 14.5f,
				UseAmmoType = 1,
				Color = new ItemColor(10, 20, 30)
			});

		IReadOnlyList<TerrariaPacket> packets = CustomItemPacketFactory.Create(
			12,
			7,
			100,
			200,
			definition);

		var tweak = (TweakItem)packets[1];
		byte[] encoded = CustomItemPacketFactory.Serialize(tweak);
		var decoded = (TweakItem)TerrariaPacket.Deserialize(encoded);

		Assert.Multiple(() =>
		{
			Assert.That(packets.Select(static packet => packet.PacketType), Is.EqualTo(new[]
			{
				PacketTypes.UpdateItemDrop,
				PacketTypes.TweakItem,
				PacketTypes.ItemOwner
			}));
			Assert.That(decoded.Flags1, Is.EqualTo(
				TweakItemFlags1.PackedColorValue |
				TweakItemFlags1.Damage |
				TweakItemFlags1.ShootSpeed |
				TweakItemFlags1.SecondaryFlags));
			Assert.That(decoded.Flags2, Is.EqualTo(TweakItemFlags2.UseAmmo));
			Assert.That(decoded.Damage, Is.EqualTo(77));
			Assert.That(decoded.ShootSpeed, Is.EqualTo(14.5f));
			Assert.That(decoded.UseAmmo, Is.EqualTo(1));
			Assert.That(decoded.PackedColorValue, Is.EqualTo(0xff1e140au));
		});
	}
}
