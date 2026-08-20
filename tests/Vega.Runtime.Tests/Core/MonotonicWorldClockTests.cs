using NUnit.Framework;
using Vega.Contracts.Worlds;
using Vega.Core.Worlds;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class MonotonicWorldClockTests
{
    private const double DayTicks = 54000;

    [Test]
    public void CorrectsMeasuredLowTpsDriftFromMonotonicTime()
    {
        var time = new ManualTimeProvider();
        var clock = CreateClock(time);
        clock.Observe(WorldId.Primary, new WorldClockValue(true, 1000));
        time.Advance(TimeSpan.FromSeconds(2));

        WorldClockDecision decision = clock.Observe(
            WorldId.Primary,
            new WorldClockValue(true, 1030));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(WorldClockAction.Correct));
            Assert.That(decision.Target.IsDaytime, Is.True);
            Assert.That(decision.Target.TimeTicks, Is.EqualTo(1120).Within(0.001));
            Assert.That(clock.SnapshotWorldClocks().Single().CorrectionCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void AdvancesAcrossDayNightBoundaryWithoutFalseReanchor()
    {
        var time = new ManualTimeProvider();
        var clock = CreateClock(time);
        clock.Observe(WorldId.Primary, new WorldClockValue(true, DayTicks - 30));
        time.Advance(TimeSpan.FromSeconds(1));

        WorldClockDecision decision = clock.Observe(
            WorldId.Primary,
            new WorldClockValue(true, DayTicks));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(WorldClockAction.None));
            Assert.That(decision.Target.IsDaytime, Is.False);
            Assert.That(decision.Target.TimeTicks, Is.EqualTo(30).Within(0.001));
        });
    }

    [Test]
    public void ReanchorsAfterExplicitLargeTimeChange()
    {
        var time = new ManualTimeProvider();
        var clock = CreateClock(time);
        clock.Observe(WorldId.Primary, new WorldClockValue(true, 1000));
        time.Advance(TimeSpan.FromSeconds(1));

        WorldClockDecision decision = clock.Observe(
            WorldId.Primary,
            new WorldClockValue(false, 10000));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(WorldClockAction.Reanchor));
            Assert.That(decision.Target, Is.EqualTo(new WorldClockValue(false, 10000)));
            Assert.That(clock.SnapshotWorldClocks().Single().ReanchorCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void KeepsIndependentAnchorsPerWorld()
    {
        var time = new ManualTimeProvider();
        var clock = CreateClock(time);
        var sandbox = new WorldId("sandbox");
        clock.Observe(WorldId.Primary, new WorldClockValue(true, 100));
        clock.Observe(sandbox, new WorldClockValue(false, 200));

        Assert.That(clock.SnapshotWorldClocks().Select(static snapshot => snapshot.WorldId),
            Is.EquivalentTo(new[] { WorldId.Primary, sandbox }));
    }

    [Test]
    public void DisabledClockNeverCreatesAnAnchorOrCorrection()
    {
        var clock = new MonotonicWorldClock(WorldClockOptions.Default, new ManualTimeProvider());

        WorldClockDecision decision = clock.Observe(
            WorldId.Primary,
            new WorldClockValue(true, 100));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(WorldClockAction.None));
            Assert.That(clock.SnapshotWorldClocks(), Is.Empty);
        });
    }

    private static MonotonicWorldClock CreateClock(TimeProvider timeProvider) => new(
        new WorldClockOptions(true, 1, 15),
        timeProvider);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;
        private DateTimeOffset utcNow = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration)
        {
            timestamp += duration.Ticks;
            utcNow += duration;
        }
    }
}
