using System.Net;
using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Core.Commands.Administration;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class ServerAccessCommandTests
{
    private string tempDirectory = null!;
    private string statePath = null!;

    [SetUp]
    public void SetUp()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), "vega-server-access-tests", Guid.NewGuid().ToString("N"));
        statePath = Path.Combine(tempDirectory, "server-access.json");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true);
    }

    [Test]
    public void StoreNormalizesIpv4CidrPersistsAndRejectsAllIpv6Forms()
    {
        var store = new FileServerAccessStore(statePath);

        Assert.That(store.TryAdd("192.168.10.42/24", out string? ipv4, out _), Is.True);
        Assert.That(store.SetWhitelistEnabled(true), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(ipv4, Is.EqualTo("192.168.10.0/24"));
            Assert.That(store.IsAllowed(IPAddress.Parse("192.168.10.99")), Is.True);
            Assert.That(store.IsAllowed(IPAddress.Parse("192.168.11.1")), Is.False);
            Assert.That(store.IsAllowed(IPAddress.Parse("2001:db8::1")), Is.False);
            Assert.That(FileServerAccessStore.TryNormalizeIpv4Network("2001:db8::1", out _, out _), Is.False);
            Assert.That(FileServerAccessStore.TryNormalizeIpv4Network("2001:db8::/64", out _, out _), Is.False);
            Assert.That(FileServerAccessStore.TryNormalizeIpv4Network("::ffff:192.168.10.42", out _, out _), Is.False);
        });

        var reloaded = new FileServerAccessStore(statePath);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.IsWhitelistEnabled, Is.True);
            Assert.That(reloaded.IsLocked, Is.False);
            Assert.That(reloaded.SnapshotEntries(), Is.EqualTo(new[] { "192.168.10.0/24" }));
        });
    }

    [Test]
    public async Task WhitelistSupportsTshockShortFormAndExplicitManagement()
    {
        var store = new FileServerAccessStore(statePath);
        var dispatcher = new CommandDispatcher([new WhitelistCommand(store)]);
        var denied = new Principal("player", []);
        var admin = new Principal("operator", ["vega.cfg.whitelist"]);

        CommandExecutionResult deniedResult = await dispatcher.DispatchAsync(
            Invocation("whitelist", ["203.0.113.7"], denied));
        CommandExecutionResult added = await dispatcher.DispatchAsync(
            Invocation("whitelist", ["203.0.113.7"], admin));
        CommandExecutionResult ipv6 = await dispatcher.DispatchAsync(
            Invocation("whitelist", ["2001:db8::7"], admin));
        CommandExecutionResult enabled = await dispatcher.DispatchAsync(
            Invocation("whitelist", ["on"], admin));
        CommandExecutionResult listed = await dispatcher.DispatchAsync(
            Invocation("whitelist", ["list"], admin));
        CommandExecutionResult removed = await dispatcher.DispatchAsync(
            Invocation("whitelist", ["del", "203.0.113.7"], admin));

        Assert.Multiple(() =>
        {
            Assert.That(deniedResult.Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
            Assert.That(added.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(ipv6.Status, Is.EqualTo(CommandExecutionStatus.Failed));
            Assert.That(enabled.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(listed.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(listed.Messages.Single().Arguments.Single(), Is.EqualTo("203.0.113.7/32"));
            Assert.That(removed.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(store.SnapshotEntries(), Is.Empty);
        });
    }

    [Test]
    public async Task LockUsesSameAdmissionStoreAndServerLockToggleSemantics()
    {
        var store = new FileServerAccessStore(statePath);
        var dispatcher = new CommandDispatcher([new LockCommand(store)]);
        var denied = new Principal("player", []);
        var admin = new Principal("operator", ["vega.cfg.lock"]);

        CommandExecutionResult deniedResult = await dispatcher.DispatchAsync(Invocation("lock", [], denied));
        CommandExecutionResult toggledOn = await dispatcher.DispatchAsync(Invocation("lock", [], admin));
        CommandExecutionResult status = await dispatcher.DispatchAsync(Invocation("lock", ["status"], admin));
        CommandExecutionResult off = await dispatcher.DispatchAsync(Invocation("lock", ["off"], admin));

        Assert.Multiple(() =>
        {
            Assert.That(deniedResult.Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
            Assert.That(toggledOn.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(status.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(off.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(store.IsLocked, Is.False);
            Assert.That(new FileServerAccessStore(statePath).IsLocked, Is.False);
        });
    }

    [Test]
    public async Task LockMakesWhitelistMandatoryWithoutSeparateWhitelistToggle()
    {
        var store = new FileServerAccessStore(statePath);
        Assert.That(store.TryAdd("10.0.0.0/8", out _, out _), Is.True);
        store.SetLocked(true);
        Assert.That(store.IsWhitelistEnabled, Is.False);

        var blocked = new PlayerId(4);
        var directory = new FakeDirectory(
            Player(blocked, "blocked", "203.0.113.8:7777"),
            Player(new PlayerId(5), "allowed", "10.20.30.40:7777"));
        var operations = new RecordingPlayerOperations();
        var lifecycle = new FakeLifecycle();
        using var enforcer = new PersistentServerAccessEnforcer(
            store, directory, operations, lifecycle, NullLogger.Instance);

        lifecycle.Open(new PlayerSessionOpened(PlayerSessionId.New(), blocked, "blocked", DateTimeOffset.UtcNow));

        (PlayerId id, string reason) kick = await operations.KickObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(kick.id, Is.EqualTo(blocked));
            Assert.That(kick.reason, Does.Contain("locked"));
        });
    }

    [Test]
    public async Task RestrictedAdmissionAllowsMatchingIpv4AndRejectsIpv6Endpoint()
    {
        var store = new FileServerAccessStore(statePath);
        Assert.That(store.TryAdd("203.0.113.0/24", out _, out _), Is.True);
        store.SetWhitelistEnabled(true);

        var allowed = new PlayerId(5);
        var ipv6 = new PlayerId(6);
        var directory = new FakeDirectory(
            Player(allowed, "allowed", "203.0.113.8:7777"),
            Player(ipv6, "ipv6", "[2001:db8::8]:7777"));
        var operations = new RecordingPlayerOperations();
        var lifecycle = new FakeLifecycle();
        using var enforcer = new PersistentServerAccessEnforcer(
            store, directory, operations, lifecycle, NullLogger.Instance);

        lifecycle.Open(new PlayerSessionOpened(PlayerSessionId.New(), allowed, "allowed", DateTimeOffset.UtcNow));
        await Task.Delay(100);
        Assert.That(operations.Kicks, Is.Empty);

        lifecycle.Open(new PlayerSessionOpened(PlayerSessionId.New(), ipv6, "ipv6", DateTimeOffset.UtcNow));
        _ = await operations.KickObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(operations.Kicks.Single().Id, Is.EqualTo(ipv6));
    }

    private static PlayerSnapshot Player(PlayerId id, string name, string endpoint) => new(
        id, name, false, 100, 100, 20, 20, new PlayerPosition(0, 0), false, 0)
    {
        RemoteEndpoint = endpoint,
        WorldId = WorldId.Primary
    };

    private static CommandInvocation Invocation(
        string name,
        IReadOnlyList<string> arguments,
        ICommandPrincipal principal) =>
        new(name, arguments, principal, NullOutput.Instance) { WorldId = WorldId.Primary };

    private sealed class Principal(string name, IReadOnlyCollection<string> permissions) : ICommandPrincipal
    {
        public string Name { get; } = name;
        public bool IsServer => false;
        public bool HasPermission(string permission) => permissions.Contains(permission);
    }

    private sealed class NullOutput : ICommandOutput
    {
        public static NullOutput Instance { get; } = new();
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class FakeDirectory(params PlayerSnapshot[] players) : IPlayerDirectory
    {
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => players;

        public bool TryGet(PlayerId id, out PlayerSnapshot? snapshot)
        {
            snapshot = players.FirstOrDefault(player => player.Id == id);
            return snapshot is not null;
        }

        public bool TryFindExact(string name, out PlayerSnapshot? snapshot)
        {
            snapshot = players.FirstOrDefault(player =>
                string.Equals(name, player.Name, StringComparison.OrdinalIgnoreCase));
            return snapshot is not null;
        }
    }

    private sealed class RecordingPlayerOperations : IPlayerOperations
    {
        public List<(PlayerId Id, string Reason)> Kicks { get; } = [];
        public TaskCompletionSource<(PlayerId Id, string Reason)> KickObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<PlayerOperationResult> KickAsync(
            PlayerId id,
            string reason,
            CancellationToken cancellationToken = default)
        {
            Kicks.Add((id, reason));
            KickObserved.TrySetResult((id, reason));
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }

        public ValueTask<PlayerOperationResult> TeleportAsync(
            PlayerId id,
            PlayerPosition destination,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());
    }

    private sealed class FakeLifecycle : IPlayerSessionLifecycle
    {
        public event Action<PlayerSessionOpened>? Opened;
        public event Action<PlayerSessionClosed>? Closed;
        public void Open(PlayerSessionOpened opened) => Opened?.Invoke(opened);
    }

    private sealed class NullLogger : IOperationsLogger
    {
        public static NullLogger Instance { get; } = new();
        public void Log(
            OperationsLogLevel level,
            string source,
            string text,
            Exception? exception = null,
            string? correlationId = null) { }
    }
}
