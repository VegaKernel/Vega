using System.Text.Json;
using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Operations.Logging;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class OperationsCommandAuditSinkTests
{
    [Test]
    public void RecordEmitsJsonWithoutLogInjection()
    {
        var logger = new RecordingLogger();
        var sink = new OperationsCommandAuditSink(logger);

        sink.Record(new CommandAuditRecord(
            "broadcast",
            ["hello\n[Critical] forged"],
            "player\nadmin",
            PrincipalIsServer: false,
            CommandExecutionStatus.Success,
            ErrorCode: null));

        Assert.That(logger.Entries, Has.Count.EqualTo(1));
        LogEntry entry = logger.Entries[0];
        Assert.Multiple(() =>
        {
            Assert.That(entry.Level, Is.EqualTo(OperationsLogLevel.Information));
            Assert.That(entry.Source, Is.EqualTo("command-audit"));
        });

        using JsonDocument document = JsonDocument.Parse(entry.Text);
        JsonElement root = document.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("command").GetString(), Is.EqualTo("broadcast"));
            Assert.That(root.GetProperty("principal").GetString(), Is.EqualTo("player\nadmin"));
            Assert.That(root.GetProperty("arguments")[0].GetString(), Is.EqualTo("hello\n[Critical] forged"));
            Assert.That(root.GetProperty("status").GetString(), Is.EqualTo("Success"));
        });
    }

    [TestCase(CommandExecutionStatus.Forbidden)]
    [TestCase(CommandExecutionStatus.Failed)]
    public void RejectedOrFailedCommandsAreWarnings(CommandExecutionStatus status)
    {
        var logger = new RecordingLogger();
        var sink = new OperationsCommandAuditSink(logger);

        sink.Record(new CommandAuditRecord("test", [], "server", true, status, "failure"));

        Assert.That(logger.Entries.Single().Level, Is.EqualTo(OperationsLogLevel.Warning));
    }

    private sealed class RecordingLogger : IOperationsLogger
    {
        public List<LogEntry> Entries { get; } = [];

        public void Log(
            OperationsLogLevel level,
            string source,
            string text,
            Exception? exception = null,
            string? correlationId = null) =>
            Entries.Add(new LogEntry(level, source, text));
    }

    private sealed record LogEntry(OperationsLogLevel Level, string Source, string Text);
}
