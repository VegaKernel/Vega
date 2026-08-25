using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Core.Commands;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class CommandDispatcherLoggingTests
{
    [Test]
    public async Task LogsCanonicalCommandWithoutArguments()
    {
        var logger = new RecordingLogger();
        var dispatcher = new CommandDispatcher(
            [new StubHandler(new CommandDescriptor("login", ["auth"], []))],
            logger);

        CommandExecutionResult result = await dispatcher.DispatchAsync(
            Invocation("auth", ["account", "top-secret-password"]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(logger.Entries, Has.Count.EqualTo(1));
            Assert.That(logger.Entries[0].Source, Is.EqualTo("command"));
            Assert.That(logger.Entries[0].Text, Does.Contain("command=login"));
            Assert.That(logger.Entries[0].Text, Does.Contain("principal=tester"));
            Assert.That(logger.Entries[0].Text, Does.Not.Contain("account"));
            Assert.That(logger.Entries[0].Text, Does.Not.Contain("top-secret-password"));
        });
    }

    [Test]
    public async Task UnknownCommandIsLoggedWithoutArguments()
    {
        var logger = new RecordingLogger();
        var dispatcher = new CommandDispatcher([], logger);

        CommandExecutionResult result = await dispatcher.DispatchAsync(
            Invocation("not-a-command", ["secret"]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Failed));
            Assert.That(logger.Entries, Has.Count.EqualTo(1));
            Assert.That(logger.Entries[0].Text, Does.Contain("command=not-a-command"));
            Assert.That(logger.Entries[0].Text, Does.Not.Contain("secret"));
            Assert.That(logger.Entries[0].Level, Is.EqualTo(OperationsLogLevel.Warning));
        });
    }

    [Test]
    public async Task ForbiddenInvocationIsLoggedWithoutArguments()
    {
        var logger = new RecordingLogger();
        var dispatcher = new CommandDispatcher(
            [new StubHandler(new CommandDescriptor("login", [], ["vega.login"]))],
            logger);

        CommandExecutionResult result = await dispatcher.DispatchAsync(
            Invocation("login", ["top-secret-password"], hasPermission: false));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
            Assert.That(logger.Entries, Has.Count.EqualTo(1));
            Assert.That(logger.Entries[0].Text, Does.Contain("status=Forbidden"));
            Assert.That(logger.Entries[0].Text, Does.Contain("error=command.permission_denied"));
            Assert.That(logger.Entries[0].Text, Does.Not.Contain("top-secret-password"));
        });
    }

    [Test]
    public async Task LoggerFailureDoesNotFailCommand()
    {
        var dispatcher = new CommandDispatcher(
            [new StubHandler(new CommandDescriptor("safe", [], []))],
            new ThrowingLogger());

        CommandExecutionResult result = await dispatcher.DispatchAsync(Invocation("safe", []));

        Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
    }

    private static CommandInvocation Invocation(
        string name,
        IReadOnlyList<string> arguments,
        bool hasPermission = true) =>
        new(name, arguments, new TestPrincipal(hasPermission), NullOutput.Instance);

    private sealed class StubHandler(CommandDescriptor descriptor) : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = descriptor;

        public ValueTask<CommandExecutionResult> ExecuteAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(CommandExecutionResult.Success);
    }

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

    private sealed class ThrowingLogger : IOperationsLogger
    {
        public void Log(
            OperationsLogLevel level,
            string source,
            string text,
            Exception? exception = null,
            string? correlationId = null) =>
            throw new InvalidOperationException("logging unavailable");
    }

    private sealed record Entry(OperationsLogLevel Level, string Source, string Text);

    private sealed class TestPrincipal(bool hasPermission) : ICommandPrincipal
    {
        public string Name => "tester";
        public bool IsServer => false;
        public bool HasPermission(string permission) => hasPermission;
    }

    private sealed class NullOutput : ICommandOutput
    {
        public static NullOutput Instance { get; } = new();

        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
