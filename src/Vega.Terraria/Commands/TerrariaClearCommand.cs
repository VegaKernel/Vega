using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Terraria.Worlds;

namespace Vega.Terraria.Commands;

internal static class TerrariaClearCommandRegistration
{
    public static IDisposable Register(
        ICommandDispatcher dispatcher,
        WorldId runtimeWorldId,
        IGameThreadDispatcher gameThread,
        IPlayerDirectory players)
    {
        if (dispatcher is not ICommandRegistry registry)
        {
            throw new InvalidOperationException(
                "The Vega command dispatcher must expose ICommandRegistry for clear command registration.");
        }

        var entities = new TerrariaWorldEntityMaintenance(gameThread);
        var npcs = new TerrariaWorldNpcService(gameThread, runtimeWorldId);
        return registry.Register(new ClearCommand(gameThread, players, entities, npcs));
    }
}

internal sealed class ClearCommand(
    IGameThreadDispatcher gameThread,
    IPlayerDirectory players,
    IWorldEntityMaintenance entities,
    IWorldNpcService npcs) : ICommandHandler
{
    private const int DefaultRadiusTiles = 50;
    private const int MaxRadiusTiles = 5000;

    public CommandDescriptor Descriptor { get; } = new(
        "clear",
        [],
        ["vega.world.clear"],
        AllowServer: false,
        Syntax: "clear <item|npc|projectile> [radius:1..5000]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count is < 1 or > 2 ||
            invocation.Principal is not IPlayerCommandPrincipal principal ||
            !players.TryGet(principal.PlayerId, out PlayerSnapshot? caller) ||
            caller is null)
        {
            return Usage();
        }

        string kind = invocation.Arguments[0].ToLowerInvariant();
        if (kind is not ("item" or "npc" or "projectile"))
        {
            return Usage();
        }

        int radiusTiles = DefaultRadiusTiles;
        if (invocation.Arguments.Count == 2 &&
            (!int.TryParse(invocation.Arguments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out radiusTiles) ||
             radiusTiles is < 1 or > MaxRadiusTiles))
        {
            return Usage();
        }

        float radiusPixels = checked(radiusTiles * 16f);
        float radiusSquared = radiusPixels * radiusPixels;

        if (kind == "npc")
        {
            IReadOnlyList<NpcSnapshot> snapshot = await npcs
                .SnapshotAsync(invocation.WorldId, cancellationToken)
                .ConfigureAwait(false);
            NpcSnapshot[] selected = snapshot
                .Where(npc => DistanceSquared(caller.Position, npc.Position) <= radiusSquared)
                .ToArray();

            int removed = 0;
            int skipped = 0;
            foreach (NpcSnapshot npc in selected)
            {
                NpcOperationResult npcResult = await npcs
                    .DespawnAsync(new NpcDespawnRequest(npc.Handle, invocation.Principal.Name), cancellationToken)
                    .ConfigureAwait(false);
                if (npcResult.IsSuccess) removed++;
                else skipped++;
            }

            return Result("NPC", removed, skipped, radiusTiles);
        }

        ClearEntityResult result = await ClearWorldEntitiesAsync(
            invocation.WorldId,
            caller.Position,
            radiusSquared,
            clearItems: kind == "item",
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return CommandExecutionResult.Failed(
                result.ErrorCode ?? "command.clear.failed",
                MessageDescriptor.Error(result.ErrorCode ?? "command.clear.failed")
                    .WithDefaultText("World entity cleanup is unavailable."));
        }

        return Result(kind == "item" ? "item" : "projectile", result.Removed, result.Skipped, radiusTiles);
    }

    private ValueTask<ClearEntityResult> ClearWorldEntitiesAsync(
        WorldId worldId,
        PlayerPosition center,
        float radiusSquared,
        bool clearItems,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(ClearEntityResult.Failed("command.clear.cancelled"));
        }

        if (!gameThread.IsGameThreadKnown)
        {
            return ValueTask.FromResult(ClearEntityResult.Failed("command.clear.dispatcher-unavailable"));
        }

        ClearEntityResult Execute()
        {
            WorldEntityCleanupSnapshot snapshot = entities.Snapshot(worldId);
            WorldItemCleanupHandle[] items = clearItems
                ? snapshot.Items
                    .Where(item => DistanceSquared(center, item.Position) <= radiusSquared)
                    .Select(static item => item.Handle)
                    .ToArray()
                : [];
            WorldProjectileCleanupHandle[] projectiles = clearItems
                ? []
                : snapshot.Projectiles
                    .Where(projectile => DistanceSquared(center, projectile.Position) <= radiusSquared)
                    .Select(static projectile => projectile.Handle)
                    .ToArray();

            WorldEntityRemovalResult removed = entities.Remove(
                new WorldEntityRemovalRequest(worldId, items, projectiles));
            return clearItems
                ? ClearEntityResult.Success(removed.RemovedItems, removed.SkippedItems)
                : ClearEntityResult.Success(removed.RemovedProjectiles, removed.SkippedProjectiles);
        }

        if (gameThread.IsGameThread)
        {
            return ValueTask.FromResult(Execute());
        }

        var completion = new TaskCompletionSource<ClearEntityResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!gameThread.TryPost("command.clear.entities", () =>
            {
                try
                {
                    completion.TrySetResult(Execute());
                }
                catch
                {
                    completion.TrySetResult(ClearEntityResult.Failed("command.clear.failed"));
                }
            }))
        {
            return ValueTask.FromResult(ClearEntityResult.Failed("command.clear.dispatcher-unavailable"));
        }

        return new ValueTask<ClearEntityResult>(completion.Task.WaitAsync(cancellationToken));
    }

    private static float DistanceSquared(PlayerPosition left, PlayerPosition right)
    {
        float x = left.X - right.X;
        float y = left.Y - right.Y;
        return x * x + y * y;
    }

    private static CommandExecutionResult Result(string kind, int removed, int skipped, int radiusTiles) =>
        CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.clear.applied", kind, removed, skipped, radiusTiles)
                .WithDefaultText("Cleared {1} {0}(s) within {3} tile(s); {2} changed or disappeared before removal."));

    private static CommandExecutionResult Usage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.clear.usage",
        [MessageDescriptor.Error("command.clear.usage")
            .WithDefaultText($"Usage: clear <item|npc|projectile> [radius:1..{MaxRadiusTiles}]. Default radius: {DefaultRadiusTiles} tiles.")]);

    private readonly record struct ClearEntityResult(
        bool Succeeded,
        int Removed,
        int Skipped,
        string? ErrorCode = null)
    {
        public static ClearEntityResult Success(int removed, int skipped) => new(true, removed, skipped);
        public static ClearEntityResult Failed(string code) => new(false, 0, 0, code);
    }
}
