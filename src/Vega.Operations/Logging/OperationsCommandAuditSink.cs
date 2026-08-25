using System.Text.Json;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;

namespace Vega.Operations.Logging;

public sealed class OperationsCommandAuditSink(IOperationsLogger logger) : ICommandAuditSink
{
    private readonly IOperationsLogger logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public void Record(CommandAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        OperationsLogLevel level = record.Status is CommandExecutionStatus.Forbidden or CommandExecutionStatus.Failed
            ? OperationsLogLevel.Warning
            : OperationsLogLevel.Information;
        string payload = JsonSerializer.Serialize(new
        {
            command = record.CommandName,
            arguments = record.Arguments,
            principal = record.PrincipalName,
            principalType = record.PrincipalIsServer ? "server" : "player",
            status = record.Status.ToString(),
            errorCode = record.ErrorCode
        });

        logger.Log(level, "command-audit", payload);
    }
}
