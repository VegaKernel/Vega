using Vega.Admin.Contracts;

namespace Vega.Admin.Client;

/// <summary>
/// Transport-neutral administration boundary consumed by local and remote clients.
/// Implementations may be in-process or networked, but observable administration semantics
/// must remain server-owned and equivalent.
/// </summary>
public interface IVegaAdminClient
{
    ValueTask<AdminProtocolInfo> GetProtocolInfoAsync(
        CancellationToken cancellationToken = default);

    ValueTask<AdminServerStatusSnapshot> GetServerStatusAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AdminPlayerSnapshot>> GetPlayersAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AdminCommandDescriptor>> GetCommandCatalogAsync(
        CancellationToken cancellationToken = default);

    ValueTask<AdminCommandResult> ExecuteCommandAsync(
        AdminCommandRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AdminModuleSnapshot>> GetModulesAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AdminLogRecord>> QueryLogsAsync(
        AdminLogQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AdminChatRecord>> GetChatAsync(
        int limit = 100,
        CancellationToken cancellationToken = default);

    ValueTask<AdminAdministrationSnapshot> GetAdministrationAsync(
        CancellationToken cancellationToken = default);

    ValueTask<AdminDiagnosticsSnapshot> GetDiagnosticsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AdminConfigurationDescriptor>> GetConfigurationsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<AdminConfigurationMutationResult> ReadConfigurationAsync(
        AdminConfigurationKey key,
        CancellationToken cancellationToken = default);

    ValueTask<AdminConfigurationMutationResult> ReplaceConfigurationAsync(
        AdminConfigurationReplaceRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<AdminConfigurationMutationResult> ReloadConfigurationAsync(
        AdminConfigurationMutationRequest request,
        CancellationToken cancellationToken = default);
}
