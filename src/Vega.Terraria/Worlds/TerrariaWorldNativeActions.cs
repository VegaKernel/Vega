using Terraria.ID;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Terraria.Players;

namespace Vega.Terraria.Worlds;

/// <summary>Small authoritative vanilla world actions; no custom tile rewriting or liquid simulation.</summary>
public sealed class TerrariaWorldNativeActions(IGameThreadDispatcher dispatcher, WorldId worldId,
    TerrariaPlayerBoundary players) : IWorldNativeMaintenanceOperations
{
    public ValueTask<WorldNativeActionResult> SwitchEvilAsync(WorldId requestedWorld, bool? crimson,
        CancellationToken cancellationToken = default) => RunAsync(requestedWorld, "world.evil", () =>
    {
        bool target = crimson ?? !global::Terraria.WorldGen.crimson;
        if (global::Terraria.WorldGen.crimson != target)
        {
            global::Terraria.WorldGen.crimson = target;
            if (global::Terraria.Main.netMode == 2) global::Terraria.NetMessage.SendData(MessageID.WorldData);
        }
        return new(WorldNativeActionStatus.Applied, Crimson: target);
    }, cancellationToken);

    public ValueTask<WorldNativeActionResult> SettleAsync(WorldId requestedWorld, CancellationToken cancellationToken = default) =>
        RunAsync(requestedWorld, "world.settle", () =>
        {
            if (global::Terraria.Liquid.panicMode) return new(WorldNativeActionStatus.AlreadyActive);
            // StartPanic sets native state/queues only. UpdateLiquid settles one row per subsequent update.
            global::Terraria.Liquid.StartPanic();
            return global::Terraria.Liquid.panicMode ? new(WorldNativeActionStatus.Applied)
                : new(WorldNativeActionStatus.Failed, "world.settle_failed");
        }, cancellationToken);

    public ValueTask<WorldNativeActionResult> GrowAsync(WorldGrowRequest request, CancellationToken cancellationToken = default) =>
        RunAsync(request.WorldId, "world.grow", () => Grow(request), cancellationToken);

    private WorldNativeActionResult Grow(WorldGrowRequest request)
    {
        WorldTilePosition position;
        if (request.Player is { } expected && request.Coordinates is null)
        {
            if (!players.TryGetSessionPlayer(expected, out var live) || live.dead || live.ghost)
                return Invalid("grow.player_unavailable");
            position = new((int)(live.position.X / 16f), (int)(live.position.Y / 16f) + 3);
        }
        else if (request.Player is null && request.Coordinates is { } coordinates) position = coordinates;
        else return Invalid("grow.invalid_target");

        int x = position.X, y = position.Y;
        if (x < 40 || y < 40 || x >= global::Terraria.Main.maxTilesX - 40 || y >= global::Terraria.Main.maxTilesY - 40)
            return Invalid("grow.outside_world");

        int requiredGround = request.Kind switch
        {
            WorldGrowKind.Basic or WorldGrowKind.Sakura or WorldGrowKind.Willow => TileID.Grass,
            WorldGrowKind.Boreal => TileID.SnowBlock,
            WorldGrowKind.Mahogany => TileID.JungleGrass,
            WorldGrowKind.Shadewood => TileID.CrimsonGrass,
            WorldGrowKind.Ebonwood => TileID.CorruptGrass,
            WorldGrowKind.Pearlwood => TileID.HallowedGrass,
            WorldGrowKind.Palm => TileID.Sand,
            WorldGrowKind.HallowPalm => TileID.Pearlsand,
            WorldGrowKind.CrimsonPalm => TileID.Crimsand,
            WorldGrowKind.CorruptPalm => TileID.Ebonsand,
            WorldGrowKind.Topaz or WorldGrowKind.Amethyst or WorldGrowKind.Sapphire or WorldGrowKind.Emerald or
                WorldGrowKind.Ruby or WorldGrowKind.Diamond or WorldGrowKind.Amber => TileID.Stone,
            WorldGrowKind.Mushroom => TileID.MushroomGrass,
            _ => -1
        };
        var ground = global::Terraria.Main.tile[x, y];
        // Unlike the legacy grow command, never change the supporting ground, walls or nearby tiles.
        if (requiredGround < 0 || !ground.active() || ground.type != requiredGround) return Invalid("grow.incompatible_ground");

        bool grown = request.Kind switch
        {
            WorldGrowKind.Palm or WorldGrowKind.HallowPalm or WorldGrowKind.CrimsonPalm or WorldGrowKind.CorruptPalm =>
                global::Terraria.WorldGen.GrowPalmTree(x, y),
            WorldGrowKind.Sakura => global::Terraria.WorldGen.TryGrowingTreeByType(TileID.VanityTreeSakura, x, y),
            WorldGrowKind.Willow => global::Terraria.WorldGen.TryGrowingTreeByType(TileID.VanityTreeYellowWillow, x, y),
            WorldGrowKind.Topaz => global::Terraria.WorldGen.TryGrowingTreeByType(TileID.TreeTopaz, x, y),
            WorldGrowKind.Amethyst => global::Terraria.WorldGen.TryGrowingTreeByType(TileID.TreeAmethyst, x, y),
            WorldGrowKind.Sapphire => global::Terraria.WorldGen.TryGrowingTreeByType(TileID.TreeSapphire, x, y),
            WorldGrowKind.Emerald => global::Terraria.WorldGen.TryGrowingTreeByType(TileID.TreeEmerald, x, y),
            WorldGrowKind.Ruby => global::Terraria.WorldGen.TryGrowingTreeByType(TileID.TreeRuby, x, y),
            WorldGrowKind.Diamond => global::Terraria.WorldGen.TryGrowingTreeByType(TileID.TreeDiamond, x, y),
            WorldGrowKind.Amber => global::Terraria.WorldGen.TryGrowingTreeByType(TileID.TreeAmber, x, y),
            WorldGrowKind.Mushroom => global::Terraria.WorldGen.GrowShroom(x, y),
            _ => global::Terraria.WorldGen.GrowTree(x, y)
        };
        return grown ? new(WorldNativeActionStatus.Applied, Position: position)
            : new(WorldNativeActionStatus.Failed, "grow.native_declined");
    }

    private ValueTask<WorldNativeActionResult> RunAsync(WorldId requestedWorld, string operation,
        Func<WorldNativeActionResult> action, CancellationToken token)
    {
        if (requestedWorld != worldId) return ValueTask.FromResult(Invalid("world.wrong_world"));
        if (token.IsCancellationRequested) return ValueTask.FromResult(new WorldNativeActionResult(WorldNativeActionStatus.Cancelled));
        if (!dispatcher.IsGameThreadKnown) return ValueTask.FromResult(new WorldNativeActionResult(WorldNativeActionStatus.Unavailable));
        WorldNativeActionResult Apply()
        {
            dispatcher.AssertGameThread();
            if (token.IsCancellationRequested) return new(WorldNativeActionStatus.Cancelled);
            return global::Terraria.Main.gameMenu || global::Terraria.Main.maxTilesX <= 0 || global::Terraria.Main.maxTilesY <= 0
                ? new(WorldNativeActionStatus.Unavailable, "world.not_ready") : action();
        }
        if (dispatcher.IsGameThread) return ValueTask.FromResult(Apply());
        var completion = new TaskCompletionSource<WorldNativeActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryPost(operation, () =>
        {
            try { completion.TrySetResult(Apply()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) return ValueTask.FromResult(new WorldNativeActionResult(WorldNativeActionStatus.Unavailable));
        return new(completion.Task.WaitAsync(token));
    }

    private static WorldNativeActionResult Invalid(string code) => new(WorldNativeActionStatus.Invalid, code);
}
