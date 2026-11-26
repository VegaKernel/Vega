using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;

namespace Vega.Core.Commands.System;

/// <summary>Bounded recent process logs; no persistent subscription or exception stack data.</summary>
public sealed class DisplayLogsCommand(IOperationsLogStore logs) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("displaylogs", [], ["vega.operations.logs"], Syntax: "displaylogs [count:1..20] [information|warning|error|critical]");
    public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        int count = 10;
        OperationsLogLevel level = OperationsLogLevel.Information;
        var args = invocation.Arguments;
        if (args.Count > 2 || args.Count > 0 && (!int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out count) || count is < 1 or > 20) ||
            args.Count == 2 && (!Enum.TryParse(args[1], true, out level) || level is < OperationsLogLevel.Information or > OperationsLogLevel.Critical))
            return ValueTask.FromResult(new CommandExecutionResult(CommandExecutionStatus.InvalidUsage, "displaylogs.usage"));
        token.ThrowIfCancellationRequested();
        var entries = logs.Query(new(MinimumLevel: level, Limit: count));
        var messages = entries.Select(entry => MessageDescriptor.Info("displaylogs.entry", entry.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            entry.Level, entry.Source, SingleLine(entry.Text)).WithDefaultText("[{0} {1}] {2}: {3}")).ToArray();
        return ValueTask.FromResult(CommandExecutionResult.WithMessages(messages.Length == 0
            ? [MessageDescriptor.Info("displaylogs.empty").WithDefaultText("No matching log entries.")] : messages));
    }

    private static string SingleLine(string text)
    {
        string line = text.ReplaceLineEndings(" ");
        return line.Length <= 350 ? line : line[..350] + "…";
    }
}
