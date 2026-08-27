using System.Globalization;
using Vega.Admin.Client;
using Vega.Admin.Contracts;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;
using Vega.PluginSdk;

namespace Vega.Runtime.Administration;

/// <summary>
/// In-process administration client for the embedded TUI. It intentionally uses the same
/// transport-neutral DTOs that a future remote client will expose instead of leaking runtime
/// implementation objects into UI command and status paths.
/// </summary>
internal sealed class LocalVegaAdminClient(
    IOperationsSnapshotProvider snapshots,
    ICommandDispatcher dispatcher,
    ICommandCatalog commandCatalog,
    ManagedConfigurationAdministration? configurations = null) : IVegaAdminClient
{
    private readonly ManagedConfigurationAdministration configurations =
        configurations ?? new ManagedConfigurationAdministration();

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
        return ValueTask.FromResult(MapStatus(snapshots.Capture().Server));
    }

    public ValueTask<IReadOnlyList<AdminPlayerSnapshot>> GetPlayersAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<AdminPlayerSnapshot> players = snapshots.Capture()
            .Players
            .Select(static player => new AdminPlayerSnapshot(
                player.Id.Slot,
                player.Name,
                player.WorldId.ToString(),
                player.IsDead,
                player.Life,
                player.MaxLife,
                player.Mana,
                player.MaxMana,
                player.IsHostile,
                player.Team,
                player.Platform.Kind.ToString(),
                player.ConnectedAt))
            .ToArray();
        return ValueTask.FromResult(players);
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

    public ValueTask<IReadOnlyList<AdminModuleSnapshot>> GetModulesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<AdminModuleSnapshot> modules = snapshots.Capture()
            .Modules
            .Select(static module => new AdminModuleSnapshot(
                module.Name,
                module.Version,
                module.State.ToString(),
                module.Dependencies.ToArray(),
                checked((long)module.StartupDuration.TotalMilliseconds),
                module.IsHotReloadable,
                module.Failure))
            .ToArray();
        return ValueTask.FromResult(modules);
    }

    public ValueTask<IReadOnlyList<AdminLogRecord>> QueryLogsAsync(
        AdminLogQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        int limit = Math.Clamp(query.Limit, 1, 1_000);
        OperationsSnapshot snapshot = snapshots.Capture(new OperationsLogFilter(
            query.MinimumLevel is null ? null : ToOperationsLevel(query.MinimumLevel.Value),
            Normalize(query.Source),
            Normalize(query.Text),
            limit));
        IReadOnlyList<AdminLogRecord> logs = snapshot.Logs
            .Select(static record => new AdminLogRecord(
                record.Sequence,
                record.Timestamp,
                ToAdminLevel(record.Level),
                record.Source,
                record.Text))
            .ToArray();
        return ValueTask.FromResult(logs);
    }

    public ValueTask<IReadOnlyList<AdminChatRecord>> GetChatAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int boundedLimit = Math.Clamp(limit, 1, 1_000);
        IReadOnlyList<AdminChatRecord> chat = snapshots.Capture()
            .Chat
            .TakeLast(boundedLimit)
            .Select(static record => new AdminChatRecord(
                record.Sequence,
                record.Timestamp,
                record.Source,
                record.Text))
            .ToArray();
        return ValueTask.FromResult(chat);
    }

    public ValueTask<AdminAdministrationSnapshot> GetAdministrationAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AdministrationOperationsSnapshot administration = snapshots.Capture().Administration;
        return ValueTask.FromResult(new AdminAdministrationSnapshot(
            administration.Groups.Select(static group => new AdminPermissionGroupSnapshot(
                group.Name,
                group.ParentName,
                group.GrantedPermissions.ToArray(),
                group.DeniedPermissions.ToArray(),
                group.AccountCount,
                group.Revision)).ToArray(),
            administration.Accounts.Select(static account => new AdminAccountSnapshot(
                account.Name,
                account.GroupName,
                account.LastSeenUtc,
                account.Culture)).ToArray(),
            administration.SampledAt));
    }

    public ValueTask<AdminDiagnosticsSnapshot> GetDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(MapDiagnostics(snapshots.Capture()));
    }

    public ValueTask<IReadOnlyList<AdminConfigurationDescriptor>> GetConfigurationsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<AdminConfigurationDescriptor> descriptors = configurations
            .Snapshot()
            .Select(MapConfigurationDescriptor)
            .ToArray();
        return ValueTask.FromResult(descriptors);
    }

    public ValueTask<AdminConfigurationMutationResult> ReadConfigurationAsync(
        AdminConfigurationKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(MapConfigurationResult(
            configurations.Read(key.Plugin, key.FileName)));
    }

    public ValueTask<AdminConfigurationMutationResult> ReplaceConfigurationAsync(
        AdminConfigurationReplaceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(MapConfigurationResult(configurations.Replace(
            request.Plugin,
            request.FileName,
            request.ExpectedRevision,
            request.Json)));
    }

    public ValueTask<AdminConfigurationMutationResult> ReloadConfigurationAsync(
        AdminConfigurationMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(MapConfigurationResult(configurations.Reload(
            request.Plugin,
            request.FileName,
            request.ExpectedRevision)));
    }

    private static AdminServerStatusSnapshot MapStatus(ServerDashboardSnapshot server) => new(
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
        DateTimeOffset.UtcNow);

    private static AdminDiagnosticsSnapshot MapDiagnostics(OperationsSnapshot snapshot)
    {
        RuntimeMetricsSnapshot runtime = snapshot.Runtime;
        NetworkMetricsSnapshot network = snapshot.Network;
        TickRateStatisticsSnapshot tick = snapshot.Server.TickRate;
        return new AdminDiagnosticsSnapshot(
            tick.Current,
            tick.AverageOneMinute,
            tick.TickDurationP50Milliseconds,
            tick.TickDurationP95Milliseconds,
            tick.TickDurationP99Milliseconds,
            runtime.Host.WorkingSetBytes,
            runtime.ManagedMemoryBytes,
            runtime.GcHeapBytes,
            runtime.GcFragmentedBytes,
            runtime.Gen0Collections,
            runtime.Gen1Collections,
            runtime.Gen2Collections,
            runtime.AvailableWorkerThreads,
            runtime.MaximumWorkerThreads,
            network.InboundPackets,
            network.InboundBytes,
            network.OutboundPackets,
            network.OutboundBytes,
            network.RejectedInboundPackets,
            network.SecurityViolations,
            new Dictionary<string, long>(network.SecurityRuleCounts, StringComparer.Ordinal),
            DateTimeOffset.UtcNow);
    }

    private static AdminConfigurationDescriptor MapConfigurationDescriptor(
        ManagedConfigurationAdminDescriptor descriptor) =>
        new(
            descriptor.Plugin,
            descriptor.FileName,
            descriptor.Revision,
            descriptor.CanEdit,
            descriptor.HasRedactions,
            descriptor.DisplayName,
            descriptor.Description);

    private static AdminConfigurationMutationResult MapConfigurationResult(
        ManagedConfigurationAdminResult result) =>
        new(
            result.Succeeded,
            result.Document is null
                ? null
                : new AdminConfigurationDocument(
                    MapConfigurationDescriptor(result.Document.Descriptor),
                    result.Document.Json),
            result.ErrorCode,
            result.Error);

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

    private static AdminLogLevel ToAdminLevel(OperationsLogLevel level) => level switch
    {
        OperationsLogLevel.Trace => AdminLogLevel.Trace,
        OperationsLogLevel.Debug => AdminLogLevel.Debug,
        OperationsLogLevel.Information => AdminLogLevel.Information,
        OperationsLogLevel.Warning => AdminLogLevel.Warning,
        OperationsLogLevel.Error => AdminLogLevel.Error,
        OperationsLogLevel.Critical => AdminLogLevel.Critical,
        _ => AdminLogLevel.Information
    };

    private static OperationsLogLevel ToOperationsLevel(AdminLogLevel level) => level switch
    {
        AdminLogLevel.Trace => OperationsLogLevel.Trace,
        AdminLogLevel.Debug => OperationsLogLevel.Debug,
        AdminLogLevel.Information => OperationsLogLevel.Information,
        AdminLogLevel.Warning => OperationsLogLevel.Warning,
        AdminLogLevel.Error => OperationsLogLevel.Error,
        AdminLogLevel.Critical => OperationsLogLevel.Critical,
        _ => OperationsLogLevel.Information
    };

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
