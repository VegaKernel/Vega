using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class CommandDiscoveryCommandTests
{
    [Test]
    public async Task HelpListsOnlyCommandsAvailableToCallerAndTracksDynamicRegistrations()
    {
        var dispatcher = new CommandDispatcher([
            new FakeHandler(new CommandDescriptor("public", ["pub"], [])),
            new FakeHandler(new CommandDescriptor("secure", ["sec"], ["server.secure"]))
        ]);
        var help = new HelpCommand(dispatcher);
        var principal = new FakePrincipal("player", false, []);

        CommandExecutionResult before = await help.ExecuteAsync(Invocation("help", [], principal), default);
        using IDisposable registration = dispatcher.Register(
            new FakeHandler(new CommandDescriptor("dynamic", ["dyn"], [])));
        CommandExecutionResult after = await help.ExecuteAsync(Invocation("help", [], principal), default);

        Assert.Multiple(() =>
        {
            Assert.That(before.Messages![0].Arguments[0]?.ToString(), Does.Contain("public"));
            Assert.That(before.Messages![0].Arguments[0]?.ToString(), Does.Not.Contain("secure"));
            Assert.That(before.Messages![0].Arguments[0]?.ToString(), Does.Not.Contain("dynamic"));
            Assert.That(after.Messages![0].Arguments[0]?.ToString(), Does.Contain("dynamic"));
        });
    }

    [Test]
    public async Task HelpResolvesAliasesAndReportsPermissions()
    {
        var dispatcher = new CommandDispatcher([
            new FakeHandler(new CommandDescriptor(
                "secure",
                ["sec"],
                ["server.secure"],
                Syntax: "secure <target>"))
        ]);
        var help = new HelpCommand(dispatcher);

        CommandExecutionResult result = await help.ExecuteAsync(
            Invocation("help", ["sec"], new FakePrincipal("admin", false, ["server.secure"])),
            default);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(result.Messages![0].Arguments, Is.EqualTo(
                new object?[] { "secure", "secure <target>", "sec", "server.secure" }));
        });
    }

    [Test]
    public async Task AliasesRequiresACommandOrAlias()
    {
        var aliases = new AliasesCommand(new CommandDispatcher([
            new FakeHandler(new CommandDescriptor("public", ["pub"], []))
        ]));

        CommandExecutionResult result = await aliases.ExecuteAsync(
            Invocation("aliases", [], new FakePrincipal("player", false, [])),
            default);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
            Assert.That(result.ErrorCode, Is.EqualTo("command.aliases.usage"));
        });
    }

    [Test]
    public async Task AliasesResolvesCanonicalNameOrAliasLikeTerraZ()
    {
        var dispatcher = new CommandDispatcher([
            new FakeHandler(new CommandDescriptor("public", ["pub", "p"], [])),
            new FakeHandler(new CommandDescriptor("secure", ["sec"], ["server.secure"]))
        ]);
        var aliases = new AliasesCommand(dispatcher);
        var principal = new FakePrincipal("player", false, []);

        CommandExecutionResult byName = await aliases.ExecuteAsync(
            Invocation("aliases", ["/public"], principal),
            default);
        CommandExecutionResult byAlias = await aliases.ExecuteAsync(
            Invocation("aliases", ["sec"], principal),
            default);

        Assert.Multiple(() =>
        {
            Assert.That(byName.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(byName.Messages![0].Arguments, Is.EqualTo(new object?[] { "public", "/pub, /p" }));
            Assert.That(byAlias.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(byAlias.Messages![0].Arguments, Is.EqualTo(new object?[] { "secure", "/sec" }));
        });
    }

    private static CommandInvocation Invocation(
        string name,
        IReadOnlyList<string> arguments,
        ICommandPrincipal principal) =>
        new(name, arguments, principal, new NullOutput()) { WorldId = WorldId.Primary };

    private sealed class FakeHandler(CommandDescriptor descriptor) : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = descriptor;

        public ValueTask<CommandExecutionResult> ExecuteAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(CommandExecutionResult.Success);
    }

    private sealed class FakePrincipal(
        string name,
        bool isServer,
        IReadOnlyCollection<string> permissions) : ICommandPrincipal
    {
        public string Name { get; } = name;
        public bool IsServer { get; } = isServer;
        public bool HasPermission(string permission) => permissions.Contains(permission, StringComparer.Ordinal);
    }

    private sealed class NullOutput : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
