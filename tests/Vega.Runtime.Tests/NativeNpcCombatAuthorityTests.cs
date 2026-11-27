using NUnit.Framework;
using TerrariaRuntime;
using Vega.Operations.Metrics;
using Vega.Terraria.Hooking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class NativeNpcCombatAuthorityTests
{
    [Test]
    public void OnlyTheCurrentActiveNativeTargetIsEligible()
    {
        var npc = new global::Terraria.NPC { active = true, whoAmI = 0 };
        var old = new global::Terraria.NPC { active = true, whoAmI = 0 };
        global::Terraria.NPC[] current = [npc];
        Assert.That(NativeNpcCombatAuthorityHooks.IsCurrentTarget(npc, current), Is.True);
        Assert.That(NativeNpcCombatAuthorityHooks.IsCurrentTarget(old, current), Is.False);
        npc.active = false;
        Assert.That(NativeNpcCombatAuthorityHooks.IsCurrentTarget(npc, current), Is.False);
    }

    [Test]
    public void NativeResolverIsOwnedByItsHostLifetime()
    {
        var previous = Hooks.MessageBuffer.ServerNpcHit;
        using var guard = new NativeNpcCombatAuthorityHooks(new NetworkMetricsCounter());
        guard.Attach();
        Assert.That(Hooks.MessageBuffer.ServerNpcHit, Is.Not.Null);
        Assert.That(Hooks.MessageBuffer.ServerNpcHit, Is.Not.SameAs(previous));
        guard.Dispose();
        Assert.That(ReferenceEquals(Hooks.MessageBuffer.ServerNpcHit, previous), Is.True);
    }
}
