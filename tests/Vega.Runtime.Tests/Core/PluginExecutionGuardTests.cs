using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Core.Modules;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class PluginExecutionGuardTests
{
    [Test]
    public void RepeatedFaultsOpenCircuitAndSuppressLaterCallbacks()
    {
        var logger = new RecordingLogger();
        var guard = new PluginExecutionGuard(
            logger,
            "Vega.Test.Faulty",
            failureThreshold: 3,
            failureWindow: TimeSpan.FromMinutes(1));
        var executedAfterOpen = 0;

        for (var index = 0; index < 3; index++)
        {
            Assert.That(
                guard.TryInvoke("tick", () => throw new InvalidOperationException($"boom-{index}")),
                Is.False);
        }

        bool accepted = guard.TryInvoke("tick", () => executedAfterOpen++);
        PluginExecutionGuardSnapshot snapshot = guard.Snapshot();

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.False);
            Assert.That(executedAfterOpen, Is.Zero);
            Assert.That(snapshot.CircuitOpen, Is.True);
            Assert.That(snapshot.FaultCount, Is.EqualTo(3));
            Assert.That(snapshot.LastOperation, Is.EqualTo("tick"));
            Assert.That(snapshot.LastFailure, Does.Contain("boom-2"));
            Assert.That(logger.Entries.Count(entry => entry.Level == OperationsLogLevel.Error), Is.EqualTo(3));
            Assert.That(logger.Entries.Count(entry => entry.Level == OperationsLogLevel.Critical), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task OpenCircuitRejectsCommandsWithoutCallingPluginAgain()
    {
        var logger = new RecordingLogger();
        var guard = new PluginExecutionGuard(
            logger,
            "Vega.Test.CommandBreaker",
            failureThreshold: 1,
            failureWindow: TimeSpan.FromMinutes(1));
        var handler = new CountingThrowingHandler();
        var invocation = new CommandInvocation(
            "boom",
            [],
            Principal.Instance,
            Output.Instance);

        CommandExecutionResult first = await guard.InvokeCommandAsync(
            "command:boom",
            handler,
            invocation,
            CancellationToken.None);
        CommandExecutionResult second = await guard.InvokeCommandAsync(
            "command:boom",
            handler,
            invocation,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.EqualTo(CommandExecutionStatus.Failed));
            Assert.That(first.ErrorCode, Is.EqualTo("plugin.callback_failed"));
            Assert.That(second.Status, Is.EqualTo(CommandExecutionStatus.Failed));
            Assert.That(second.ErrorCode, Is.EqualTo("plugin.circuit_open"));
            Assert.That(handler.ExecutionCount, Is.EqualTo(1));
            Assert.That(guard.CircuitOpen, Is.True);
        });
    }

    [Test]
    public void FaultsOutsideWindowDoNotOpenCircuit()
    {
        var logger = new RecordingLogger();
        var guard = new PluginExecutionGuard(
            logger,
            "Vega.Test.Window",
            failureThreshold: 2,
            failureWindow: TimeSpan.FromMilliseconds(20));

        Assert.That(guard.TryInvoke("tick", Throw), Is.False);
        Thread.Sleep(50);
        Assert.That(guard.TryInvoke("tick", Throw), Is.False);

        Assert.That(guard.CircuitOpen, Is.False);
    }

    private static void Throw() => throw new InvalidOperationException("window boom");

    private sealed class CountingThrowingHandler : ICommandHandler
    {
        public int ExecutionCount { get; private set; }

        public CommandDescriptor Descriptor { get; } = new("boom", [], []);

        public ValueTask<CommandExecutionResult> ExecuteAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            throw new InvalidOperationException("command boom");
        }
    }

    private sealed class RecordingLogger : IOperationsLogger
    {
        private readonly List<Entry> entries = [];
        public IReadOnlyList<Entry> Entries => entries;

        public void Log(
            OperationsLogLevel level,
            string source,
            string text,
            Exception? exception = null,
            string? correlationId = null) =>
            entries.Add(new Entry(level, source, text, exception));
    }

    private sealed record Entry(
        OperationsLogLevel Level,
        string Source,
        string Text,
        Exception? Exception);

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
