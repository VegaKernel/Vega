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
        CancellationToken cancellationToken = default) =>
        ShutdownCoreAsync(reason, saveWorld: true, cancellationToken);

    public ValueTask ShutdownWithoutSaveAsync(
        string reason,
        CancellationToken cancellationToken = default) =>
        ShutdownCoreAsync(reason, saveWorld: false, cancellationToken);

    public async ValueTask SetPasswordAsync(
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!gameThread.TryPost("server-password", () =>
            {
                try
                {
                    global::Terraria.Netplay.ServerPassword = password;
                    logger.Log(
                        OperationsLogLevel.Information,
                        "server-control",
                        string.IsNullOrEmpty(password)
                            ? "Live server password was cleared."
                            : "Live server password was changed.");
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

    private async ValueTask ShutdownCoreAsync(
        string reason,
        bool saveWorld,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!gameThread.TryPost("server-shutdown", () =>
            {
                try
                {
                    global::Terraria.Netplay.SaveOnServerExit = saveWorld;
                    global::Terraria.Netplay.Disconnect = true;
                    logger.Log(
                        OperationsLogLevel.Warning,
                        "server-control",
                        reason.Length <= 512 ? reason : reason[..512]);
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
