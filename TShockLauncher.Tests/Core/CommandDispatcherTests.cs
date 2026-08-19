using NUnit.Framework;
using TShockNG.Contracts.Commands;
using TShockNG.Contracts.Worlds;
using TShockNG.Core.Commands;

namespace TShockLauncher.Tests.Core;

[TestFixture]
public sealed class CommandDispatcherTests
{
    [Test]
    public async Task DispatchesAliasToHandler()
    {
        var handler = new FakeHandler(new CommandDescriptor("alpha", ["a"], []));
        var dispatcher = new CommandDispatcher([handler]);
        var invocation = CreateInvocation("a");

        CommandExecutionResult result = await dispatcher.DispatchAsync(invocation);

        Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(handler.ExecutionCount, Is.EqualTo(1));
    }

    [Test]
    public async Task DefaultsLegacyInvocationToPrimaryWorld()
    {
        var handler = new FakeHandler(new CommandDescriptor("alpha", [], []));
        var dispatcher = new CommandDispatcher([handler]);

        await dispatcher.DispatchAsync(CreateInvocation("alpha"));

        Assert.That(handler.LastWorldId, Is.EqualTo(WorldId.Primary));
    }

    [Test]
    public async Task PreservesExplicitWorldIdentity()
    {
        var handler = new FakeHandler(new CommandDescriptor("alpha", [], []));
        var dispatcher = new CommandDispatcher([handler]);
        var worldId = new WorldId("sandbox-42");
        var invocation = CreateInvocation("alpha") with { WorldId = worldId };

        await dispatcher.DispatchAsync(invocation);

        Assert.That(handler.LastWorldId, Is.EqualTo(worldId));
    }

    [Test]
    public async Task RejectsMissingPermissionBeforeHandlerRuns()
    {
        var handler = new FakeHandler(new CommandDescriptor("secure", [], ["server.secure"]));
        var dispatcher = new CommandDispatcher([handler]);
        var invocation = CreateInvocation("secure", new FakePrincipal("player", false, []));

        CommandExecutionResult result = await dispatcher.DispatchAsync(invocation);

        Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
        Assert.That(result.ErrorCode, Is.EqualTo("command.permission_denied"));
        Assert.That(handler.ExecutionCount, Is.Zero);
    }

    [Test]
    public async Task RejectsServerWhenCommandDisallowsServer()
    {
        var handler = new FakeHandler(new CommandDescriptor("playeronly", [], [], AllowServer: false));
        var dispatcher = new CommandDispatcher([handler]);
        var invocation = CreateInvocation("playeronly", new FakePrincipal("server", true, []));

        CommandExecutionResult result = await dispatcher.DispatchAsync(invocation);

        Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
        Assert.That(result.ErrorCode, Is.EqualTo("command.server_not_allowed"));
        Assert.That(handler.ExecutionCount, Is.Zero);
    }

    [Test]
    public void RejectsDuplicateAliases()
    {
        var first = new FakeHandler(new CommandDescriptor("first", ["same"], []));
        var second = new FakeHandler(new CommandDescriptor("second", ["same"], []));

        Assert.Throws<InvalidOperationException>(() => new CommandDispatcher([first, second]));
    }

    private static CommandInvocation CreateInvocation(string name, ICommandPrincipal? principal = null)
        => new(name, [], principal ?? new FakePrincipal("player", false, ["server.secure"]), new FakeOutput());

    private sealed class FakeHandler(CommandDescriptor descriptor) : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = descriptor;
        public int ExecutionCount { get; private set; }
        public WorldId? LastWorldId { get; private set; }

        public ValueTask<CommandExecutionResult> ExecuteAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            LastWorldId = invocation.WorldId;
            return ValueTask.FromResult(CommandExecutionResult.Success);
        }
    }

    private sealed class FakePrincipal(string name, bool isServer, IReadOnlyCollection<string> permissions) : ICommandPrincipal
    {
        public string Name { get; } = name;
        public bool IsServer { get; } = isServer;
        public bool HasPermission(string permission) => permissions.Contains(permission, StringComparer.Ordinal);
    }

    private sealed class FakeOutput : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
