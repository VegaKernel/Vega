using Terraria.ID;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Worlds;

public sealed class TerrariaWorldEnvironmentService(
    IGameThreadDispatcher dispatcher,
    WorldId worldId,
    IWorldClockController? clock = null) : IWorldEnvironmentService
{
    public WorldTilePosition? GetSpawnPosition()
    {
        if (!dispatcher.IsGameThread || global::Terraria.Main.maxTilesX <= 0 || global::Terraria.Main.maxTilesY <= 0) return null;
        return new(global::Terraria.Main.spawnTileX, global::Terraria.Main.spawnTileY);
    }

    public ValueTask<WorldEnvironmentSnapshot?> CaptureAsync(
        WorldId requestedWorldId,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(
            "world.environment.capture",
            () => requestedWorldId == worldId ? Capture() : null,
            cancellationToken);

    public ValueTask<WorldEnvironmentMutationResult> PatchAsync(
        WorldEnvironmentMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return DispatchAsync(
            "world.environment.patch",
            () => Patch(request),
            cancellationToken,
            new WorldEnvironmentMutationResult(
                WorldEnvironmentMutationStatus.DispatcherUnavailable,
                ErrorCode: "world.environment.dispatcher_unavailable"),
            new WorldEnvironmentMutationResult(
                WorldEnvironmentMutationStatus.Cancelled,
                ErrorCode: "world.environment.cancelled"));
    }

    private WorldEnvironmentMutationResult Patch(WorldEnvironmentMutationRequest request)
    {
        dispatcher.AssertGameThread();
        if (request.WorldId != worldId)
        {
            return Invalid("world.environment.wrong_world");
        }

        WorldEnvironmentPatch patch = request.Patch;
        if (patch.StartHardmode)
        {
            if (patch != new WorldEnvironmentPatch(StartHardmode: true)) return Invalid("world.environment.combined_hardmode");
            if (!global::Terraria.Main.hardMode) global::Terraria.WorldGen.StartHardmode(force: false);
            return global::Terraria.Main.hardMode
                ? new(WorldEnvironmentMutationStatus.Applied, Capture())
                : new(WorldEnvironmentMutationStatus.Failed, ErrorCode: "world.hardmode.declined");
        }
        if (patch.NativeEvent is { } nativeEvent)
        {
            if (patch != new WorldEnvironmentPatch(NativeEvent: nativeEvent)) return Invalid("world.environment.combined_native_event");
            return ApplyNativeEvent(nativeEvent);
        }
        if (patch.ClearAnglerPlayer is { } playerName)
        {
            if (patch != new WorldEnvironmentPatch(ClearAnglerPlayer: playerName) ||
                string.IsNullOrWhiteSpace(playerName) || playerName.Length > 128 || playerName.Any(char.IsControl))
                return Invalid("world.clearangler_invalid_player");
            if (global::Terraria.Main.anglerWhoFinishedToday.RemoveAll(name => name.Equals(playerName, StringComparison.OrdinalIgnoreCase)) == 0)
                return Invalid("world.clearangler_player_not_found");
            if (global::Terraria.Main.netMode == 2)
                for (int slot = 0; slot < global::Terraria.Main.maxPlayers; slot++)
                    if (global::Terraria.Netplay.Clients[slot].State == 10 && global::Terraria.Main.player[slot] is { active: true } player &&
                        player.name.Equals(playerName, StringComparison.OrdinalIgnoreCase)) global::Terraria.NetMessage.SendAnglerQuest(slot);
            return new(WorldEnvironmentMutationStatus.Applied, Capture());
        }
        bool targetDaytime = patch.IsDaytime ?? global::Terraria.Main.dayTime;
        double maxTime = targetDaytime ? 54000d : 32400d;
        if (patch.TimeTicks is double time && (!double.IsFinite(time) || time < 0d || time > maxTime))
        {
            return Invalid("world.environment.invalid_time");
        }

        if (patch.MoonPhase is int moon && (moon < 0 || moon > 7))
        {
            return Invalid("world.environment.invalid_moon_phase");
        }

        if (patch.RainIntensity is float rain && (!float.IsFinite(rain) || rain < 0f || rain > 1f))
        {
            return Invalid("world.environment.invalid_rain_intensity");
        }

        if (patch.WindSpeed is float wind && (!float.IsFinite(wind) || wind < -1f || wind > 1f))
        {
            return Invalid("world.environment.invalid_wind_speed");
        }

        if (patch.Spawn is { } spawn && !InsideWorld(spawn) || patch.Dungeon is { } dungeon && !InsideWorld(dungeon))
            return Invalid("world.environment.invalid_position");
        if (patch.DefaultSpawnRate is < 1 or > 60000 || patch.DefaultMaxSpawns is < 0 or > 200)
            return Invalid("world.environment.invalid_spawn_settings");
        // Journey characters and worlds have a different admission contract. Keep
        // that mode immutable here instead of breaking connected clients.
        if (patch.GameMode is int mode && (mode is < 0 or > 2 || global::Terraria.Main.GameMode == 3))
            return Invalid("world.environment.invalid_game_mode");

        if (patch.IsDaytime is bool daytime) global::Terraria.Main.dayTime = daytime;
        if (patch.TimeTicks is double ticks) global::Terraria.Main.time = ticks;
        if (patch.IsDaytime.HasValue || patch.TimeTicks.HasValue) clock?.Reset(worldId);
        if (patch.MoonPhase is int phase) global::Terraria.Main.moonPhase = phase;
        if (patch.BloodMoon is bool bloodMoon) global::Terraria.Main.bloodMoon = bloodMoon;
        if (patch.Eclipse is bool eclipse) global::Terraria.Main.eclipse = eclipse;
        if (patch.Raining is bool raining)
        {
            if (raining) global::Terraria.Main.StartRain(instant: true, strengthOverride: patch.RainIntensity);
            else global::Terraria.Main.StopRain(instant: true);
        }
        if (patch.RainIntensity is float intensity) global::Terraria.Main.maxRaining = intensity;
        if (patch.WindSpeed is float speed)
        {
            global::Terraria.Main.windSpeedCurrent = speed;
            global::Terraria.Main.windSpeedTarget = speed;
        }
        if (patch.Spawn is { } worldSpawn)
        {
            global::Terraria.Main.spawnTileX = worldSpawn.X;
            global::Terraria.Main.spawnTileY = worldSpawn.Y;
        }
        if (patch.Dungeon is { } dungeonPosition)
        {
            global::Terraria.Main.dungeonX = dungeonPosition.X;
            global::Terraria.Main.dungeonY = dungeonPosition.Y;
        }
        if (patch.DefaultSpawnRate is int rate) global::Terraria.NPC.defaultSpawnRate = rate;
        if (patch.DefaultMaxSpawns is int maximum) global::Terraria.NPC.defaultMaxSpawns = maximum;
        if (patch.GameMode is int gameMode) global::Terraria.Main.GameMode = gameMode;
        if (patch.ForceHalloween is bool halloween)
        {
            global::Terraria.Main.forceHalloweenForever = halloween;
            global::Terraria.Main.checkHalloween();
        }
        if (patch.ForceChristmas is bool christmas)
        {
            global::Terraria.Main.forceXMasForever = christmas;
            global::Terraria.Main.checkXMas();
        }
        if (patch.ClearAnglerCompletion)
        {
            global::Terraria.Main.anglerWhoFinishedToday.Clear();
            global::Terraria.Main.anglerQuestFinished = false;
            if (global::Terraria.Main.netMode == 2)
                for (int slot = 0; slot < global::Terraria.Main.maxPlayers; slot++)
                    if (global::Terraria.Netplay.Clients[slot].State == 10) global::Terraria.NetMessage.SendAnglerQuest(slot);
        }

        if (global::Terraria.Main.netMode == 2)
        {
            global::Terraria.NetMessage.SendData(MessageID.WorldData);
        }

        return new WorldEnvironmentMutationResult(
            WorldEnvironmentMutationStatus.Applied,
            Capture());
    }

    private WorldEnvironmentSnapshot Capture()
    {
        dispatcher.AssertGameThread();
        return new WorldEnvironmentSnapshot(
            worldId,
            global::Terraria.Main.maxTilesX,
            global::Terraria.Main.maxTilesY,
            global::Terraria.Main.spawnTileX,
            global::Terraria.Main.spawnTileY,
            global::Terraria.Main.dayTime,
            global::Terraria.Main.time,
            global::Terraria.Main.moonPhase,
            global::Terraria.Main.bloodMoon,
            global::Terraria.Main.eclipse,
            global::Terraria.Main.raining,
            global::Terraria.Main.maxRaining,
            global::Terraria.Main.windSpeedCurrent,
            global::Terraria.Main.hardMode,
            DateTimeOffset.UtcNow)
        {
            FileIdentity = global::Terraria.Main.ActiveWorldFileData?.UniqueId,
            Name = global::Terraria.Main.worldName,
            Seed = global::Terraria.Main.ActiveWorldFileData?.Seed,
            GameMode = global::Terraria.Main.GameMode,
            Dungeon = new(global::Terraria.Main.dungeonX, global::Terraria.Main.dungeonY),
            DefaultSpawnRate = global::Terraria.NPC.defaultSpawnRate,
            DefaultMaxSpawns = global::Terraria.NPC.defaultMaxSpawns,
            ForceHalloween = global::Terraria.Main.forceHalloweenForever,
            ForceChristmas = global::Terraria.Main.forceXMasForever,
            NativeEvents = Enum.GetValues<NativeWorldEvent>().Where(IsNativeEventActive).ToArray()
        };
    }

    private static bool IsNativeEventActive(NativeWorldEvent kind) => kind switch
    {
        NativeWorldEvent.SlimeRain => global::Terraria.Main.slimeRain,
        NativeWorldEvent.Sandstorm => global::Terraria.GameContent.Events.Sandstorm.Happening,
        NativeWorldEvent.PumpkinMoon => global::Terraria.Main.pumpkinMoon,
        NativeWorldEvent.FrostMoon => global::Terraria.Main.snowMoon,
        NativeWorldEvent.Meteor => false, // one-shot native operation
        NativeWorldEvent.LanternNight => global::Terraria.GameContent.Events.LanternNight.ManualLanterns,
        NativeWorldEvent.MeteorShower => global::Terraria.WorldGen.meteorShowerCount > 0,
        NativeWorldEvent.CoinRain => global::Terraria.Main.coinRain > 0,
        _ => global::Terraria.Main.invasionType == InvasionType(kind)
    };

    private static int InvasionType(NativeWorldEvent kind) => kind switch
    {
        NativeWorldEvent.GoblinArmy => InvasionID.GoblinArmy,
        NativeWorldEvent.SnowLegion => InvasionID.SnowLegion,
        NativeWorldEvent.PirateInvasion => InvasionID.PirateInvasion,
        NativeWorldEvent.MartianMadness => InvasionID.MartianMadness,
        _ => -1
    };

    private WorldEnvironmentMutationResult ApplyNativeEvent(NativeWorldEventChange change)
    {
        if (!Enum.IsDefined(change.Event)) return Invalid("world.event.unknown");
        if (change.Event == NativeWorldEvent.Meteor && !change.Enabled) return Invalid("world.event.meteor_start_only");
        bool active = IsNativeEventActive(change.Event);
        if (active == change.Enabled) return new(WorldEnvironmentMutationStatus.Applied, Capture());
        switch (change.Event)
        {
            case NativeWorldEvent.SlimeRain:
                if (change.Enabled) global::Terraria.Main.StartSlimeRain(announce: true);
                else global::Terraria.Main.StopSlimeRain(announce: true);
                break;
            case NativeWorldEvent.Sandstorm:
                if (change.Enabled) global::Terraria.GameContent.Events.Sandstorm.StartSandstorm();
                else global::Terraria.GameContent.Events.Sandstorm.StopSandstorm();
                break;
            case NativeWorldEvent.Meteor:
                if (!global::Terraria.WorldGen.dropMeteor()) return new(WorldEnvironmentMutationStatus.Failed, ErrorCode: "world.event.meteor_declined");
                global::Terraria.WorldGen.spawnMeteor = false;
                break;
            case NativeWorldEvent.LanternNight:
                global::Terraria.GameContent.Events.LanternNight.ToggleManualLanterns();
                break;
            case NativeWorldEvent.MeteorShower:
                if (change.Enabled) global::Terraria.WorldGen.StartMeteorShower();
                else global::Terraria.WorldGen.meteorShowerCount = 0;
                break;
            case NativeWorldEvent.CoinRain:
                if (change.Enabled) global::Terraria.Main.StartRain(guaranteeCoinRain: true);
                else global::Terraria.Main.StopRain();
                break;
            case NativeWorldEvent.PumpkinMoon:
            case NativeWorldEvent.FrostMoon:
                if (change.Enabled)
                {
                    // BusyWithAnyInvasionOfSorts also includes the native slime-rain
                    // cooldown; that is not an active event for an explicit admin summon.
                    if (global::Terraria.Main.slimeRain || global::Terraria.Main.eclipse || global::Terraria.Main.bloodMoon ||
                        global::Terraria.Main.snowMoon || global::Terraria.Main.pumpkinMoon || global::Terraria.Main.invasionType != InvasionID.None ||
                        global::Terraria.GameContent.Events.DD2Event.Ongoing)
                        return Invalid("world.event.conflict");
                    if (global::Terraria.Main.dayTime)
                    {
                        global::Terraria.Main.dayTime = false;
                        global::Terraria.Main.time = 0;
                        clock?.Reset(worldId);
                    }
                    if (change.Event == NativeWorldEvent.PumpkinMoon) global::Terraria.Main.startPumpkinMoon();
                    else global::Terraria.Main.startSnowMoon();
                }
                else global::Terraria.Main.stopMoonEvent();
                break;
            default:
                if (!change.Enabled) return Invalid("world.event.native_invasion_stop_unsupported");
                int type = InvasionType(change.Event);
                if (global::Terraria.NPC.BusyWithAnyInvasionOfSorts() || !global::Terraria.Main.CanStartInvasion(type, ignoreDelay: false))
                    return Invalid("world.event.invasion_unavailable");
                global::Terraria.Main.StartInvasion(type);
                break;
        }
        if (global::Terraria.Main.netMode == 2) global::Terraria.NetMessage.SendData(MessageID.WorldData);
        return change.Event == NativeWorldEvent.Meteor || IsNativeEventActive(change.Event) == change.Enabled
            ? new(WorldEnvironmentMutationStatus.Applied, Capture())
            : new(WorldEnvironmentMutationStatus.Failed, ErrorCode: "world.event.native_not_applied");
    }

    private static bool InsideWorld(WorldTilePosition position) => position.X >= 40 && position.Y >= 40 &&
        position.X < global::Terraria.Main.maxTilesX - 40 && position.Y < global::Terraria.Main.maxTilesY - 40;

    private static WorldEnvironmentMutationResult Invalid(string code) =>
        new(WorldEnvironmentMutationStatus.Invalid, ErrorCode: code);

    private ValueTask<T> DispatchAsync<T>(
        string operation,
        Func<T> action,
        CancellationToken cancellationToken,
        T? unavailable = default,
        T? cancelled = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(cancelled!);
        }

        if (!dispatcher.IsGameThreadKnown)
        {
            return ValueTask.FromResult(unavailable!);
        }

        if (dispatcher.IsGameThread)
        {
            return ValueTask.FromResult(action());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryPost(
                operation,
                () =>
                {
                    try
                    {
                        completion.TrySetResult(cancellationToken.IsCancellationRequested ? cancelled! : action());
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                }))
        {
            return ValueTask.FromResult(unavailable!);
        }

        return new ValueTask<T>(completion.Task);
    }
}
