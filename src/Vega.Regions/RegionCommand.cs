using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Regions;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Regions;

public sealed class RegionCommand(IRegionService regions, IPlayerDirectory? players = null,
    IPlayerNavigation? navigation = null, IAccountService? accounts = null, IAuthorizationService? authorization = null,
    IWorldEnvironmentService? environment = null, IPlayerAccountSessionService? accountSessions = null) : ICommandHandler
{
    private const int PageSize = 20;
    private const int MaximumSelections = 255;
    private static readonly TimeSpan SelectionLifetime = TimeSpan.FromMinutes(10);
    private readonly Dictionary<PlayerSessionId, Selection> selections = [];
    private readonly HashSet<PlayerSessionId> defining = [];
    private readonly object selectionSync = new();
    private sealed record Selection(WorldId World, WorldTilePosition? First, WorldTilePosition? Second, DateTimeOffset Expires);
    private const string Syntax = "region list [page] | show|info <name> | create <name> <x> <y> <width> <height> [priority] [owner] | delete <name> | rename <name> <new-name> | resize <name> <x> <y> <width> <height> | resize|expand <name> <u/d/l/r> <amount> | priority|z <name> <value> | owner <name> <owner|none> | member|group <name> add|remove <subject> | allow|remove|allowg|removeg <subject> <region> | flag <name> <flag> <true|false> | protect <region> <true|false> | set <1/2> [x y] | clear | define <name> | name [x y] | tp <name>";
    public CommandDescriptor Descriptor { get; } = new(
        "region",
        ["regions"],
        ["vega.regions.manage"],
        Syntax: Syntax);

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 0 || invocation.Arguments[0].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            return Usage();
        }

        string action = invocation.Arguments[0].ToLowerInvariant();
        return action switch
        {
            "list" => List(invocation),
            "show" or "info" => Show(invocation),
            "create" => await CreateAsync(invocation, cancellationToken).ConfigureAwait(false),
            "delete" => await DeleteAsync(invocation, cancellationToken).ConfigureAwait(false),
            "rename" => await RenameAsync(invocation, cancellationToken).ConfigureAwait(false),
            "resize" when invocation.Arguments.Count == 4 => await DirectionalResizeAsync(invocation, cancellationToken).ConfigureAwait(false),
            "expand" => await DirectionalResizeAsync(invocation, cancellationToken).ConfigureAwait(false),
            "resize" => await ResizeAsync(invocation, cancellationToken).ConfigureAwait(false),
            "priority" or "z" => await PriorityAsync(invocation, cancellationToken).ConfigureAwait(false),
            "owner" => await OwnerAsync(invocation, cancellationToken).ConfigureAwait(false),
            "member" => await AccessAsync(invocation, RegionAccessKind.Member, cancellationToken).ConfigureAwait(false),
            "group" => await AccessAsync(invocation, RegionAccessKind.Group, cancellationToken).ConfigureAwait(false),
            "flag" => await FlagAsync(invocation, cancellationToken).ConfigureAwait(false),
            "protect" => await ProtectAsync(invocation, cancellationToken).ConfigureAwait(false),
            "allow" or "remove" => await CompatibleAccessAsync(invocation, RegionAccessKind.Member, action == "allow", cancellationToken).ConfigureAwait(false),
            "allowg" or "removeg" => await CompatibleAccessAsync(invocation, RegionAccessKind.Group, action == "allowg", cancellationToken).ConfigureAwait(false),
            "tp" => await TeleportAsync(invocation, cancellationToken).ConfigureAwait(false),
            "set" => await SetPointAsync(invocation, cancellationToken).ConfigureAwait(false),
            "clear" => ClearPoints(invocation),
            "define" => await DefineAsync(invocation, cancellationToken).ConfigureAwait(false),
            "name" => await NameAtAsync(invocation, cancellationToken).ConfigureAwait(false),
            _ => Usage()
        };
    }

    private CommandExecutionResult List(CommandInvocation invocation)
    {
        if (invocation.Arguments.Count > 2 || (invocation.Arguments.Count == 2 && (!TryInt(invocation.Arguments[1], out int parsed) || parsed < 1))) return Usage();
        int page = invocation.Arguments.Count == 2 ? int.Parse(invocation.Arguments[1], CultureInfo.InvariantCulture) : 1;
        var all = regions.SnapshotRegions(invocation.WorldId);
        int pages = Math.Max(1, (int)(((long)all.Count + PageSize - 1) / PageSize));
        if (page > pages) return Error("command.region.page-out-of-range", "Region page is out of range.");
        RegionSnapshot[] snapshot = all.Skip((page - 1) * PageSize).Take(PageSize).ToArray();
        return snapshot.Length == 0
            ? Info("command.region.list-empty", "There are no regions in this world.")
            : CommandExecutionResult.WithMessages(new[] { MessageDescriptor.Info("command.region.list-page", page, pages).WithDefaultText("Regions ({0}/{1}):") }.Concat(snapshot.Select(region =>
                MessageDescriptor.Info(
                        "command.region.list-entry",
                        region.Id,
                        region.Name,
                        region.X,
                        region.Y,
                        region.Width,
                        region.Height,
                        region.Priority,
                        region.Revision)
                    .WithDefaultText("{0} | {1} | ({2},{3}) {4}x{5} | priority {6} | revision {7}")))
                .ToArray());
    }

    private CommandExecutionResult Show(CommandInvocation invocation)
    {
        if (invocation.Arguments.Count != 2) return Usage();
        RegionSnapshot? region = Find(invocation);
        return region is null
            ? NotFound()
            : CommandExecutionResult.WithMessages(
                MessageDescriptor.Info(
                        "command.region.details",
                        region.Name,
                        region.WorldId.Value,
                        region.Owner ?? "none",
                        region.Priority,
                        Join(region.Members),
                        Join(region.Groups),
                        region.Flags.Count == 0
                            ? "none"
                            : string.Join(", ", region.Flags.Select(static pair => $"{pair.Key}={pair.Value}")),
                        region.Revision)
                    .WithDefaultText("{0} | world {1} | owner {2} | priority {3} | members {4} | groups {5} | flags {6} | revision {7}"),
                MessageDescriptor.Info("command.region.geometry", region.X, region.Y, region.Width, region.Height,
                    !region.Flags.TryGetValue("protected", out bool value) || value).WithDefaultText("X: {0}; Y: {1}; W: {2}; H: {3}; protected: {4}"));
    }

    private async ValueTask<CommandExecutionResult> CreateAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count is < 6 or > 8 || !ValidName(invocation.Arguments[1]) ||
            !TryInt(invocation.Arguments[2], out int x) ||
            !TryInt(invocation.Arguments[3], out int y) ||
            !TryInt(invocation.Arguments[4], out int width) ||
            !TryInt(invocation.Arguments[5], out int height) ||
            (invocation.Arguments.Count >= 7 && !TryInt(invocation.Arguments[6], out _)))
        {
            return Usage();
        }

        int priority = invocation.Arguments.Count >= 7
            ? int.Parse(invocation.Arguments[6], CultureInfo.InvariantCulture)
            : 0;
        string? owner = invocation.Arguments.Count == 8 ? invocation.Arguments[7] : null;
        if (!await ValidBoundsAsync(invocation.WorldId, x, y, width, height, cancellationToken).ConfigureAwait(false)) return InvalidBounds();
        return Mutation(await regions.CreateAsync(
            invocation.WorldId,
            invocation.Arguments[1],
            x,
            y,
            width,
            height,
            priority,
            owner,
            invocation.Principal.Name,
            cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<CommandExecutionResult> DeleteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count < 2) return Usage();
        RegionSnapshot? before = FindByName(invocation.WorldId, string.Join(' ', invocation.Arguments.Skip(1)));
        return before is null
            ? NotFound()
            : Mutation(await regions.DeleteAsync(
                invocation.WorldId,
                before.Name,
                before.Revision,
                invocation.Principal.Name,
                cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<CommandExecutionResult> ResizeAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 6 ||
            !TryInt(invocation.Arguments[2], out int x) ||
            !TryInt(invocation.Arguments[3], out int y) ||
            !TryInt(invocation.Arguments[4], out int width) ||
            !TryInt(invocation.Arguments[5], out int height))
        {
            return Usage();
        }

        if (!await ValidBoundsAsync(invocation.WorldId, x, y, width, height, cancellationToken).ConfigureAwait(false)) return InvalidBounds();
        return await UpdateAsync(
            invocation,
            new RegionUpdate(X: x, Y: y, Width: width, Height: height),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CommandExecutionResult> DirectionalResizeAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count != 4 || !TryInt(invocation.Arguments[3], out int amount) || amount == 0) return Usage();
        if (Find(invocation) is not { } before) return NotFound();
        long x = before.X, y = before.Y, width = before.Width, height = before.Height;
        switch (invocation.Arguments[2].ToLowerInvariant())
        {
            case "u": case "up": y -= amount; height += amount; break;
            case "d": case "down": height += amount; break;
            case "l": case "left": x -= amount; width += amount; break;
            case "r": case "right": width += amount; break;
            default: return Usage();
        }
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > int.MaxValue || y + height > int.MaxValue ||
            !await ValidBoundsAsync(invocation.WorldId, (int)x, (int)y, (int)width, (int)height, token).ConfigureAwait(false)) return InvalidBounds();
        // Use the revision of the snapshot used to calculate the rectangle, not a second lookup.
        return Mutation(await regions.UpdateAsync(invocation.WorldId, before.Name, new((int)x, (int)y, (int)width, (int)height),
            before.Revision, invocation.Principal.Name, token).ConfigureAwait(false));
    }

    private async ValueTask<CommandExecutionResult> RenameAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count != 3 || !ValidName(invocation.Arguments[2])) return Usage();
        if (Find(invocation) is not { } before) return NotFound();
        return Mutation(await regions.RenameAsync(invocation.WorldId, before.Name, invocation.Arguments[2], before.Revision,
            invocation.Principal.Name, token).ConfigureAwait(false));
    }

    private async ValueTask<CommandExecutionResult> ProtectAsync(CommandInvocation invocation, CancellationToken token) =>
        invocation.Arguments.Count == 3 && bool.TryParse(invocation.Arguments[2], out bool enabled)
            ? await UpdateAsync(invocation, new(Flag: "protected", FlagValue: enabled), token).ConfigureAwait(false) : Usage();

    private async ValueTask<CommandExecutionResult> CompatibleAccessAsync(CommandInvocation invocation, RegionAccessKind kind, bool enabled, CancellationToken token)
    {
        if (invocation.Arguments.Count < 3 || !ValidName(invocation.Arguments[1])) return Usage();
        string subject = invocation.Arguments[1];
        var before = FindByName(invocation.WorldId, string.Join(' ', invocation.Arguments.Skip(2)));
        if (before is null) return NotFound();
        if (kind == RegionAccessKind.Member)
        {
            if (accounts is null || await accounts.FindByNameAsync(subject, token).ConfigureAwait(false) is not { } account)
                return Error("command.region.account-not-found", "Account not found.");
            subject = account.Name;
        }
        else
        {
            if (authorization?.TryGetGroup(subject, out var group) != true || group is null)
                return Error("command.region.group-not-found", "Group not found.");
            subject = group.Name;
        }
        return Mutation(await regions.SetAccessAsync(invocation.WorldId, before.Name, kind, subject, enabled, before.Revision,
            invocation.Principal.Name, token).ConfigureAwait(false));
    }

    private async ValueTask<CommandExecutionResult> TeleportAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count < 2) return Usage();
        if (!invocation.Principal.HasPermission("vega.navigation.tp")) return new(CommandExecutionStatus.Forbidden, "command.region.teleport-forbidden");
        if (navigation is null || Self(invocation) is not { } player) return MissingPlayer();
        var region = FindByName(invocation.WorldId, string.Join(' ', invocation.Arguments.Skip(1)));
        if (region is null) return NotFound();
        var position = new PlayerPosition((region.X + region.Width / 2f) * 16f, (region.Y + region.Height / 2f) * 16f);
        var result = await navigation.ToPositionAsync(player, position, token).ConfigureAwait(false);
        return result.IsSuccess ? CommandExecutionResult.WithMessages(MessageDescriptor.Success("command.region.teleported", region.Name)
            .WithDefaultText("Teleported to region {0}.")) : Error(result.ErrorCode ?? "command.region.teleport-failed", "Region teleport failed.");
    }

    private async ValueTask<CommandExecutionResult> SetPointAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count is not (2 or 4) || !TryInt(invocation.Arguments[1], out int index) || index is not (1 or 2)) return Usage();
        if (Self(invocation) is not { SessionId: { } session } player) return MissingPlayer();
        if (!TryPoint(invocation.Arguments, 2, player, out var point)) return Usage();
        if (!await ValidBoundsAsync(invocation.WorldId, point.X, point.Y, 1, 1, token).ConfigureAwait(false)) return InvalidBounds();
        if (Self(invocation)?.SessionId != session) return MissingPlayer();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (selectionSync)
        {
            foreach (var pair in selections.Where(pair => pair.Value.Expires <= now).ToArray()) selections.Remove(pair.Key);
            if (!selections.TryGetValue(session, out var current) || current.World != invocation.WorldId)
            {
                if (selections.Count >= MaximumSelections && !selections.ContainsKey(session))
                {
                    var live = players!.SnapshotOnlinePlayers().Where(snapshot => snapshot.SessionId.HasValue).Select(snapshot => snapshot.SessionId!.Value).ToHashSet();
                    foreach (var key in selections.Keys.Where(key => !live.Contains(key)).ToArray()) selections.Remove(key);
                    if (selections.Count >= MaximumSelections) return Error("command.region.selection-limit", "Too many active region selections.");
                }
                current = new(invocation.WorldId, null, null, now + SelectionLifetime);
            }
            selections[session] = index == 1 ? current with { First = point, Expires = now + SelectionLifetime } : current with { Second = point, Expires = now + SelectionLifetime };
        }
        return CommandExecutionResult.WithMessages(MessageDescriptor.Success("command.region.point-set", index, point.X, point.Y)
            .WithDefaultText("Region point {0} set to ({1},{2})."));
    }

    private CommandExecutionResult ClearPoints(CommandInvocation invocation)
    {
        if (invocation.Arguments.Count != 1) return Usage();
        if (Self(invocation) is not { SessionId: { } session }) return MissingPlayer();
        lock (selectionSync) selections.Remove(session);
        return Info("command.region.points-cleared", "Temporary region points cleared.");
    }

    private async ValueTask<CommandExecutionResult> DefineAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count < 2 || !ValidName(string.Join(' ', invocation.Arguments.Skip(1)))) return Usage();
        if (Self(invocation) is not { SessionId: { } session } player) return MissingPlayer();
        if (accountSessions?.TryGetBinding(player, out var binding) != true || !accountSessions.TryGetAccount(player.Id, out var account) || account?.Id != binding.AccountId)
            return Error("command.region.auth-required", "Defining a region requires an authenticated account.");
        Selection selection;
        WorldTilePosition first, second;
        lock (selectionSync)
        {
            if (!selections.TryGetValue(session, out var current) || current.Expires <= DateTimeOffset.UtcNow || current.World != invocation.WorldId ||
                current.First is not { } p1 || current.Second is not { } p2)
                return Error("command.region.points-required", "Set both region points first.");
            if (!defining.Add(session)) return Error("command.region.selection-busy", "A region is already being defined from your selection.");
            selection = current; first = p1; second = p2;
        }
        try
        {
            int x = Math.Min(first.X, second.X), y = Math.Min(first.Y, second.Y);
            long width = Math.Abs((long)first.X - second.X) + 1, height = Math.Abs((long)first.Y - second.Y) + 1;
            if (width > int.MaxValue || height > int.MaxValue || !await ValidBoundsAsync(invocation.WorldId, x, y, (int)width, (int)height, token).ConfigureAwait(false)) return InvalidBounds();
            if (Self(invocation)?.SessionId != session || !accountSessions.TryGetBinding(player, out var currentBinding) || currentBinding != binding)
                return MissingPlayer();
            lock (selectionSync)
                if (!selections.TryGetValue(session, out var current) || !ReferenceEquals(current, selection))
                    return Error("command.region.selection-changed", "Selection changed while preparing the region.");
            // The selection is consumed only after persistent creation succeeded.
            // A newer selection made during the DB operation remains available.
            var result = await regions.CreateAsync(invocation.WorldId, string.Join(' ', invocation.Arguments.Skip(1)), x, y, (int)width, (int)height,
                0, account.Name, invocation.Principal.Name, token).ConfigureAwait(false);
            if (result.Succeeded) lock (selectionSync)
                if (selections.TryGetValue(session, out var current) && ReferenceEquals(current, selection)) selections.Remove(session);
            return Mutation(result);
        }
        finally { lock (selectionSync) defining.Remove(session); }
    }

    private async ValueTask<CommandExecutionResult> NameAtAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count is not (1 or 3)) return Usage();
        PlayerSnapshot? player = invocation.Arguments.Count == 1 ? Self(invocation) : null;
        if (invocation.Arguments.Count == 1 && player is null) return MissingPlayer();
        if (!TryPoint(invocation.Arguments, 1, player, out var point)) return Usage();
        if (!await ValidBoundsAsync(invocation.WorldId, point.X, point.Y, 1, 1, token).ConfigureAwait(false)) return InvalidBounds();
        var region = regions.FindTopRegion(invocation.WorldId, point.X, point.Y);
        return region is null ? Info("command.region.none-at-point", "No region at this point.") : CommandExecutionResult.WithMessages(
            MessageDescriptor.Info("command.region.at-point", region.Name, region.Priority, point.X, point.Y).WithDefaultText("Region {0} (priority {1}) at ({2},{3})."));
    }

    private static bool TryPoint(IReadOnlyList<string> args, int start, PlayerSnapshot? player, out WorldTilePosition point)
    {
        point = default;
        int x, y;
        if (args.Count == start + 2)
        {
            if (!TryInt(args[start], out x) || !TryInt(args[start + 1], out y)) return false;
        }
        else
        {
            if (player is null || !float.IsFinite(player.Position.X) || !float.IsFinite(player.Position.Y) ||
                player.Position.X < 0 || player.Position.Y < 0 || player.Position.X / 16d >= int.MaxValue || player.Position.Y / 16d >= int.MaxValue) return false;
            x = (int)(player.Position.X / 16d); y = (int)(player.Position.Y / 16d);
        }
        point = new(x, y);
        return true;
    }

    private PlayerSnapshot? Self(CommandInvocation invocation) => invocation.Principal is IPlayerCommandPrincipal { SessionId: { } session } principal &&
        players?.TryGet(principal.PlayerId, out var player) == true && player?.SessionId == session && player.WorldId == invocation.WorldId ? player : null;

    private async ValueTask<bool> ValidBoundsAsync(WorldId world, int x, int y, int width, int height, CancellationToken token)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || (long)x + width > int.MaxValue || (long)y + height > int.MaxValue) return false;
        if (environment is null) return true;
        var snapshot = await environment.CaptureAsync(world, token).ConfigureAwait(false);
        return snapshot is not null && snapshot.WorldId == world && (long)x + width <= snapshot.WidthTiles && (long)y + height <= snapshot.HeightTiles;
    }

    private async ValueTask<CommandExecutionResult> PriorityAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken) =>
        invocation.Arguments.Count == 3 && TryInt(invocation.Arguments[2], out int priority)
            ? await UpdateAsync(invocation, new RegionUpdate(Priority: priority), cancellationToken).ConfigureAwait(false)
            : Usage();

    private async ValueTask<CommandExecutionResult> OwnerAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken) =>
        invocation.Arguments.Count == 3
            ? await UpdateAsync(
                invocation,
                invocation.Arguments[2].Equals("none", StringComparison.OrdinalIgnoreCase)
                    ? new RegionUpdate(ClearOwner: true)
                    : new RegionUpdate(Owner: invocation.Arguments[2]),
                cancellationToken).ConfigureAwait(false)
            : Usage();

    private async ValueTask<CommandExecutionResult> FlagAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 4 ||
            !bool.TryParse(invocation.Arguments[3], out bool value))
        {
            return Usage();
        }

        return await UpdateAsync(
            invocation,
            new RegionUpdate(Flag: invocation.Arguments[2], FlagValue: value),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CommandExecutionResult> AccessAsync(
        CommandInvocation invocation,
        RegionAccessKind kind,
        CancellationToken cancellationToken)
    {
        string operation = invocation.Arguments.Count > 2
            ? invocation.Arguments[2].ToLowerInvariant()
            : string.Empty;
        if (invocation.Arguments.Count != 4 || operation is not ("add" or "remove"))
        {
            return Usage();
        }

        RegionSnapshot? before = regions.SnapshotRegions(invocation.WorldId)
            .FirstOrDefault(region => region.Name.Equals(invocation.Arguments[1], StringComparison.OrdinalIgnoreCase));
        return before is null
            ? NotFound()
            : Mutation(await regions.SetAccessAsync(
                invocation.WorldId,
                before.Name,
                kind,
                invocation.Arguments[3],
                operation == "add",
                before.Revision,
                invocation.Principal.Name,
                cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<CommandExecutionResult> UpdateAsync(
        CommandInvocation invocation,
        RegionUpdate update,
        CancellationToken cancellationToken)
    {
        RegionSnapshot? before = Find(invocation);
        return before is null
            ? NotFound()
            : Mutation(await regions.UpdateAsync(
                invocation.WorldId,
                before.Name,
                update,
                before.Revision,
                invocation.Principal.Name,
                cancellationToken).ConfigureAwait(false));
    }

    private RegionSnapshot? Find(CommandInvocation invocation) =>
        invocation.Arguments.Count < 2
            ? null
            : FindByName(invocation.WorldId, invocation.Arguments[1]);

    private RegionSnapshot? FindByName(WorldId world, string name) => regions.SnapshotRegions(world).FirstOrDefault(region =>
        region.WorldId == world && region.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool ValidName(string name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 255 && !name.Any(char.IsControl);

    private static CommandExecutionResult Mutation(RegionMutationResult result) =>
        result.Succeeded
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Success(
                        "command.region.mutation-applied",
                        result.After?.Name ?? result.Before?.Name ?? "region",
                        result.Code)
                    .WithDefaultText("Region {0}: {1}."))
            : CommandExecutionResult.Failed(
                "command.region.mutation-failed",
                MessageDescriptor.Error("command.region.mutation-failed", result.Code)
                    .WithDefaultText("Region operation failed: {0}."));

    private static bool TryInt(string value, out int parsed) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);

    private static string Join(IEnumerable<string> values)
    {
        string[] snapshot = values.ToArray();
        return snapshot.Length == 0 ? "none" : string.Join(", ", snapshot);
    }

    private static CommandExecutionResult Info(string key, string text) =>
        CommandExecutionResult.WithMessages(MessageDescriptor.Info(key).WithDefaultText(text));

    private static CommandExecutionResult Error(string key, string text) => CommandExecutionResult.Failed(key, MessageDescriptor.Error(key).WithDefaultText(text));

    private static CommandExecutionResult InvalidBounds() => Error("command.region.invalid-bounds", "Region bounds must be positive and inside the current world.");

    private static CommandExecutionResult MissingPlayer() => Error("command.region.player-unavailable", "This command requires the current connected player session.");

    private static CommandExecutionResult NotFound() => CommandExecutionResult.Failed(
        "command.region.not-found",
        MessageDescriptor.Error("command.region.not-found").WithDefaultText("Region not found."));

    private static CommandExecutionResult Usage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.region.usage",
        [MessageDescriptor.Error("command.region.usage").WithDefaultText("Usage: " + Syntax + ".")]);
}
