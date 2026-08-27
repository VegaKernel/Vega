using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Core.Messaging;
using Vega.Core.Modules;
using Vega.Terraria.Operations;

namespace Vega.Runtime.Tests.Operations;

[TestFixture]
public sealed class GenericLoggerIntegrationTests
{
    [Test]
    public async Task PluginSdkLogUsesTheHostOperationsLoggerWithPluginSource()
    {
        var logger = new RecordingLogger();
        var commands = new CommandDispatcher([]);
        string dataDirectory = Path.Combine(Path.GetTempPath(), "vega-plugin-log-tests", Guid.NewGuid().ToString("N"));

        await using (var scope = new HotModuleRegistrationScope(
            commands,
            logger,
            "ExamplePlugin",
            dataDirectory))
        {
            scope.Log.Write(OperationsLogLevel.Information, "plugin message");
        }

        Entry entry = logger.Entries.Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.Level, Is.EqualTo(OperationsLogLevel.Information));
            Assert.That(entry.Source, Is.EqualTo("plugin:ExamplePlugin"));
            Assert.That(entry.Text, Is.EqualTo("plugin message"));
        });

        if (Directory.Exists(dataDirectory))
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    public void ChatPipelineProducesReadyGenericLogMessage()
    {
        PlayerSnapshot sender = Player(3, "Alice");
        PlayerSnapshot recipient = Player(4, "Bob");
        var chat = new ChatPipeline();
        var entries = new List<OperationsLogMessage>();
        chat.LogProduced += entries.Add;

        ChatPipelineResult result = chat.Process(new ChatMessageContext(
            WorldId.Primary,
            sender,
            "hello world",
            [recipient]));

        OperationsLogMessage entry = entries.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.Allowed, Is.True);
            Assert.That(entry.Source, Is.EqualTo("chat"));
            Assert.That(entry.Text, Is.EqualTo("Alice: hello world"));
        });
    }

    [Test]
    public void ForwarderMovesReadyMessagesToLoggerAndDetachesOnDispose()
    {
        var logger = new RecordingLogger();
        var source = new RecordingSource();
        using (var forwarder = new OperationsLogSourceForwarder(logger, source))
        {
            source.Emit(new OperationsLogMessage(
                OperationsLogLevel.Warning,
                "command",
                "command=kick status=Forbidden"));
        }

        source.Emit(new OperationsLogMessage(
            OperationsLogLevel.Information,
            "command",
            "command=after-dispose"));

        Assert.Multiple(() =>
        {
            Assert.That(logger.Entries, Has.Count.EqualTo(1));
            Assert.That(logger.Entries[0].Source, Is.EqualTo("command"));
            Assert.That(logger.Entries[0].Text, Is.EqualTo("command=kick status=Forbidden"));
        });
    }

    private static PlayerSnapshot Player(int slot, string name) => new(
        new PlayerId(slot),
        name,
        IsDead: false,
        Life: 100,
        MaxLife: 100,
        Mana: 20,
        MaxMana: 20,
        new PlayerPosition(slot * 16, slot * 16),
        IsHostile: false,
        Team: 0);

    private sealed class RecordingLogger : IOperationsLogger
    {
        public List<Entry> Entries { get; } = [];

        public void Log(
            OperationsLogLevel level,
            string source,
            string text,
            Exception? exception = null,
            string? correlationId = null) =>
            Entries.Add(new Entry(level, source, text));
    }

    private sealed class RecordingSource : IOperationsLogSource
    {
        public event Action<OperationsLogMessage>? LogProduced;

        public void Emit(OperationsLogMessage message) => LogProduced?.Invoke(message);
    }

    private sealed record Entry(OperationsLogLevel Level, string Source, string Text);
}
