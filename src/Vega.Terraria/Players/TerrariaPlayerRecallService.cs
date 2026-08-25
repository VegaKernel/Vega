using Terraria.ID;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;

namespace Vega.Terraria.Players;

/// <summary>
/// Performs Terraria-native recall to a player's personal spawn point on the authoritative game thread.
/// </summary>
public sealed class TerrariaPlayerRecallService(IGameThreadDispatcher dispatcher) : IPlayerRecallOperations
{
    public ValueTask<PlayerOperationResult> RecallHomeAsync(
        PlayerId id,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(
            "player.recall-home",
            () =>
            {
                if (!TryGetActivePlayer(id, out global::Terraria.Player? player) || player is null)
                {
                    return PlayerOperationResult.NotFound();
                }

                if (player.dead)
                {
                    return PlayerOperationResult.Failed("player.recall-home.dead");
                }

                player.Spawn(global::Terraria.PlayerSpawnContext.RecallFromItem);
                global::Terraria.NetMessage.SendData(
                    MessageID.PlayerSpawn,
                    -1,
                    -1,
                    null,
                    id.Slot,
                    (float)global::Terraria.PlayerSpawnContext.RecallFromItem);
                return PlayerOperationResult.Applied();
            },
            "player.recall-home.failed",
            cancellationToken);

    public ValueTask<PlayerOperationResult> KickAsync(
        PlayerId id,
        string reason,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(PlayerOperationResult.Failed("player.recall.kick_unsupported"));

    public ValueTask<PlayerOperationResult> TeleportAsync(
        PlayerId id,
        PlayerPosition destination,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(PlayerOperationResult.Failed("player.recall.teleport_unsupported"));

    private static bool TryGetActivePlayer(PlayerId id, out global::Terraria.Player? player)
    {
        global::Terraria.Player[] all = global::Terraria.Main.player;
        if ((uint)id.Slot < (uint)all.Length && all[id.Slot] is { active: true } active)
        {
            player = active;
            return true;
        }

        player = null;
        return false;
    }

    private ValueTask<PlayerOperationResult> DispatchAsync(
        string operation,
        Func<PlayerOperationResult> action,
        string failureCode,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(PlayerOperationResult.Failed(operation + ".cancelled"));
        }

        if (!dispatcher.IsGameThreadKnown)
        {
            return ValueTask.FromResult(PlayerOperationResult.Failed(operation + ".dispatcher_unavailable"));
        }

        if (dispatcher.IsGameThread)
        {
            return ValueTask.FromResult(action());
        }

        var completion = new TaskCompletionSource<PlayerOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryPost(operation, () =>
            {
                try
                {
                    completion.TrySetResult(action());
                }
                catch
                {
                    completion.TrySetResult(PlayerOperationResult.Failed(failureCode));
                }
            }))
        {
            return ValueTask.FromResult(PlayerOperationResult.Failed(operation + ".dispatcher_unavailable"));
        }

        return new ValueTask<PlayerOperationResult>(completion.Task.WaitAsync(cancellationToken));
    }
}
