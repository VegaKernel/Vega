using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Worlds;

namespace Vega.Modules.Administration;

public sealed class TimeCommand(IWorldEnvironmentService world) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("time", [], ["vega.world.time"],
        Syntax: "time [day|noon|night|midnight|HH:mm]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 0)
        {
            var snapshot = await world.CaptureAsync(invocation.WorldId, cancellationToken).ConfigureAwait(false);
            if (snapshot is null) return Unavailable();
            int minutes = (270 + (snapshot.IsDaytime ? 0 : 900) + (int)(snapshot.TimeTicks / 60)) % 1440;
            return CommandExecutionResult.WithMessages(MessageDescriptor.Info("command.time.current", Format(minutes))
                .WithDefaultText("World time: {0}."));
        }

        if (invocation.Arguments.Count != 1 || !TryParse(invocation.Arguments[0], out int minute))
            return new(CommandExecutionStatus.InvalidUsage, "command.time.usage",
                [MessageDescriptor.Error("command.time.usage").WithDefaultText("Usage: time [day|noon|night|midnight|HH:mm].")]);

        int phaseMinute = (minute - 270 + 1440) % 1440;
        bool day = phaseMinute < 900;
        var patch = new WorldEnvironmentPatch(IsDaytime: day, TimeTicks: (day ? phaseMinute : phaseMinute - 900) * 60d);
        var result = await world.PatchAsync(new(invocation.WorldId, patch, invocation.Principal.Name), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? CommandExecutionResult.WithMessages(MessageDescriptor.Success("command.time.applied", Format(minute)).WithDefaultText("World time set to {0}."))
            : Unavailable(result.ErrorCode);
    }

    private static bool TryParse(string value, out int minutes)
    {
        minutes = value.ToLowerInvariant() switch { "day" => 270, "noon" => 720, "night" => 1170, "midnight" => 0, _ => -1 };
        if (minutes >= 0) return true;
        string[] parts = value.Split(':');
        if (parts.Length != 2 || parts[0].Length is < 1 or > 2 || parts[1].Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hour) || hour is < 0 or > 23 ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minute) || minute is < 0 or > 59)
            return false;
        minutes = hour * 60 + minute;
        return true;
    }

    private static string Format(int minute) => FormattableString.Invariant($"{minute / 60:00}:{minute % 60:00}");

    private static CommandExecutionResult Unavailable(string? code = null) => CommandExecutionResult.Failed(
        code ?? "command.time.unavailable", MessageDescriptor.Error("command.time.unavailable").WithDefaultText("Unable to access world time."));
}

public sealed class WindCommand(IWorldEnvironmentService world) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("wind", [], ["vega.world.wind"], Syntax: "wind <mph>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 1 ||
            !float.TryParse(invocation.Arguments[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float speed) ||
            !float.IsFinite(speed) || speed is < -40 or > 40)
            return new(CommandExecutionStatus.InvalidUsage, "command.wind.usage",
                [MessageDescriptor.Error("command.wind.usage").WithDefaultText("Usage: wind <speed between -40 and 40 mph>.")]);

        var patch = new WorldEnvironmentPatch(WindSpeed: speed / 50f);
        var result = await world.PatchAsync(new(invocation.WorldId, patch, invocation.Principal.Name), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? CommandExecutionResult.WithMessages(MessageDescriptor.Success("command.wind.applied", speed).WithDefaultText("Wind speed set to {0} mph."))
            : CommandExecutionResult.Failed(result.ErrorCode ?? "command.wind.unavailable",
                MessageDescriptor.Error("command.wind.unavailable").WithDefaultText("Unable to set world wind."));
    }
}

public sealed class WorldInfoCommand(IWorldEnvironmentService world) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("worldinfo", [], ["vega.world.info"], Syntax: "worldinfo");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 0) return new(CommandExecutionStatus.InvalidUsage, "worldinfo.usage");
        var snapshot = await world.CaptureAsync(invocation.WorldId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return CommandExecutionResult.Failed("worldinfo.unavailable");
        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Info("worldinfo.identity", snapshot.Name ?? snapshot.WorldId.Value, snapshot.WorldId.Value, snapshot.FileIdentity)
                .WithDefaultText("World: {0} | context: {1} | file identity: {2}"),
            MessageDescriptor.Info("worldinfo.size", snapshot.WidthTiles, snapshot.HeightTiles, snapshot.SpawnTileX, snapshot.SpawnTileY)
                .WithDefaultText("Size: {0}x{1}; spawn: ({2}, {3})"),
            MessageDescriptor.Info("worldinfo.mode", snapshot.Seed, snapshot.GameMode, snapshot.Hardmode)
                .WithDefaultText("Seed: {0}; mode: {1}; hardmode: {2}"));
    }
}

public sealed class WorldEventCommand(IWorldEnvironmentService world) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("worldevent", [], ["vega.world.events"],
        Syntax: "worldevent <rain|bloodmoon|eclipse|fullmoon|slimerain|sandstorm|pumpkinmoon|frostmoon|goblinarmy|snowlegion|pirateinvasion|martianmadness|meteor|lanterns|meteorshower|coinrain> [on|off] | worldevent invade <type> | worldevent rain <slime|coin>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        var args = invocation.Arguments;
        if (args.Count is < 1 or > 2) return Usage();
        string kind = args[0].ToLowerInvariant() switch { "full moon" => "fullmoon", "blood moon" => "bloodmoon", "lanterns" or "lanternsnight" => "lanternnight", var value => value };
        string? action = args.Count == 2 ? args[1].ToLowerInvariant() : null;
        if (kind is "invade" or "invasion")
        {
            kind = action switch
            {
                "goblin" or "goblins" => "goblinarmy", "snowman" or "snowmen" => "snowlegion",
                "pirate" or "pirates" => "pirateinvasion", "pumpkin" or "pumpkinmoon" => "pumpkinmoon",
                "frost" or "frostmoon" => "frostmoon", "martian" or "martians" => "martianmadness", _ => ""
            };
            action = "on";
        }
        else if (kind == "rain" && (action is "slime" or "coin"))
        {
            kind = action == "slime" ? "slimerain" : "coinrain";
            action = null;
        }
        NativeWorldEvent? native = kind switch
        {
            "slimerain" => NativeWorldEvent.SlimeRain, "sandstorm" => NativeWorldEvent.Sandstorm,
            "pumpkinmoon" => NativeWorldEvent.PumpkinMoon, "frostmoon" => NativeWorldEvent.FrostMoon,
            "goblinarmy" => NativeWorldEvent.GoblinArmy, "snowlegion" => NativeWorldEvent.SnowLegion,
            "pirateinvasion" => NativeWorldEvent.PirateInvasion, "martianmadness" => NativeWorldEvent.MartianMadness,
            "meteor" => NativeWorldEvent.Meteor, "lanternnight" => NativeWorldEvent.LanternNight,
            "meteorshower" => NativeWorldEvent.MeteorShower, "coinrain" => NativeWorldEvent.CoinRain, _ => null
        };
        if (native is null && kind is not ("rain" or "bloodmoon" or "eclipse" or "fullmoon")) return Usage();
        if (action is not (null or "on" or "off") || (kind is "fullmoon" or "meteor") && action == "off") return Usage();
        if (!invocation.Principal.HasPermission($"vega.world.events.{kind}")) return new(CommandExecutionStatus.Forbidden,
            "command.permission_denied", [MessageDescriptor.Error("command.permission_denied").WithDefaultText("Permission denied.")]);
        WorldEnvironmentSnapshot? state = await world.CaptureAsync(invocation.WorldId, cancellationToken).ConfigureAwait(false);
        if (state is null) return CommandExecutionResult.Failed("worldevent.unavailable");
        bool invasion = native is NativeWorldEvent.GoblinArmy or NativeWorldEvent.SnowLegion or NativeWorldEvent.PirateInvasion or NativeWorldEvent.MartianMadness;
        if (invasion && action == "off") return Usage();
        bool enabled = action == "on" || action is null && (invasion || !(kind switch
        {
            "rain" => state.Raining, "bloodmoon" => state.BloodMoon, "eclipse" => state.Eclipse,
            _ => native is { } e && state.NativeEvents.Contains(e)
        }));
        WorldEnvironmentPatch patch = native is { } nativeKind ? new(NativeEvent: new(nativeKind, enabled)) : kind switch
        {
            "rain" => new(Raining: enabled),
            "bloodmoon" => new(BloodMoon: enabled, IsDaytime: enabled && state.IsDaytime ? false : null,
                TimeTicks: enabled && state.IsDaytime ? 0 : null, Eclipse: enabled ? false : null),
            "eclipse" => new(Eclipse: enabled, IsDaytime: enabled && !state.IsDaytime ? true : null,
                TimeTicks: enabled && !state.IsDaytime ? 0 : null, BloodMoon: enabled ? false : null),
            _ => new(IsDaytime: false, TimeTicks: 0, MoonPhase: 0)
        };
        var result = await world.PatchAsync(new(invocation.WorldId, patch, invocation.Principal.Name), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? CommandExecutionResult.WithMessages(MessageDescriptor.Success("worldevent.applied", kind, enabled)
            .WithDefaultText("World event {0}: enabled={1}.")) : CommandExecutionResult.Failed(result.ErrorCode ?? "worldevent.failed");
    }

    private static CommandExecutionResult Usage() => new(CommandExecutionStatus.InvalidUsage, "worldevent.usage",
        [MessageDescriptor.Error("worldevent.usage").WithDefaultText("Use help worldevent. Invasions support start only.")]);
}
