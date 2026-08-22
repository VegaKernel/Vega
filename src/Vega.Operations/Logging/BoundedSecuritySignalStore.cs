using Vega.Contracts.Networking;

namespace Vega.Operations.Logging;

public sealed class BoundedSecuritySignalStore(int capacity = 512) :
    ISecuritySignalRecorder,
    ISecuritySignalReader
{
    private readonly object sync = new();
    private readonly Queue<SecuritySignal> signals = new(capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity)));

    public void Record(SecuritySignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        lock (sync)
        {
            if (signals.Count == capacity)
            {
                signals.Dequeue();
            }

            signals.Enqueue(signal);
        }
    }

    public IReadOnlyList<SecuritySignal> Read(
        SecuritySignalExposure exposure,
        int limit = 100)
    {
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        lock (sync)
        {
            return Array.AsReadOnly(signals
                .Reverse()
                .Take(limit)
                .Select(signal => exposure == SecuritySignalExposure.Sensitive
                    ? signal
                    : signal with
                    {
                        TransportEndpoint = null,
                        EffectiveEndpoint = null
                    })
                .ToArray());
        }
    }
}
