using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Commands;
using Vega.Modules.Administration;
using Vega.Persistence.Sqlite;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class AccountCommandTests
{
    private string directory = null!;

    [SetUp]
    public void SetUp() => directory = Path.Combine(
        Path.GetTempPath(), "Vega.AccountCommand.Tests", Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Test]
    public async Task TemporaryGroupCommandsGrantListAndRevokePersistentLeaseFromModule()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-23T12:00:00Z");
        var time = new FixedTimeProvider(now);
        SqliteVegaDatabase database = await SqliteVegaDatabase.OpenAsync(
            new SqliteVegaDatabaseOptions(Path.Combine(directory, "vega.db")));
        await database.Authorization.CreateGroupAsync("moderator", "default", "Server");
        AccountMutationResult account = await database.Accounts.CreateAsync(
            "Alice", "correct horse battery staple", "default", "Server");
        var command = new AccountCommand(
            database.Accounts, database.Authorization, database.RoleLeases, time);

        CommandExecutionResult granted = await ExecuteAsync(command, "temp", "Alice", "moderator", "90m");
        RoleLeaseSnapshot? lease = database.RoleLeases.FindActive(account.After!.Id, now);
        CommandExecutionResult listed = await ExecuteAsync(command, "temp-list");
        CommandExecutionResult revoked = await ExecuteAsync(command, "temp-clear", "Alice");

        Assert.Multiple(() =>
        {
            Assert.That(granted.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(lease!.GroupName, Is.EqualTo("moderator"));
            Assert.That(lease.ExpiresAt, Is.EqualTo(now.AddMinutes(90)));
            Assert.That(listed.Messages, Has.Count.EqualTo(1));
            Assert.That(revoked.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(database.RoleLeases.FindActive(account.After.Id, now), Is.Null);
        });
    }

    private static ValueTask<CommandExecutionResult> ExecuteAsync(
        AccountCommand command,
        params string[] arguments) => command.ExecuteAsync(
        new CommandInvocation("account", arguments, ServerPrincipal.Instance, NullOutput.Instance),
        CancellationToken.None);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ServerPrincipal : ICommandPrincipal
    {
        public static ServerPrincipal Instance { get; } = new();
        public string Name => "Server";
        public bool IsServer => true;
        public bool HasPermission(string permission) => true;
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
