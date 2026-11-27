using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Core.Commands.System;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class TickRateCommandTests
{
    [Test]
    public async Task TpsAdministrativeAliasesResetAndToggleTheExistingMonitor()
    {
        int resets = 0;
        bool alerts = false;
        var command = new TickRateCommand(new FixedState(TickRateStatisticsSnapshot.Empty), () => resets++, () => alerts = !alerts);
        Assert.That((await command.ExecuteAsync(Invocation() with { Name = "tpsreset" }, default)).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await command.ExecuteAsync(Invocation() with { Name = "tpsalerts" }, default)).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(resets, Is.EqualTo(1));
        Assert.That(alerts, Is.True);
    }
    [Test]
    public async Task CommandReportsRollingRatesExtremesAndTickPercentiles()
    {
        DateTimeOffset now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        var command = new TickRateCommand(new FixedState(new TickRateStatisticsSnapshot(
            59.5,
            58.25,
            42,
            now.AddMinutes(-1),
            60,
            now,
            60,
            16.2,
            18.4,
            24.8)));

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation() with { Name = "tpsmetrics" },
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(result.Messages, Has.Count.EqualTo(2));
            Assert.That(result.Messages![0].DefaultText, Does.Contain("average 1m"));
            Assert.That(result.Messages[0].Arguments, Does.Contain(58.25));
            Assert.That(result.Messages[1].Arguments, Does.Contain(24.8));
            Assert.That(command.Descriptor.Aliases, Does.Contain("tpsstats"));
            Assert.That(command.Descriptor.Permissions, Does.Contain("tickrate.check"));
        });
    }

    [Test]
    public async Task CommandReportsWarmupBeforeFirstSample()
    {
        var command = new TickRateCommand(new FixedState(TickRateStatisticsSnapshot.Empty));

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(),
            CancellationToken.None);

        Assert.That(result.Messages!.Single().Key, Is.EqualTo("command.tps.waiting"));
    }

    private static CommandInvocation Invocation() => new(
        "tps",
        Array.Empty<string>(),
        ServerPrincipal.Instance,
        NullOutput.Instance);

    private sealed class ServerPrincipal : ICommandPrincipal
    {
        public static ServerPrincipal Instance { get; } = new();

        public string Name => "server";

        public bool IsServer => true;

        public bool HasPermission(string permission) => true;
    }

    private sealed class NullOutput : ICommandOutput
    {
        public static NullOutput Instance { get; } = new();

        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class FixedState(TickRateStatisticsSnapshot tickRate) : IServerRuntimeStateReader
    {
        public ServerRuntimeStateSnapshot Read() => new(
            "World",
            16,
            7777,
            true,
            tickRate.Current,
            DateTimeOffset.UtcNow)
        {
            TickRate = tickRate
        };
    }
}
