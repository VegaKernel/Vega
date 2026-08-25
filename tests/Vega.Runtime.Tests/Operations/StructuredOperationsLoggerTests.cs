using NUnit.Framework;
using Vega.Contracts.Operations;
using Vega.Operations.Logging;

namespace Vega.Runtime.Tests.Operations;

[TestFixture]
public sealed class StructuredOperationsLoggerTests
{
    [Test]
    public async Task SlowSinkDoesNotBlockLogCaller()
    {
        using var sink = new BlockingSink();
        using var logger = new StructuredOperationsLogger([sink], queueCapacity: 4);

        Task call = Task.Run(() => logger.Log(OperationsLogLevel.Information, "test", "message"));

        Task completed = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.That(completed, Is.SameAs(call));
        Assert.That(sink.Started.Wait(TimeSpan.FromSeconds(2)), Is.True);

        sink.Release();
        await call;
    }

    [Test]
    public void FullQueueDropsRecordsInsteadOfBlockingProducer()
    {
        using var sink = new BlockingSink();
        var logger = new StructuredOperationsLogger([sink], queueCapacity: 1);
        try
        {
            logger.Log(OperationsLogLevel.Information, "test", "first");
            Assert.That(sink.Started.Wait(TimeSpan.FromSeconds(2)), Is.True);

            logger.Log(OperationsLogLevel.Information, "test", "queued");
            for (var index = 0; index < 32; index++)
            {
                logger.Log(OperationsLogLevel.Information, "test", $"overflow-{index}");
            }

            Assert.That(logger.DroppedRecords, Is.GreaterThan(0));
        }
        finally
        {
            sink.Release();
            logger.Dispose();
        }

        Assert.That(
            sink.Records.Any(static record =>
                record.Source == "logging" && record.Text.Contains("Dropped", StringComparison.Ordinal)),
            Is.True);
    }

    [Test]
    public void DisposeDrainsQueuedRecords()
    {
        var sink = new RecordingSink();
        var logger = new StructuredOperationsLogger([sink], queueCapacity: 16);

        for (var index = 0; index < 10; index++)
        {
            logger.Log(OperationsLogLevel.Information, "test", $"message-{index}");
        }

        logger.Dispose();

        Assert.That(sink.Records.Count(static record => record.Source == "test"), Is.EqualTo(10));
    }

    private sealed class RecordingSink : IOperationsLogSink
    {
        private readonly object sync = new();
        private readonly List<OperationsLogRecord> records = [];

        public IReadOnlyList<OperationsLogRecord> Records
        {
            get
            {
                lock (sync)
                {
                    return records.ToArray();
                }
            }
        }

        public void Emit(OperationsLogRecord record)
        {
            lock (sync)
            {
                records.Add(record);
            }
        }
    }

    private sealed class BlockingSink : IOperationsLogSink, IDisposable
    {
        private readonly ManualResetEventSlim started = new(false);
        private readonly ManualResetEventSlim release = new(false);
        private readonly object sync = new();
        private readonly List<OperationsLogRecord> records = [];

        public ManualResetEventSlim Started => started;

        public IReadOnlyList<OperationsLogRecord> Records
        {
            get
            {
                lock (sync)
                {
                    return records.ToArray();
                }
            }
        }

        public void Emit(OperationsLogRecord record)
        {
            started.Set();
            release.Wait();
            lock (sync)
            {
                records.Add(record);
            }
        }

        public void Release() => release.Set();

        public void Dispose()
        {
            release.Set();
            started.Dispose();
            release.Dispose();
        }
    }
}
