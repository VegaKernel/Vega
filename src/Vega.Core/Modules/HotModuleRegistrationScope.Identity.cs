using System.Collections.Frozen;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

internal sealed partial class HotModuleRegistrationScope
{
    private sealed class PluginIdentity(HotModuleRegistrationScope owner, WorldId? worldId, IPlayerDirectory? directory,
        IPlayerAccountSessionService? authentication, IAccountService? accounts, IAuthorizationService? groups,
        IPlayerAuthorizationService? playerPermissions, IGameThreadDispatcher? gameThread, IRoleLeaseService? roleLeases,
        IReadOnlyList<string>? identityAssignmentGroups) : IPluginIdentity
    {
        public bool AccountsAvailable => accounts is not null;
        public bool GroupsAvailable => groups is not null;
        public bool AuthenticatedPermissionsAvailable => worldId.HasValue && directory is not null &&
            authentication is not null && playerPermissions is not null;
        public bool AccountChangesAvailable => gameThread is not null && accounts is IAccountChangeSource;
        public bool GroupChangesAvailable => gameThread is not null && groups is IGroupChangeSource;
        public bool TemporaryGroupChangesAvailable => gameThread is not null && roleLeases is IRoleLeaseChangeSource;
        private readonly HashSet<string> assignable = new(identityAssignmentGroups ?? [], StringComparer.OrdinalIgnoreCase);
        public bool GroupAssignmentsAvailable => accounts is not null && groups is not null && assignable.Count != 0;

        public ValueTask<PluginIdentityMutationResult> AssignGroupAsync(AccountId account, string group, CancellationToken cancellationToken = default)
        {
            Ensure(GroupAssignmentsAvailable);
            return owner.ReadServiceAsync<PluginIdentityMutationResult>(token => new(Task.Run<PluginIdentityMutationResult>(async () =>
            {
                var before = await accounts!.FindByIdAsync(account, token).ConfigureAwait(false);
                if (before is null) return new(false, "account.not-found");
                if (!assignable.Contains(before.GroupName) || !assignable.Contains(group)) return new(false, "plugin.identity.group-denied");
                var result = await accounts.SetGroupAsync(before.Name, group, before.Revision, owner.logSource, token).ConfigureAwait(false);
                return new(result.Succeeded, result.Code, result.After is null ? null : Detach(result.After));
            }, token)), cancellationToken);
        }

        public ValueTask<PluginIdentityMutationResult> GrantTemporaryGroupAsync(AccountId account, string group, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
        {
            Ensure(GroupAssignmentsAvailable && roleLeases is not null);
            return owner.ReadServiceAsync<PluginIdentityMutationResult>(token => new(Task.Run<PluginIdentityMutationResult>(async () =>
            {
                var before = await accounts!.FindByIdAsync(account, token).ConfigureAwait(false);
                if (before is null) return new(false, "account.not-found");
                var now = DateTimeOffset.UtcNow;
                var active = roleLeases!.FindActive(account, now);
                if (!assignable.Contains(before.GroupName) || !assignable.Contains(group) ||
                    (active is not null && (!assignable.Contains(active.GroupName) || !active.IssuedBy.Equals(owner.logSource, StringComparison.Ordinal))))
                    return new(false, "plugin.identity.group-denied");
                var result = await roleLeases.GrantConditionalAsync(account, group, now, expiresAt, owner.logSource, before.Revision, active?.Id, active?.Revision, token).ConfigureAwait(false);
                return new(result.Succeeded, result.Code, Detach(before), result.After);
            }, token)), cancellationToken);
        }

        public ValueTask<PluginIdentityMutationResult> RevokeOwnTemporaryGroupAsync(AccountId account, CancellationToken cancellationToken = default)
        {
            Ensure(GroupAssignmentsAvailable && roleLeases is not null);
            return owner.ReadServiceAsync<PluginIdentityMutationResult>(token => new(Task.Run<PluginIdentityMutationResult>(async () =>
            {
                var active = roleLeases!.FindActive(account, DateTimeOffset.UtcNow);
                if (active is null) return new(false, "role-lease.not-found");
                if (!assignable.Contains(active.GroupName) || !active.IssuedBy.Equals(owner.logSource, StringComparison.Ordinal)) return new(false, "plugin.identity.group-denied");
                var result = await roleLeases.RevokeAsync(active.Id, active.Revision, owner.logSource, token).ConfigureAwait(false);
                return new(result.Succeeded, result.Code, TemporaryGroup: result.After);
            }, token)), cancellationToken);
        }

        public void OnAccountChanged(Action<PluginAccountChanged> handler)
        {
            Ensure(AccountChangesAvailable);
            var source = (IAccountChangeSource)accounts!;
            Subscribe<AccountChange, PluginAccountChanged>("account-changed", handler,
                callback => source.AccountChanged += callback, callback => source.AccountChanged -= callback,
                change => new(change.Operation, change.Actor, change.Before is null ? null : Detach(change.Before), change.After is null ? null : Detach(change.After)));
        }

        public void OnGroupChanged(Action<PluginGroupChanged> handler)
        {
            Ensure(GroupChangesAvailable);
            var source = (IGroupChangeSource)groups!;
            Subscribe<GroupChange, PluginGroupChanged>("group-changed", handler,
                callback => source.GroupChanged += callback, callback => source.GroupChanged -= callback,
                change => new(change.Operation, change.Actor, change.Before is null ? null : Detach(change.Before), change.After is null ? null : Detach(change.After)));
        }

        public void OnTemporaryGroupChanged(Action<RoleLeaseChange> handler)
        {
            Ensure(TemporaryGroupChangesAvailable);
            var source = (IRoleLeaseChangeSource)roleLeases!;
            Subscribe<RoleLeaseChange, RoleLeaseChange>("temporary-group-changed", handler,
                callback => source.RoleLeaseChanged += callback, callback => source.RoleLeaseChanged -= callback, change => change);
        }

        public ValueTask<RoleLeaseSnapshot?> FindTemporaryGroupAsync(AccountId account, CancellationToken cancellationToken = default)
        {
            Ensure(roleLeases is not null);
            if (account.Value <= 0) throw new ArgumentOutOfRangeException(nameof(account));
            return owner.ReadServiceAsync<RoleLeaseSnapshot?>(token => new(Task.Run(() => roleLeases!.FindActive(account, DateTimeOffset.UtcNow), token)), cancellationToken);
        }

        private void Subscribe<TInput, TOutput>(string operation, Action<TOutput> handler,
            Action<Action<TInput>> attach, Action<Action<TInput>> detach, Func<TInput, TOutput> project)
        {
            ArgumentNullException.ThrowIfNull(handler);
            Action<TOutput>? active = handler;
            void Observe(TInput value)
            {
                if (!IsActive()) return;
                // Detach account metadata and group collections before leaving the SQL writer callback.
                TOutput snapshot = project(value);
                gameThread!.TryPost(operation, () =>
                {
                    if (IsActive() && Volatile.Read(ref active) is { } current)
                        owner.executionGuard.TryInvoke(operation, current, snapshot);
                });
            }
            lock (owner.sync)
            {
                ObjectDisposedException.ThrowIf(owner.disposed, owner);
                attach(Observe);
                owner.Track(new CleanupRegistration(() => { Interlocked.Exchange(ref active, null); detach(Observe); }));
            }
        }

        public ValueTask<PluginAccountIdentity?> FindAccountAsync(string name, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            Ensure(AccountsAvailable);
            // SQLite providers may perform synchronous I/O even through async ADO methods.
            return owner.ReadServiceAsync<PluginAccountIdentity?>(token => new(Task.Run(async () =>
            {
                AccountSnapshot? account = await accounts!.FindByNameAsync(name, token).ConfigureAwait(false);
                return account is null ? null : Detach(account);
            }, token)), cancellationToken);
        }

        public ValueTask<PluginAccountIdentity?> FindAccountByIdAsync(AccountId id, CancellationToken cancellationToken = default)
        {
            Ensure(AccountsAvailable);
            if (id.Value <= 0) throw new ArgumentOutOfRangeException(nameof(id));
            return owner.ReadServiceAsync<PluginAccountIdentity?>(token => new(Task.Run(async () =>
            {
                AccountSnapshot? account = await accounts!.FindByIdAsync(id, token).ConfigureAwait(false);
                return account is null ? null : Detach(account);
            }, token)), cancellationToken);
        }

        public bool TryGetAuthenticatedAccount(PlayerSnapshot player, out PluginAccountIdentity? account)
        {
            account = null;
            if (!TryBinding(player, out PlayerAccountBinding binding) || !authentication!.TryRunWithBinding(binding,
                () => authentication.TryGetAccount(player.Id, out var current) && current?.Id == binding.AccountId
                    ? Detach(current) : null, out PluginAccountIdentity? result) || result is null || !IsActive()) return false;
            account = result;
            return true;
        }

        public ValueTask<IReadOnlyList<GroupSnapshot>> SnapshotGroupsAsync(CancellationToken cancellationToken = default)
        {
            Ensure(GroupsAvailable);
            return owner.ReadServiceAsync<IReadOnlyList<GroupSnapshot>>(token => new(Task.Run<IReadOnlyList<GroupSnapshot>>(
                () => Array.AsReadOnly(groups!.SnapshotGroups().Select(Detach).ToArray()), token)), cancellationToken);
        }

        public ValueTask<GroupSnapshot?> FindGroupAsync(string name, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            Ensure(GroupsAvailable);
            return owner.ReadServiceAsync<GroupSnapshot?>(token => new(Task.Run(() =>
                groups!.TryGetGroup(name, out var group) && group is not null ? Detach(group) : null, token)), cancellationToken);
        }

        public ValueTask<bool> GroupHasPermissionAsync(string groupName, string permission, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
            ArgumentException.ThrowIfNullOrWhiteSpace(permission);
            Ensure(GroupsAvailable);
            return owner.ReadServiceAsync<bool>(token => new(Task.Run(() => groups!.HasPermission(groupName, permission), token)), cancellationToken);
        }

        public bool TryHasAuthenticatedPermission(PlayerSnapshot player, string permission, out bool allowed)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(permission);
            allowed = false;
            if (!AuthenticatedPermissionsAvailable || !TryBinding(player, out PlayerAccountBinding binding) ||
                !authentication!.TryRunWithBinding(binding, () => playerPermissions!.HasPermission(player.Id, permission), out bool result) ||
                !IsActive()) return false;
            allowed = result;
            return true;
        }

        private bool TryBinding(PlayerSnapshot player, out PlayerAccountBinding binding)
        {
            ArgumentNullException.ThrowIfNull(player);
            binding = default;
            return IsActive() && worldId.HasValue && player.WorldId == worldId && player.SessionId is { } session &&
                directory is not null && directory.TryGet(player.Id, out var current) && current is not null &&
                current.WorldId == player.WorldId && current.SessionId == session &&
                authentication is not null && authentication.TryGetBinding(current, out binding);
        }

        private bool IsActive() => owner.executionGuard.AcceptsCallbacks && !owner.lifetime.IsCancellationRequested;
        private static PluginAccountIdentity Detach(AccountSnapshot account) => new(account.Id, account.Name, account.GroupName);
        private static GroupSnapshot Detach(GroupSnapshot group) => group with
        {
            Permissions = group.Permissions.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            DeniedPermissions = group.DeniedPermissions.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
        };
        private static void Ensure(bool available)
        {
            if (!available) throw new NotSupportedException("Identity query is unavailable in this host.");
        }
    }

    private sealed class PluginWarps(HotModuleRegistrationScope owner, WorldId? worldId, IWorldPublicWarpReader? reader) : IPluginWarps
    {
        public bool IsAvailable => worldId.HasValue && reader is not null;
        public ValueTask<WorldWarpCatalogSnapshot?> SnapshotAsync(CancellationToken cancellationToken = default)
        {
            if (!IsAvailable) throw new NotSupportedException("Warp queries are unavailable in this host.");
            return owner.ReadServiceAsync<WorldWarpCatalogSnapshot?>(async token =>
            {
                WorldWarpCatalogSnapshot? catalog = await reader!.ReadAsync(worldId!.Value, token).ConfigureAwait(false);
                return catalog is not null && catalog.WorldId == worldId && catalog.FileIdentity != Guid.Empty
                    ? catalog with { Warps = Array.AsReadOnly(catalog.Warps.ToArray()) } : null;
            }, cancellationToken);
        }
    }
}
