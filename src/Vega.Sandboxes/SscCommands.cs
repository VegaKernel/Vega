using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;

namespace Vega.Sandboxes;

/// <summary>Explicit checkpoint of all initialized authenticated characters in the current world.</summary>
public sealed class SaveSscCommand(SandboxSscSessionCoordinator coordinator) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("savessc", [], ["vega.ssc.save"], Syntax: "savessc");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 0) return new(CommandExecutionStatus.InvalidUsage, "ssc.save_usage");
        var authorized = coordinator.AuthorizeCommand(invocation, "vega.ssc.save");
        if (authorized is null) return new(CommandExecutionStatus.Forbidden, "ssc.command_identity_changed");
        var result = await coordinator.SaveAllAsync(authorized, cancellationToken).ConfigureAwait(false);
        return result.Failed == 0
            ? CommandExecutionResult.WithMessages(MessageDescriptor.Success("ssc.saved_all", result.Saved).WithDefaultText("Saved {0} authenticated characters."))
            : CommandExecutionResult.Failed("ssc.partial_save", MessageDescriptor.Error("ssc.partial_save", result.Saved, result.Failed)
                .WithDefaultText("Saved {0} authenticated characters; {1} could not be saved."));
    }
}

/// <summary>Explicitly saves current authenticated state of one player. Unlike TShock's failed-SSC-login branch,
/// this requires a successful login and an initialized, current persistence revision.</summary>
public sealed class OverrideSscCommand(SandboxSscSessionCoordinator coordinator, IPlayerDirectory players) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("overridessc", ["ossc"], ["vega.ssc.override"], Syntax: "overridessc <authenticated-player>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 0) return new(CommandExecutionStatus.InvalidUsage, "ssc.override_usage");
        var authorized = coordinator.AuthorizeCommand(invocation, "vega.ssc.override");
        if (authorized is null) return new(CommandExecutionStatus.Forbidden, "ssc.command_identity_changed");
        var target = SscTargets.Find(players, invocation, string.Join(' ', invocation.Arguments));
        if (target is null) return CommandExecutionResult.Failed("ssc.player_not_unique");
        var result = await coordinator.SaveCurrentAsync(target, authorized, cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? CommandExecutionResult.WithMessages(MessageDescriptor.Success("ssc.override_current", target.Name).WithDefaultText("Saved current authenticated character state of {0}."))
            : CommandExecutionResult.Failed(result.ErrorCode ?? "ssc.save_failed");
    }
}

/// <summary>Imports the native first-entry client snapshot only after explicit confirmation and authentication.</summary>
public sealed class UploadSscCommand(SandboxSscSessionCoordinator coordinator, IPlayerDirectory players) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("uploadssc", [], ["vega.ssc.upload"], Syntax: "uploadssc [player] confirm");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        var args = invocation.Arguments;
        if (args.Count == 0 || !args[^1].Equals("confirm", StringComparison.OrdinalIgnoreCase))
            return new(CommandExecutionStatus.InvalidUsage, "ssc.upload_confirmation_required",
                [MessageDescriptor.Warning("ssc.upload_confirmation_required").WithDefaultText("Usage: uploadssc [player] confirm. This replaces the persisted character with their initial client join data.")]);
        bool others = args.Count > 1;
        var authorized = others ? coordinator.AuthorizeCommand(invocation, "vega.ssc.upload", "vega.ssc.upload.others")
            : coordinator.AuthorizeCommand(invocation, "vega.ssc.upload");
        if (authorized is null) return new(CommandExecutionStatus.Forbidden, "ssc.upload_permission");
        var target = others ? SscTargets.Find(players, invocation, string.Join(' ', args.Take(args.Count - 1))) : SscTargets.Self(players, invocation);
        if (target is null) return CommandExecutionResult.Failed("ssc.player_not_unique");
        var result = await coordinator.UploadJoinAsync(target, authorized, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
            return CommandExecutionResult.WithMessages(MessageDescriptor.Success("ssc.upload_applied", target.Name).WithDefaultText("Imported and applied initial join character data for {0}."));
        return result.ErrorCode == "ssc.import_saved_apply_failed"
            ? CommandExecutionResult.Failed(result.ErrorCode, MessageDescriptor.Warning(result.ErrorCode).WithDefaultText("The import was persisted, but live application did not complete. Reconnect to load the saved character."))
            : CommandExecutionResult.Failed(result.ErrorCode ?? "ssc.import_failed");
    }
}

internal static class SscTargets
{
    public static PlayerSnapshot? Self(IPlayerDirectory players, CommandInvocation invocation) =>
        invocation.Principal is IPlayerCommandPrincipal { SessionId: { } session } principal &&
        players.TryGet(principal.PlayerId, out var player) && player?.SessionId == session && player.WorldId == invocation.WorldId ? player : null;

    public static PlayerSnapshot? Find(IPlayerDirectory players, CommandInvocation invocation, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var online = players.SnapshotOnlinePlayers().Where(player => player.WorldId == invocation.WorldId && player.SessionId.HasValue).ToArray();
        var matches = online.Where(player => player.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0 && int.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out int slot))
            matches = online.Where(player => player.Id.Slot == slot).ToArray();
        if (matches.Length == 0) matches = online.Where(player => player.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
