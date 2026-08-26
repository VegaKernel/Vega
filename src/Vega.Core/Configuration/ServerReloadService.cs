using Vega.Contracts.Events;
using Vega.Contracts.Runtime;
using Vega.Core.GeoIP;

namespace Vega.Core.Configuration;

public sealed class ServerReloadService : IServerReloadService, IDisposable
{
    private readonly VegaServerConfigurationState configuration;
    private readonly IAsyncEventHub events;
    private readonly string geoIpConfigurationPath;
    private readonly GeoIpDatabaseUpdater geoIpUpdater;
    private int disposed;

    public ServerReloadService(
        VegaServerConfigurationState configuration,
        IAsyncEventHub events)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.events = events ?? throw new ArgumentNullException(nameof(events));
        geoIpConfigurationPath = this.configuration.Path;

        bool autoUpdate = false;
        try
        {
            autoUpdate = GeoIpAutoUpdateOptions.Load(geoIpConfigurationPath).AutoUpdate;
        }
        catch (Exception ex) when (
            ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // The main configuration loader owns startup validation. GeoIP auto-update
            // stays disabled here rather than turning an optional network feature into
            // a second startup failure path.
        }

        geoIpUpdater = new GeoIpDatabaseUpdater(
            Path.Combine(AppContext.BaseDirectory, "data", "geoip"),
            autoUpdate);
        AppDomain.CurrentDomain.ProcessExit += HandleProcessExit;
    }

    public async ValueTask<ServerReloadResult> ReloadAsync(
        string requestedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

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

        GeoIpAutoUpdateOptions geoIpOptions;
        try
        {
            geoIpOptions = GeoIpAutoUpdateOptions.Load(geoIpConfigurationPath);
            _ = configuration.Reload();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new ServerReloadResult(
                ServerReloadStatus.Failed,
                ex.Message,
                "config.invalid");
        }

        // Switching the option only wakes/cancels the background worker. The reload
        // command never waits for GitHub or a database download.
        geoIpUpdater.SetEnabled(geoIpOptions.AutoUpdate);

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

    private void HandleProcessExit(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.ProcessExit -= HandleProcessExit;
        geoIpUpdater.Dispose();
    }
}
