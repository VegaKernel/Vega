using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;
using Vega.Core.Commands;
using Vega.Core.Modules;
using Vega.Runtime;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class RuntimeModuleGenerationRouterTests
{
    [Test]
    public async Task ExistingCommandProxyDispatchesOnlyToTheActiveGeneration()
    {
        int generation = 0;
        var router = new RuntimeModuleGenerationRouter();
        RestartableModuleGraph Factory(IReadOnlySet<string>? _)
        {
            int current = ++generation;
            var handler = new GenerationCommand(current);
            var state = new RuntimeModuleGeneration(null, null, null, null,
                new Dictionary<string, ICommandHandler>(StringComparer.OrdinalIgnoreCase)
                {
                    [handler.Descriptor.Name] = handler
                });
            return new RestartableModuleGraph(new EmptyHost(), ["module"], () => router.Activate(state));
        }

        using var coordinator = new SoftRestartCoordinator(
            new SoftRestartOptions(true, 2, 2, 10), Factory, new NullLogger());
        router.Attach(coordinator);
        var dispatcher = new CommandDispatcher([]);
        using IDisposable registration = router.RegisterCommands(dispatcher);

        Assert.That((await Dispatch(dispatcher)).Messages![0].DefaultText, Is.EqualTo("generation:1"));
        Assert.That((await coordinator.RestartAsync(null)).Status, Is.EqualTo(SoftRestartStatus.Succeeded));
        Assert.That((await Dispatch(dispatcher)).Messages![0].DefaultText, Is.EqualTo("generation:2"));
    }

    private static ValueTask<CommandExecutionResult> Dispatch(CommandDispatcher dispatcher) =>
        dispatcher.DispatchAsync(new CommandInvocation("probe", [], new ServerPrincipal(), new NullOutput()));

    private sealed class GenerationCommand(int generation) : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new("probe", [], []);
        public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            CommandExecutionResult.WithMessages(
                Vega.Contracts.Messaging.MessageDescriptor.Info("probe")
                    .WithDefaultText($"generation:{generation}")));
    }

    private sealed class EmptyHost : IVegaModuleHost
    {
        public void Start() { }
        public IReadOnlyList<ModuleOperationsSnapshot> SnapshotModules() => [];
        public void Dispose() { }
    }

    private sealed class ServerPrincipal : ICommandPrincipal
    {
        public string Name => "server";
        public bool IsServer => true;
        public bool HasPermission(string permission) => true;
    }

    private sealed class NullOutput : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class NullLogger : IOperationsLogger
    {
        public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null,
            string? correlationId = null) { }
    }
}
