using Microsoft.Xna.Framework;
using NUnit.Framework;
using Vega.Terraria.Hooking;

namespace Vega.Runtime.Tests;

public sealed class NativeMovementInterestHooksTests
{
    [Test]
    public void DistantSamplesPreserveTransitionsAndResetWhenSlotsAreReused()
    {
        var sample = new NativeMovementInterestHooks.MovementSample();
        var distant = new Vector2(4000, 0);
        var boundary = new Vector2(3200, 0);
        Assert.That(sample.ShouldSend(default, distant, 0, false, 1, 1, 1, 20), Is.True);
        Assert.That(sample.ShouldSend(default, boundary, 0, false, 1, 1, 2, 20), Is.False);
        Assert.That(sample.ShouldSend(default, distant, 0, false, 1, 1, 21, 20), Is.True);
        Assert.That(sample.ShouldSend(default, distant, 1, false, 1, 1, 22, 20), Is.True);
        Assert.That(sample.ShouldSend(default, distant, 1, true, 1, 1, 23, 20), Is.True);
        Assert.That(sample.ShouldSend(new Vector2(0, 700), distant, 1, false, 1, 1, 24, 20), Is.True);
        Assert.That(sample.ShouldSend(default, distant, 1, false, 1, 2, 25, 20), Is.True);
        Assert.That(sample.ShouldSend(default, distant, 1, false, 2, 2, 26, 20), Is.True);
        Assert.That(sample.ShouldSend(default, default, 1, false, 2, 2, 27, 20), Is.True);
        Assert.That(sample.ShouldSend(default, boundary, 1, false, 2, 2, 28, 20), Is.True);
    }
}
