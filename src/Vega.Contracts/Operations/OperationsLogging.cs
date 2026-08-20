namespace TShockNG.Contracts.Operations;

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
