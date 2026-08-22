using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Runtime;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Core.Commands.Administration;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class ServerControlCommandTests
{
    [Test]
    public async Task SaveAndShutdownUseOneAuthorizedServerControlBoundary()
    {
        var control = new RecordingServerControl();
        var dispatcher = new CommandDispatcher([
            new SaveCommand(control),
            new ShutdownCommand(control)
        ]);
        var denied = new Principal("player", []);
        var operatorPrincipal = new Principal("operator", ["tshock.cfg.maintenance"]);

        CommandExecutionResult deniedSave = await dispatcher.DispatchAsync(
            Invocation("save", [], denied));
        CommandExecutionResult saved = await dispatcher.DispatchAsync(
            Invocation("save", [], operatorPrincipal));
        CommandExecutionResult stopped = await dispatcher.DispatchAsync(
            Invocation("stop", ["Planned", "maintenance"], operatorPrincipal));

        Assert.Multiple(() =>
        {
            Assert.That(deniedSave.Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
            Assert.That(saved.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(stopped.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(control.SaveCount, Is.EqualTo(1));
            Assert.That(control.ShutdownReason, Is.EqualTo("Planned maintenance"));
        });
    }

    private static CommandInvocation Invocation(
        string name,
        IReadOnlyList<string> arguments,
        ICommandPrincipal principal) =>
        new(name, arguments, principal, NullOutput.Instance) { WorldId = WorldId.Primary };

    private sealed class RecordingServerControl : IServerControl
    {
        public int SaveCount { get; private set; }
        public string? ShutdownReason { get; private set; }

        public ValueTask SaveAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask ShutdownAsync(string reason, CancellationToken cancellationToken = default)
        {
            ShutdownReason = reason;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Principal(string name, IReadOnlyCollection<string> permissions) : ICommandPrincipal
    {
        public string Name { get; } = name;
        public bool IsServer => false;
        public bool HasPermission(string permission) => permissions.Contains(permission);
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
