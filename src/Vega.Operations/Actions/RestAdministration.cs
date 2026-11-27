using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Bans;
using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Moderation;
using Vega.Contracts.Regions;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;

namespace Vega.Operations.Actions;

/// <summary>Typed maintenance-only administrative routes. Authority and validation remain in the domain services.</summary>
public sealed class RestAdministration(WorldId world, IAccountService accounts, IAuthorizationService groups,
    IRegionService regions, IBanService bans, IMuteService mutes, ISandboxDirectory sandboxes,
    ISandboxControlService? sandboxControl = null, string? sandboxTemplateRoot = null)
{
    private const string Actor = "rest:administration";
    private const int PageSize = 100;
    private const int MaximumDurationSeconds = 366 * 24 * 60 * 60;
    private static readonly JsonSerializerOptions RequestOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    public object? Read(string route)
    {
        string[] parts = route.Split('/');
        if (parts.Length is < 1 or > 2) return null;
        int page = 0;
        if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out page) ||
            page > (int.MaxValue - PageSize) / PageSize)) return null;
        return parts[0] switch
        {
            "accounts" => Page(accounts.SnapshotAccounts().OrderBy(a => a.Id.Value).Select(a => new
                { id = a.Id.Value, a.Name, a.GroupName, a.LastSeenUtc, a.Culture, a.Revision }), page),
            "groups" => Page(groups.SnapshotGroups().OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Select(g => new
                { g.Name, g.ParentName, g.Permissions, g.DeniedPermissions, g.Revision }), page),
            "regions" => Page(regions.SnapshotRegions(world).OrderBy(r => r.Id).Select(r => new
                { world = r.WorldId.Value, r.Id, r.Name, r.X, r.Y, r.Width, r.Height, r.Priority, r.Owner, r.Members, r.Groups, r.Flags, r.Revision }), page),
            "bans" => Page(bans.SnapshotActive(DateTimeOffset.UtcNow).OrderBy(b => b.Id.Value).Select(b => new
                { id = b.Id.Value, kind = b.Subject.Kind.ToString(), value = b.Subject.Value, b.Reason, b.CreatedBy, b.CreatedAt, b.ExpiresAt, b.Revision }), page),
            "mutes" => Page(mutes.SnapshotActive(DateTimeOffset.UtcNow).OrderBy(m => m.Id.Value).Select(m => new
                { id = m.Id.Value, kind = m.SubjectKind.ToString(), value = m.SubjectValue, m.Reason, m.IssuedBy, m.IssuedAt, m.ExpiresAt, m.Revision }), page),
            "sandboxes" => Page(sandboxes.Snapshot().OrderBy(s => s.Descriptor.WorldId.Value, StringComparer.Ordinal).Select(s => new
                { world = s.Descriptor.WorldId.Value, name = s.Descriptor.DisplayName, state = s.State.ToString(),
                    persistence = s.Descriptor.Persistence.ToString(), s.Descriptor.PlayerCapacity, s.Descriptor.Revision,
                    s.Descriptor.CreatedAt, sscMode = s.Descriptor.SscMode.ToString(), s.OnlinePlayers, s.TicksPerSecond, s.Resources, s.DesiredRunning }), page),
            "sandboxTemplates" => Page(Templates().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => new { f.Name, bytes = f.Length }), page),
            _ => null
        };
    }

    private static object Page<T>(IEnumerable<T> items, int page)
    {
        T[] slice = items.Skip(page * PageSize).Take(PageSize + 1).ToArray();
        return new { page, nextPage = slice.Length > PageSize ? (int?)(page + 1) : null, items = slice.Take(PageSize).ToArray() };
    }

    internal static bool IsMutationRoute(string route) => route is
        "accounts/create" or "accounts/delete" or "accounts/group" or "accounts/password" or "accounts/culture" or
        "groups/create" or "groups/delete" or "groups/parent" or "groups/permission" or
        "regions/create" or "regions/delete" or "regions/resize" or "regions/priority" or "regions/owner" or "regions/flag" or "regions/access" or
        "bans/add" or "bans/revoke" or "mutes/add" or "mutes/revoke" or "sandboxes/start" or "sandboxes/stop" or "sandboxes/restart" or
        "sandboxes/create" or "sandboxes/reset" or "sandboxes/destroy";

    public async ValueTask<int> WriteAsync(string route, JsonElement payload, CancellationToken token)
    {
        if (payload.ValueKind != JsonValueKind.Object) return 400;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in payload.EnumerateObject())
            if (!names.Add(property.Name)) return 400;

        int slash = route.IndexOf('/');
        if (slash < 0) return 404;
        try
        {
            return route[..slash] switch
            {
                "accounts" => await WriteAccountsAsync(route, payload, token),
                "groups" => await WriteGroupsAsync(route, payload, token),
                "regions" => await WriteRegionsAsync(route, payload, token),
                "bans" or "mutes" => await WriteModerationAsync(route, payload, DateTimeOffset.UtcNow, token),
                "sandboxes" => await WriteSandboxesAsync(route, payload, token),
                _ => 404
            };
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or OverflowException) { return 400; }
    }

    private static T Body<T>(JsonElement payload) where T : class =>
        payload.Deserialize<T>(RequestOptions) ?? throw new JsonException("Missing administration body.");

    private async ValueTask<int> WriteAccountsAsync(string route, JsonElement payload, CancellationToken token)
    {
        switch (route)
        {
            case "accounts/create":
                var createAccount = Body<CreateAccount>(payload);
                return Status(await accounts.CreateAsync(Text(createAccount.Name), Text(createAccount.Password, 512), Text(createAccount.Group), Actor, token));
            case "accounts/delete":
                var deleteAccount = Body<RevisionTarget>(payload);
                return Status(await accounts.DeleteAsync(Text(deleteAccount.Name), Revision(deleteAccount.Revision), Actor, token));
            case "accounts/group":
                var group = Body<AccountGroup>(payload);
                return Status(await accounts.SetGroupAsync(Text(group.Name), Text(group.Group), Revision(group.Revision), Actor, token));
            case "accounts/password":
                var password = Body<AccountPassword>(payload);
                return Status(await accounts.ChangePasswordAsync(Text(password.Name), Text(password.Password, 512), Revision(password.Revision), Actor, token));
            case "accounts/culture":
                var culture = Body<AccountCulture>(payload);
                return Status(await accounts.SetCultureAsync(Text(culture.Name), Optional(culture.Culture), Revision(culture.Revision), Actor, token));
            default: return 404;
        }
    }

    private async ValueTask<int> WriteGroupsAsync(string route, JsonElement payload, CancellationToken token)
    {
        switch (route)
        {
            case "groups/create":
                var createGroup = Body<CreateGroup>(payload);
                return Status(await groups.CreateGroupAsync(Text(createGroup.Name), Optional(createGroup.Parent), Actor, token));
            case "groups/delete":
                var deleteGroup = Body<RevisionTarget>(payload);
                return Status(await groups.DeleteGroupAsync(Text(deleteGroup.Name), Revision(deleteGroup.Revision), Actor, token));
            case "groups/parent":
                var parent = Body<GroupParent>(payload);
                return Status(await groups.SetParentAsync(Text(parent.Name), Optional(parent.Parent), Revision(parent.Revision), Actor, token));
            case "groups/permission":
                var permission = Body<GroupPermission>(payload);
                PermissionEffect? effect = permission.Effect switch
                { "grant" => PermissionEffect.Grant, "deny" => PermissionEffect.Deny, "revoke" => null, _ => throw new ArgumentException() };
                return Status(await groups.SetPermissionAsync(Text(permission.Name), Text(permission.Permission, 256), effect, Revision(permission.Revision), Actor, token));
            default: return 404;
        }
    }

    private async ValueTask<int> WriteRegionsAsync(string route, JsonElement payload, CancellationToken token)
    {
        switch (route)
        {
            case "regions/create":
                var createRegion = Body<CreateRegion>(payload);
                return Status(await regions.CreateAsync(world, Text(createRegion.Name), createRegion.X, createRegion.Y,
                    createRegion.Width, createRegion.Height, createRegion.Priority, Optional(createRegion.Owner), Actor, token));
            case "regions/delete":
                var deleteRegion = Body<RevisionTarget>(payload);
                return Status(await regions.DeleteAsync(world, Text(deleteRegion.Name), Revision(deleteRegion.Revision), Actor, token));
            case "regions/resize":
                var resize = Body<ResizeRegion>(payload);
                return Status(await regions.UpdateAsync(world, Text(resize.Name), new(resize.X, resize.Y, resize.Width, resize.Height), Revision(resize.Revision), Actor, token));
            case "regions/priority":
                var priority = Body<RegionPriority>(payload);
                return Status(await regions.UpdateAsync(world, Text(priority.Name), new(Priority: priority.Priority), Revision(priority.Revision), Actor, token));
            case "regions/owner":
                var owner = Body<RegionOwner>(payload);
                return Status(await regions.UpdateAsync(world, Text(owner.Name), new(Owner: Optional(owner.Owner), ClearOwner: owner.Owner is null), Revision(owner.Revision), Actor, token));
            case "regions/flag":
                var flag = Body<RegionFlag>(payload);
                return Status(await regions.UpdateAsync(world, Text(flag.Name), new(Flag: Text(flag.Flag), FlagValue: flag.Enabled), Revision(flag.Revision), Actor, token));
            case "regions/access":
                var access = Body<RegionAccess>(payload);
                RegionAccessKind kind = access.Kind switch { "member" => RegionAccessKind.Member, "group" => RegionAccessKind.Group, _ => throw new ArgumentException() };
                return Status(await regions.SetAccessAsync(world, Text(access.Name), kind, Text(access.Subject), access.Enabled, Revision(access.Revision), Actor, token));
            default: return 404;
        }
    }

    private async ValueTask<int> WriteModerationAsync(string route, JsonElement payload, DateTimeOffset now, CancellationToken token)
    {
        switch (route)
        {
            case "bans/add":
                var ban = Body<BanRequest>(payload);
                GlobalBanSubjectKind banKind = ban.Kind switch
                { "name" => GlobalBanSubjectKind.PlayerName, "account" => GlobalBanSubjectKind.Account, "uuid" => GlobalBanSubjectKind.Uuid,
                    "ip" => GlobalBanSubjectKind.IpAddress, "cidr" => GlobalBanSubjectKind.Network, _ => throw new ArgumentException() };
                if (!bans.TryNormalizeSubject(new(banKind, Text(ban.Value, 256)), out var subject, out _) || subject is null) return 400;
                return await bans.CreateAsync(new(GlobalBanId.New(), subject, Text(ban.Reason, 512), Actor, now,
                    ban.DurationSeconds is { } seconds ? Expiry(now, seconds) : null, 1), token) ? 204 : 409;
            case "bans/revoke":
                var revokeBan = Body<RevisionId>(payload);
                if (revokeBan.Id == Guid.Empty) return 400;
                var existing = bans.SnapshotActive(now).FirstOrDefault(b => b.Id.Value == revokeBan.Id);
                if (existing is null) return 404;
                if (existing.Revision != Revision(revokeBan.Revision) || existing.Revision == long.MaxValue) return 409;
                return await bans.RevokeAsync(new(existing.Id, Actor, now, existing.Revision + 1), token) ? 204 : 409;
            case "mutes/add":
                var mute = Body<MuteRequest>(payload);
                MuteSubjectKind muteKind = mute.Kind switch { "name" => MuteSubjectKind.PlayerName, "account" => MuteSubjectKind.Account, _ => throw new ArgumentException() };
                return Status(await mutes.CreateAsync(new(MuteId.New(), muteKind, Text(mute.Value), Text(mute.Reason, 512), Actor, now, Expiry(now, mute.DurationSeconds), 1), token));
            case "mutes/revoke":
                var revokeMute = Body<RevisionId>(payload);
                if (revokeMute.Id == Guid.Empty) return 400;
                return Status(await mutes.RevokeAsync(new(revokeMute.Id), Revision(revokeMute.Revision), Actor, now, token));
            default: return 404;
        }
    }

    private async ValueTask<int> WriteSandboxesAsync(string route, JsonElement payload, CancellationToken token)
    {
        if (sandboxControl is null) return 404;
        switch (route)
        {
            case "sandboxes/start":
            case "sandboxes/stop":
            case "sandboxes/restart":
                var target = new WorldId(Text(Body<SandboxTarget>(payload).WorldId));
                if (target == WorldId.Primary) return 400;
                SandboxOperationResult result = route switch
                {
                    "sandboxes/start" => await sandboxControl.StartAsync(target, token),
                    "sandboxes/stop" => await sandboxControl.StopAsync(target, token),
                    _ => await sandboxControl.RestartAsync(target, token)
                };
                return Status(result);
            case "sandboxes/create": return await CreateSandboxAsync(sandboxControl, payload, token);
            case "sandboxes/reset":
                var reset = Body<ResetSandbox>(payload);
                if (!reset.Confirm || reset.WorldId.Equals("primary", StringComparison.OrdinalIgnoreCase)) return 400;
                string? replacement = reset.Template is null ? null : ResolveTemplate(reset.Template);
                if (reset.Template is not null && replacement is null) return 404;
                return Status(await sandboxControl.ResetConfirmedAsync(new(Text(reset.WorldId)), Revision(reset.Revision), reset.CreatedAt, replacement, token));
            case "sandboxes/destroy":
                var destroy = Body<DestroySandbox>(payload);
                if (!destroy.Confirm || destroy.WorldId.Equals("primary", StringComparison.OrdinalIgnoreCase)) return 400;
                return Status(await sandboxControl.DestroyConfirmedAsync(new(Text(destroy.WorldId)), Revision(destroy.Revision), destroy.CreatedAt, token));
            default: return 404;
        }
    }

    private async ValueTask<int> CreateSandboxAsync(ISandboxControlService control, JsonElement payload, CancellationToken token)
    {
        var create = Body<CreateSandbox>(payload);
        var persistence = create.Persistence switch
        { "temporary" => SandboxPersistenceMode.Temporary, "persistent" => SandboxPersistenceMode.Persistent, _ => throw new ArgumentException() };
        var ssc = create.SscMode switch
        { "inherit" => SandboxSscMode.Inherit, "copyOnEnter" => SandboxSscMode.CopyOnEnter,
            "isolated" => SandboxSscMode.Isolated, "disabled" => SandboxSscMode.Disabled, _ => throw new ArgumentException() };
        if (create.Capacity is < 1 or > 255 || create.WorldId.Equals("primary", StringComparison.OrdinalIgnoreCase)) return 400;
        string? template = ResolveTemplate(create.Template);
        if (template is null) return 404;
        return Status(await control.CreateAsync(new SandboxCreateRequest(Text(create.WorldId), Text(create.Name), template,
            persistence, create.Capacity, new(SandboxAccessMode.Public), null, ssc, new HashSet<string>()), token));
    }

    private static string Text(string value, int max = 128) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl)
        ? value : throw new ArgumentException("Invalid administration value.");
    private static string? Optional(string? value) => value is null ? null : Text(value);
    private static long Revision(long value) => value >= 0 ? value : throw new ArgumentException("Invalid revision.");
    private static DateTimeOffset Expiry(DateTimeOffset now, int seconds) => seconds is > 0 and <= MaximumDurationSeconds
        ? now.AddSeconds(seconds) : throw new ArgumentException("Invalid duration.");
    private static int Status(AccountMutationResult result) => MutationStatus(result.Succeeded, result.Code);
    private static int Status(GroupMutationResult result) => MutationStatus(result.Succeeded, result.Code);
    private static int Status(RegionMutationResult result) => MutationStatus(result.Succeeded, result.Code);
    private static int Status(MuteMutationResult result) => MutationStatus(result.Succeeded, result.Code);
    private static int Status(SandboxOperationResult result) => result.Status switch
    {
        SandboxOperationStatus.Applied or SandboxOperationStatus.AlreadyInDesiredState => 204,
        SandboxOperationStatus.NotFound => 404, SandboxOperationStatus.Conflict => 409,
        SandboxOperationStatus.Rejected => 400, SandboxOperationStatus.TimedOut => 504, _ => 500
    };
    private static int MutationStatus(bool succeeded, string code) => succeeded ? 204 :
        code.EndsWith("not-found", StringComparison.Ordinal) ? 404 :
        code.EndsWith("revision-conflict", StringComparison.Ordinal) || code.EndsWith("exists", StringComparison.Ordinal) ? 409 : 400;

    private IEnumerable<FileInfo> Templates()
    {
        if (sandboxTemplateRoot is null || !Directory.Exists(sandboxTemplateRoot)) return [];
        var root = new DirectoryInfo(Path.GetFullPath(sandboxTemplateRoot));
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0) return [];
        return root.EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(file => file.Extension.Equals(".wld", StringComparison.OrdinalIgnoreCase) &&
                (file.Attributes & FileAttributes.ReparsePoint) == 0);
    }

    private string? ResolveTemplate(string name)
    {
        if (Text(name) != Path.GetFileName(name) || name.IndexOfAny(['/', '\\', ':']) >= 0 ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !Path.GetExtension(name).Equals(".wld", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a curated template file name, not a path.");
        return Templates().FirstOrDefault(file => string.Equals(file.Name, name,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))?.FullName;
    }

    private sealed record CreateAccount(string Name, string Password, string Group);
    private sealed record RevisionTarget(string Name, long Revision);
    private sealed record AccountGroup(string Name, long Revision, string Group);
    private sealed record AccountPassword(string Name, long Revision, string Password);
    private sealed record AccountCulture(string Name, long Revision, string? Culture);
    private sealed record CreateGroup(string Name, string? Parent);
    private sealed record GroupParent(string Name, long Revision, string? Parent);
    private sealed record GroupPermission(string Name, long Revision, string Permission, string Effect);
    private sealed record CreateRegion(string Name, int X, int Y, int Width, int Height, int Priority, string? Owner);
    private sealed record ResizeRegion(string Name, long Revision, int X, int Y, int Width, int Height);
    private sealed record RegionPriority(string Name, long Revision, int Priority);
    private sealed record RegionOwner(string Name, long Revision, string? Owner);
    private sealed record RegionFlag(string Name, long Revision, string Flag, bool Enabled);
    private sealed record RegionAccess(string Name, long Revision, string Kind, string Subject, bool Enabled);
    private sealed record BanRequest(string Kind, string Value, string Reason, int? DurationSeconds);
    private sealed record MuteRequest(string Kind, string Value, string Reason, int DurationSeconds);
    private sealed record RevisionId(Guid Id, long Revision);
    private sealed record SandboxTarget(string WorldId);
    private sealed record CreateSandbox(string WorldId, string Name, string Template, string Persistence, int Capacity, string SscMode);
    private sealed record ResetSandbox(string WorldId, long Revision, DateTimeOffset CreatedAt, bool Confirm, string? Template);
    private sealed record DestroySandbox(string WorldId, long Revision, DateTimeOffset CreatedAt, bool Confirm);
}
