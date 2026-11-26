using Vega.Contracts.Operations;
using Vega.Contracts.Runtime;
using Vega.Contracts.Threading;
using Vega.Terraria.Worlds;

namespace Vega.Terraria.Operations;

public sealed class TerrariaServerControl(
    TerrariaWorldMaintenanceService worldMaintenance,
    IGameThreadDispatcher gameThread,
    IOperationsLogger logger) : IServerControl
{
    public ValueTask SaveAsync(CancellationToken cancellationToken = default) =>
        worldMaintenance.SaveAsync(cancellationToken);

    public ValueTask ShutdownAsync(
        string reason,
        CancellationToken cancellationToken = default) => ShutdownCoreAsync(reason, true, cancellationToken);

    public ValueTask ShutdownWithoutSaveAsync(string reason, CancellationToken cancellationToken = default) =>
        ShutdownCoreAsync(reason, false, cancellationToken);

    private ValueTask ShutdownCoreAsync(string reason, bool save, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return DispatchAsync("server-shutdown", () =>
        {
            global::Terraria.Netplay.SaveOnServerExit = save;
            global::Terraria.Netplay.Disconnect = true;
            logger.Log(OperationsLogLevel.Warning, "server-control", (save ? "" : "Without world save: ") + (reason.Length <= 512 ? reason : reason[..512]));
        }, cancellationToken);
    }

    public ValueTask SetPasswordAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length > 128 || password.Any(char.IsControl)) throw new ArgumentException("Invalid server password.", nameof(password));
        return DispatchAsync("server-password", () =>
        {
            global::Terraria.Netplay.ServerPassword = password;
            logger.Log(OperationsLogLevel.Information, "server-control", password.Length == 0 ? "Server password cleared." : "Server password changed.");
        }, cancellationToken);
    }

    private async ValueTask DispatchAsync(string operation, Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!gameThread.TryPost(operation, () =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    action();
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
        {
            throw new InvalidOperationException("The Terraria game-thread queue is full.");
        }

        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
