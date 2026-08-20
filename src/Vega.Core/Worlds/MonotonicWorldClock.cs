using System.Text.Json;
using Vega.Contracts.Operations;
using Vega.Contracts.Worlds;

namespace Vega.Core.Worlds;

public sealed record WorldClockOptions(
    bool Enabled,
    double CorrectionThresholdSeconds,
    double ReanchorThresholdSeconds)
{
    public static WorldClockOptions Default { get; } = new(false, 1, 15);

    public static WorldClockOptions Normalize(WorldClockOptions? candidate)
    {
        double correction = Math.Clamp(
            candidate?.CorrectionThresholdSeconds ?? Default.CorrectionThresholdSeconds,
            0.1,
            10);
        double reanchor = Math.Clamp(
            candidate?.ReanchorThresholdSeconds ?? Default.ReanchorThresholdSeconds,
            correction * 2,
            300);
        return new WorldClockOptions(candidate?.Enabled ?? Default.Enabled, correction, reanchor);
    }
}

public static class WorldClockOptionsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static WorldClockOptions Load(string path, IOperationsLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            return File.Exists(path)
                ? WorldClockOptions.Normalize(
                    JsonSerializer.Deserialize<WorldClockOptions>(File.ReadAllText(path), SerializerOptions))
                : WorldClockOptions.Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "world-clock",
                $"World-clock configuration '{Path.GetFullPath(path)}' is invalid; synchronization remains disabled.",
                ex);
            return WorldClockOptions.Default;
        }
    }
}

public sealed class MonotonicWorldClock : IWorldClockController
{
    internal const double TicksPerSecond = 60;
    internal const double DayTicks = 54000;
    internal const double NightTicks = 32400;
    internal const double CycleTicks = DayTicks + NightTicks;
    private readonly object sync = new();
    private readonly WorldClockOptions options;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<WorldId, Anchor> anchors = [];

    public MonotonicWorldClock(WorldClockOptions options, TimeProvider? timeProvider = null)
    {
        this.options = WorldClockOptions.Normalize(options);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool Enabled => options.Enabled;

    public WorldClockDecision Observe(WorldId worldId, WorldClockValue observed)
    {
        Validate(observed);
        lock (sync)
        {
            long now = timeProvider.GetTimestamp();
            DateTimeOffset sampledAt = timeProvider.GetUtcNow();
            if (!options.Enabled)
            {
                return new WorldClockDecision(WorldClockAction.None, observed, 0);
            }

            if (!anchors.TryGetValue(worldId, out Anchor? anchor))
            {
                anchors[worldId] = Anchor.Create(observed, now, sampledAt);
                return new WorldClockDecision(WorldClockAction.None, observed, 0);
            }

            double predictedPosition = NormalizePosition(
                anchor.Position + timeProvider.GetElapsedTime(anchor.Timestamp, now).TotalSeconds * TicksPerSecond);
            double observedPosition = ToPosition(observed);
            double drift = CircularDelta(predictedPosition, observedPosition);
            WorldClockValue predicted = FromPosition(predictedPosition);
            double absoluteDrift = Math.Abs(drift);

            if (absoluteDrift > options.ReanchorThresholdSeconds * TicksPerSecond)
            {
                anchor.Reanchor(observed, now, sampledAt, drift);
                return new WorldClockDecision(WorldClockAction.Reanchor, observed, drift);
            }

            anchor.Observe(observed, predicted, sampledAt, drift);
            if (absoluteDrift > options.CorrectionThresholdSeconds * TicksPerSecond)
            {
                anchor.CorrectionCount++;
                return new WorldClockDecision(WorldClockAction.Correct, predicted, drift);
            }

            return new WorldClockDecision(WorldClockAction.None, predicted, drift);
        }
    }

    public void Reset(WorldId worldId)
    {
        lock (sync)
        {
            anchors.Remove(worldId);
        }
    }

    public IReadOnlyList<WorldClockSnapshot> SnapshotWorldClocks()
    {
        lock (sync)
        {
            return Array.AsReadOnly(anchors
                .OrderBy(static pair => pair.Key.Value, StringComparer.Ordinal)
                .Select(pair => pair.Value.Snapshot(pair.Key, options.Enabled))
                .ToArray());
        }
    }

    private static void Validate(WorldClockValue value)
    {
        double limit = value.IsDaytime ? DayTicks : NightTicks;
        if (!double.IsFinite(value.TimeTicks) || value.TimeTicks < 0 || value.TimeTicks > limit)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"World time must be between 0 and {limit} ticks.");
        }
    }

    private static double ToPosition(WorldClockValue value) =>
        value.IsDaytime ? value.TimeTicks : DayTicks + value.TimeTicks;

    private static WorldClockValue FromPosition(double position)
    {
        double normalized = NormalizePosition(position);
        return normalized < DayTicks
            ? new WorldClockValue(true, normalized)
            : new WorldClockValue(false, normalized - DayTicks);
    }

    private static double NormalizePosition(double position)
    {
        double normalized = position % CycleTicks;
        return normalized < 0 ? normalized + CycleTicks : normalized;
    }

    private static double CircularDelta(double expected, double observed)
    {
        double delta = NormalizePosition(expected - observed);
        return delta > CycleTicks / 2 ? delta - CycleTicks : delta;
    }

    private sealed class Anchor
    {
        private Anchor(
            double position,
            long timestamp,
            WorldClockValue observed,
            DateTimeOffset sampledAt)
        {
            Position = position;
            Timestamp = timestamp;
            Observed = observed;
            Predicted = observed;
            SampledAt = sampledAt;
        }

        public double Position { get; private set; }
        public long Timestamp { get; private set; }
        public WorldClockValue Observed { get; private set; }
        public WorldClockValue Predicted { get; private set; }
        public double DriftTicks { get; private set; }
        public long CorrectionCount { get; set; }
        public long ReanchorCount { get; private set; }
        public DateTimeOffset SampledAt { get; private set; }

        public static Anchor Create(WorldClockValue value, long timestamp, DateTimeOffset sampledAt) =>
            new(ToPosition(value), timestamp, value, sampledAt);

        public void Observe(
            WorldClockValue observed,
            WorldClockValue predicted,
            DateTimeOffset sampledAt,
            double drift)
        {
            Observed = observed;
            Predicted = predicted;
            DriftTicks = drift;
            SampledAt = sampledAt;
        }

        public void Reanchor(
            WorldClockValue observed,
            long timestamp,
            DateTimeOffset sampledAt,
            double drift)
        {
            Position = ToPosition(observed);
            Timestamp = timestamp;
            Observed = observed;
            Predicted = observed;
            DriftTicks = drift;
            ReanchorCount++;
            SampledAt = sampledAt;
        }

        public WorldClockSnapshot Snapshot(WorldId worldId, bool enabled) => new(
            worldId,
            enabled,
            true,
            Observed,
            Predicted,
            DriftTicks,
            CorrectionCount,
            ReanchorCount,
            SampledAt);
    }
}
