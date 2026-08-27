using Vega.Admin.Client;
using Vega.Admin.Contracts;

namespace Vega.TerminalUI;

/// <summary>
/// Worker-dispatching wrapper used by the embedded Terminal.Gui host. The current in-process
/// adapter can produce completed ValueTasks after doing synchronous projection work; dispatching
/// here preserves the same IVegaAdminClient semantics without letting that work block the UI
/// thread. A future network client can remain naturally asynchronous behind the same boundary.
/// </summary>
internal sealed class BackgroundAdminClient(IVegaAdminClient inner) : IVegaAdminClient
{
    private readonly IVegaAdminClient inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public ValueTask<AdminProtocolInfo> GetProtocolInfoAsync(
        CancellationToken cancellationToken = default) =>
        Run(() => inner.GetProtocolInfoAsync(cancellationToken), cancellationToken);

    public ValueTask<AdminServerStatusSnapshot> GetServerStatusAsync(
        CancellationToken cancellationToken = default) =>
        Run(() => inner.GetServerStatusAsync(cancellationToken), cancellationToken);

    public ValueTask<IReadOnlyList<AdminPlayerSnapshot>> GetPlayersAsync(
        CancellationToken cancellationToken = default) =>
        Run(() => inner.GetPlayersAsync(cancellationToken), cancellationToken);

    public ValueTask<IReadOnlyList<AdminCommandDescriptor>> GetCommandCatalogAsync(
        CancellationToken cancellationToken = default) =>
        Run(() => inner.GetCommandCatalogAsync(cancellationToken), cancellationToken);

    public ValueTask<AdminCommandResult> ExecuteCommandAsync(
        AdminCommandRequest request,
        CancellationToken cancellationToken = default) =>
        Run(() => inner.ExecuteCommandAsync(request, cancellationToken), cancellationToken);

    public ValueTask<IReadOnlyList<AdminModuleSnapshot>> GetModulesAsync(
        CancellationToken cancellationToken = default) =>
        Run(() => inner.GetModulesAsync(cancellationToken), cancellationToken);

    public ValueTask<IReadOnlyList<AdminLogRecord>> QueryLogsAsync(
        AdminLogQuery query,
        CancellationToken cancellationToken = default) =>
        Run(() => inner.QueryLogsAsync(query, cancellationToken), cancellationToken);

    public ValueTask<IReadOnlyList<AdminChatRecord>> GetChatAsync(
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        Run(() => inner.GetChatAsync(limit, cancellationToken), cancellationToken);

    public ValueTask<AdminAdministrationSnapshot> GetAdministrationAsync(
        CancellationToken cancellationToken = default) =>
        Run(() => inner.GetAdministrationAsync(cancellationToken), cancellationToken);

    public ValueTask<AdminDiagnosticsSnapshot> GetDiagnosticsAsync(
        CancellationToken cancellationToken = default) =>
        Run(() => inner.GetDiagnosticsAsync(cancellationToken), cancellationToken);

    public ValueTask<IReadOnlyList<AdminConfigurationDescriptor>> GetConfigurationsAsync(
        CancellationToken cancellationToken = default) =>
        Run(() => inner.GetConfigurationsAsync(cancellationToken), cancellationToken);

    public ValueTask<AdminConfigurationMutationResult> ReadConfigurationAsync(
        AdminConfigurationKey key,
        CancellationToken cancellationToken = default) =>
        Run(() => inner.ReadConfigurationAsync(key, cancellationToken), cancellationToken);

    public ValueTask<AdminConfigurationMutationResult> ReplaceConfigurationAsync(
        AdminConfigurationReplaceRequest request,
        CancellationToken cancellationToken = default) =>
        Run(() => inner.ReplaceConfigurationAsync(request, cancellationToken), cancellationToken);

    public ValueTask<AdminConfigurationMutationResult> ReloadConfigurationAsync(
        AdminConfigurationMutationRequest request,
        CancellationToken cancellationToken = default) =>
        Run(() => inner.ReloadConfigurationAsync(request, cancellationToken), cancellationToken);

    private static ValueTask<T> Run<T>(
        Func<ValueTask<T>> operation,
        CancellationToken cancellationToken) =>
        new(Task.Run(
            async () => await operation().ConfigureAwait(false),
            cancellationToken));
}
