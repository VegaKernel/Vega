using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Modules.Administration;

public sealed class BroadcastCommand(IPlayerDirectory players, IPlayerMessenger messenger) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("broadcast", ["bc", "say"], ["vega.admin.broadcast"], Syntax: "broadcast <message>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        string message = string.Join(' ', invocation.Arguments);
        if (string.IsNullOrWhiteSpace(message) || message.Length > 512)
            return new(CommandExecutionStatus.InvalidUsage, "broadcast.usage");
        int delivered = 0;
        foreach (var player in players.SnapshotOnlinePlayers().Where(p => p.WorldId == invocation.WorldId))
        {
            token.ThrowIfCancellationRequested();
            if (players.TryGet(player.Id, out var current) && current is not null && current.SessionId == player.SessionId && current.WorldId == invocation.WorldId &&
                (await messenger.SendMessageAsync(player, $"[Server] {message}", token).ConfigureAwait(false)).IsSuccess) delivered++;
        }
        return CommandExecutionResult.WithMessages(MessageDescriptor.Info("broadcast.sent", delivered).WithDefaultText("Broadcast delivered to {0} players."));
    }
}

public sealed class ClearCommand(IWorldEntityMaintenance entities, IWorldNpcService npcs, IGameThreadDispatcher dispatcher) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("clear", [], ["vega.world.clear"], Syntax: "clear <item|projectile|npc> [all]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        var args = invocation.Arguments;
        if (args.Count is < 1 or > 2 || args.Count == 2 && !args[1].Equals("all", StringComparison.OrdinalIgnoreCase))
            return new(CommandExecutionStatus.InvalidUsage, "clear.usage");
        string kind = args[0].ToLowerInvariant() switch { "i" => "item", "n" => "npc", "p" or "proj" => "projectile", var value => value };
        bool all = args.Count == 2;
        if (kind is not ("item" or "items" or "projectile" or "projectiles" or "npc" or "npcs"))
            return new(CommandExecutionStatus.InvalidUsage, "clear.usage");
        if (all && !invocation.Principal.HasPermission("vega.world.clear.all"))
            return new(CommandExecutionStatus.Forbidden, "command.permission_denied");

        if (kind is "npc" or "npcs")
        {
            var active = await npcs.SnapshotAsync(invocation.WorldId, token).ConfigureAwait(false);
            int removed = 0;
            foreach (var npc in active.Where(n => all || !n.Friendly && !n.TownNpc && !n.Boss))
            {
                token.ThrowIfCancellationRequested();
                if ((await npcs.DespawnAsync(new(npc.Handle, invocation.Principal.Name), token).ConfigureAwait(false)).IsSuccess) removed++;
            }
            return Completed(kind, removed);
        }

        CommandExecutionResult Apply()
        {
            token.ThrowIfCancellationRequested();
            var snapshot = entities.Snapshot(invocation.WorldId);
            bool items = kind is "item" or "items";
            var result = entities.Remove(new(invocation.WorldId,
                items ? snapshot.Items.Select(i => i.Handle).ToArray() : [],
                items ? [] : snapshot.Projectiles.Where(p => all || !p.IsMinion && !p.IsSentry).Select(p => p.Handle).ToArray()));
            return Completed(kind, items ? result.RemovedItems : result.RemovedProjectiles);
        }

        if (!dispatcher.IsGameThreadKnown) return CommandExecutionResult.Failed("clear.not_ready");
        if (dispatcher.IsGameThread) return Apply();
        var completion = new TaskCompletionSource<CommandExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryPost("world.clear", () =>
        {
            try { completion.TrySetResult(Apply()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) return CommandExecutionResult.Failed("clear.queue_full");
        return await completion.Task.WaitAsync(token).ConfigureAwait(false);
    }

    private static CommandExecutionResult Completed(string kind, int removed) => CommandExecutionResult.WithMessages(
        MessageDescriptor.Success("clear.completed", removed, kind).WithDefaultText("Removed {0} {1}.") );
}
