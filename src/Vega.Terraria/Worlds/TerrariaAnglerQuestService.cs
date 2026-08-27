using Terraria.ID;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Worlds;

public sealed class TerrariaAnglerQuestService(
    IGameThreadDispatcher dispatcher,
    WorldId worldId) : IWorldAnglerQuestService
{
    public ValueTask<AnglerQuestClearResult> ClearAsync(
        AnglerQuestClearRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Actor);
        return DispatchAsync(
            () => Clear(request),
            cancellationToken,
            new AnglerQuestClearResult(
                AnglerQuestClearStatus.DispatcherUnavailable,
                ErrorCode: "angler.dispatcher_unavailable"),
            new AnglerQuestClearResult(
                AnglerQuestClearStatus.Cancelled,
                ErrorCode: "angler.operation_cancelled"));
    }

    private AnglerQuestClearResult Clear(AnglerQuestClearRequest request)
    {
        dispatcher.AssertGameThread();
        if (request.WorldId != worldId)
        {
            return new AnglerQuestClearResult(
                AnglerQuestClearStatus.Invalid,
                ErrorCode: "angler.wrong_world");
        }

        List<string> completed = global::Terraria.Main.anglerWhoFinishedToday;
        if (request.PlayerName is null)
        {
            int removed = completed.Count;
            completed.Clear();
            global::Terraria.NetMessage.SendAnglerQuest(-1);
            return new AnglerQuestClearResult(AnglerQuestClearStatus.Applied, removed);
        }

        string playerName = request.PlayerName;
        int removedEntries = completed.RemoveAll(name =>
            name.Equals(playerName, StringComparison.OrdinalIgnoreCase));
        if (removedEntries == 0)
        {
            return new AnglerQuestClearResult(
                AnglerQuestClearStatus.NotFound,
                ErrorCode: "angler.player_not_found");
        }

        for (int slot = 0; slot < global::Terraria.Main.maxPlayers; slot++)
        {
            global::Terraria.Player? player = global::Terraria.Main.player[slot];
            if (player is not null &&
                player.active &&
                player.name.Equals(playerName, StringComparison.OrdinalIgnoreCase))
            {
                global::Terraria.NetMessage.SendData(MessageID.AnglerQuest, slot);
            }
        }

        return new AnglerQuestClearResult(AnglerQuestClearStatus.Applied, removedEntries);
    }

    private ValueTask<AnglerQuestClearResult> DispatchAsync(
        Func<AnglerQuestClearResult> action,
        CancellationToken cancellationToken,
        AnglerQuestClearResult unavailable,
        AnglerQuestClearResult cancelled)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(cancelled);
        }

        if (!dispatcher.IsGameThreadKnown)
        {
            return ValueTask.FromResult(unavailable);
        }

        if (dispatcher.IsGameThread)
        {
            return ValueTask.FromResult(action());
        }

        var completion = new TaskCompletionSource<AnglerQuestClearResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryPost(
                "world.angler-quests.clear",
                () => completion.TrySetResult(cancellationToken.IsCancellationRequested ? cancelled : action())))
        {
            return ValueTask.FromResult(unavailable);
        }

        return new ValueTask<AnglerQuestClearResult>(completion.Task);
    }
}
