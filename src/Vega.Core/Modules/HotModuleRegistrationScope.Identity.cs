using System.Collections.Frozen;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

internal sealed partial class HotModuleRegistrationScope
{
    private ValueTask<T> ReadServiceAsync<T>(Func<CancellationToken, ValueTask<T>> read, CancellationToken callerToken)
    {
        CancellationTokenSource source;
        TaskCompletionSource settled;
        lock (sync)
        {
            if (disposed || !executionGuard.AcceptsCallbacks || lifetime.IsCancellationRequested || callerToken.IsCancellationRequested)
                return ValueTask.FromCanceled<T>(new CancellationToken(true));
            source = callerToken.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(callerToken) : new CancellationTokenSource();
            pendingActionTokens.Add(source, null);
            settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // Track settlement, not the caller-visible result: handled store failures must not
            // be misreported as an activation retirement failure.
            TrackTask(settled.Task);
        }
        return ReadCoreAsync(read, source, settled);
    }

    private async ValueTask<T> ReadCoreAsync<T>(Func<CancellationToken, ValueTask<T>> read,
        CancellationTokenSource source, TaskCompletionSource settled)
    {
        try
        {
            source.Token.ThrowIfCancellationRequested();
            if (!executionGuard.AcceptsCallbacks) throw new OperationCanceledException(new CancellationToken(true));
            T result = await read(source.Token).ConfigureAwait(false);
            source.Token.ThrowIfCancellationRequested();
            if (!executionGuard.AcceptsCallbacks) throw new OperationCanceledException(new CancellationToken(true));
            return result;
        }
        finally
        {
            Task? cancellation;
            lock (sync) pendingActionTokens.Remove(source, out cancellation);
            if (cancellation is not null)
            {
                try { await cancellation.ConfigureAwait(false); }
                catch (Exception) { /* Retirement observes cancellation failure. */ }
            }
            source.Dispose();
            settled.TrySetResult();
        }
    }

    private sealed class PluginIdentity(HotModuleRegistrationScope owner, WorldId? worldId, IPlayerDirectory? directory,
        IPlayerAccountSessionService? authentication, IAccountService? accounts, IAuthorizationService? groups,
        IPlayerAuthorizationService? playerPermissions) : IPluginIdentity
    {
        public bool AccountsAvailable => accounts is not null;
        public bool GroupsAvailable => groups is not null;
        public bool AuthenticatedPermissionsAvailable => worldId.HasValue && directory is not null &&
            authentication is not null && playerPermissions is not null;

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
