using System.Diagnostics;
using System.Reflection;

namespace Vega.PluginSdk;

internal static class VegaDebugNetworkDiagnostics
{
    private static readonly object Sync = new();
    private static readonly List<VegaDebugNetworkPacket> Packets = [];
    private static readonly Dictionary<(string Processor, string PacketType), MutableMetric> Metrics = [];
    private static readonly List<IDisposable> ListenerSubscriptions = [];
    private static IDisposable? allListenersSubscription;
    private static CancellationTokenSource? expiration;
    private static VegaDebugNetworkTraceOptions options = new();
    private static HashSet<int>? packetIds;
    private static DateTimeOffset? startedAt;
    private static DateTimeOffset? expiresAt;
    private static long droppedPackets;
    private static long generation;
    private static bool active;

    public static void Start(VegaDebugNetworkTraceOptions requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        int durationSeconds = Math.Clamp(requested.DurationSeconds, 1, 300);
        int maxEvents = Math.Clamp(requested.MaxEvents, 16, 100_000);
        var normalized = requested with
        {
            DurationSeconds = durationSeconds,
            MaxEvents = maxEvents,
            PacketIds = requested.PacketIds?
                .Where(static id => id is >= 0 and <= byte.MaxValue)
                .Distinct()
                .ToArray()
        };

        long session;
        CancellationToken token;
        lock (Sync)
        {
            StopLocked();
            options = normalized;
            packetIds = normalized.PacketIds is { Count: > 0 }
                ? normalized.PacketIds.ToHashSet()
                : null;
            Packets.Clear();
            Metrics.Clear();
            droppedPackets = 0;
            startedAt = DateTimeOffset.UtcNow;
            expiresAt = startedAt.Value.AddSeconds(durationSeconds);
            active = true;
            session = ++generation;
            expiration = new CancellationTokenSource();
            token = expiration.Token;
            allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(new ListenerObserver());
        }

        _ = ExpireAsync(session, TimeSpan.FromSeconds(durationSeconds), token);
    }

    public static void Stop()
    {
        lock (Sync)
        {
            StopLocked();
        }
    }

    public static VegaDebugNetworkTraceSnapshot Capture()
    {
        lock (Sync)
        {
            IReadOnlyList<VegaDebugNetworkPacket> packets = Array.AsReadOnly(Packets.ToArray());
            IReadOnlyList<VegaDebugPacketProcessorMetric> metrics = Array.AsReadOnly(Metrics.Values
                .Select(static metric => metric.Snapshot())
                .OrderByDescending(static metric => metric.TotalMicroseconds)
                .ThenBy(static metric => metric.Processor, StringComparer.Ordinal)
                .ToArray());
            return new VegaDebugNetworkTraceSnapshot(
                active,
                startedAt,
                expiresAt,
                packets,
                metrics,
                droppedPackets);
        }
    }

    private static async Task ExpireAsync(long session, TimeSpan duration, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (Sync)
        {
            if (active && generation == session)
            {
                StopLocked();
            }
        }
    }

    private static void StopLocked()
    {
        active = false;
        expiration?.Cancel();
        expiration?.Dispose();
        expiration = null;
        allListenersSubscription?.Dispose();
        allListenersSubscription = null;
        foreach (IDisposable subscription in ListenerSubscriptions)
        {
            subscription.Dispose();
        }
        ListenerSubscriptions.Clear();
    }

    private static void Subscribe(DiagnosticListener listener)
    {
        if (listener.Name is not ("Vega.Network" or "Vega.PacketPipeline"))
        {
            return;
        }

        lock (Sync)
        {
            if (!active)
            {
                return;
            }

            ListenerSubscriptions.Add(listener.Subscribe(new EventObserver()));
        }
    }

    private static void Record(string eventName, object? payload)
    {
        if (payload is null)
        {
            return;
        }

        lock (Sync)
        {
            if (!active)
            {
                return;
            }

            if (eventName is "Packet.Inbound" or "Packet.Outbound")
            {
                RecordPacket(eventName, payload);
                return;
            }

            if (eventName == "ProcessorCompleted")
            {
                RecordProcessor(payload);
            }
        }
    }

    private static void RecordPacket(string eventName, object payload)
    {
        bool inbound = eventName == "Packet.Inbound";
        if (inbound && !options.IncludeInbound || !inbound && !options.IncludeOutbound)
        {
            return;
        }

        int? slot = ReadInt(payload, "Slot");
        if (options.Slot is int requestedSlot && slot != requestedSlot)
        {
            return;
        }

        int? packetId = ReadInt(payload, "PacketId");
        if (packetIds is not null && (packetId is null || !packetIds.Contains(packetId.Value)))
        {
            return;
        }

        if (Packets.Count >= options.MaxEvents)
        {
            droppedPackets++;
            return;
        }

        Packets.Add(new VegaDebugNetworkPacket(
            ReadDateTimeOffset(payload, "Timestamp") ?? DateTimeOffset.UtcNow,
            inbound ? "Inbound" : "Outbound",
            packetId,
            ReadInt(payload, "Size") ?? 0,
            slot));
    }

    private static void RecordProcessor(object payload)
    {
        string processor = ReadString(payload, "Processor") ?? "<unknown>";
        string packetType = ReadString(payload, "PacketType") ?? "<unknown>";
        var key = (processor, packetType);
        if (!Metrics.TryGetValue(key, out MutableMetric? metric))
        {
            metric = new MutableMetric(processor, packetType);
            Metrics.Add(key, metric);
        }

        metric.Record(
            ReadDouble(payload, "ElapsedMicroseconds") ?? 0,
            ReadString(payload, "Decision"),
            ReadString(payload, "Reason"));
    }

    private static object? Read(object payload, string propertyName) =>
        payload.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(payload);

    private static string? ReadString(object payload, string propertyName) =>
        Read(payload, propertyName)?.ToString();

    private static int? ReadInt(object payload, string propertyName)
    {
        object? value = Read(payload, propertyName);
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static double? ReadDouble(object payload, string propertyName)
    {
        object? value = Read(payload, propertyName);
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadDateTimeOffset(object payload, string propertyName) =>
        Read(payload, propertyName) is DateTimeOffset timestamp ? timestamp : null;

    private sealed class ListenerObserver : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener value) => Subscribe(value);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    private sealed class EventObserver : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value) => Record(value.Key, value.Value);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    private sealed class MutableMetric(string processor, string packetType)
    {
        private long calls;
        private double total;
        private double max;
        private long nonAllow;
        private string? lastDecision;
        private string? lastReason;

        public void Record(double elapsedMicroseconds, string? decision, string? reason)
        {
            calls++;
            total += elapsedMicroseconds;
            max = Math.Max(max, elapsedMicroseconds);
            if (!string.Equals(decision, "Allow", StringComparison.OrdinalIgnoreCase))
            {
                nonAllow++;
            }
            lastDecision = decision;
            lastReason = reason;
        }

        public VegaDebugPacketProcessorMetric Snapshot() => new(
            processor,
            packetType,
            calls,
            total,
            calls == 0 ? 0 : total / calls,
            max,
            nonAllow,
            lastDecision,
            lastReason);
    }
}
