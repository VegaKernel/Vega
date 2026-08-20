using NUnit.Framework;
using TShockNG.Contracts.Commands;
using TShockNG.Contracts.Messaging;
using TShockNG.Contracts.Operations;
using TShockNG.Contracts.Updates;
using TShockNG.Core.Commands.System;

namespace TShockLauncher.Tests.Core;

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
    public async Task CheckUpdatesReturnsSemanticMessageInsteadOfWritingLegacyOutput()
    {
        var command = new CheckUpdatesCommand(new SuccessfulUpdateChecker());
        var invocation = CreateInvocation();

        CommandExecutionResult result = await command.ExecuteAsync(invocation, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(result.Messages, Has.Count.EqualTo(1));
        Assert.That(result.Messages![0].Key, Is.EqualTo("command.checkupdates.queued"));
        Assert.That(result.Messages[0].Severity, Is.EqualTo(MessageSeverity.Info));
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
