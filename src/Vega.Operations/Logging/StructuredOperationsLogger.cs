using System.Text.Json;
using System.Threading.Channels;
using Vega.Contracts.Operations;

namespace Vega.Operations.Logging;

public sealed class StructuredOperationsLogger : IOperationsLogger, IDisposable
{
    private const int DefaultQueueCapacity = 4096;

    private readonly IReadOnlyList<IOperationsLogSink> inlineSinks;
    private readonly IReadOnlyList<IOperationsLogSink> queuedSinks;
    private readonly Channel<OperationsLogRecord> queue;
    private readonly Task worker;
    private long sequence;
    private long droppedRecords;
    private long unreportedDroppedRecords;
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

        IOperationsLogSink[] configured = sinks.ToArray();
        inlineSinks = Array.AsReadOnly(configured
            .Where(static sink => sink is IInlineOperationsLogSink)
            .ToArray());
        queuedSinks = Array.AsReadOnly(configured
            .Where(static sink => sink is not IInlineOperationsLogSink)
            .ToArray());

        queue = Channel.CreateBounded<OperationsLogRecord>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        worker = queuedSinks.Count == 0 ? Task.CompletedTask : Task.Run(DrainAsync);
    }

    /// <summary>Total number of records dropped from queued sinks during this logger lifetime.</summary>
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

        var record = new OperationsLogRecord(
            Interlocked.Increment(ref sequence),
            DateTimeOffset.UtcNow,
            level,
            source,
            text,
            exception?.ToString(),
            correlationId);

        EmitTo(inlineSinks, record);

        if (queuedSinks.Count == 0)
        {
            return;
        }

        if (!queue.Writer.TryWrite(record) && Volatile.Read(ref disposed) == 0)
        {
            Interlocked.Increment(ref droppedRecords);
            Interlocked.Increment(ref unreportedDroppedRecords);
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
            while (queue.Reader.TryRead(out OperationsLogRecord? record))
            {
                if (record is not null)
                {
                    EmitTo(queuedSinks, record);
                }
            }

            EmitDroppedSummary();
        }

        EmitDroppedSummary();
    }

    private void EmitDroppedSummary()
    {
        long dropped = Interlocked.Exchange(ref unreportedDroppedRecords, 0);
        if (dropped <= 0)
        {
            return;
        }

        var summary = new OperationsLogRecord(
            Interlocked.Increment(ref sequence),
            DateTimeOffset.UtcNow,
            OperationsLogLevel.Warning,
            "logging",
            $"Dropped {dropped} log record(s) for queued sinks because the bounded logging queue was full.");

        EmitTo(inlineSinks, summary);
        EmitTo(queuedSinks, summary);
    }

    private static void EmitTo(IReadOnlyList<IOperationsLogSink> sinks, OperationsLogRecord record)
    {
        foreach (IOperationsLogSink sink in sinks)
        {
            try
            {
                sink.Emit(record);
            }
            catch
            {
                // A diagnostic sink must never take down the server or block independent sinks.
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
