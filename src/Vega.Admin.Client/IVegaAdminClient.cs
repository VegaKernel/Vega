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

    ValueTask<IReadOnlyList<AdminCommandDescriptor>> GetCommandCatalogAsync(
        CancellationToken cancellationToken = default);

    ValueTask<AdminCommandResult> ExecuteCommandAsync(
        AdminCommandRequest request,
        CancellationToken cancellationToken = default);
}
