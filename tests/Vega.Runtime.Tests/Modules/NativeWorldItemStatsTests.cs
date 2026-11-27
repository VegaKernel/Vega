using NUnit.Framework;
using Vega.Contracts.Items;
using Vega.Terraria.Players;

namespace Vega.Runtime.Tests.Modules;

[TestFixture]
public sealed class NativeWorldItemStatsTests
{
    [Test]
    public void AppliesSupportedStatsToTheExistingNativeTemplateWithoutChangingIdentity()
    {
        var item = new global::Terraria.Item
        {
            type = 39, stack = 1, maxStack = 1, damage = 20, knockBack = 2, useAnimation = 20, useTime = 20,
            shoot = 1, shootSpeed = 8, scale = 1, ammo = 0, useAmmo = 1
        };
        var stats = new CustomWorldItemStats { Damage = 50, Knockback = 4, UseTime = 15, ShootSpeed = 11, Scale = 1.25f, Color = new(1, 2, 3) };
        Assert.That(NativeWorldItemStats.CanApply(item, 1, stats), Is.True);
        NativeWorldItemStats.Apply(item, stats);
        Assert.That(item.damage, Is.EqualTo(50));
        Assert.That(item.knockBack, Is.EqualTo(4));
        Assert.That(item.useTime, Is.EqualTo(15));
        Assert.That(item.shootSpeed, Is.EqualTo(11));
        Assert.That(item.scale, Is.EqualTo(1.25f));
        Assert.That(item.color.R, Is.EqualTo(1));
        Assert.That((item.type, item.stack, item.shoot, item.ammo, item.useAmmo, item.useAnimation), Is.EqualTo((39, 1, 1, 0, 1, 20)));
    }

    [Test]
    public void RejectsInvalidStatsAndGameplayOverridesOnIncompatibleTemplates()
    {
        var material = new global::Terraria.Item { type = 1, stack = 10, maxStack = 9999, damage = 0, shoot = 0 };
        Assert.That(new CustomWorldItemStats().IsValid, Is.False);
        Assert.That(new CustomWorldItemStats { Scale = float.NaN }.IsValid, Is.False);
        Assert.That(new CustomWorldItemStats { UseTime = 0 }.IsValid, Is.False);
        Assert.That(new CustomWorldItemStats { Damage = ushort.MaxValue }.IsValid, Is.False);
        Assert.That(NativeWorldItemStats.CanApply(material, 10, new() { Damage = 10 }), Is.False);
        Assert.That(NativeWorldItemStats.CanApply(material, 10, new() { Color = new(1, 2, 3) }), Is.True);
        var melee = new global::Terraria.Item { type = 24, stack = 1, maxStack = 1, damage = 15, useTime = 20, useAnimation = 20, shoot = 0 };
        Assert.That(NativeWorldItemStats.CanApply(melee, 1, new() { ShootSpeed = 15 }), Is.False);
        Assert.Throws<ArgumentException>(() => NativeWorldItemStats.Apply(material, new() { Damage = 10 }));
    }
}
