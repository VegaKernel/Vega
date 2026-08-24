using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;
using Vega.Contracts.Updates;
using Vega.Core.Commands.System;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class StructuredCommandOutputTests
{
    [Test]
    public async Task ServerInfoReturnsMessageDescriptorsWithoutWritingLegacyOutput()
    {
        var metrics = new HostMetricsSnapshot(
            10,
            20,
            TimeSpan.FromSeconds(3),
            "TestOS",
            8,
            "test-host");
        var command = new ServerInfoCommand(new StubMetricsReader(metrics));
        var invocation = CreateInvocation();

        CommandExecutionResult result = await command.ExecuteAsync(invocation, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(result.Messages, Has.Count.EqualTo(6));
        Assert.That(result.Messages![0].Key, Is.EqualTo("command.serverinfo.memory"));
        Assert.That(result.Messages[0].Arguments, Is.EqualTo(new object?[] { 10L }));
        Assert.That(result.Messages[0].DefaultText, Is.EqualTo("Working set: {0:N0} bytes"));
    }

    [Test]
    public async Task CheckUpdatesReturnsCompletedMessageInsteadOfWritingLegacyOutput()
    {
        var command = new CheckUpdatesCommand(new SuccessfulUpdateChecker());
        var invocation = CreateInvocation();

        CommandExecutionResult result = await command.ExecuteAsync(invocation, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(result.Messages, Has.Count.EqualTo(1));
        Assert.That(result.Messages![0].Key, Is.EqualTo("command.checkupdates.completed"));
        Assert.That(result.Messages[0].Severity, Is.EqualTo(MessageSeverity.Info));
        Assert.That(result.Messages[0].DefaultText, Does.StartWith("Update check completed."));
    }

    [Test]
    public void CheckUpdatesRequiresMaintenancePermission()
    {
        var command = new CheckUpdatesCommand(new SuccessfulUpdateChecker());

        Assert.That(command.Descriptor.Permissions, Is.EqualTo(new[] { "vega.cfg.maintenance" }));
    }

    [Test]
    public async Task CheckUpdatesReturnsStructuredFailure()
    {
        var command = new CheckUpdatesCommand(new FailingUpdateChecker());

        CommandExecutionResult result = await command.ExecuteAsync(CreateInvocation(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Failed));
            Assert.That(result.ErrorCode, Is.EqualTo("update.check_failed"));
            Assert.That(result.Messages![0].Key, Is.EqualTo("command.checkupdates.failed"));
            Assert.That(result.Messages[0].DefaultText, Is.EqualTo("Unable to check for updates."));
        });
    }

    [Test]
    public void CheckUpdatesPropagatesCallerCancellation()
    {
        var command = new CheckUpdatesCommand(new CancelledUpdateChecker());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await command.ExecuteAsync(CreateInvocation(), cancellation.Token));
    }

    private static CommandInvocation CreateInvocation() =>
        new("test", [], new StubPrincipal(), new ThrowingLegacyOutput());

    private sealed class StubMetricsReader(HostMetricsSnapshot snapshot) : IHostMetricsReader
    {
        public HostMetricsSnapshot Read() => snapshot;
    }

    private sealed class SuccessfulUpdateChecker : IUpdateChecker
    {
        public Task CheckAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FailingUpdateChecker : IUpdateChecker
    {
        public Task CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("Unavailable"));
    }

    private sealed class CancelledUpdateChecker : IUpdateChecker
    {
        public Task CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromCanceled(cancellationToken);
    }

    private sealed class StubPrincipal : ICommandPrincipal
    {
        public string Name => "test";
        public bool IsServer => false;
        public bool HasPermission(string permission) => true;
    }

    private sealed class ThrowingLegacyOutput : ICommandOutput
    {
        private static ValueTask Unexpected() => throw new AssertionException("Modern Core command wrote directly to legacy output.");

        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => Unexpected();
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => Unexpected();
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => Unexpected();
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => Unexpected();
    }
}
