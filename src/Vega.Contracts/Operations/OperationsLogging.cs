namespace Vega.Contracts.Operations;

public enum OperationsLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

public sealed record OperationsLogRecord(
    long Sequence,
    DateTimeOffset Timestamp,
    OperationsLogLevel Level,
    string Source,
    string Text,
    string? Exception = null,
    string? CorrelationId = null);

public sealed record OperationsLogFilter(
    OperationsLogLevel? MinimumLevel = null,
    string? Source = null,
    string? Text = null,
    int Limit = 200);

public interface IOperationsLogSink
{
    void Emit(OperationsLogRecord record);
}

/// <summary>
/// Marker for sinks whose <see cref="IOperationsLogSink.Emit"/> implementation is guaranteed to be
/// bounded, non-blocking and safe to execute on the caller thread. Potentially blocking sinks such
/// as files, terminals or remote transports must not implement this interface.
/// </summary>
public interface IInlineOperationsLogSink : IOperationsLogSink
{
}

public interface IOperationsLogStore
{
    int Capacity { get; }

    IReadOnlyList<OperationsLogRecord> Query(OperationsLogFilter filter);
}

public interface IOperationsLogger
{
    void Log(
        OperationsLogLevel level,
        string source,
        string text,
        Exception? exception = null,
        string? correlationId = null);
}
