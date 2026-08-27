using System.Globalization;
using System.Text;
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
                    descriptor.Owner,
                    descriptor.TitleKey,
                    descriptor.DescriptionKey,
                    descriptor.HelpKey,
                    descriptor.FallbackTitle,
                    descriptor.FallbackDescription,
                    descriptor.FallbackHelp)
                {
                    AllowPlayers = descriptor.AllowPlayers
                })
                .OrderBy(static descriptor => descriptor.Owner, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        return ValueTask.FromResult(catalog);
    }

    public async ValueTask<AdminCommandResult> ExecuteCommandAsync(
        AdminCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        OperationsActionResult result = await commands
            .ExecuteAsync(RenderCommandText(request), cancellationToken)
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
                Arguments: Array.Empty<AdminMessageArgument>(),
                Severity: AdminMessageSeverity.Info,
                FallbackText: message)).ToArray());
    }

    private static string RenderCommandText(AdminCommandRequest request)
    {
        var builder = new StringBuilder();
        if (request.Silent)
        {
            builder.Append('.');
        }

        builder.Append(request.Name);
        foreach (string argument in request.Arguments)
        {
            builder.Append(' ');
            AppendArgument(builder, argument);
        }

        return builder.ToString();
    }

    private static void AppendArgument(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && !argument.Any(static character => char.IsWhiteSpace(character) || character is '"' or '\\'))
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        foreach (char character in argument)
        {
            if (character is '"' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        builder.Append('"');
    }
}

/// <summary>
/// Transitional adapter for the embedded legacy Operations dashboard. Command execution is
/// already routed through IVegaAdminClient while the remaining dashboard projections migrate
/// away from IOperationsSnapshotProvider in later Phase 27 slices.
/// </summary>
internal sealed class AdminOperationsCommandServiceAdapter(IVegaAdminClient adminClient) : IOperationsCommandService
{
    public async ValueTask<OperationsActionResult> ExecuteAsync(
        string commandText,
        CancellationToken cancellationToken = default)
    {
        if (!CommandTextParser.TryParse(commandText, out ParsedCommandText parsed))
        {
            return new OperationsActionResult(
                CommandExecutionStatus.InvalidUsage,
                "command.empty",
                ["Enter a command."]);
        }

        AdminCommandResult result = await adminClient
            .ExecuteCommandAsync(
                new AdminCommandRequest(parsed.Name, parsed.Arguments, parsed.Silent),
                cancellationToken)
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

        object?[] arguments = message.Arguments.Select(RenderArgument).ToArray();
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, arguments);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private static object? RenderArgument(AdminMessageArgument argument) => argument.Kind switch
    {
        AdminScalarKind.Null => null,
        AdminScalarKind.Integer when long.TryParse(
            argument.Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out long integer) => integer,
        AdminScalarKind.Number when double.TryParse(
            argument.Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double number) => number,
        AdminScalarKind.Boolean when bool.TryParse(argument.Value, out bool flag) => flag,
        AdminScalarKind.DateTimeOffset when DateTimeOffset.TryParse(
            argument.Value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset timestamp) => timestamp,
        AdminScalarKind.Duration when TimeSpan.TryParse(
            argument.Value,
            CultureInfo.InvariantCulture,
            out TimeSpan duration) => duration,
        _ => argument.Value
    };
}

/// <summary>
/// Local embedded-dashboard compatibility view of the Phase 27 command catalog. The future
/// Vega.Admin.Tui consumes AdminCommandDescriptor directly and will not require this adapter.
/// </summary>
internal sealed class AdminCommandCatalogAdapter(IVegaAdminClient adminClient) : ICommandCatalog
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
                descriptor.Syntax)
            {
                Owner = descriptor.Owner,
                AllowPlayers = descriptor.AllowPlayers,
                TitleKey = descriptor.TitleKey,
                DescriptionKey = descriptor.DescriptionKey,
                HelpKey = descriptor.HelpKey,
                FallbackTitle = descriptor.FallbackTitle,
                FallbackDescription = descriptor.FallbackDescription,
                FallbackHelp = descriptor.FallbackHelp
            })
            .ToArray();
    }
}
