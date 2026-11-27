using System.Data.Common;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Modules.Navigation;

public enum HomeCommandAction { Set, Delete, Teleport }

/// <summary>Original Homes commands, separate from vanilla bed/spawn /home.</summary>
public sealed class HomeCommand(HomeCommandAction action, IPlayerDirectory players, IPlayerNavigation navigation,
    IWorldEnvironmentService environment, IPlayerAccountSessionService accounts, IAuthorizationService authorization,
    IPlayerHomeStore homes) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = action switch
    {
        HomeCommandAction.Set => new("sethome", [], ["homes.set", "vega.navigation.homes.set"], AllowServer: false, Syntax: "sethome [home name]"),
        HomeCommandAction.Delete => new("delhome", [], ["homes.delete", "vega.navigation.homes.delete"], AllowServer: false, Syntax: "delhome [home name]"),
        _ => new("myhome", [], ["homes.tp", "vega.navigation.homes.tp"], AllowServer: false, Syntax: "myhome [home name|-l]")
    };

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count > 1) return Usage();
        string name = invocation.Arguments.Count == 0 ? "home" : invocation.Arguments[0];
        bool list = action == HomeCommandAction.Teleport && name.Equals("-l", StringComparison.OrdinalIgnoreCase);
        if (!list && !PlayerHome.IsValidName(name)) return Usage();
        if (ResolveAuthenticatedPlayer(invocation) is not { } authenticated)
            return Failure("homes.login", "You're not logged in.");
        var (principal, player, binding, session) = authenticated;

        try
        {
            var world = await environment.CaptureAsync(invocation.WorldId, token).ConfigureAwait(false);
            if (world?.FileIdentity is not { } worldFile || worldFile == Guid.Empty)
                return Failure("homes.world", "The world has no persistent identity yet.");
            // Capture the current position only while the original authentication tenure is valid.
            if (!accounts.TryRunWithBinding(binding, () => players.TryGet(player.Id, out var current) && current?.SessionId == session && current.WorldId == invocation.WorldId ? current : null, out var live) || live is null)
                return Failure("homes.login", "You're not logged in.");
            if (!Descriptor.Permissions.Any(principal.HasPermission)) return new(CommandExecutionStatus.Forbidden, "command.permission_denied");

            if (list)
            {
                var points = await Task.Run(async () => await homes.ListAsync(binding.AccountId, invocation.WorldId, worldFile, token).ConfigureAwait(false), token).ConfigureAwait(false);
                if (!accounts.TryRunWithBinding(binding, () => true, out _)) return Failure("homes.login", "You're not logged in.");
                return CommandExecutionResult.WithMessages(points.Count == 0
                    ? MessageDescriptor.Info("homes.list.empty").WithDefaultText("You have no homes set.")
                    : MessageDescriptor.Info("homes.list", string.Join(", ", points.Select(home => home.Name))).WithDefaultText("List of homes: {0}"));
            }

            if (action == HomeCommandAction.Set)
                return await SetHomeAsync(invocation, principal, binding, live, world, worldFile, name, token).ConfigureAwait(false);
            if (action == HomeCommandAction.Delete)
                return await Task.Run(async () => await homes.DeleteAsync(binding.AccountId, invocation.WorldId, worldFile, name, token).ConfigureAwait(false), token).ConfigureAwait(false)
                    ? Success("homes.deleted", "Deleted your home '{0}'.", name) : Missing(name);

            var point = await Task.Run(async () => await homes.FindAsync(binding.AccountId, invocation.WorldId, worldFile, name, token).ConfigureAwait(false), token).ConfigureAwait(false);
            if (point is null) return Missing(name);
            var moved = await navigation.ToAuthenticatedPositionAsync(live, point.Position, worldFile, accounts, binding, token).ConfigureAwait(false);
            return moved.IsSuccess ? Success("homes.teleported", "Teleported you to your home '{0}'.", name)
                : Failure(moved.ErrorCode ?? "homes.teleport", "Teleport failed: player or destination unavailable.");
        }
        catch (DbException)
        {
            return Failure("homes.storage", "Home storage is unavailable.");
        }
    }

    private (IPlayerCommandPrincipal Principal, PlayerSnapshot Player, PlayerAccountBinding Binding, PlayerSessionId Session)?
        ResolveAuthenticatedPlayer(CommandInvocation invocation)
    {
        if (invocation.Principal is not IPlayerCommandPrincipal { SessionId: { } session, AccountName: { } accountName } principal ||
            !players.TryGet(principal.PlayerId, out var player) || player is null || player.SessionId != session || player.WorldId != invocation.WorldId ||
            !accounts.TryGetBinding(player, out var binding) || !accounts.TryRunWithBinding(binding,
                () => accounts.TryGetAccount(player.Id, out var account) && account?.Id == binding.AccountId && account.Name.Equals(accountName, StringComparison.OrdinalIgnoreCase), out bool bound) || !bound)
            return null;

        return (principal, player, binding, session);
    }

    private async ValueTask<CommandExecutionResult> SetHomeAsync(CommandInvocation invocation, IPlayerCommandPrincipal principal,
        PlayerAccountBinding binding, PlayerSnapshot live, WorldEnvironmentSnapshot world, Guid worldFile, string name, CancellationToken token)
    {
        if (live.IsDead || !float.IsFinite(live.Position.X) || !float.IsFinite(live.Position.Y) || live.Position.X < 0 || live.Position.Y < 0 ||
            live.Position.X >= world.WidthTiles * 16f || live.Position.Y >= world.HeightTiles * 16f)
            return Failure("homes.invalid_position", "Cannot set home at this position.");
        int limit = DynamicPermissionLimit.Resolve("homes.set", authorization.SnapshotGroups().SelectMany(group => group.Permissions),
            principal.HasPermission, principal.HasPermission("vega.navigation.homes.set") ? 1 : 0);
        var result = await Task.Run(async () => await homes.SetAsync(new(binding.AccountId, invocation.WorldId, worldFile, name, live.Position), limit, token).ConfigureAwait(false), token).ConfigureAwait(false);
        return result switch
        {
            HomeWriteResult.Applied => Success("homes.set", "Set your home '{0}'.", name),
            HomeWriteResult.LimitReached => Failure("homes.limit", "You have reached your home limit!"),
            _ => Failure("homes.storage", "Could not set home, check logs for more details.")
        };
    }

    private CommandExecutionResult Usage() => new(CommandExecutionStatus.InvalidUsage, "homes.usage",
        [MessageDescriptor.Error("homes.usage", $"/{Descriptor.Name} <home name>").WithDefaultText("Invalid syntax! Proper syntax: {0}")]);
    private static CommandExecutionResult Missing(string name) => Failure("homes.not_found", "Home '{0}' not found!", name);
    private static CommandExecutionResult Success(string key, string text, params object?[] args) =>
        CommandExecutionResult.WithMessages(MessageDescriptor.Success(key, args).WithDefaultText(text));
    private static CommandExecutionResult Failure(string key, string text, params object?[] args) =>
        CommandExecutionResult.Failed(key, MessageDescriptor.Error(key, args).WithDefaultText(text));
}
