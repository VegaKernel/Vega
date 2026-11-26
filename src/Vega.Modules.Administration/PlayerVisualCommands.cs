using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Players;

namespace Vega.Modules.Administration;

public sealed class AnnoyCommand(IPlayerDirectory players, IPlayerVisualOperations visuals) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("annoy", [], ["vega.players.annoy"], Syntax: "annoy <player> <seconds:1..30>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count != 2 || !int.TryParse(invocation.Arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds is < 1 or > 30)
            return new(CommandExecutionStatus.InvalidUsage, "annoy.usage");
        var target = VitalTargets.Find(players, invocation, invocation.Arguments[0]);
        if (target is null) return CommandExecutionResult.Failed("annoy.player_not_unique");
        return VitalTargets.Result(await visuals.AnnoyAsync(target, seconds, token).ConfigureAwait(false), "annoy.requested",
            "Requested a cosmetic sound effect for {0} for {1} seconds.", target.Name, seconds);
    }
}

public sealed class FireworkCommand(IPlayerDirectory players, IPlayerVisualOperations visuals) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("firework", [], ["vega.players.firework"],
        Syntax: "firework <player> [red|green|blue|yellow|star|spiral|rings|flower]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count is < 1 or > 2) return new(CommandExecutionStatus.InvalidUsage, "firework.usage");
        FireworkStyle? style = invocation.Arguments.Count == 1 ? FireworkStyle.Red : invocation.Arguments[1].ToLowerInvariant() switch
        {
            "red" or "r" => FireworkStyle.Red, "green" or "g" => FireworkStyle.Green,
            "blue" or "b" => FireworkStyle.Blue, "yellow" or "y" => FireworkStyle.Yellow,
            "star" or "r2" => FireworkStyle.Star, "spiral" or "g2" => FireworkStyle.Spiral,
            "rings" or "b2" => FireworkStyle.Rings, "flower" or "y2" => FireworkStyle.Flower, _ => null
        };
        if (style is null) return new(CommandExecutionStatus.InvalidUsage, "firework.usage");
        var target = VitalTargets.Find(players, invocation, invocation.Arguments[0]);
        if (target is null) return CommandExecutionResult.Failed("firework.player_not_unique");
        return VitalTargets.Result(await visuals.FireworkAsync(target, style.Value, token).ConfigureAwait(false), "firework.requested",
            "Launched a {0} firework near {1}.", style.Value, target.Name);
    }
}

public sealed class GodModeCommand(IPlayerDirectory players, IPlayerGodModeOperations power) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("godmode", ["god"], ["vega.players.godmode"],
        Syntax: "godmode [player] [on|off]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        var args = invocation.Arguments;
        bool? enabled = args.Count > 0 ? args[^1].ToLowerInvariant() switch { "on" => true, "off" => false, _ => null } : null;
        int nameParts = args.Count - (enabled.HasValue ? 1 : 0);
        bool others = nameParts > 0;
        if (others && !invocation.Principal.HasPermission("vega.players.godmode.others"))
            return new(CommandExecutionStatus.Forbidden, "godmode.others_permission");
        var target = others ? VitalTargets.Find(players, invocation, string.Join(' ', args.Take(nameParts))) : VitalTargets.Self(players, invocation);
        if (target is null) return CommandExecutionResult.Failed("godmode.player_not_unique");
        var result = await power.SetGodModeAsync(target, enabled, token).ConfigureAwait(false);
        return VitalTargets.Result(result.Result, "godmode.requested", "Native godmode for {0}: {1}.", target.Name, result.Enabled);
    }
}

/// <summary>The vanilla owner-controlled velocity sync requires native SSC, not merely disk persistence.</summary>
public sealed class RocketCommand(IPlayerDirectory players, Func<PlayerSnapshot, CancellationToken, ValueTask<PlayerOperationResult>> rocket) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("rocket", [], ["vega.players.rocket"], Syntax: "rocket <player>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count == 0) return new(CommandExecutionStatus.InvalidUsage, "rocket.usage");
        var target = VitalTargets.Find(players, invocation, string.Join(' ', invocation.Arguments));
        if (target is null || target.IsDead) return CommandExecutionResult.Failed("rocket.player_unavailable");
        var result = await rocket(target, token).ConfigureAwait(false);
        if (result.ErrorCode == "rocket.native_ssc_required")
            return CommandExecutionResult.Failed(result.ErrorCode, Vega.Contracts.Messaging.MessageDescriptor.Error(result.ErrorCode)
                .WithDefaultText("Rocket requires native server-side characters; character checkpoint persistence alone is not enough."));
        return VitalTargets.Result(result, "rocket.requested", "Requested a native velocity launch for {0}.", target.Name);
    }
}
