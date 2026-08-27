using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Runtime;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Core.Commands.Administration;
using Vega.Core.Commands.System;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class ServerControlCommandTests
{
    [Test]
    public async Task SaveOffAndOffNoSaveUseOneAuthorizedServerControlBoundary()
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
        CommandExecutionResult stoppedWithoutSave = await dispatcher.DispatchAsync(
            Invocation("off-nosave", ["Emergency", "stop"], operatorPrincipal));

        Assert.Multiple(() =>
        {
            Assert.That(deniedSave.Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
            Assert.That(saved.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(stopped.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(stoppedWithoutSave.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(control.SaveCount, Is.EqualTo(1));
            Assert.That(control.Shutdowns, Has.Count.EqualTo(2));
            Assert.That(control.Shutdowns[0], Is.EqualTo(("Planned maintenance", true)));
            Assert.That(control.Shutdowns[1], Is.EqualTo(("Emergency stop", false)));
        });
    }

    [Test]
    public async Task ServerPasswordRequiresDedicatedPermissionAndNeverEchoesSecret()
    {
        const string secret = "super-secret-password";
        var control = new RecordingServerControl();
        var dispatcher = new CommandDispatcher([new ServerPasswordCommand(control)]);
        var denied = new Principal("player", []);
        var operatorPrincipal = new Principal("operator", ["vega.cfg.password"]);

        CommandExecutionResult deniedResult = await dispatcher.DispatchAsync(
            Invocation("serverpassword", [secret], denied));
        CommandExecutionResult invalidResult = await dispatcher.DispatchAsync(
            Invocation("serverpassword", ["one", "two"], operatorPrincipal));
        CommandExecutionResult changed = await dispatcher.DispatchAsync(
            Invocation("serverpassword", [secret], operatorPrincipal));

        Assert.Multiple(() =>
        {
            Assert.That(deniedResult.Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
            Assert.That(invalidResult.Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
            Assert.That(changed.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(control.Password, Is.EqualTo(secret));
            Assert.That(changed.Messages, Has.Count.EqualTo(1));
            Assert.That(changed.Messages[0].DefaultText, Does.Not.Contain(secret));
            Assert.That(changed.Messages[0].Arguments.Select(static value => value?.ToString() ?? string.Empty),
                Has.None.Contains(secret));
        });
    }

    [Test]
    public async Task VersionAliasIsRegisteredAndDoesNotReadHostMetrics()
    {
        var dispatcher = new CommandDispatcher([
            new ServerInfoCommand(null!)
        ]);
        var principal = new Principal("operator", ["vega.info"]);

        CommandExecutionResult result = await dispatcher.DispatchAsync(
            Invocation("version", [], principal));

        Assert.Multiple(() =>
        {
            Assert.That(dispatcher.CanDispatch("version"), Is.True);
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(result.Messages, Has.Count.EqualTo(1));
            Assert.That(result.Messages[0].Key, Is.EqualTo("command.version.current"));
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
        public List<(string Reason, bool SaveWorld)> Shutdowns { get; } = [];
        public string? Password { get; private set; }

        public ValueTask SaveAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask ShutdownAsync(string reason, CancellationToken cancellationToken = default)
        {
            Shutdowns.Add((reason, true));
            return ValueTask.CompletedTask;
        }

        public ValueTask ShutdownWithoutSaveAsync(string reason, CancellationToken cancellationToken = default)
        {
            Shutdowns.Add((reason, false));
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
