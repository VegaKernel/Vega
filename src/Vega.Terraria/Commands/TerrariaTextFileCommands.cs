using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;

namespace Vega.Terraria.Commands;

internal sealed class ServerTextFileCommand(
    string name,
    string fileName,
    string messageCode) : ICommandHandler
{
    private const int MaximumLineLength = 4_096;

    public CommandDescriptor Descriptor { get; } = new(
        name,
        [],
        [],
        Syntax: name);

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 0)
        {
            return new CommandExecutionResult(
                CommandExecutionStatus.InvalidUsage,
                $"command.{name}.usage",
                [MessageDescriptor.Error($"command.{name}.usage")
                    .WithDefaultText($"Usage: {name}.")]);
        }

        string path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path))
        {
            return CommandExecutionResult.WithMessages(
                MessageDescriptor.Info($"{messageCode}.missing", fileName)
                    .WithDefaultText("No {0} file is configured."));
        }

        try
        {
            string[] lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
            if (lines.Length == 0)
            {
                return CommandExecutionResult.WithMessages(
                    MessageDescriptor.Info($"{messageCode}.empty")
                        .WithDefaultText("No text is configured."));
            }

            MessageDescriptor[] messages = lines
                .Select(static line => line.Length <= MaximumLineLength ? line : line[..MaximumLineLength])
                .Select(line => MessageDescriptor.Info(messageCode, line).WithDefaultText("{0}"))
                .ToArray();
            return CommandExecutionResult.WithMessages(messages);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CommandExecutionResult.Failed(
                $"command.{name}.read-failed",
                MessageDescriptor.Error($"command.{name}.read-failed")
                    .WithDefaultText($"Could not read {fileName}."));
        }
    }
}
