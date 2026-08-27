using Terraria.ID;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Worlds;

public sealed class TerrariaWorldEnvironmentService(
    IGameThreadDispatcher dispatcher,
    WorldId worldId) : IWorldEnvironmentService
{
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

        if (patch.GameMode is int gameMode && (gameMode < 0 || gameMode > 3))
        {
            return Invalid("world.environment.invalid_game_mode");
        }

        if (patch.IsDaytime is bool daytime) global::Terraria.Main.dayTime = daytime;
        if (patch.TimeTicks is double ticks) global::Terraria.Main.time = ticks;
        if (patch.MoonPhase is int phase) global::Terraria.Main.moonPhase = phase;
        if (patch.BloodMoon is bool bloodMoon) global::Terraria.Main.bloodMoon = bloodMoon;
        if (patch.Eclipse is bool eclipse) global::Terraria.Main.eclipse = eclipse;
        if (patch.Raining is bool raining) global::Terraria.Main.raining = raining;
        if (patch.RainIntensity is float intensity) global::Terraria.Main.maxRaining = intensity;
        if (patch.WindSpeed is float speed) global::Terraria.Main.windSpeedCurrent = speed;
        if (patch.GameMode is int mode) global::Terraria.Main.GameMode = mode;

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
            DateTimeOffset.UtcNow,
            global::Terraria.Main.GameMode);
    }

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
                () => completion.TrySetResult(cancellationToken.IsCancellationRequested ? cancelled! : action())))
        {
            return ValueTask.FromResult(unavailable!);
        }

        return new ValueTask<T>(completion.Task);
    }
}
