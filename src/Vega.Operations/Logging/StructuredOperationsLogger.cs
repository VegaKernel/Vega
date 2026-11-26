using Vega.Contracts.Operations;

namespace Vega.Operations.Logging;

public sealed class StructuredOperationsLogger : IOperationsLogger
{
    private readonly IReadOnlyList<IOperationsLogSink> sinks;
    private long sequence;

    public StructuredOperationsLogger(IEnumerable<IOperationsLogSink> sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        this.sinks = Array.AsReadOnly(sinks.ToArray());
    }

    public void Log(
        OperationsLogLevel level,
        string source,
        string text,
        Exception? exception = null,
        string? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(text);

        var record = new OperationsLogRecord(
            Interlocked.Increment(ref sequence),
            DateTimeOffset.UtcNow,
            level,
            source,
            text,
            exception?.ToString(),
            correlationId);

        foreach (IOperationsLogSink sink in sinks)
        {
            try
            {
                sink.Emit(record);
            }
            catch
            {
                // A diagnostic sink must never take down the server or block other sinks.
            }
        }
    }
}

public sealed class ConsoleOperationsLogSink : IOperationsLogSink
{
    private static readonly object Sync = new();

    public void Emit(OperationsLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        string line = $"{record.Timestamp:O} [{record.Level}] {record.Source}: {record.Text}";

        lock (Sync)
        {
            TextWriter writer = record.Level >= OperationsLogLevel.Warning ? Console.Error : Console.Out;
            writer.WriteLine(line);
            if (!string.IsNullOrWhiteSpace(record.Exception))
            {
                writer.WriteLine(record.Exception);
            }
        }
    }
}
