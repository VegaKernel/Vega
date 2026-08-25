using Vega.Contracts.Operations;

namespace Vega.Operations.Logging;

public sealed class BoundedOperationsLogStore : IInlineOperationsLogSink, IOperationsLogStore
{
    private readonly object sync = new();
    private readonly OperationsLogRecord?[] records;
    private int count;
    private int next;

    public BoundedOperationsLogStore(int capacity = 2_000)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Capacity = capacity;
        records = new OperationsLogRecord[capacity];
    }

    public int Capacity { get; }

    public void Emit(OperationsLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (sync)
        {
            records[next] = record;
            next = (next + 1) % Capacity;
            count = Math.Min(count + 1, Capacity);
        }
    }

    public IReadOnlyList<OperationsLogRecord> Query(OperationsLogFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        int limit = Math.Clamp(filter.Limit, 0, Capacity);
        if (limit == 0)
        {
            return Array.Empty<OperationsLogRecord>();
        }

        lock (sync)
        {
            var matches = new List<OperationsLogRecord>(Math.Min(count, limit));
            int oldest = (next - count + Capacity) % Capacity;

            for (int index = 0; index < count; index++)
            {
                OperationsLogRecord? record = records[(oldest + index) % Capacity];
                if (record is not null && Matches(record, filter))
                {
                    matches.Add(record);
                }
            }

            if (matches.Count > limit)
            {
                matches.RemoveRange(0, matches.Count - limit);
            }

            return Array.AsReadOnly(matches.ToArray());
        }
    }

    private static bool Matches(OperationsLogRecord record, OperationsLogFilter filter) =>
        (filter.MinimumLevel is null || record.Level >= filter.MinimumLevel) &&
        (string.IsNullOrWhiteSpace(filter.Source) ||
            record.Source.Contains(filter.Source, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(filter.Text) ||
            record.Text.Contains(filter.Text, StringComparison.OrdinalIgnoreCase) ||
            (record.Exception?.Contains(filter.Text, StringComparison.OrdinalIgnoreCase) ?? false));
}
