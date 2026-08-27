using System.Globalization;
using Vega.Admin.Client;
using Vega.Admin.Contracts;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;

namespace Vega.TerminalUI;

/// <summary>
/// Transitional adapter for the embedded legacy Operations dashboard. Command execution is
/// already routed through IVegaAdminClient while the remaining dashboard projections migrate
/// away from IOperationsSnapshotProvider in later Phase 27 slices.
/// </summary>
public sealed class AdminOperationsCommandServiceAdapter(IVegaAdminClient adminClient) : IOperationsCommandService
{
    public async ValueTask<OperationsActionResult> ExecuteAsync(
        string commandText,
        CancellationToken cancellationToken = default)
    {
        AdminCommandResult result = await adminClient
            .ExecuteCommandAsync(new AdminCommandRequest(commandText), cancellationToken)
            .ConfigureAwait(false);
        return new OperationsActionResult(
            result.Status switch
            {
                AdminCommandExecutionStatus.Success => CommandExecutionStatus.Success,
                AdminCommandExecutionStatus.InvalidUsage => CommandExecutionStatus.InvalidUsage,
                AdminCommandExecutionStatus.Forbidden => CommandExecutionStatus.Forbidden,
                _ => CommandExecutionStatus.Failed
            },
            result.ErrorCode,
            result.Messages.Select(Render).ToArray());
    }

    private static string Render(AdminMessageDescriptor message)
    {
        string template = message.FallbackText ?? message.Key ?? message.Severity.ToString();
        if (message.Arguments.Count == 0)
        {
            return template;
        }

        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, message.Arguments.ToArray());
        }
        catch (FormatException)
        {
            return template;
        }
    }
}

/// <summary>
/// Local embedded-dashboard compatibility view of the Phase 27 command catalog. The future
/// Vega.Admin.Tui consumes AdminCommandDescriptor directly and will not require this adapter.
/// </summary>
public sealed class AdminCommandCatalogAdapter(IVegaAdminClient adminClient) : ICommandCatalog
{
    public IReadOnlyList<CommandDescriptor> SnapshotCommands()
    {
        IReadOnlyList<AdminCommandDescriptor> commands = adminClient
            .GetCommandCatalogAsync()
            .AsTask()
            .GetAwaiter()
            .GetResult();
        return commands
            .Select(static descriptor => new CommandDescriptor(
                descriptor.Name,
                descriptor.Aliases.ToArray(),
                descriptor.Permissions.ToArray(),
                descriptor.AllowServer,
                descriptor.Syntax))
            .ToArray();
    }
}
