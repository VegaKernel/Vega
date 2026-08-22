using NUnit.Framework;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Players;
using Vega.Core.Authorization;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class PlayerSessionAuthorizationServiceTests
{
    [Test]
    public void ActiveRoleLeaseOverridesBaseGroupAndExpirationFallsBackWithoutTimer()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-23T12:00:00Z");
        var time = new AdjustableTimeProvider(now);
        var lifecycle = new SessionLifecycle();
        var authorization = new AuthorizationService();
        var leases = new RoleLeaseService();
        var accountId = new AccountId(42);
        var playerId = new PlayerId(3);
        leases.Current = new RoleLeaseSnapshot(
            Guid.NewGuid(), accountId, "moderator", "Server", now, now.AddMinutes(10), 1, false);
        using var service = new PlayerSessionAuthorizationService(lifecycle, time);

        service.ReplaceAccountGroup(playerId, "default", authorization, accountId, leases);

        Assert.Multiple(() =>
        {
            Assert.That(service.HasPermission(playerId, "vega.moderate"), Is.True);
            Assert.That(service.HasPermission(playerId, "vega.play"), Is.False);
            Assert.That(service.HasPermission(playerId, "vega.group.moderator"), Is.True);
        });

        time.UtcNow = now.AddMinutes(11);

        Assert.Multiple(() =>
        {
            Assert.That(service.HasPermission(playerId, "vega.moderate"), Is.False);
            Assert.That(service.HasPermission(playerId, "vega.play"), Is.True);
            Assert.That(service.HasPermission(playerId, "vega.group.default"), Is.True);
        });
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class SessionLifecycle : IPlayerSessionLifecycle
    {
        public event Action<PlayerSessionOpened>? Opened
        {
            add { }
            remove { }
        }

        public event Action<PlayerSessionClosed>? Closed;

        public void Close(PlayerId playerId) => Closed?.Invoke(new PlayerSessionClosed(
            PlayerSessionId.New(), playerId, "test", DateTimeOffset.UtcNow));
    }

    private sealed class AuthorizationService : IAuthorizationService
    {
        private readonly GroupSnapshot[] groups =
        [
            new("default", null, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vega.play" }),
            new("moderator", null, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vega.moderate" })
        ];

        public bool HasPermission(string groupName, string permission) => groups.Any(group =>
            group.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase) &&
            group.Permissions.Contains(permission));

        public bool TryGetGroup(string groupName, out GroupSnapshot? group)
        {
            group = groups.FirstOrDefault(candidate =>
                candidate.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));
            return group is not null;
        }

        public IReadOnlyList<GroupSnapshot> SnapshotGroups() => groups;

        public ValueTask<GroupMutationResult> CreateGroupAsync(string name, string? parentName, string actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<GroupMutationResult> DeleteGroupAsync(string name, long expectedRevision, string actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<GroupMutationResult> SetParentAsync(string name, string? parentName, long expectedRevision, string actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<GroupMutationResult> SetPermissionAsync(string name, string permission, PermissionEffect? effect, long expectedRevision, string actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RoleLeaseService : IRoleLeaseService
    {
        public RoleLeaseSnapshot? Current { get; set; }

        public IReadOnlyList<RoleLeaseSnapshot> SnapshotActive(DateTimeOffset now) =>
            Current?.IsActive(now) == true ? [Current] : [];

        public RoleLeaseSnapshot? FindActive(AccountId accountId, DateTimeOffset now) =>
            Current?.AccountId == accountId && Current.IsActive(now) ? Current : null;

        public ValueTask<RoleLeaseMutationResult> GrantAsync(AccountId accountId, string groupName, DateTimeOffset issuedAt, DateTimeOffset expiresAt, string actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<RoleLeaseMutationResult> RevokeAsync(Guid leaseId, long expectedRevision, string actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
