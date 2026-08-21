using System.Diagnostics;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;

namespace Vega.Operations.Metrics;

public sealed class SampledRuntimeMetricsReader(IHostMetricsReader hostMetrics) : IRuntimeMetricsReader
{
    public RuntimeMetricsSnapshot Read()
    {
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        ThreadPool.GetAvailableThreads(out int availableWorkers, out _);
        ThreadPool.GetMaxThreads(out int maximumWorkers, out _);

        return new RuntimeMetricsSnapshot(
            hostMetrics.Read(),
            GC.GetTotalMemory(false),
            gc.HeapSizeBytes,
            gc.FragmentedBytes,
            GC.GetTotalAllocatedBytes(false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            availableWorkers,
            maximumWorkers,
            DateTimeOffset.UtcNow);
    }
}

public sealed class NetworkMetricsCounter : INetworkMetricsReader, INetworkMetricsRecorder
{
    private const int MaximumSecurityRules = 64;
    private readonly object securitySync = new();
    private readonly Dictionary<string, long> securityRuleCounts = new(StringComparer.Ordinal);
    private long inboundPackets;
    private long inboundBytes;
    private long outboundPackets;
    private long outboundBytes;
    private long rejectedInboundPackets;
    private long securityViolations;

    public void RecordInbound(int byteCount)
    {
        if (byteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }

        Interlocked.Increment(ref inboundPackets);
        Interlocked.Add(ref inboundBytes, byteCount);
    }

    public void RecordOutbound(int byteCount)
    {
        if (byteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }

        Interlocked.Increment(ref outboundPackets);
        Interlocked.Add(ref outboundBytes, byteCount);
    }

    public void RecordSecurityDecision(PacketProcessingDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        switch (decision.Kind)
        {
            case PacketDecisionKind.Allow:
                return;
            case PacketDecisionKind.Reject:
                Interlocked.Increment(ref rejectedInboundPackets);
                break;
            case PacketDecisionKind.Violation:
                Interlocked.Increment(ref securityViolations);
                break;
        }

        if (string.IsNullOrWhiteSpace(decision.Reason))
        {
            return;
        }

        lock (securitySync)
        {
            string rule = decision.Reason;
            if (!securityRuleCounts.ContainsKey(rule) && securityRuleCounts.Count >= MaximumSecurityRules - 1)
            {
                rule = "other";
            }

            securityRuleCounts.TryGetValue(rule, out long count);
            securityRuleCounts[rule] = count + 1;
        }
    }

    public NetworkMetricsSnapshot Read()
    {
        Dictionary<string, long> rules;
        lock (securitySync)
        {
            rules = new Dictionary<string, long>(securityRuleCounts, StringComparer.Ordinal);
        }

        return new NetworkMetricsSnapshot(
            Interlocked.Read(ref inboundPackets),
            Interlocked.Read(ref inboundBytes),
            Interlocked.Read(ref outboundPackets),
            Interlocked.Read(ref outboundBytes),
            DateTimeOffset.UtcNow)
        {
            RejectedInboundPackets = Interlocked.Read(ref rejectedInboundPackets),
            SecurityViolations = Interlocked.Read(ref securityViolations),
            SecurityRuleCounts = rules
        };
    }
}
