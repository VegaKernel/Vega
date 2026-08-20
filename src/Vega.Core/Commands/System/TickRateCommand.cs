using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;

namespace Vega.Core.Commands.System;

public sealed class TickRateCommand(IServerRuntimeStateReader serverState) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "tps",
        ["tpsstats"],
        ["tickrate.check"]);

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 0)
        {
            return ValueTask.FromResult(new CommandExecutionResult(
                CommandExecutionStatus.InvalidUsage,
                "command.tps.usage",
                [MessageDescriptor.Error("command.tps.usage")
                    .WithDefaultText("Usage: tps. Alias: tpsstats.")]));
        }

        TickRateStatisticsSnapshot tickRate = serverState.Read().TickRate;
        if (tickRate.SampleCount == 0)
        {
            return ValueTask.FromResult(CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.tps.waiting")
                    .WithDefaultText("Tick-rate statistics are waiting for the first complete sample.")));
        }

        return ValueTask.FromResult(CommandExecutionResult.WithMessages(
            MessageDescriptor.Info(
                    "command.tps.rate",
                    tickRate.Current,
                    tickRate.AverageOneMinute,
                    tickRate.Minimum,
                    FormatTimestamp(tickRate.MinimumAt),
                    tickRate.Maximum,
                    FormatTimestamp(tickRate.MaximumAt))
                .WithDefaultText("TPS current {0:F2}, average 1m {1:F2}, min {2:F2} at {3}, max {4:F2} at {5}."),
            MessageDescriptor.Info(
                    "command.tps.duration",
                    tickRate.TickDurationP50Milliseconds,
                    tickRate.TickDurationP95Milliseconds,
                    tickRate.TickDurationP99Milliseconds)
                .WithDefaultText("Tick interval: p50 {0:F2} ms, p95 {1:F2} ms, p99 {2:F2} ms.")));
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp?.UtcDateTime.ToString("HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "n/a";
}
