using Vega.Contracts.Operations;

namespace Vega.Terraria.Operations;

internal sealed class TickRateMonitor
{
    internal const int OneMinuteSampleCapacity = 60;
    internal const int OneMinuteTickCapacity = 3_600;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan sampleInterval;
    private readonly Queue<double> history = new(OneMinuteSampleCapacity);
    private readonly double[] tickDurations = new double[OneMinuteTickCapacity];
    private readonly double[] percentileScratch = new double[OneMinuteTickCapacity];
    private long windowStartedAt;
    private long previousTickAt;
    private bool hasPreviousTick;
    private int tickDurationIndex;
    private int tickDurationCount;
    private int updatesInWindow;
    private double minimum = double.PositiveInfinity;
    private double maximum;
    private DateTimeOffset? minimumAt;
    private DateTimeOffset? maximumAt;
    private TickRateStatisticsSnapshot snapshot = TickRateStatisticsSnapshot.Empty;

    public TickRateMonitor(TimeProvider? timeProvider = null, TimeSpan? sampleInterval = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.sampleInterval = sampleInterval ?? TimeSpan.FromSeconds(1);
        if (this.sampleInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleInterval));
        }

        windowStartedAt = this.timeProvider.GetTimestamp();
    }

    public TickRateStatisticsSnapshot RecordTick()
    {
        long now = timeProvider.GetTimestamp();
        RecordTickDuration(now);
        updatesInWindow++;
        TimeSpan elapsed = timeProvider.GetElapsedTime(windowStartedAt, now);
        if (elapsed < sampleInterval)
        {
            return snapshot;
        }

        double current = updatesInWindow / elapsed.TotalSeconds;
        updatesInWindow = 0;
        windowStartedAt = now;

        history.Enqueue(current);
        if (history.Count > OneMinuteSampleCapacity)
        {
            history.Dequeue();
        }

        DateTimeOffset sampledAt = timeProvider.GetUtcNow();
        if (current < minimum)
        {
            minimum = current;
            minimumAt = sampledAt;
        }

        if (current > maximum)
        {
            maximum = current;
            maximumAt = sampledAt;
        }

        (double p50, double p95, double p99) = CalculatePercentiles();
        snapshot = new TickRateStatisticsSnapshot(
            current,
            history.Average(),
            minimum,
            minimumAt,
            maximum,
            maximumAt,
            history.Count,
            p50,
            p95,
            p99);
        return snapshot;
    }

    public void Reset()
    {
        history.Clear();
        updatesInWindow = 0;
        previousTickAt = 0;
        hasPreviousTick = false;
        tickDurationIndex = 0;
        tickDurationCount = 0;
        Array.Clear(tickDurations);
        minimum = double.PositiveInfinity;
        maximum = 0;
        minimumAt = null;
        maximumAt = null;
        snapshot = TickRateStatisticsSnapshot.Empty;
        windowStartedAt = timeProvider.GetTimestamp();
    }

    private void RecordTickDuration(long now)
    {
        if (hasPreviousTick)
        {
            double milliseconds = timeProvider.GetElapsedTime(previousTickAt, now).TotalMilliseconds;
            if (milliseconds > 0 && double.IsFinite(milliseconds))
            {
                tickDurations[tickDurationIndex] = milliseconds;
                tickDurationIndex = (tickDurationIndex + 1) % tickDurations.Length;
                tickDurationCount = Math.Min(tickDurationCount + 1, tickDurations.Length);
            }
        }

        previousTickAt = now;
        hasPreviousTick = true;
    }

    private (double P50, double P95, double P99) CalculatePercentiles()
    {
        if (tickDurationCount == 0)
        {
            return (0, 0, 0);
        }

        for (int index = 0; index < tickDurationCount; index++)
        {
            percentileScratch[index] = tickDurations[index];
        }

        Array.Sort(percentileScratch, 0, tickDurationCount);
        return (
            PercentileFromSorted(0.50),
            PercentileFromSorted(0.95),
            PercentileFromSorted(0.99));
    }

    private double PercentileFromSorted(double percentile)
    {
        int rank = Math.Clamp(
            (int)Math.Ceiling(percentile * tickDurationCount) - 1,
            0,
            tickDurationCount - 1);
        return percentileScratch[rank];
    }
}
