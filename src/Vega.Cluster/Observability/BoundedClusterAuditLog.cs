using Vega.Contracts.Cluster;

namespace Vega.Cluster.Observability;

public sealed class BoundedClusterAuditLog : IClusterAuditSink
{
    private readonly object sync = new();
    private readonly ClusterAuditRecord?[] records;
    private int count;
    private int next;

    public BoundedClusterAuditLog(int capacity = 2_000)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        records = new ClusterAuditRecord[capacity];
    }

    public void Record(ClusterAuditRecord record)
    {
        lock (sync)
        {
            records[next] = record;
            next = (next + 1) % records.Length;
            count = Math.Min(count + 1, records.Length);
        }
    }

    public IReadOnlyList<ClusterAuditRecord> Snapshot()
    {
        lock (sync)
        {
            var snapshot = new ClusterAuditRecord[count];
            int oldest = (next - count + records.Length) % records.Length;
            for (int index = 0; index < count; index++)
            {
                snapshot[index] = records[(oldest + index) % records.Length]!;
            }

            return Array.AsReadOnly(snapshot);
        }
    }
}
