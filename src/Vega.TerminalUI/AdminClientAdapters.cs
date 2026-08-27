using System.Globalization;
using System.Text;
using Vega.Admin.Client;
using Vega.Admin.Contracts;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.PluginSdk;

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
    private static readonly Lazy<ManagedConfigurationAdministration> ConfigurationAdministration =
        new(CreateConfigurationAdministration, LazyThreadSafetyMode.ExecutionAndPublication);

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
        return ValueTask.FromResult(MapStatus(server));
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
        OperationsSnapshot snapshot = snapshots.Capture();
        return ValueTask.FromResult(MapDiagnostics(snapshot));
    }

    public ValueTask<IReadOnlyList<AdminConfigurationDescriptor>> GetConfigurationsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<AdminConfigurationDescriptor> descriptors = ConfigurationAdministration.Value
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
            ConfigurationAdministration.Value.Read(key.Plugin, key.FileName)));
    }

    public ValueTask<AdminConfigurationMutationResult> ReplaceConfigurationAsync(
        AdminConfigurationReplaceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(MapConfigurationResult(ConfigurationAdministration.Value.Replace(
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
        return ValueTask.FromResult(MapConfigurationResult(ConfigurationAdministration.Value.Reload(
            request.Plugin,
            request.FileName,
            request.ExpectedRevision)));
    }

    private static ManagedConfigurationAdministration CreateConfigurationAdministration()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "config", "admin-configuration.json");
        return ManagedConfigurationAdministration.LoadPolicy(path, out _);
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
