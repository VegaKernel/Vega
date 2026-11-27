using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class CommandDispatcherTests
{
    [Test]
    public async Task BatchRegistrationPublishesAtomicallyAndConcurrentRetirementRemovesAllAliases()
    {
        var dispatcher = new CommandDispatcher([new FakeHandler(new CommandDescriptor("existing", [], []))]);
        var first = new FakeHandler(new CommandDescriptor("first", ["f"], []));
        var duplicate = new FakeHandler(new CommandDescriptor("second", ["existing"], []));
        Assert.Throws<InvalidOperationException>(() => dispatcher.RegisterBatch([first, duplicate]));
        Assert.That(dispatcher.CanDispatch("first"), Is.False);
        var registration = dispatcher.RegisterBatch([first, new FakeHandler(new CommandDescriptor("second", ["s"], []))]);
        Assert.That(dispatcher.CanDispatch("f") && dispatcher.CanDispatch("s"), Is.True);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(registration.Dispose)));
        Assert.That(dispatcher.CanDispatch("first") || dispatcher.CanDispatch("f") || dispatcher.CanDispatch("second") || dispatcher.CanDispatch("s"), Is.False);
        Assert.That(dispatcher.CanDispatch("existing"), Is.True);
    }
    [Test]
    public async Task CompletionObservationUsesCanonicalNameAndContainsNoCommandArguments()
    {
        var dispatcher = new CommandDispatcher([new FakeHandler(new CommandDescriptor("alpha", ["a"], ["admin"]))]);
        var observed = new List<CommandCompletedObservation>();
        dispatcher.Completed += _ => throw new InvalidOperationException("fixture fault");
        dispatcher.Completed += observed.Add;
        var world = new WorldId("fixture");
        var authorized = new FakePrincipal("server", true, ["admin"]);
        var denied = new FakePrincipal("server", true, []);
        await dispatcher.DispatchAsync(CreateInvocation("a", denied));
        await dispatcher.DispatchAsync(CreateInvocation("unknown", authorized));
        await dispatcher.DispatchAsync(CreateInvocation("a", authorized) with { WorldId = world, Arguments = ["private"] });
        Assert.That(observed, Is.EqualTo(new[] { new CommandCompletedObservation(world, "alpha", null, null, true, CommandExecutionStatus.Success) }));
    }

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
    public void ReportsOnlyRegisteredNamesAndAliasesAsOwned()
    {
        var handler = new FakeHandler(new CommandDescriptor("alpha", ["a"], []));
        var dispatcher = new CommandDispatcher([handler]);

        Assert.Multiple(() =>
        {
            Assert.That(dispatcher.CanDispatch("alpha"), Is.True);
            Assert.That(dispatcher.CanDispatch("A"), Is.True);
            Assert.That(dispatcher.CanDispatch("legacy"), Is.False);
            Assert.That(dispatcher.CanDispatch(string.Empty), Is.False);
        });
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
        Assert.That(result.Messages, Has.Count.EqualTo(1));
        Assert.That(result.Messages![0].DefaultText, Is.EqualTo("You do not have permission to use this command."));
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
        Assert.That(result.Messages, Has.Count.EqualTo(1));
        Assert.That(handler.ExecutionCount, Is.Zero);
    }

    [Test]
    public void RejectsDuplicateAliases()
    {
        var first = new FakeHandler(new CommandDescriptor("first", ["same"], []));
        var second = new FakeHandler(new CommandDescriptor("second", ["same"], []));

        Assert.Throws<InvalidOperationException>(() => new CommandDispatcher([first, second]));
    }

    [Test]
    public void CommandCatalogTracksDynamicRegistrationsWithoutAliasDuplicates()
    {
        var dispatcher = new CommandDispatcher([
            new FakeHandler(new CommandDescriptor("alpha", ["a"], []))
        ]);

        Assert.That(dispatcher.SnapshotCommands().Select(static descriptor => descriptor.Name),
            Is.EqualTo(new[] { "alpha" }));

        using IDisposable registration = dispatcher.Register(
            new FakeHandler(new CommandDescriptor("beta", ["b", "bee"], [])));

        Assert.That(dispatcher.SnapshotCommands().Select(static descriptor => descriptor.Name),
            Is.EqualTo(new[] { "alpha", "beta" }));

        registration.Dispose();
        Assert.That(dispatcher.SnapshotCommands().Select(static descriptor => descriptor.Name),
            Is.EqualTo(new[] { "alpha" }));
    }

    [Test]
    public async Task DynamicRegistrationIsRemovedWithItsLease()
    {
        var dispatcher = new CommandDispatcher([]);
        var handler = new FakeHandler(new CommandDescriptor("dynamic", ["dyn"], []));

        IDisposable registration = dispatcher.Register(handler);
        Assert.That(dispatcher.CanDispatch("dyn"), Is.True);
        Assert.That((await dispatcher.DispatchAsync(CreateInvocation("dynamic"))).Status,
            Is.EqualTo(CommandExecutionStatus.Success));

        registration.Dispose();

        Assert.That(dispatcher.CanDispatch("dynamic"), Is.False);
        Assert.That(dispatcher.CanDispatch("dyn"), Is.False);
        Assert.That((await dispatcher.DispatchAsync(CreateInvocation("dynamic"))).ErrorCode,
            Is.EqualTo("command.not_found"));
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
