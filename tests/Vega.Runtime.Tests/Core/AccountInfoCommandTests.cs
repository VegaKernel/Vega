using NUnit.Framework;
using Vega.Contracts.Accounts;
using Vega.Contracts.Commands;
using Vega.Modules.Administration;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class AccountInfoCommandTests
{
    private static readonly AccountSnapshot Alice = new(
        new AccountId(42),
        "Alice Smith",
        "moderator",
        DateTimeOffset.Parse("2026-08-25T12:00:00Z"))
    {
        Culture = "ru-RU",
        Revision = 7
    };

    [Test]
    public void DescriptorUsesDedicatedReadOnlyPermissionAndAlias()
    {
        var command = CreateCommand();

        Assert.Multiple(() =>
        {
            Assert.That(command.Descriptor.Name, Is.EqualTo("accountinfo"));
            Assert.That(command.Descriptor.Aliases, Does.Contain("ai"));
            Assert.That(command.Descriptor.Permissions, Is.EqualTo(new[] { AccountInfoCommand.Permission }));
            Assert.That(command.Descriptor.IsAvailableTo(new Principal(AccountInfoCommand.Permission)), Is.True);
            Assert.That(command.Descriptor.IsAvailableTo(new Principal("vega.accounts.manage")), Is.False);
        });
    }

    [Test]
    public async Task JoinsArgumentsSoAccountNamesMayContainSpaces()
    {
        var accounts = new StubAccounts(Alice);
        var command = new AccountInfoCommand(accounts, new StubIdentities([]));

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(new Principal(AccountInfoCommand.Permission), "Alice", "Smith"),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(accounts.LastLookup, Is.EqualTo("Alice Smith"));
            Assert.That(result.Messages, Has.Count.EqualTo(2));
            Assert.That(result.Messages![0].Arguments[0], Is.EqualTo("Alice Smith"));
        });
    }

    [Test]
    public async Task BasicInspectionDoesNotExposeIdentityEvidence()
    {
        var command = CreateCommand();

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(new Principal(AccountInfoCommand.Permission), "Alice", "Smith"),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(result.Messages, Has.Count.EqualTo(2));
            Assert.That(result.Messages!.Any(message => message.Key == "command.accountinfo.identity"), Is.False);
        });
    }

    [Test]
    public async Task IdentityPermissionExposesOnlyLatestIpAndClientUuid()
    {
        var command = CreateCommand();
        var principal = new Principal(AccountInfoCommand.Permission, AccountInfoCommand.IdentityPermission);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(principal, "Alice", "Smith"),
            CancellationToken.None);

        var identity = result.Messages!.Single(message => message.Key == "command.accountinfo.identity");
        Assert.Multiple(() =>
        {
            Assert.That(identity.Arguments[0], Is.EqualTo("10.0.0.2"));
            Assert.That(identity.Arguments[1], Is.EqualTo("uuid-new"));
            Assert.That(identity.Arguments, Does.Not.Contain("10.0.0.1"));
            Assert.That(identity.Arguments, Does.Not.Contain("uuid-old"));
        });
    }

    private static AccountInfoCommand CreateCommand() => new(
        new StubAccounts(Alice),
        new StubIdentities(
        [
            new AccountIdentityEvidenceSnapshot(
                Alice.Id, Alice.Name, AccountIdentityEvidenceKind.IpAddress, "10.0.0.1",
                DateTimeOffset.Parse("2026-08-24T10:00:00Z"),
                DateTimeOffset.Parse("2026-08-24T10:00:00Z"), 1),
            new AccountIdentityEvidenceSnapshot(
                Alice.Id, Alice.Name, AccountIdentityEvidenceKind.IpAddress, "10.0.0.2",
                DateTimeOffset.Parse("2026-08-25T10:00:00Z"),
                DateTimeOffset.Parse("2026-08-25T11:00:00Z"), 2),
            new AccountIdentityEvidenceSnapshot(
                Alice.Id, Alice.Name, AccountIdentityEvidenceKind.ClientUuid, "uuid-old",
                DateTimeOffset.Parse("2026-08-24T10:00:00Z"),
                DateTimeOffset.Parse("2026-08-24T10:00:00Z"), 1),
            new AccountIdentityEvidenceSnapshot(
                Alice.Id, Alice.Name, AccountIdentityEvidenceKind.ClientUuid, "uuid-new",
                DateTimeOffset.Parse("2026-08-25T10:00:00Z"),
                DateTimeOffset.Parse("2026-08-25T12:00:00Z"), 1)
        ]));

    private static CommandInvocation Invocation(ICommandPrincipal principal, params string[] arguments) =>
        new("accountinfo", arguments, principal, NullOutput.Instance);

    private sealed class Principal(params string[] permissions) : ICommandPrincipal
    {
        private readonly HashSet<string> permissions = new(permissions, StringComparer.OrdinalIgnoreCase);
        public string Name => "tester";
        public bool IsServer => false;
        public bool HasPermission(string permission) => permissions.Contains(permission);
    }

    private sealed class StubAccounts(AccountSnapshot account) : IAccountService
    {
        public string? LastLookup { get; private set; }

        public IReadOnlyList<AccountSnapshot> SnapshotAccounts() => [account];

        public ValueTask<AccountSnapshot?> FindByNameAsync(string name, CancellationToken cancellationToken = default)
        {
            LastLookup = name;
            return ValueTask.FromResult<AccountSnapshot?>(
                string.Equals(name, account.Name, StringComparison.OrdinalIgnoreCase) ? account : null);
        }

        public ValueTask<AccountMutationResult> CreateAsync(string name, string password, string groupName, string actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<AccountMutationResult> DeleteAsync(string name, long expectedRevision, string actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<AccountMutationResult> SetGroupAsync(string name, string groupName, long expectedRevision, string actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<AccountMutationResult> SetCultureAsync(string name, string? culture, long expectedRevision, string actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<AccountMutationResult> ChangePasswordAsync(string name, string password, long expectedRevision, string actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<AccountMutationResult> RecordLoginAsync(string name, DateTimeOffset seenAt, long expectedRevision, string actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> VerifyPasswordAsync(string name, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubIdentities(IReadOnlyList<AccountIdentityEvidenceSnapshot> evidence)
        : IAccountIdentityEvidenceService
    {
        public IReadOnlyList<AccountIdentityEvidenceSnapshot> SnapshotEvidence() => evidence;
        public IReadOnlyList<PossibleAccountLinkSnapshot> FindPossibleAccounts(AccountId accountId) => [];
        public ValueTask RecordAsync(AccountId accountId, string accountName, string? clientUuid, string? ipAddress, DateTimeOffset seenAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NullOutput : ICommandOutput
    {
        public static NullOutput Instance { get; } = new();
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
