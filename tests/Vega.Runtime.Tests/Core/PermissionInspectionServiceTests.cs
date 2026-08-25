using NUnit.Framework;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Commands;
using Vega.Core.Authorization;
using Vega.Core.Commands;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class PermissionInspectionServiceTests
{
    [Test]
    public void InspectMapsPermissionToLiveCommandOwnerAndDirectGroups()
    {
        var dispatcher = new CommandDispatcher([new TestCommand("demo", "vega.demo.use")]);
        var authorization = new StubAuthorization(
            Group("default"),
            Group("moderator", permissions: ["vega.demo.use"]),
            Group("admin", permissions: ["*"]));
        var inspector = new PermissionInspectionService(dispatcher, authorization, new StubAccounts());

        PermissionInspectionSnapshot result = inspector.Inspect("vega.demo.use");

        Assert.Multiple(() =>
        {
            Assert.That(result.Commands, Has.Count.EqualTo(1));
            Assert.That(result.Commands[0].Command, Is.EqualTo("demo"));
            Assert.That(result.Commands[0].OwnerAssembly, Does.Contain("Vega.Runtime.Tests"));
            Assert.That(result.Groups.Select(static group => group.Group),
                Is.EquivalentTo(new[] { "moderator", "admin" }));
        });
    }

    [Test]
    public void RedundancyUsesVegaParentPrecedenceInsteadOfTShockPrefixWildcards()
    {
        var parent = Group(
            "parent",
            permissions: ["vega.demo.use"],
            denied: ["vega.demo.blocked"]);
        var child = Group(
            "child",
            parent: "parent",
            permissions: ["vega.demo.use", "vega.demo.blocked", "vega.other.*"],
            denied: ["vega.demo.blocked-again"]);
        var authorization = new StubAuthorization(parent, child);
        var inspector = new PermissionInspectionService(
            new CommandDispatcher([]),
            authorization,
            new StubAccounts());

        IReadOnlyList<PermissionRedundancy> redundancies = inspector.FindRedundantPermissions("child");

        Assert.Multiple(() =>
        {
            Assert.That(redundancies.Any(item => item.Permission == "vega.demo.use" && item.Effect == "grant"), Is.True);
            Assert.That(redundancies.Any(item => item.Permission == "vega.demo.blocked"), Is.False,
                "A child grant that overrides a parent deny is not redundant.");
            Assert.That(redundancies.Any(item => item.Permission == "vega.other.*"), Is.False,
                "Vega does not treat prefix .* as an authorization wildcard.");
        });
    }

    [Test]
    public void GlobalWildcardMakesSpecificChildGrantRedundant()
    {
        var authorization = new StubAuthorization(
            Group("root", permissions: ["*"]),
            Group("child", parent: "root", permissions: ["vega.demo.use"]));
        var inspector = new PermissionInspectionService(
            new CommandDispatcher([]),
            authorization,
            new StubAccounts());

        PermissionRedundancy duplicate = inspector.FindRedundantPermissions("child").Single();

        Assert.Multiple(() =>
        {
            Assert.That(duplicate.Permission, Is.EqualTo("vega.demo.use"));
            Assert.That(duplicate.InheritedFrom, Is.EqualTo("root"));
        });
    }

    [Test]
    public void GroupTreeAndUsersUseSnapshotsWithoutDatabaseSpecificQueries()
    {
        var authorization = new StubAuthorization(
            Group("default"),
            Group("staff", parent: "default"),
            Group("admin", parent: "staff"));
        var accounts = new StubAccounts(
            Account(1, "alice", "staff"),
            Account(2, "bob", "admin"),
            Account(3, "carol", "staff"));
        var inspector = new PermissionInspectionService(
            new CommandDispatcher([]),
            authorization,
            accounts);

        IReadOnlyList<string> tree = inspector.FormatGroupTree();
        IReadOnlyList<AccountSnapshot> users = inspector.UsersInGroup("staff");

        Assert.Multiple(() =>
        {
            Assert.That(tree, Is.EqualTo(new[]
            {
                "- default [grants:0, denies:0]",
                "  - staff [grants:0, denies:0]",
                "    - admin [grants:0, denies:0]"
            }));
            Assert.That(users.Select(static account => account.Name), Is.EqualTo(new[] { "alice", "carol" }));
        });
    }

    private static GroupSnapshot Group(
        string name,
        string? parent = null,
        string[]? permissions = null,
        string[]? denied = null) =>
        new(name, parent, new HashSet<string>(permissions ?? [], StringComparer.Ordinal))
        {
            DeniedPermissions = new HashSet<string>(denied ?? [], StringComparer.Ordinal)
        };

    private static AccountSnapshot Account(int id, string name, string group) =>
        new(new AccountId(id), name, group, null);

    private sealed class TestCommand(string name, string permission) : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new(name, [], [permission]);

        public ValueTask<CommandExecutionResult> ExecuteAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(CommandExecutionResult.Success);
    }

    private sealed class StubAuthorization(params GroupSnapshot[] groups) : IAuthorizationService
    {
        private readonly GroupSnapshot[] snapshot = groups;

        public bool HasPermission(string groupName, string permission) => false;

        public bool TryGetGroup(string groupName, out GroupSnapshot? group)
        {
            group = snapshot.FirstOrDefault(candidate =>
                candidate.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));
            return group is not null;
        }

        public IReadOnlyList<GroupSnapshot> SnapshotGroups() => snapshot;

        public ValueTask<GroupMutationResult> CreateGroupAsync(
            string name,
            string? parentName,
            string actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<GroupMutationResult> DeleteGroupAsync(
            string name,
            long expectedRevision,
            string actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<GroupMutationResult> SetParentAsync(
            string name,
            string? parentName,
            long expectedRevision,
            string actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<GroupMutationResult> SetPermissionAsync(
            string name,
            string permission,
            PermissionEffect? effect,
            long expectedRevision,
            string actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubAccounts(params AccountSnapshot[] accounts) : IAccountService
    {
        private readonly AccountSnapshot[] snapshot = accounts;

        public IReadOnlyList<AccountSnapshot> SnapshotAccounts() => snapshot;

        public ValueTask<AccountSnapshot?> FindByNameAsync(
            string name,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AccountSnapshot?>(snapshot.FirstOrDefault(account =>
                account.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

        public ValueTask<AccountMutationResult> CreateAsync(
            string name,
            string password,
            string groupName,
            string actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<AccountMutationResult> DeleteAsync(
            string name,
            long expectedRevision,
            string actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<AccountMutationResult> SetGroupAsync(
            string name,
            string groupName,
            long expectedRevision,
            string actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<AccountMutationResult> SetCultureAsync(
            string name,
            string? culture,
            long expectedRevision,
            string actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<AccountMutationResult> ChangePasswordAsync(
            string name,
            string password,
            long expectedRevision,
            string actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<AccountMutationResult> RecordLoginAsync(
            string name,
            DateTimeOffset seenAt,
            long expectedRevision,
            string actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<bool> VerifyPasswordAsync(
            string name,
            string password,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
