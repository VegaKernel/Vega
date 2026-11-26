using System.Text.Json;
using System.Threading.Channels;
using Vega.Contracts.Operations;

namespace Vega.Operations.Logging;

public sealed class JsonFileOperationsLogSink : IOperationsLogSink, IDisposable
{
    private const int QueueCapacity = 4096;
    private const int BatchSize = 128;
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly Channel<OperationsLogRecord> queue;
    private readonly Action<string> report;
    private readonly Task worker;
    private long droppedRecords;
    private int disposed;

    public JsonFileOperationsLogSink(string path)
        : this(OpenWriter(path), QueueCapacity, Console.Error.WriteLine) { }

    internal JsonFileOperationsLogSink(TextWriter writer, int capacity, Action<string> report)
    {
        this.report = report;
        queue = Channel.CreateBounded<OperationsLogRecord>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        worker = Task.Run(() => WriteAsync(writer));
    }

    public long DroppedRecords => Interlocked.Read(ref droppedRecords);

    public void Emit(OperationsLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!queue.Writer.TryWrite(record)) Interlocked.Increment(ref droppedRecords);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        queue.Writer.TryComplete();
        try { worker.WaitAsync(ShutdownTimeout).GetAwaiter().GetResult(); }
        catch (TimeoutException) { Report("Vega file logging did not finish within 5 seconds; queued records may not be saved."); }
        long dropped = DroppedRecords;
        if (dropped > 0) Report($"Vega file logging: {dropped} records dropped or not confirmed written.");
    }

    private async Task WriteAsync(TextWriter writer)
    {
        int unflushed = 0;
        bool reportedOverflow = false;
        try
        {
            while (await queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (unflushed < BatchSize && queue.Reader.TryRead(out OperationsLogRecord? record))
                {
                    unflushed++;
                    await writer.WriteLineAsync(JsonSerializer.Serialize(record)).ConfigureAwait(false);
                }
                await writer.FlushAsync().ConfigureAwait(false);
                unflushed = 0;

                if (!reportedOverflow && DroppedRecords > 0 && Volatile.Read(ref disposed) == 0)
                {
                    reportedOverflow = true;
                    Report("Vega file logging queue overflowed; incoming file log records were dropped.");
                }
            }
        }
        catch (Exception ex)
        {
            queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out _)) unflushed++;
            Interlocked.Add(ref droppedRecords, unflushed);
            Report($"Vega file logging disabled after a write failure: {ex.Message}");
        }
        finally
        {
            try { await writer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Report($"Vega file logging failed to close: {ex.Message}"); }
        }
    }

    private void Report(string message)
    {
        try { report(message); }
        catch { }
    }

    private static StreamWriter OpenWriter(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return new StreamWriter(new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite,
            4096, FileOptions.Asynchronous));
    }
}
