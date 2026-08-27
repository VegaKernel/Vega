using System.Globalization;
using Vega.Admin.Client;
using Vega.Admin.Contracts;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;

namespace Vega.TerminalUI;

/// <summary>
/// Transitional local IVegaAdminClient for the embedded Operations dashboard. It keeps the
/// existing server-local dashboard working while its individual projections migrate to Admin
/// Contracts. Remote clients must not use this implementation.
/// </summary>
internal sealed class EmbeddedOperationsAdminClient(
    IOperationsSnapshotProvider snapshots,
    IOperationsCommandService commands,
    ICommandCatalog? commandCatalog) : IVegaAdminClient
{
    public ValueTask<AdminProtocolInfo> GetProtocolInfoAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(AdminProtocolInfo.Phase27Initial);
    }

    public ValueTask<AdminServerStatusSnapshot> GetServerStatusAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ServerDashboardSnapshot server = snapshots.Capture().Server;
        return ValueTask.FromResult(new AdminServerStatusSnapshot(
            server.ServerName,
            server.Version,
            server.LifecycleStage.ToString(),
            server.Uptime,
            server.WorldName,
            server.IsWorldReady,
            server.IsNetworkReady,
            server.OnlinePlayers,
            server.MaximumPlayers,
            server.TickRate.Current,
            server.TickRate.AverageOneMinute,
            DateTimeOffset.UtcNow));
    }

    public ValueTask<IReadOnlyList<AdminCommandDescriptor>> GetCommandCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<AdminCommandDescriptor> catalog = commandCatalog is null
            ? Array.Empty<AdminCommandDescriptor>()
            : commandCatalog.SnapshotCommands()
                .Select(static descriptor => new AdminCommandDescriptor(
                    descriptor.Name,
                    descriptor.Aliases.ToArray(),
                    descriptor.Syntax,
                    descriptor.Permissions.ToArray(),
                    descriptor.AllowServer,
                    Owner: "unassigned"))
                .OrderBy(static descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        return ValueTask.FromResult(catalog);
    }

    public async ValueTask<AdminCommandResult> ExecuteCommandAsync(
        AdminCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        OperationsActionResult result = await commands
            .ExecuteAsync(request.CommandText, cancellationToken)
            .ConfigureAwait(false);
        return new AdminCommandResult(
            result.Status switch
            {
                CommandExecutionStatus.Success => AdminCommandExecutionStatus.Success,
                CommandExecutionStatus.InvalidUsage => AdminCommandExecutionStatus.InvalidUsage,
                CommandExecutionStatus.Forbidden => AdminCommandExecutionStatus.Forbidden,
                _ => AdminCommandExecutionStatus.Failed
            },
            result.ErrorCode,
            result.Messages.Select(static message => new AdminMessageDescriptor(
                Key: null,
                Arguments: Array.Empty<object?>(),
                AdminMessageSeverity.Info,
                FallbackText: message)).ToArray());
    }
}

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
