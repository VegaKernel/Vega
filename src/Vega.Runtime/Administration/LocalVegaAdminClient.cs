using System.Globalization;
using Vega.Admin.Client;
using Vega.Admin.Contracts;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;

namespace Vega.Runtime.Administration;

/// <summary>
/// In-process administration client for the embedded TUI. It intentionally uses the same
/// transport-neutral DTOs that a future remote client will expose instead of leaking runtime
/// implementation objects into UI command and status paths.
/// </summary>
internal sealed class LocalVegaAdminClient(
    IOperationsSnapshotProvider snapshots,
    ICommandDispatcher dispatcher,
    ICommandCatalog commandCatalog) : IVegaAdminClient
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
        IReadOnlyList<AdminCommandDescriptor> commands = commandCatalog
            .SnapshotCommands()
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
        return ValueTask.FromResult(commands);
    }

    public async ValueTask<AdminCommandResult> ExecuteCommandAsync(
        AdminCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return new AdminCommandResult(
                AdminCommandExecutionStatus.InvalidUsage,
                "command.empty",
                [new AdminMessageDescriptor(
                    "command.empty",
                    Array.Empty<AdminMessageArgument>(),
                    AdminMessageSeverity.Error,
                    "Enter a command.")]);
        }

        var output = new BufferedAdminCommandOutput();
        var invocation = new CommandInvocation(
            request.Name.Trim(),
            request.Arguments.ToArray(),
            LocalAdminPrincipal.Instance,
            output,
            request.Silent);
        CommandExecutionResult result = await dispatcher
            .DispatchAsync(invocation, cancellationToken)
            .ConfigureAwait(false);

        List<AdminMessageDescriptor> messages = [.. output.Messages];
        if (result.Messages is not null)
        {
            messages.AddRange(result.Messages.Select(MapMessage));
        }

        return new AdminCommandResult(
            MapStatus(result.Status),
            result.ErrorCode,
            messages.AsReadOnly());
    }

    private static AdminCommandExecutionStatus MapStatus(CommandExecutionStatus status) => status switch
    {
        CommandExecutionStatus.Success => AdminCommandExecutionStatus.Success,
        CommandExecutionStatus.InvalidUsage => AdminCommandExecutionStatus.InvalidUsage,
        CommandExecutionStatus.Forbidden => AdminCommandExecutionStatus.Forbidden,
        _ => AdminCommandExecutionStatus.Failed
    };

    private static AdminMessageDescriptor MapMessage(MessageDescriptor message) => new(
        message.Key,
        message.Arguments.Select(MapArgument).ToArray(),
        message.Severity switch
        {
            MessageSeverity.Success => AdminMessageSeverity.Success,
            MessageSeverity.Warning => AdminMessageSeverity.Warning,
            MessageSeverity.Error => AdminMessageSeverity.Error,
            _ => AdminMessageSeverity.Info
        },
        message.DefaultText,
        message.Source);

    private static AdminMessageArgument MapArgument(object? value) => value switch
    {
        null => new(AdminScalarKind.Null, null),
        string text => new(AdminScalarKind.String, text),
        bool flag => new(AdminScalarKind.Boolean, flag ? "true" : "false"),
        byte or sbyte or short or ushort or int or uint or long or ulong =>
            new(AdminScalarKind.Integer, Convert.ToString(value, CultureInfo.InvariantCulture)),
        float or double or decimal =>
            new(AdminScalarKind.Number, ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture)),
        DateTimeOffset timestamp =>
            new(AdminScalarKind.DateTimeOffset, timestamp.ToString("O", CultureInfo.InvariantCulture)),
        DateTime timestamp =>
            new(AdminScalarKind.DateTimeOffset, timestamp.ToString("O", CultureInfo.InvariantCulture)),
        TimeSpan duration =>
            new(AdminScalarKind.Duration, duration.ToString("c", CultureInfo.InvariantCulture)),
        IFormattable formattable =>
            new(AdminScalarKind.String, formattable.ToString(null, CultureInfo.InvariantCulture)),
        _ => new(AdminScalarKind.String, value.ToString())
    };

    private sealed class LocalAdminPrincipal : ICommandPrincipal
    {
        public static LocalAdminPrincipal Instance { get; } = new();

        public string Name => "Vega Local Admin";

        public bool IsServer => true;

        public bool HasPermission(string permission) => true;
    }

    private sealed class BufferedAdminCommandOutput : ICommandOutput
    {
        private readonly List<AdminMessageDescriptor> messages = [];

        public IReadOnlyList<AdminMessageDescriptor> Messages => messages;

        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) =>
            Add(AdminMessageSeverity.Info, message);

        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) =>
            Add(AdminMessageSeverity.Success, message);

        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) =>
            Add(AdminMessageSeverity.Warning, message);

        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) =>
            Add(AdminMessageSeverity.Error, message);

        private ValueTask Add(AdminMessageSeverity severity, string message)
        {
            messages.Add(new AdminMessageDescriptor(
                Key: null,
                Arguments: Array.Empty<AdminMessageArgument>(),
                Severity: severity,
                FallbackText: message));
            return ValueTask.CompletedTask;
        }
    }
}
