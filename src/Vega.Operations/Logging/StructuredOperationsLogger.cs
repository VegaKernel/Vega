using System.Text.Json;
using System.Threading.Channels;
using Vega.Contracts.Operations;

namespace Vega.Operations.Logging;

public sealed class StructuredOperationsLogger : IOperationsLogger, IDisposable
{
    private const int DefaultQueueCapacity = 4096;

    private readonly IReadOnlyList<IOperationsLogSink> sinks;
    private readonly Channel<PendingLogRecord> queue;
    private readonly Task worker;
    private long sequence;
    private long droppedRecords;
    private int disposed;

    public StructuredOperationsLogger(
        IEnumerable<IOperationsLogSink> sinks,
        int queueCapacity = DefaultQueueCapacity)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        if (queueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        }

        this.sinks = Array.AsReadOnly(sinks.ToArray());
        queue = Channel.CreateBounded<PendingLogRecord>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        worker = Task.Run(DrainAsync);
    }

    public long DroppedRecords => Interlocked.Read(ref droppedRecords);

    public void Log(
        OperationsLogLevel level,
        string source,
        string text,
        Exception? exception = null,
        string? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(text);

        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        var pending = new PendingLogRecord(
            DateTimeOffset.UtcNow,
            level,
            source,
            text,
            exception,
            correlationId);

        if (!queue.Writer.TryWrite(pending) && Volatile.Read(ref disposed) == 0)
        {
            Interlocked.Increment(ref droppedRecords);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        queue.Writer.TryComplete();
        try
        {
            worker.GetAwaiter().GetResult();
        }
        catch
        {
            // Logging shutdown must never prevent the server from terminating.
        }
    }

    private async Task DrainAsync()
    {
        while (await queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (queue.Reader.TryRead(out PendingLogRecord? pending))
            {
                if (pending is null)
                {
                    continue;
                }

                EmitDroppedSummary();
                Emit(CreateRecord(pending));
            }
        }

        EmitDroppedSummary();
    }

    private OperationsLogRecord CreateRecord(PendingLogRecord pending) => new(
        Interlocked.Increment(ref sequence),
        pending.Timestamp,
        pending.Level,
        pending.Source,
        pending.Text,
        pending.Exception?.ToString(),
        pending.CorrelationId);

    private void EmitDroppedSummary()
    {
        long dropped = Interlocked.Exchange(ref droppedRecords, 0);
        if (dropped <= 0)
        {
            return;
        }

        Emit(new OperationsLogRecord(
            Interlocked.Increment(ref sequence),
            DateTimeOffset.UtcNow,
            OperationsLogLevel.Warning,
            "logging",
            $"Dropped {dropped} log record(s) because the bounded logging queue was full."));
    }

    private void Emit(OperationsLogRecord record)
    {
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

    private sealed record PendingLogRecord(
        DateTimeOffset Timestamp,
        OperationsLogLevel Level,
        string Source,
        string Text,
        Exception? Exception,
        string? CorrelationId);
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

public sealed class JsonFileOperationsLogSink : IOperationsLogSink, IDisposable
{
    private readonly object sync = new();
    private readonly StreamWriter writer;

    public JsonFileOperationsLogSink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        writer = new StreamWriter(new FileStream(
            fullPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite))
        {
            AutoFlush = true
        };
    }

    public void Emit(OperationsLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        string json = JsonSerializer.Serialize(record);
        lock (sync)
        {
            writer.WriteLine(json);
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            writer.Dispose();
        }
    }
}
