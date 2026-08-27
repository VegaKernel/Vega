using Microsoft.Xna.Framework;
using Terraria.ID;
using Terraria.Localization;
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

    public ValueTask<PlayerOperationResult> RecallWorldSpawnAsync(
        PlayerId id,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(
            "player.recall-world-spawn",
            () =>
            {
                if (!TryGetActivePlayer(id, out global::Terraria.Player? player) || player is null)
                {
                    return PlayerOperationResult.NotFound();
                }

                player.Bottom = new Vector2(
                    global::Terraria.Main.spawnTileX * 16f + 8f,
                    global::Terraria.Main.spawnTileY * 16f);
                float x = Math.Clamp(
                    player.position.X,
                    640f,
                    global::Terraria.Main.rightWorld - 640f - player.width);
                float y = Math.Clamp(
                    player.position.Y,
                    640f,
                    global::Terraria.Main.bottomWorld - 640f - player.height);
                var position = new Vector2(x, y);

                global::Terraria.NetMessage.SendTileSquare(
                    id.Slot,
                    (short)(x / 16f - 7f),
                    (short)(y / 16f - 7f),
                    15,
                    15,
                    TileChangeType.None);
                global::Terraria.RemoteClient.CheckSection(id.Slot, position);
                player.Teleport(position, 1);
                global::Terraria.NetMessage.SendData(
                    MessageID.TeleportEntity,
                    -1,
                    -1,
                    NetworkText.Empty,
                    0,
                    player.whoAmI,
                    x,
                    y,
                    1);
                return PlayerOperationResult.Applied();
            },
            "player.recall-world-spawn.failed",
            cancellationToken);

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
