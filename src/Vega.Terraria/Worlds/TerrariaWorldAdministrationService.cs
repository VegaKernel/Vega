using Terraria.GameContent.Events;
using Terraria.ID;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Worlds;

internal enum WorldAdministrationStatus
{
    Applied,
    Invalid,
    PlayerNotFound,
    AlreadyRunning,
    Rejected,
    DispatcherUnavailable,
    Cancelled,
    Failed
}

internal readonly record struct WorldAdministrationResult(
    WorldAdministrationStatus Status,
    string? ErrorCode = null,
    string? Value = null,
    int? TileX = null,
    int? TileY = null)
{
    public bool IsSuccess => Status == WorldAdministrationStatus.Applied;
}

internal readonly record struct WorldEventRequest(
    string EventType,
    string? Variant = null,
    int Wave = 1);

internal interface IWorldAdministrationService
{
    ValueTask<WorldAdministrationResult> ToggleHalloweenAsync(WorldId worldId, CancellationToken cancellationToken = default);

    ValueTask<WorldAdministrationResult> ToggleXmasAsync(WorldId worldId, CancellationToken cancellationToken = default);

    ValueTask<WorldAdministrationResult> ToggleHardmodeAsync(WorldId worldId, CancellationToken cancellationToken = default);

    ValueTask<WorldAdministrationResult> ToggleEvilAsync(WorldId worldId, CancellationToken cancellationToken = default);

    ValueTask<WorldAdministrationResult> TriggerEventAsync(WorldId worldId, WorldEventRequest request, CancellationToken cancellationToken = default);

    ValueTask<WorldAdministrationResult> GrowAsync(WorldId worldId, PlayerId playerId, string plant, bool allowEvil, CancellationToken cancellationToken = default);

    ValueTask<WorldAdministrationResult> SetSpawnAsync(WorldId worldId, PlayerId playerId, CancellationToken cancellationToken = default);

    ValueTask<WorldAdministrationResult> SetDungeonAsync(WorldId worldId, PlayerId playerId, CancellationToken cancellationToken = default);

    ValueTask<WorldAdministrationResult> SettleLiquidsAsync(WorldId worldId, CancellationToken cancellationToken = default);
}

internal sealed class TerrariaWorldAdministrationService(
    IGameThreadDispatcher dispatcher,
    WorldId worldId,
    TerrariaWorldMaintenanceService worldMaintenance) : IWorldAdministrationService
{
    public ValueTask<WorldAdministrationResult> ToggleHalloweenAsync(
        WorldId requestedWorldId,
        CancellationToken cancellationToken = default) =>
        DispatchAsync("world.admin.halloween", requestedWorldId, ToggleHalloween, cancellationToken);

    public ValueTask<WorldAdministrationResult> ToggleXmasAsync(
        WorldId requestedWorldId,
        CancellationToken cancellationToken = default) =>
        DispatchAsync("world.admin.xmas", requestedWorldId, ToggleXmas, cancellationToken);

    public ValueTask<WorldAdministrationResult> ToggleHardmodeAsync(
        WorldId requestedWorldId,
        CancellationToken cancellationToken = default) =>
        DispatchAsync("world.admin.hardmode", requestedWorldId, ToggleHardmode, cancellationToken);

    public ValueTask<WorldAdministrationResult> ToggleEvilAsync(
        WorldId requestedWorldId,
        CancellationToken cancellationToken = default) =>
        DispatchAsync("world.admin.evil", requestedWorldId, ToggleEvil, cancellationToken);

    public ValueTask<WorldAdministrationResult> TriggerEventAsync(
        WorldId requestedWorldId,
        WorldEventRequest request,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(
            "world.admin.event",
            requestedWorldId,
            () => TriggerEvent(request),
            cancellationToken);

    public ValueTask<WorldAdministrationResult> GrowAsync(
        WorldId requestedWorldId,
        PlayerId playerId,
        string plant,
        bool allowEvil,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(
            "world.admin.grow",
            requestedWorldId,
            () => Grow(playerId, plant, allowEvil),
            cancellationToken);

    public async ValueTask<WorldAdministrationResult> SetSpawnAsync(
        WorldId requestedWorldId,
        PlayerId playerId,
        CancellationToken cancellationToken = default)
    {
        WorldAdministrationResult result = await DispatchAsync(
            "world.admin.set_spawn",
            requestedWorldId,
            () => SetSpawn(playerId),
            cancellationToken).ConfigureAwait(false);
        return await SaveAfterLocationMutationAsync(result, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<WorldAdministrationResult> SetDungeonAsync(
        WorldId requestedWorldId,
        PlayerId playerId,
        CancellationToken cancellationToken = default)
    {
        WorldAdministrationResult result = await DispatchAsync(
            "world.admin.set_dungeon",
            requestedWorldId,
            () => SetDungeon(playerId),
            cancellationToken).ConfigureAwait(false);
        return await SaveAfterLocationMutationAsync(result, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<WorldAdministrationResult> SettleLiquidsAsync(
        WorldId requestedWorldId,
        CancellationToken cancellationToken = default) =>
        DispatchAsync("world.admin.settle", requestedWorldId, SettleLiquids, cancellationToken);

    private static WorldAdministrationResult ToggleHalloween()
    {
        global::Terraria.Main.halloween = !global::Terraria.Main.halloween;
        SendWorldData();
        return Applied(global::Terraria.Main.halloween ? "enabled" : "disabled");
    }

    private static WorldAdministrationResult ToggleXmas()
    {
        global::Terraria.Main.xMas = !global::Terraria.Main.xMas;
        SendWorldData();
        return Applied(global::Terraria.Main.xMas ? "enabled" : "disabled");
    }

    private static WorldAdministrationResult ToggleHardmode()
    {
        if (global::Terraria.Main.hardMode)
        {
            global::Terraria.Main.hardMode = false;
            SendWorldData();
            return Applied("off");
        }

        global::Terraria.WorldGen.StartHardmode();
        if (!global::Terraria.Main.hardMode)
        {
            return new WorldAdministrationResult(
                WorldAdministrationStatus.Rejected,
                "world.admin.hardmode_cancelled");
        }

        return Applied("on");
    }

    private static WorldAdministrationResult ToggleEvil()
    {
        global::Terraria.WorldGen.crimson = !global::Terraria.WorldGen.crimson;
        SendWorldData();
        return Applied(global::Terraria.WorldGen.crimson ? "crimson" : "corruption");
    }

    private static WorldAdministrationResult TriggerEvent(WorldEventRequest request)
    {
        string eventType = request.EventType.Trim().ToLowerInvariant();
        switch (eventType)
        {
            case "meteor":
                global::Terraria.WorldGen.spawnMeteor = false;
                global::Terraria.WorldGen.dropMeteor();
                return Applied("meteor");

            case "fullmoon":
            case "full moon":
                global::Terraria.Main.dayTime = false;
                global::Terraria.Main.time = 0d;
                global::Terraria.Main.moonPhase = 0;
                SendWorldData();
                return Applied("fullmoon");

            case "bloodmoon":
            case "blood moon":
                global::Terraria.Main.bloodMoon = !global::Terraria.Main.bloodMoon;
                SendWorldData();
                return Applied(global::Terraria.Main.bloodMoon ? "bloodmoon:on" : "bloodmoon:off");

            case "eclipse":
                global::Terraria.Main.eclipse = !global::Terraria.Main.eclipse;
                SendWorldData();
                return Applied(global::Terraria.Main.eclipse ? "eclipse:on" : "eclipse:off");

            case "invasion":
            case "invade":
                return ToggleInvasion(request);

            case "sandstorm":
                if (Sandstorm.Happening)
                {
                    Sandstorm.StopSandstorm();
                    return Applied("sandstorm:off");
                }

                Sandstorm.StartSandstorm();
                return Applied("sandstorm:on");

            case "rain":
                return ToggleRain(request.Variant);

            case "lanternsnight":
            case "lanterns":
                LanternNight.ToggleManualLanterns();
                return Applied(LanternNight.LanternsUp ? "lanterns:on" : "lanterns:off");

            case "meteorshower":
                if (global::Terraria.WorldGen.meteorShowerCount > 0)
                {
                    global::Terraria.WorldGen.meteorShowerCount = 0;
                    return Applied("meteorshower:off");
                }

                global::Terraria.WorldGen.StartMeteorShower();
                return Applied("meteorshower:on");

            default:
                return Invalid("world.admin.event.invalid_type");
        }
    }

    private static WorldAdministrationResult ToggleInvasion(WorldEventRequest request)
    {
        if (global::Terraria.Main.invasionSize > 0)
        {
            if (DD2Event.Ongoing)
            {
                DD2Event.StopInvasion();
                return Applied("invasion:dd2:off");
            }

            global::Terraria.Main.invasionSize = 0;
            SendWorldData();
            return Applied("invasion:off");
        }

        string variant = request.Variant?.Trim().ToLowerInvariant() ?? string.Empty;
        switch (variant)
        {
            case "goblin":
            case "goblins":
                global::Terraria.Main.StartInvasion(1);
                return Applied("invasion:goblins");
            case "snowman":
            case "snowmen":
                global::Terraria.Main.StartInvasion(2);
                return Applied("invasion:snowmen");
            case "pirate":
            case "pirates":
                global::Terraria.Main.StartInvasion(3);
                return Applied("invasion:pirates");
            case "martian":
            case "martians":
                global::Terraria.Main.StartInvasion(4);
                return Applied("invasion:martians");
            case "pumpkin":
            case "pumpkinmoon":
                if (request.Wave <= 0)
                {
                    return Invalid("world.admin.event.invalid_wave");
                }

                global::Terraria.Main.dayTime = false;
                global::Terraria.Main.time = 0d;
                global::Terraria.Main.pumpkinMoon = true;
                global::Terraria.Main.snowMoon = false;
                global::Terraria.Main.bloodMoon = false;
                global::Terraria.NPC.waveKills = 0f;
                global::Terraria.NPC.waveNumber = request.Wave;
                SendWorldData();
                return Applied($"invasion:pumpkinmoon:{request.Wave}");
            case "frost":
            case "frostmoon":
                if (request.Wave <= 0)
                {
                    return Invalid("world.admin.event.invalid_wave");
                }

                global::Terraria.Main.dayTime = false;
                global::Terraria.Main.time = 0d;
                global::Terraria.Main.snowMoon = true;
                global::Terraria.Main.pumpkinMoon = false;
                global::Terraria.Main.bloodMoon = false;
                global::Terraria.NPC.waveKills = 0f;
                global::Terraria.NPC.waveNumber = request.Wave;
                SendWorldData();
                return Applied($"invasion:frostmoon:{request.Wave}");
            default:
                return Invalid("world.admin.event.invalid_invasion");
        }
    }

    private static WorldAdministrationResult ToggleRain(string? requestedVariant)
    {
        string variant = requestedVariant?.Trim().ToLowerInvariant() ?? "normal";
        switch (variant)
        {
            case "slime":
                if (global::Terraria.Main.raining)
                {
                    return new WorldAdministrationResult(
                        WorldAdministrationStatus.Rejected,
                        "world.admin.event.slime_rain_conflicts_with_rain");
                }

                if (global::Terraria.Main.slimeRain)
                {
                    global::Terraria.Main.StopSlimeRain(false);
                    SendWorldData();
                    return Applied("rain:slime:off");
                }

                global::Terraria.Main.StartSlimeRain(false);
                SendWorldData();
                return Applied("rain:slime:on");

            case "coin":
                if (global::Terraria.Main.coinRain != 0)
                {
                    global::Terraria.Main.StopRain();
                    SendWorldData();
                    return Applied("rain:coin:off");
                }

                global::Terraria.Main.StartRain(guaranteeCoinRain: true);
                SendWorldData();
                return Applied("rain:coin:on");

            case "normal":
                if (global::Terraria.Main.raining)
                {
                    global::Terraria.Main.StopRain();
                    SendWorldData();
                    return Applied("rain:off");
                }

                global::Terraria.Main.StartRain();
                SendWorldData();
                return Applied("rain:on");

            default:
                return Invalid("world.admin.event.invalid_rain_type");
        }
    }

    private static WorldAdministrationResult Grow(PlayerId playerId, string rawPlant, bool allowEvil)
    {
        if (!TryGetPlayer(playerId, out global::Terraria.Player? player))
        {
            return new WorldAdministrationResult(
                WorldAdministrationStatus.PlayerNotFound,
                "world.admin.player_not_found");
        }

        int x = (int)(player!.position.X / 16f);
        int y = (int)(player.position.Y / 16f) + 3;
        if (x < 2 || x >= global::Terraria.Main.maxTilesX - 2 || y < 21 || y >= global::Terraria.Main.maxTilesY - 2)
        {
            return Invalid("world.admin.grow.out_of_world");
        }

        string plant = rawPlant.Trim().ToLowerInvariant();
        bool evilPlant = plant is
            "shadewood" or "ebonwood" or "pearlwood" or
            "hallowpalm" or "crimsonpalm" or "corruptpalm";
        if (evilPlant && !allowEvil)
        {
            return new WorldAdministrationResult(
                WorldAdministrationStatus.Rejected,
                "world.admin.grow.evil_denied");
        }

        bool PrepareArea(ushort groundType)
        {
            for (int i = x - 2; i < x + 3; i++)
            {
                global::Terraria.ITile tile = global::Terraria.Main.tile[i, y];
                tile.active(true);
                tile.type = groundType;
                tile.wall = WallID.None;
            }

            global::Terraria.Main.tile[x, y - 1].wall = WallID.None;
            return true;
        }

        void GrowTree(ushort groundType)
        {
            PrepareArea(groundType);
            global::Terraria.WorldGen.GrowTree(x, y);
        }

        void GrowTreeByType(ushort treeType, ushort preparedGround = TileID.Grass)
        {
            PrepareArea(preparedGround);
            global::Terraria.WorldGen.TryGrowingTreeByType(treeType, x, y);
        }

        void GrowPalm(ushort sandType, ushort supportingType)
        {
            for (int i = x - 2; i < x + 3; i++)
            {
                global::Terraria.ITile top = global::Terraria.Main.tile[i, y];
                top.active(true);
                top.type = sandType;
                top.wall = WallID.None;

                global::Terraria.ITile support = global::Terraria.Main.tile[i, y + 1];
                support.active(true);
                support.type = supportingType;
                support.wall = WallID.None;
            }

            global::Terraria.Main.tile[x, y - 1].wall = WallID.None;
            global::Terraria.WorldGen.GrowPalmTree(x, y);
        }

        string displayName;
        switch (plant)
        {
            case "basic": GrowTree(TileID.Grass); displayName = "Basic Tree"; break;
            case "boreal": GrowTree(TileID.SnowBlock); displayName = "Boreal Tree"; break;
            case "mahogany": GrowTree(TileID.JungleGrass); displayName = "Rich Mahogany"; break;
            case "sakura": GrowTreeByType(TileID.VanityTreeSakura); displayName = "Sakura Tree"; break;
            case "willow": GrowTreeByType(TileID.VanityTreeYellowWillow); displayName = "Willow Tree"; break;
            case "shadewood": GrowTree(TileID.CrimsonGrass); displayName = "Shadewood Tree"; break;
            case "ebonwood": GrowTree(TileID.CorruptGrass); displayName = "Ebonwood Tree"; break;
            case "pearlwood": GrowTree(TileID.HallowedGrass); displayName = "Pearlwood Tree"; break;
            case "palm": GrowPalm(TileID.Sand, TileID.HardenedSand); displayName = "Desert Palm"; break;
            case "hallowpalm": GrowPalm(TileID.Pearlsand, TileID.HallowHardenedSand); displayName = "Hallow Palm"; break;
            case "crimsonpalm": GrowPalm(TileID.Crimsand, TileID.CrimsonHardenedSand); displayName = "Crimson Palm"; break;
            case "corruptpalm": GrowPalm(TileID.Ebonsand, TileID.CorruptHardenedSand); displayName = "Corruption Palm"; break;
            case "topaz": GrowTreeByType(TileID.TreeTopaz, 1); displayName = "Topaz Gemtree"; break;
            case "amethyst": GrowTreeByType(TileID.TreeAmethyst, 1); displayName = "Amethyst Gemtree"; break;
            case "sapphire": GrowTreeByType(TileID.TreeSapphire, 1); displayName = "Sapphire Gemtree"; break;
            case "emerald": GrowTreeByType(TileID.TreeEmerald, 1); displayName = "Emerald Gemtree"; break;
            case "ruby": GrowTreeByType(TileID.TreeRuby, 1); displayName = "Ruby Gemtree"; break;
            case "diamond": GrowTreeByType(TileID.TreeDiamond, 1); displayName = "Diamond Gemtree"; break;
            case "amber": GrowTreeByType(TileID.TreeAmber, 1); displayName = "Amber Gemtree"; break;
            case "cactus":
                global::Terraria.Main.tile[x, y].type = TileID.Sand;
                global::Terraria.WorldGen.GrowCactus(x, y);
                displayName = "Cactus";
                break;
            case "herb":
                global::Terraria.Main.tile[x, y].active(true);
                global::Terraria.Main.tile[x, y].frameX = 36;
                global::Terraria.Main.tile[x, y].type = TileID.MatureHerbs;
                global::Terraria.WorldGen.GrowAlch(x, y);
                displayName = "Herb";
                break;
            case "mushroom":
                PrepareArea(TileID.MushroomGrass);
                global::Terraria.WorldGen.GrowShroom(x, y);
                displayName = "Glowing Mushroom Tree";
                break;
            default:
                return Invalid("world.admin.grow.unknown_plant");
        }

        if (global::Terraria.Main.netMode == 2)
        {
            global::Terraria.NetMessage.SendTileSquare(-1, x - 2, y - 20, 5, 25);
        }

        return Applied(displayName, x, y);
    }

    private static WorldAdministrationResult SetSpawn(PlayerId playerId)
    {
        if (!TryGetPlayer(playerId, out global::Terraria.Player? player))
        {
            return new WorldAdministrationResult(
                WorldAdministrationStatus.PlayerNotFound,
                "world.admin.player_not_found");
        }

        int x = (int)(player!.position.X / 16f) + 1;
        int y = (int)(player.position.Y / 16f) + 3;
        if (!InWorld(x, y))
        {
            return Invalid("world.admin.spawn.out_of_world");
        }

        global::Terraria.Main.spawnTileX = x;
        global::Terraria.Main.spawnTileY = y;
        SendWorldData();
        return Applied("spawn", x, y);
    }

    private static WorldAdministrationResult SetDungeon(PlayerId playerId)
    {
        if (!TryGetPlayer(playerId, out global::Terraria.Player? player))
        {
            return new WorldAdministrationResult(
                WorldAdministrationStatus.PlayerNotFound,
                "world.admin.player_not_found");
        }

        int x = (int)(player!.position.X / 16f) + 1;
        int y = (int)(player.position.Y / 16f) + 3;
        if (!InWorld(x, y))
        {
            return Invalid("world.admin.dungeon.out_of_world");
        }

        global::Terraria.Main.dungeonX = x;
        global::Terraria.Main.dungeonY = y;
        SendWorldData();
        return Applied("dungeon", x, y);
    }

    private static WorldAdministrationResult SettleLiquids()
    {
        if (global::Terraria.Liquid.panicMode)
        {
            return new WorldAdministrationResult(
                WorldAdministrationStatus.AlreadyRunning,
                "world.admin.liquids.already_settling");
        }

        global::Terraria.Liquid.StartPanic();
        return Applied("settling");
    }

    private async ValueTask<WorldAdministrationResult> SaveAfterLocationMutationAsync(
        WorldAdministrationResult result,
        CancellationToken cancellationToken)
    {
        if (!result.IsSuccess)
        {
            return result;
        }

        try
        {
            await worldMaintenance.SaveAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            return new WorldAdministrationResult(
                WorldAdministrationStatus.Cancelled,
                "world.admin.save_cancelled",
                result.Value,
                result.TileX,
                result.TileY);
        }
        catch
        {
            return new WorldAdministrationResult(
                WorldAdministrationStatus.Failed,
                "world.admin.save_failed",
                result.Value,
                result.TileX,
                result.TileY);
        }
    }

    private ValueTask<WorldAdministrationResult> DispatchAsync(
        string operation,
        WorldId requestedWorldId,
        Func<WorldAdministrationResult> action,
        CancellationToken cancellationToken)
    {
        if (requestedWorldId != worldId)
        {
            return ValueTask.FromResult(Invalid("world.admin.wrong_world"));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(new WorldAdministrationResult(
                WorldAdministrationStatus.Cancelled,
                "world.admin.cancelled"));
        }

        if (!dispatcher.IsGameThreadKnown)
        {
            return ValueTask.FromResult(new WorldAdministrationResult(
                WorldAdministrationStatus.DispatcherUnavailable,
                "world.admin.dispatcher_unavailable"));
        }

        if (dispatcher.IsGameThread)
        {
            return ValueTask.FromResult(action());
        }

        var completion = new TaskCompletionSource<WorldAdministrationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryPost(
                operation,
                () => completion.TrySetResult(
                    cancellationToken.IsCancellationRequested
                        ? new WorldAdministrationResult(
                            WorldAdministrationStatus.Cancelled,
                            "world.admin.cancelled")
                        : action())))
        {
            return ValueTask.FromResult(new WorldAdministrationResult(
                WorldAdministrationStatus.DispatcherUnavailable,
                "world.admin.dispatcher_unavailable"));
        }

        return new ValueTask<WorldAdministrationResult>(completion.Task);
    }

    private static bool TryGetPlayer(PlayerId playerId, out global::Terraria.Player? player)
    {
        player = null;
        if (playerId.Slot < 0 || playerId.Slot >= global::Terraria.Main.maxPlayers)
        {
            return false;
        }

        global::Terraria.Player candidate = global::Terraria.Main.player[playerId.Slot];
        if (candidate is null || !candidate.active)
        {
            return false;
        }

        player = candidate;
        return true;
    }

    private static bool InWorld(int x, int y) =>
        x >= 0 && y >= 0 &&
        x < global::Terraria.Main.maxTilesX &&
        y < global::Terraria.Main.maxTilesY;

    private static void SendWorldData()
    {
        if (global::Terraria.Main.netMode == 2)
        {
            global::Terraria.NetMessage.SendData(MessageID.WorldData);
        }
    }

    private static WorldAdministrationResult Applied(
        string value,
        int? tileX = null,
        int? tileY = null) =>
        new(WorldAdministrationStatus.Applied, Value: value, TileX: tileX, TileY: tileY);

    private static WorldAdministrationResult Invalid(string errorCode) =>
        new(WorldAdministrationStatus.Invalid, errorCode);
}