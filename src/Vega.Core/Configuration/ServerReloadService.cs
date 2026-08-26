using Vega.Contracts.Events;
using Vega.Contracts.Runtime;

namespace Vega.Core.Configuration;

public sealed class ServerReloadService(
    VegaServerConfigurationState configuration,
    IAsyncEventHub events) : IServerReloadService
{
    private readonly VegaServerConfigurationState configuration =
        configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly IAsyncEventHub events = events ?? throw new ArgumentNullException(nameof(events));

    public async ValueTask<ServerReloadResult> ReloadAsync(
        string requestedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);

        EventOutcome<ServerReloading> reloading;
        try
        {
            reloading = await events.DispatchAsync(
                new ServerReloading(requestedBy),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ServerReloadResult(
                ServerReloadStatus.Failed,
                "A pre-reload hook failed; live configuration was not changed.",
                "reload.pre_hook_failed");
        }

        if (reloading.IsCancelled)
        {
            EventCancellation cancellation = reloading.Cancellation!.Value;
            return new ServerReloadResult(
                ServerReloadStatus.Cancelled,
                string.IsNullOrWhiteSpace(cancellation.Message)
                    ? "Server reload was cancelled by a lifecycle handler."
                    : cancellation.Message!,
                cancellation.Code);
        }

        try
        {
            _ = configuration.Reload();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new ServerReloadResult(
                ServerReloadStatus.Failed,
                ex.Message,
                "config.invalid");
        }

        try
        {
            _ = await events.DispatchAsync(
                new ServerReloaded(requestedBy, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ServerReloadResult(
                ServerReloadStatus.Succeeded,
                "Configuration was reloaded, but a post-reload hook failed.",
                "reload.post_hook_failed");
        }

        return new ServerReloadResult(
            ServerReloadStatus.Succeeded,
            "Configuration reloaded. Restart-only server/listener/world settings will take effect after a process restart.");
    }
}
