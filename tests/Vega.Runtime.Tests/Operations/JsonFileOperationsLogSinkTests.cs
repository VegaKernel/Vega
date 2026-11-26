using System.Collections.Concurrent;
using System.Text.Json;
using NUnit.Framework;
using Vega.Contracts.Operations;
using Vega.Operations.Logging;

namespace Vega.Runtime.Tests.Operations;

public sealed class JsonFileOperationsLogSinkTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    [Test]
    public async Task SlowWriterDoesNotBlockProducersAndShutdownDrainsOnlyAcceptedRecords()
    {
        var writer = new ControlledWriter();
        var diagnostics = new ConcurrentQueue<string>();
        using var sink = new JsonFileOperationsLogSink(writer, 2, diagnostics.Enqueue);
        try
        {
            await Task.Run(() => sink.Emit(Record(1))).WaitAsync(Timeout);
            await writer.FlushStarted.Task.WaitAsync(Timeout);
            await Task.Run(() =>
            {
                sink.Emit(Record(2));
                sink.Emit(Record(3));
                sink.Emit(Record(4));
            }).WaitAsync(Timeout);
            Assert.That(sink.DroppedRecords, Is.EqualTo(1));
        }
        finally { writer.ReleaseFlush.TrySetResult(); }

        sink.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(writer.Lines.Select(Parse), Is.EqualTo(new[] { Record(1), Record(2), Record(3) }));
            Assert.That(writer.Flushes, Is.EqualTo(2));
            Assert.That(diagnostics, Has.Some.Contains("1 records dropped"));
        });
    }

    [Test]
    public async Task DiskFailureCountsUnconfirmedAndQueuedRecordsWithoutBreakingMemoryHistory()
    {
        var writer = new ControlledWriter();
        var failureReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sink = new JsonFileOperationsLogSink(writer, 2, _ => failureReported.TrySetResult());
        var store = new BoundedOperationsLogStore();
        var logger = new StructuredOperationsLogger([sink, store]);
        try
        {
            logger.Log(OperationsLogLevel.Warning, "test", "unflushed");
            await writer.FlushStarted.Task.WaitAsync(Timeout);
            logger.Log(OperationsLogLevel.Warning, "test", "queued");
            writer.ReleaseFlush.TrySetException(new IOException("disk unavailable"));
            await failureReported.Task.WaitAsync(Timeout);
            logger.Log(OperationsLogLevel.Warning, "test", "after failure");
        }
        finally { writer.ReleaseFlush.TrySetResult(); }

        Assert.Multiple(() =>
        {
            Assert.That(sink.DroppedRecords, Is.EqualTo(3));
            Assert.That(writer.Lines, Has.Count.EqualTo(1));
            Assert.That(store.Query(new OperationsLogFilter()).Select(record => record.Text),
                Is.EqualTo(new[] { "unflushed", "queued", "after failure" }));
        });
    }

    [Test]
    public void SeparateInstancesAppendJsonLinesToTheSameFile()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory();
        string path = Path.Combine(directory.FullName, "operations.jsonl");
        try
        {
            using (var first = new JsonFileOperationsLogSink(path)) first.Emit(Record(1));
            using (var second = new JsonFileOperationsLogSink(path)) second.Emit(Record(2));

            Assert.That(File.ReadLines(path).Select(Parse), Is.EqualTo(new[] { Record(1), Record(2) }));
        }
        finally { directory.Delete(recursive: true); }
    }

    private static OperationsLogRecord Record(long sequence) =>
        new(sequence, DateTimeOffset.UnixEpoch, OperationsLogLevel.Information, "test", $"message {sequence}");

    private static OperationsLogRecord? Parse(string json) => JsonSerializer.Deserialize<OperationsLogRecord>(json);

    private sealed class ControlledWriter : StringWriter
    {
        public TaskCompletionSource FlushStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFlush { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Lines { get; } = [];
        public int Flushes { get; private set; }

        public override Task WriteLineAsync(string? value)
        {
            Lines.Add(value!);
            return Task.CompletedTask;
        }

        public override Task FlushAsync()
        {
            Flushes++;
            FlushStarted.TrySetResult();
            return ReleaseFlush.Task;
        }
    }
}
