using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Core.Commands;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class CommandDispatcherAuditTests
{
    [Test]
    public async Task NameOnlyAuditNeverExposesInvocationArguments()
    {
        var sink = new RecordingAuditSink();
        var dispatcher = new CommandDispatcher(
            [new StubHandler(new CommandDescriptor("login", ["auth"], [], AuditMode: CommandAuditMode.NameOnly))],
            sink);

        CommandExecutionResult result = await dispatcher.DispatchAsync(
            Invocation("auth", ["account", "top-secret-password"]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(sink.Records, Has.Count.EqualTo(1));
            Assert.That(sink.Records[0].CommandName, Is.EqualTo("login"));
            Assert.That(sink.Records[0].Arguments, Is.Empty);
            Assert.That(sink.Records[0].PrincipalName, Is.EqualTo("tester"));
        });
    }

    [Test]
    public async Task NameAndArgumentsAuditUsesCanonicalNameAndCopiesArguments()
    {
        var sink = new RecordingAuditSink();
        var dispatcher = new CommandDispatcher(
            [new StubHandler(new CommandDescriptor(
                "broadcast",
                ["bc"],
                [],
                AuditMode: CommandAuditMode.NameAndArguments))],
            sink);
        string[] arguments = ["maintenance", "soon"];

        await dispatcher.DispatchAsync(Invocation("bc", arguments));
        arguments[0] = "mutated-after-dispatch";

        Assert.Multiple(() =>
        {
            Assert.That(sink.Records, Has.Count.EqualTo(1));
            Assert.That(sink.Records[0].CommandName, Is.EqualTo("broadcast"));
            Assert.That(sink.Records[0].Arguments, Is.EqualTo(new[] { "maintenance", "soon" }));
        });
    }

    [Test]
    public async Task NoneAuditModeEmitsNoRecord()
    {
        var sink = new RecordingAuditSink();
        var dispatcher = new CommandDispatcher(
            [new StubHandler(new CommandDescriptor("quiet", [], [], AuditMode: CommandAuditMode.None))],
            sink);

        await dispatcher.DispatchAsync(Invocation("quiet", ["secret"]));

        Assert.That(sink.Records, Is.Empty);
    }

    [Test]
    public async Task ForbiddenInvocationIsAuditedWithoutLeakingNameOnlyArguments()
    {
        var sink = new RecordingAuditSink();
        var dispatcher = new CommandDispatcher(
            [new StubHandler(new CommandDescriptor("login", [], ["vega.login"]))],
            sink);

        CommandExecutionResult result = await dispatcher.DispatchAsync(
            Invocation("login", ["top-secret-password"], hasPermission: false));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
            Assert.That(sink.Records, Has.Count.EqualTo(1));
            Assert.That(sink.Records[0].Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
            Assert.That(sink.Records[0].ErrorCode, Is.EqualTo("command.permission_denied"));
            Assert.That(sink.Records[0].Arguments, Is.Empty);
        });
    }

    [Test]
    public async Task AuditSinkFailureDoesNotFailCommand()
    {
        var dispatcher = new CommandDispatcher(
            [new StubHandler(new CommandDescriptor("safe", [], []))],
            new ThrowingAuditSink());

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

    private sealed class RecordingAuditSink : ICommandAuditSink
    {
        public List<CommandAuditRecord> Records { get; } = [];

        public void Record(CommandAuditRecord record) => Records.Add(record);
    }

    private sealed class ThrowingAuditSink : ICommandAuditSink
    {
        public void Record(CommandAuditRecord record) => throw new InvalidOperationException("audit unavailable");
    }

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
