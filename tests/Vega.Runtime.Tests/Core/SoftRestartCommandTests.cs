using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Modules;
using Vega.Core.Commands.Administration;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class SoftRestartCommandTests
{
    [Test]
    public async Task RequiresConfirmationForAllModulesAndAcceptsModulesRestartAlias()
    {
        var service = new RecordingRestartService();
        var command = new SoftRestartCommand(service);

        CommandExecutionResult missingConfirmation = await command.ExecuteAsync(
            Invocation("softrestart", []), CancellationToken.None);
        CommandExecutionResult alias = await command.ExecuteAsync(
            Invocation("modules", ["restart", "Pvp", "Mentions"]), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(missingConfirmation.Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
            Assert.That(alias.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(service.Modules, Is.EquivalentTo(new[] { "Pvp", "Mentions" }));
        });
    }

    private static CommandInvocation Invocation(string name, IReadOnlyList<string> arguments) =>
        new(name, arguments, Principal.Instance, Output.Instance);

    private sealed class RecordingRestartService : ISoftRestartService
    {
        public IReadOnlySet<string>? Modules { get; private set; }
        public SoftRestartSnapshot Read() => new(true, false, 1, null, null, null, null);

        public ValueTask<SoftRestartResult> RestartAsync(
            IReadOnlySet<string>? modules,
            CancellationToken cancellationToken = default)
        {
            Modules = modules;
            return ValueTask.FromResult(new SoftRestartResult(
                SoftRestartStatus.Succeeded, 1, 2, TimeSpan.Zero, "restarted", modules?.ToArray() ?? []));
        }
    }

    private sealed class Principal : ICommandPrincipal
    {
        public static Principal Instance { get; } = new();
        public string Name => "server";
        public bool IsServer => true;
        public bool HasPermission(string permission) => true;
    }

    private sealed class Output : ICommandOutput
    {
        public static Output Instance { get; } = new();
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
