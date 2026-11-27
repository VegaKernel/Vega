using System.Text.Json;
using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Authorization;
using Vega.Contracts.Commands;
using Vega.Contracts.Items;
using Vega.Contracts.Messaging;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;

namespace Vega.Modules.Restrictions;

public readonly record struct ContentRestriction(RestrictedContentKind Kind, int Type);

public sealed class RestrictionStore
{
    private readonly string path;
    private readonly int[] limits;
    private readonly SemaphoreSlim gate = new(1, 1);
    private HashSet<ContentRestriction> current;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public RestrictionStore(string path, int itemCount, int projectileCount, int tileCount)
    {
        this.path = Path.GetFullPath(path);
        limits = [itemCount, projectileCount, tileCount];
        current = File.Exists(this.path) ? Load() : [];
    }

    private HashSet<ContentRestriction> Load()
    {
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Restriction file is too large.");
        var loaded = JsonSerializer.Deserialize<ContentRestriction[]>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Restriction file is empty.");
        if (loaded.Any(entry => !IsValid(entry.Kind, entry.Type))) throw new InvalidDataException("Restriction file contains an invalid content ID.");
        return loaded.ToHashSet();
    }

    public bool IsValid(RestrictedContentKind kind, int type) => (uint)kind < limits.Length && type >= (kind == RestrictedContentKind.Tile ? 0 : 1) && type < limits[(int)kind];
    public bool HasRestrictions => Volatile.Read(ref current).Count != 0;
    public bool Contains(RestrictedContentKind kind, int type) => Volatile.Read(ref current).Contains(new(kind, type));
    public IReadOnlyList<int> List(RestrictedContentKind kind) => Volatile.Read(ref current).Where(x => x.Kind == kind).Select(x => x.Type).Order().ToArray();

    public async ValueTask<bool> ReloadAsync(CancellationToken token = default)
    {
        if (!await gate.WaitAsync(0, token).ConfigureAwait(false)) throw new IOException("Content restrictions are busy.");
        try
        {
            if (!File.Exists(path)) return false;
            var next = await Task.Run(Load, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Volatile.Write(ref current, next);
            return true;
        }
        finally { gate.Release(); }
    }

    public async ValueTask<bool> SetAsync(RestrictedContentKind kind, int type, bool restricted, CancellationToken token = default)
    {
        if (!IsValid(kind, type)) return false;
        if (!await gate.WaitAsync(0, token).ConfigureAwait(false)) throw new IOException("Content restrictions are busy.");
        try
        {
            var next = new HashSet<ContentRestriction>(Volatile.Read(ref current));
            if (!(restricted ? next.Add(new(kind, type)) : next.Remove(new(kind, type)))) return false;
            await Task.Run(async () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                    {
                        await JsonSerializer.SerializeAsync(stream, next.OrderBy(x => x.Kind).ThenBy(x => x.Type).ToArray(), JsonOptions, token).ConfigureAwait(false);
                        await stream.FlushAsync(token).ConfigureAwait(false);
                        stream.Flush(flushToDisk: true);
                    }
                    File.Move(temporary, path, true);
                    Volatile.Write(ref current, next);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }, token).ConfigureAwait(false);
            return true;
        }
        finally { gate.Release(); }
    }
}

public sealed class ContentRestrictionPolicy(RestrictionStore store, IPlayerAuthorizationService authorization, IPlayerItemSelection selection)
    : IPacketProcessor<PacketView>, IContentRestrictionPolicy
{
    public bool IsAllowed(PlayerId player, RestrictedContentKind kind, int type) => !store.Contains(kind, type) ||
        authorization.HasPermission(player, BypassPermission(kind)) ||
        authorization.HasPermission(player, ContentPermission(kind, type));

    internal static string BypassPermission(RestrictedContentKind kind) => kind switch
    {
        RestrictedContentKind.Item => "vega.restrictions.bypass.item",
        RestrictedContentKind.Projectile => "vega.restrictions.bypass.projectile",
        _ => "vega.restrictions.bypass.tile"
    };

    internal static string ContentPermission(RestrictedContentKind kind, int type) =>
        $"vega.restrictions.allow.{(kind == RestrictedContentKind.Item ? "item" : kind == RestrictedContentKind.Projectile ? "projectile" : "tile")}.{type.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (!store.HasRestrictions || context.Direction != PacketDirection.Inbound || context.PlayerId is not { } player)
            return PacketProcessingDecision.Allow;
        var packet = context.Packet;
        switch (packet.PacketType)
        {
            case PacketTypes.PlayerUpdate:
                var update = packet.AsPlayerUpdateView();
                if ((update.ControlFlags & UpdatePlayerControlFlags.ControlUseItem) != 0 &&
                    selection.TryGetItemType(player, update.SelectedItem, out int item) && !IsAllowed(player, RestrictedContentKind.Item, item))
                    return PacketProcessingDecision.Reject("restriction.item_use");
                break;
            case PacketTypes.ProjectileNew:
                if (!IsAllowed(player, RestrictedContentKind.Projectile, packet.AsProjectileNewView().Type))
                    return PacketProcessingDecision.Reject("restriction.projectile");
                break;
            case PacketTypes.Tile:
                var tile = packet.AsTileView();
                if (tile.Action is TileAction.PlaceTile or TileAction.ReplaceTile && !IsAllowed(player, RestrictedContentKind.Tile, tile.Value))
                    return PacketProcessingDecision.Reject("restriction.tile");
                break;
            case PacketTypes.PlaceObject:
                if (!IsAllowed(player, RestrictedContentKind.Tile, packet.AsPlaceObjectView().Type))
                    return PacketProcessingDecision.Reject("restriction.tile");
                break;
        }
        return PacketProcessingDecision.Allow;
    }
}

public sealed class RestrictionCommand(RestrictionStore store, RestrictedContentKind kind,
    Func<string, int?>? resolveName = null, IAuthorizationService? groups = null) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(kind switch
    {
        RestrictedContentKind.Item => "itemban", RestrictedContentKind.Projectile => "projban", _ => "tileban"
    }, [], ["vega.restrictions.manage"], Syntax: "<command> list [page] | add|del <id|name> | allow|disallow <id|name> <group> | help");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        var args = invocation.Arguments;
        if (args.Count == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            int page = 1;
            if (args.Count > 2 || args.Count == 2 && (!int.TryParse(args[1], out page) || page < 1)) return Usage();
            var entries = store.List(kind);
            int pages = Math.Max(1, (entries.Count + 49) / 50);
            if (page > pages) return Usage();
            return CommandExecutionResult.WithMessages(MessageDescriptor.Info("restriction.list", kind, page, pages,
                string.Join(", ", entries.Skip((page - 1) * 50).Take(50))).WithDefaultText("{0} restrictions ({1}/{2}): {3}"));
        }
        string action = args[0].ToLowerInvariant();
        if (action == "help" && args.Count == 1)
            return CommandExecutionResult.WithMessages(MessageDescriptor.Info("restriction.help", Descriptor.Syntax).WithDefaultText("{0}"));
        bool groupAction = action is "allow" or "disallow";
        if (args.Count != (groupAction ? 3 : 2) || action is not ("add" or "del" or "allow" or "disallow")) return Usage();
        int? resolved = int.TryParse(args[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            ? parsed : resolveName?.Invoke(args[1]);
        if (resolved is not int type || !store.IsValid(kind, type)) return Usage();
        if (groupAction)
            return await SetGroupAllowanceAsync(invocation, type, action == "allow", token).ConfigureAwait(false);
        try
        {
            bool changed = await store.SetAsync(kind, type, action == "add", token).ConfigureAwait(false);
            return CommandExecutionResult.WithMessages(MessageDescriptor.Info("restriction.updated", kind, type, changed)
                .WithDefaultText("{0} restriction {1} updated={2}."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return CommandExecutionResult.Failed("restriction.storage", MessageDescriptor.Error("restriction.storage").WithDefaultText("Restriction storage unavailable."));
        }
    }

    // Group-specific allowances are ordinary durable group permissions, not a
    // separate bypass store. Group deletion and inheritance therefore follow
    // the authoritative authorization model, including explicit denies.
    private async ValueTask<CommandExecutionResult> SetGroupAllowanceAsync(CommandInvocation invocation, int type, bool allow, CancellationToken token)
    {
        string permission = ContentRestrictionPolicy.ContentPermission(kind, type);
        if (groups is null || !groups.TryGetGroup(invocation.Arguments[2], out var group) || group is null ||
            (allow && !store.Contains(kind, type))) return CommandExecutionResult.Failed("restriction.group_or_content_missing");
        if (!invocation.Principal.IsServer &&
            (!invocation.Principal.HasPermission("vega.groups.manage") || !invocation.Principal.HasPermission(permission)))
            return new(CommandExecutionStatus.Forbidden, "restriction.group_delegation_denied");
        var result = await groups.SetPermissionAsync(group.Name, permission, allow ? PermissionEffect.Grant : PermissionEffect.Deny,
            group.Revision, invocation.Principal.Name, token).ConfigureAwait(false);
        return result.Succeeded
            ? CommandExecutionResult.WithMessages(MessageDescriptor.Info("restriction.group_updated", group.Name, permission, allow)
                .WithDefaultText("Group {0}: direct allowance {1} enabled={2}."))
            : CommandExecutionResult.Failed("restriction.group_update_failed");
    }

    private CommandExecutionResult Usage() => new(CommandExecutionStatus.InvalidUsage, "restriction.usage",
        [MessageDescriptor.Error("restriction.usage", Descriptor.Syntax).WithDefaultText("Usage: {0}.")]);
}
