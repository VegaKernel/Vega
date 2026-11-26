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
        var operatorPrincipal = new Principal("operator", ["vega.cfg.maintenance"]);

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

    [Test]
    public async Task NoSaveShutdownAndPasswordRequireDistinctRightsAndNeverEchoThePassword()
    {
        var control = new RecordingServerControl();
        var dispatcher = new CommandDispatcher([new ShutdownCommand(control), new ShutdownCommand(control, save: false), new ServerPasswordCommand(control)]);
        var maintenance = new Principal("operator", ["vega.cfg.maintenance"]);
        Assert.That((await dispatcher.DispatchAsync(Invocation("off-nosave", [], maintenance))).Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
        Assert.That((await dispatcher.DispatchAsync(Invocation("serverpassword", ["example"], maintenance))).Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
        var privileged = new Principal("operator", ["vega.cfg.maintenance.nosave", "vega.cfg.password"]);
        var password = await dispatcher.DispatchAsync(Invocation("serverpassword", ["set", "example"], privileged));
        Assert.That(password.Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(password.Messages!.Single().Arguments, Is.Empty);
        Assert.That(control.Password, Is.EqualTo("example"));
        Assert.That((await dispatcher.DispatchAsync(Invocation("serverpassword", ["clear"], privileged))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(control.Password, Is.Empty);
        Assert.That((await dispatcher.DispatchAsync(Invocation("exit-nosave", ["test"], privileged))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(control.ShutdownWithoutSave, Is.True);
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
        public string? Password { get; private set; }
        public bool ShutdownWithoutSave { get; private set; }

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

        public ValueTask ShutdownWithoutSaveAsync(string reason, CancellationToken cancellationToken = default)
        {
            ShutdownWithoutSave = true;
            ShutdownReason = reason;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetPasswordAsync(string password, CancellationToken cancellationToken = default)
        {
            Password = password;
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
