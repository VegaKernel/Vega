using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;

namespace Vega.Core.Commands.System;

public sealed class TickRateCommand(IServerRuntimeStateReader serverState, Action? reset = null, Func<bool>? toggleAlerts = null) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "tps",
        ["tpsstats", "tpsalerts", "tpsreset", "tpsmetrics"],
        ["tickrate.check", "tickrate.admin"],
        Syntax: "tps | tpsstats | tpsalerts | tpsreset | tpsmetrics");

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
                    .WithDefaultText("Usage: tps | tpsstats | tpsalerts | tpsreset | tpsmetrics.")]));
        }

        string name = invocation.Name.ToLowerInvariant();
        if (name != "tps" && !invocation.Principal.IsServer && !invocation.Principal.HasPermission("tickrate.admin"))
            return ValueTask.FromResult(new CommandExecutionResult(CommandExecutionStatus.Forbidden, "command.tps.admin"));
        if (name == "tpsreset" && reset is not null)
        {
            reset();
            return ValueTask.FromResult(CommandExecutionResult.WithMessages(MessageDescriptor.Success("command.tps.reset").WithDefaultText("TPS statistics have been reset.")));
        }
        if (name == "tpsalerts" && toggleAlerts is not null)
            return ValueTask.FromResult(CommandExecutionResult.WithMessages(MessageDescriptor.Success("command.tps.alerts", toggleAlerts() ? "enabled" : "disabled").WithDefaultText("TPS alerts are now {0}.")));

        TickRateStatisticsSnapshot tickRate = serverState.Read().TickRate;
        if (tickRate.SampleCount == 0)
        {
            return ValueTask.FromResult(CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.tps.waiting")
                    .WithDefaultText("Tick-rate statistics are waiting for the first complete sample.")));
        }

        if (name == "tpsstats")
            return ValueTask.FromResult(CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.tps.stats.header").WithDefaultText("=== TPS Statistics ==="),
                MessageDescriptor.Info("command.tps.stats.current", tickRate.Current).WithDefaultText("Current: {0:F2}"),
                MessageDescriptor.Info("command.tps.stats.min", tickRate.Minimum, FormatTimestamp(tickRate.MinimumAt)).WithDefaultText("Min: {0:F2} at {1}"),
                MessageDescriptor.Info("command.tps.stats.max", tickRate.Maximum, FormatTimestamp(tickRate.MaximumAt)).WithDefaultText("Max: {0:F2} at {1}"),
                MessageDescriptor.Info("command.tps.stats.avg", tickRate.AverageOneMinute).WithDefaultText("Avg (1 min): {0:F2}")));

        if (name == "tps")
            return ValueTask.FromResult(CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.tps.current", tickRate.Current).WithDefaultText("Current TPS: {0:F2}"),
                MessageDescriptor.Success("command.tps.average", tickRate.AverageOneMinute).WithDefaultText("Average TPS (1 min): {0:F2}")));

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
