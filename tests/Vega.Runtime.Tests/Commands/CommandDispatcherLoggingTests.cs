using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Core.Commands;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class CommandDispatcherLoggingTests
{
    [Test]
    public async Task PublishesCanonicalCommandWithoutArguments()
    {
        var dispatcher = new CommandDispatcher(
            [new StubHandler(new CommandDescriptor("login", ["auth"], []))]);
        var entries = new List<OperationsLogMessage>();
        dispatcher.LogProduced += entries.Add;

        CommandExecutionResult result = await dispatcher.DispatchAsync(
            Invocation("auth", ["account", "top-secret-password"]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].Source, Is.EqualTo("command"));
            Assert.That(entries[0].Text, Does.Contain("command=login"));
            Assert.That(entries[0].Text, Does.Contain("principal=tester"));
            Assert.That(entries[0].Text, Does.Not.Contain("account"));
            Assert.That(entries[0].Text, Does.Not.Contain("top-secret-password"));
        });
    }

    [Test]
    public async Task UnknownCommandIsPublishedWithoutArguments()
    {
        var dispatcher = new CommandDispatcher([]);
        var entries = new List<OperationsLogMessage>();
        dispatcher.LogProduced += entries.Add;

        CommandExecutionResult result = await dispatcher.DispatchAsync(
            Invocation("not-a-command", ["secret"]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Failed));
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].Text, Does.Contain("command=not-a-command"));
            Assert.That(entries[0].Text, Does.Not.Contain("secret"));
            Assert.That(entries[0].Level, Is.EqualTo(OperationsLogLevel.Warning));
        });
    }

    [Test]
    public async Task ForbiddenInvocationIsPublishedWithoutArguments()
    {
        var dispatcher = new CommandDispatcher(
            [new StubHandler(new CommandDescriptor("login", [], ["vega.login"]))]);
        var entries = new List<OperationsLogMessage>();
        dispatcher.LogProduced += entries.Add;

        CommandExecutionResult result = await dispatcher.DispatchAsync(
            Invocation("login", ["top-secret-password"], hasPermission: false));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].Text, Does.Contain("status=Forbidden"));
            Assert.That(entries[0].Text, Does.Contain("error=command.permission_denied"));
            Assert.That(entries[0].Text, Does.Not.Contain("top-secret-password"));
        });
    }

    [Test]
    public async Task ObserverFailureDoesNotFailCommandOrBlockLaterObservers()
    {
        var dispatcher = new CommandDispatcher(
            [new StubHandler(new CommandDescriptor("safe", [], []))]);
        var entries = new List<OperationsLogMessage>();
        dispatcher.LogProduced += static _ => throw new InvalidOperationException("observer unavailable");
        dispatcher.LogProduced += entries.Add;

        CommandExecutionResult result = await dispatcher.DispatchAsync(Invocation("safe", []));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].Text, Does.Contain("command=safe"));
        });
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
