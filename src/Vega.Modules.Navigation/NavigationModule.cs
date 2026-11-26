using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Modules;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Modules.Navigation;

/// <summary>Player navigation commands and per-session incoming teleport preferences.</summary>
public sealed class NavigationModule(
    ICommandRegistry registry,
    IPlayerDirectory players,
    IPlayerNavigation navigation,
    IPlayerSessionLifecycle sessions,
    IWorldEnvironmentService? environment = null,
    WarpStore? warps = null,
    IWorldNpcService? npcs = null,
    IPlayerMessenger? messenger = null) : IVegaModule
{
    private const string OthersPermission = "vega.navigation.others";
    private const string OverridePermission = "vega.navigation.override";
    private readonly HashSet<PlayerSessionId> blocked = [];
    private readonly List<IDisposable> registrations = [];

    public VegaModuleDescriptor Descriptor { get; } = new("Vega.Modules.Navigation", "1.0.0", ["Vega.Contracts"]);

    public void Start()
    {
        if (registrations.Count != 0) return;
        try
        {
            Register("spawn", "spawn", "vega.navigation.spawn", SpawnAsync);
            Register("home", "home", "vega.navigation.home", HomeAsync);
            Register("tp", "tp <player> | tp <source> <destination>", "vega.navigation.tp", TeleportAsync);
            Register("tphere", "tphere <player>", OthersPermission, TeleportHereAsync);
            Register("tppos", "tppos <tile-x> <tile-y>", "vega.navigation.tppos", TeleportPositionAsync);
            Register("pos", "pos [player]", "vega.navigation.pos", PositionAsync);
            Register("tpallow", "tpallow", "vega.navigation.allow", ToggleAsync);
            if (npcs is not null) Register("tpnpc", "tpnpc <npc-name|slot:number>", "vega.navigation.tpnpc", TeleportNpcAsync);
            if (environment is not null && warps is not null)
                registrations.Add(registry.Register(new Handler(new("warp", [], ["vega.navigation.warp"],
                    Syntax: "warp <name> | list [page] | add <name> | del <name> | hide <name> <true|false> | send <player> <name>"), WarpAsync)));
            sessions.Closed += OnClosed;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        sessions.Closed -= OnClosed;
        foreach (IDisposable registration in registrations) registration.Dispose();
        registrations.Clear();
        lock (blocked) blocked.Clear();
    }

    private void Register(string name, string syntax, string permission,
        Func<CommandInvocation, CancellationToken, ValueTask<CommandExecutionResult>> execute) =>
        registrations.Add(registry.Register(new Handler(new(name, [], [permission], AllowServer: false, Syntax: syntax), execute)));

    private async ValueTask<CommandExecutionResult> SpawnAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 0) return Usage("spawn");
        if (Self(invocation) is not { } player) return Missing();
        return Result(await navigation.ToWorldSpawnAsync(player, cancellationToken).ConfigureAwait(false), "navigation.spawn", "Teleported to the world spawn.");
    }

    private async ValueTask<CommandExecutionResult> HomeAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 0) return Usage("home");
        if (Self(invocation) is not { } player) return Missing();
        return Result(await navigation.ToHomeAsync(player, cancellationToken).ConfigureAwait(false), "navigation.home", "Teleported to your spawn point.");
    }

    private async ValueTask<CommandExecutionResult> TeleportNpcAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count == 0) return Usage("tpnpc <npc-name|slot:number>");
        if (Self(invocation) is not { } player) return Missing();
        string search = string.Join(' ', invocation.Arguments);
        var active = await npcs!.SnapshotAsync(invocation.WorldId, token).ConfigureAwait(false);
        var matches = search.StartsWith("slot:", StringComparison.OrdinalIgnoreCase) && int.TryParse(search[5..], out int slot)
            ? active.Where(n => n.Handle.Slot == slot).ToArray()
            : active.Where(n => string.Equals(n.Name, search, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) matches = active.Where(n => n.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) return Failure("navigation.npc_not_unique", "NPC not found or ambiguous; use a unique name or slot:number.");
        return Result(await navigation.ToPositionAsync(player, matches[0].Position, token).ConfigureAwait(false),
            "navigation.tpnpc", "Teleported to {0}.", matches[0].Name);
    }

    private async ValueTask<CommandExecutionResult> TeleportAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count is < 1 or > 2) return Usage("tp <player> | tp <source> <destination>");
        if (Self(invocation) is not { } actor) return Missing();
        if (invocation.Arguments.Count == 2 && !invocation.Principal.HasPermission(OthersPermission)) return Forbidden();
        PlayerSnapshot source = actor;
        if (invocation.Arguments.Count == 2)
        {
            if (!TryResolve(invocation.Arguments[0], invocation.WorldId, out var resolved, out var error)) return error;
            source = resolved!;
        }
        if (!TryResolve(invocation.Arguments[^1], invocation.WorldId, out var target, out var targetError)) return targetError;
        return await MoveAsync(invocation, actor, source, target!, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CommandExecutionResult> TeleportHereAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 0) return Usage("tphere <player>");
        if (Self(invocation) is not { } actor) return Missing();
        if (!TryResolve(string.Join(' ', invocation.Arguments), invocation.WorldId, out var source, out var error)) return error;
        return await MoveAsync(invocation, actor, source!, actor, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CommandExecutionResult> MoveAsync(CommandInvocation invocation, PlayerSnapshot actor,
        PlayerSnapshot source, PlayerSnapshot target, CancellationToken cancellationToken)
    {
        if (source.Id == target.Id) return Failure("navigation.same_player", "Source and destination must be different players.");
        if (!invocation.Principal.HasPermission(OverridePermission) &&
            ((source.Id != actor.Id && IsBlocked(source)) || (target.Id != actor.Id && IsBlocked(target))))
            return Failure("navigation.teleport_disabled", "The player has disabled incoming teleports.");
        return Result(await navigation.ToPlayerAsync(source, target, cancellationToken).ConfigureAwait(false),
            "navigation.teleported", "Teleported {0} to {1}.", source.Name, target.Name);
    }

    private async ValueTask<CommandExecutionResult> TeleportPositionAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 2 ||
            !int.TryParse(invocation.Arguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(invocation.Arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out int y))
            return Usage("tppos <tile-x> <tile-y>");
        if (Self(invocation) is not { } player) return Missing();
        return Result(await navigation.ToPositionAsync(player, new(x * 16f, y * 16f), cancellationToken).ConfigureAwait(false),
            "navigation.teleported_position", "Teleported to tile ({0}, {1}).", x, y);
    }

    private ValueTask<CommandExecutionResult> PositionAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        PlayerSnapshot? player = Self(invocation);
        if (player is null) return ValueTask.FromResult(Missing());
        if (invocation.Arguments.Count != 0 && !TryResolve(string.Join(' ', invocation.Arguments), invocation.WorldId, out player, out var error))
            return ValueTask.FromResult(error);
        return ValueTask.FromResult(player is null ? Missing() : CommandExecutionResult.WithMessages(
            MessageDescriptor.Info("navigation.position", player.Name, (int)(player.Position.X / 16f), (int)(player.Position.Y / 16f))
                .WithDefaultText("{0} is at tile ({1}, {2}).")));
    }

    private ValueTask<CommandExecutionResult> ToggleAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 0) return ValueTask.FromResult(Usage("tpallow"));
        if (Self(invocation)?.SessionId is not { } session) return ValueTask.FromResult(Missing());
        bool allowed;
        lock (blocked)
        {
            allowed = blocked.Remove(session);
            if (!allowed) blocked.Add(session);
        }
        return ValueTask.FromResult(CommandExecutionResult.WithMessages(MessageDescriptor.Success(
            allowed ? "navigation.teleports_allowed" : "navigation.teleports_blocked")
            .WithDefaultText(allowed ? "Incoming teleports enabled." : "Incoming teleports disabled.")));
    }

    private async ValueTask<CommandExecutionResult> WarpAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count == 0) return Usage("warp <name> | list | add | del | hide | send");
        var args = invocation.Arguments;
        string action = args[0].ToLowerInvariant();
        bool manage = invocation.Principal.HasPermission("vega.navigation.warp.manage");
        if (action is "add" or "del" or "hide" && !manage) return new(CommandExecutionStatus.Forbidden, "command.permission_denied",
            [MessageDescriptor.Error("command.permission_denied").WithDefaultText("You do not have permission to manage warps.")]);
        if (action == "send" && !invocation.Principal.HasPermission(OthersPermission)) return Forbidden();
        var actor = Self(invocation);
        if (!invocation.Principal.IsServer && actor is null) return Missing();
        if (action == "add" && actor is null) return Missing();
        try
        {
            var snapshot = await environment!.CaptureAsync(invocation.WorldId, token).ConfigureAwait(false);
            if (snapshot?.FileIdentity is not { } world || world == Guid.Empty)
                return Failure("navigation.world_unavailable", "The world has no persistent identity yet.");
            switch (action)
            {
                case "list":
                    int page = 1;
                    if (args.Count > 2 || (args.Count == 2 && (!int.TryParse(args[1], out page) || page < 1))) return Usage("warp list [page]");
                    var visible = (await warps!.ReadAsync(world, token).ConfigureAwait(false)).Where(p => !p.Hidden)
                        .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                    int pages = Math.Max(1, (visible.Length + 19) / 20);
                    if (page > pages) return Failure("navigation.warp_page", "There are only {0} pages.", pages);
                    return CommandExecutionResult.WithMessages(MessageDescriptor.Info("navigation.warp_list", page, pages,
                        string.Join(", ", visible.Skip((page - 1) * 20).Take(20).Select(p => p.Name))).WithDefaultText("Warps ({0}/{1}): {2}"));
                case "add":
                    if (args.Count != 2 || !WarpStore.ValidName(args[1])) return Usage("warp add <name>");
                    if (actor!.IsDead) return Failure("navigation.dead", "Dead players cannot create warps.");
                    return WarpMutation(await warps!.AddAsync(world, new(args[1], actor.Position), token).ConfigureAwait(false), args[1]);
                case "del":
                    if (args.Count != 2) return Usage("warp del <name>");
                    return WarpMutation(await warps!.RemoveAsync(world, args[1], token).ConfigureAwait(false), args[1]);
                case "hide":
                    if (args.Count != 3 || !bool.TryParse(args[2], out bool hidden)) return Usage("warp hide <name> <true|false>");
                    return WarpMutation(await warps!.SetHiddenAsync(world, args[1], hidden, token).ConfigureAwait(false), args[1]);
                default:
                    PlayerSnapshot? source = actor;
                    string name = string.Join(' ', args);
                    if (action == "send")
                    {
                        if (args.Count != 3) return Usage("warp send <player> <name>");
                        if (!TryResolve(args[1], invocation.WorldId, out source, out var error)) return error;
                        if (!invocation.Principal.HasPermission(OverridePermission) && IsBlocked(source!))
                            return Failure("navigation.teleport_disabled", "The player has disabled incoming teleports.");
                        name = args[2];
                    }
                    if (source is null) return Missing();
                    var point = (await warps!.ReadAsync(world, token).ConfigureAwait(false)).FirstOrDefault(p =>
                        string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (point is null) return Failure("navigation.warp_not_found", "Warp '{0}' was not found.", name);
                    if (action == "send" && !invocation.Principal.HasPermission(OverridePermission) && IsBlocked(source))
                        return Failure("navigation.teleport_disabled", "The player has disabled incoming teleports.");
                    var moved = await navigation.ToPositionAsync(source, point.Position, token).ConfigureAwait(false);
                    var result = Result(moved, "navigation.warped", "Teleported {0} to warp '{1}'.", source.Name, point.Name);
                    if (moved.IsSuccess && action == "send" && source.SessionId != actor?.SessionId && messenger is not null)
                    {
                        // A successful teleport remains successful if the target leaves before notification.
                        // Use the exact snapshot overload, never a bare reusable player slot.
                        if (!token.IsCancellationRequested && players.TryGet(source.Id, out var current) &&
                            current is { SessionId: { } currentSession } && currentSession == source.SessionId &&
                            current.WorldId == invocation.WorldId)
                        {
                            try
                            {
                                await messenger.SendMessageAsync(source, $"{invocation.Principal.Name} sent you to warp '{point.Name}'.", token)
                                    .ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                        }
                    }
                    return result;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            return Failure("navigation.warp_storage", "Warp storage is unavailable; no changes were applied.");
        }
    }

    private static CommandExecutionResult WarpMutation(bool applied, string name) => applied
        ? CommandExecutionResult.WithMessages(MessageDescriptor.Success("navigation.warp_updated", name).WithDefaultText("Warp '{0}' updated."))
        : Failure("navigation.warp_conflict", "Warp '{0}' could not be changed (missing, duplicate or storage limit).", name);

    private PlayerSnapshot? Self(CommandInvocation invocation) => invocation.Principal is IPlayerCommandPrincipal { SessionId: { } session } principal &&
        players.TryGet(principal.PlayerId, out var player) && player?.SessionId == session && player.WorldId == invocation.WorldId
        ? player : null;

    private bool IsBlocked(PlayerSnapshot player)
    {
        lock (blocked) return player.SessionId is { } session && blocked.Contains(session);
    }

    private void OnClosed(PlayerSessionClosed session)
    {
        lock (blocked) blocked.Remove(session.SessionId);
    }

    private bool TryResolve(string search, WorldId world, out PlayerSnapshot? player, out CommandExecutionResult error)
    {
        player = null;
        error = Missing();
        if (string.IsNullOrWhiteSpace(search) || search == "*") return false;
        var online = players.SnapshotOnlinePlayers().Where(p => p.WorldId == world && p.SessionId is not null).ToArray();
        var exact = online.Where(p => string.Equals(p.Name, search, StringComparison.OrdinalIgnoreCase)).ToArray();
        bool hasSlot = int.TryParse(search, NumberStyles.None, CultureInfo.InvariantCulture, out int slot);
        var matches = exact.Length > 0 ? exact : online.Where(p =>
            p.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase) ||
            hasSlot && p.Id.Slot == slot).ToArray();
        if (matches.Length == 1) { player = matches[0]; return true; }
        if (matches.Length > 1) error = Failure("navigation.ambiguous", "Multiple players match: {0}.", string.Join(", ", matches.Select(p => p.Name)));
        return false;
    }

    private static CommandExecutionResult Result(PlayerOperationResult result, string key, string text, params object?[] args) => result.IsSuccess
        ? CommandExecutionResult.WithMessages(MessageDescriptor.Success(key, args).WithDefaultText(text))
        : Failure(result.ErrorCode ?? "navigation.failed", "Teleport failed: player or destination unavailable.");

    private static CommandExecutionResult Missing() => Failure("navigation.player_not_found", "Player not found in this world.");
    private static CommandExecutionResult Failure(string code, string text, params object?[] args) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code, args).WithDefaultText(text));
    private static CommandExecutionResult Forbidden() => new(CommandExecutionStatus.Forbidden, "command.permission_denied",
        [MessageDescriptor.Error("command.permission_denied").WithDefaultText("You do not have permission to teleport other players.")]);
    private static CommandExecutionResult Usage(string syntax) => new(CommandExecutionStatus.InvalidUsage, "navigation.usage",
        [MessageDescriptor.Error("navigation.usage", syntax).WithDefaultText("Usage: {0}.")]);

    private sealed class Handler(CommandDescriptor descriptor, Func<CommandInvocation, CancellationToken, ValueTask<CommandExecutionResult>> execute) : ICommandHandler
    {
        public CommandDescriptor Descriptor => descriptor;
        public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken) => execute(invocation, cancellationToken);
    }
}
