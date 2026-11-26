using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Modules.Navigation;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class NavigationModuleTests
{
    private static readonly WorldId World = new("test");

    [Test]
    public async Task WarpSendNotifiesOnlyTheSuccessfullyMovedSession()
    {
        string path = Path.Combine(Path.GetTempPath(), "vega-warp-notify-tests", Guid.NewGuid().ToString("N"));
        var fixture = new Fixture();
        var world = new RecordingWorld();
        try
        {
            using var module = fixture.Start(world, new WarpStore(path));
            Assert.That((await fixture.Run(fixture.Alice, "warp", ["add", "Town"], "vega.navigation.warp.manage")).Status,
                Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That((await fixture.Run(fixture.Alice, "warp", ["send", "Bob", "Town"])).Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
            fixture.Navigation.PositionResult = PlayerOperationResult.NotFound();
            Assert.That((await fixture.Run(fixture.Alice, "warp", ["send", "Bob", "Town"], "vega.navigation.others")).Status,
                Is.EqualTo(CommandExecutionStatus.Failed));
            Assert.That(fixture.Messenger.Notifications, Is.Empty);
            fixture.Navigation.PositionResult = PlayerOperationResult.Applied();
            Assert.That((await fixture.Run(fixture.Alice, "warp", ["send", "Bob", "Town"], "vega.navigation.others")).Status,
                Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(fixture.Messenger.Notifications.Single().Player.SessionId, Is.EqualTo(fixture.Bob.SessionId));
            Assert.That(fixture.Messenger.Notifications.Single().Text, Does.Contain("Town"));
            fixture.Messenger.Notifications.Clear();
            fixture.Navigation.AfterPosition = () => fixture.Players.Replace(fixture.Bob, fixture.Bob with { SessionId = PlayerSessionId.New() });
            Assert.That((await fixture.Run(fixture.Alice, "warp", ["send", "Bob", "Town"], "vega.navigation.others")).Status,
                Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(fixture.Messenger.Notifications, Is.Empty);
        }
        finally { if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true); }
    }

    [Test]
    public async Task TeleportResolvesExactNamesAndChecksOtherPlayerPermission()
    {
        var fixture = new Fixture();
        using var module = fixture.Start();
        Assert.That((await fixture.Run(fixture.Alice, "tp", ["Bo"])).ErrorCode, Is.EqualTo("navigation.ambiguous"));
        Assert.That((await fixture.Run(fixture.Alice, "tp", ["Bob"])).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Navigation.Last, Is.EqualTo((fixture.Alice, fixture.Bob)));
        Assert.That((await fixture.Run(fixture.Alice, "tp", ["Bob", "Bobby"])).Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
        Assert.That((await fixture.Run(fixture.Alice, "tp", ["Bob", "Bobby"], "vega.navigation.others")).Status,
            Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Navigation.Last, Is.EqualTo((fixture.Bob, fixture.Bobby)));
    }

    [Test]
    public async Task IncomingTeleportPreferenceBelongsToSessionAndOverrideIsExplicit()
    {
        var fixture = new Fixture();
        using var module = fixture.Start();
        Assert.That((await fixture.Run(fixture.Bob, "tpallow", [])).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await fixture.Run(fixture.Alice, "tp", ["Bob"])).ErrorCode, Is.EqualTo("navigation.teleport_disabled"));
        Assert.That((await fixture.Run(fixture.Alice, "tphere", ["Bob"], "vega.navigation.others")).ErrorCode, Is.EqualTo("navigation.teleport_disabled"));
        Assert.That(fixture.Navigation.Last, Is.Null);
        Assert.That((await fixture.Run(fixture.Alice, "tp", ["Bob"], "vega.navigation.override")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        var replacement = fixture.Bob with { SessionId = PlayerSessionId.New() };
        fixture.Players.Replace(fixture.Bob, replacement);
        Assert.That((await fixture.Run(fixture.Bob, "spawn", [])).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That((await fixture.Run(fixture.Alice, "tp", ["Bob"])).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Navigation.Last, Is.EqualTo((fixture.Alice, replacement)));
    }

    [Test]
    public async Task CoordinatesAndSpawnUseTypedOperationsInCurrentWorld()
    {
        var fixture = new Fixture();
        using var module = fixture.Start();
        Assert.That((await fixture.Run(fixture.Alice, "tppos", ["-1", "100"])).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        Assert.That((await fixture.Run(fixture.Alice, "tppos", ["100", "200"])).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Navigation.Position, Is.EqualTo(new PlayerPosition(1600, 3200)));
        Assert.That((await fixture.Run(fixture.Alice, "spawn", [])).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Navigation.Spawned, Is.EqualTo(fixture.Alice));
        var position = await fixture.Run(fixture.Alice, "pos", ["Bob"]);
        Assert.That(position.Messages!.Single().Arguments, Is.EqualTo(new object[] { "Bob", 100, 100 }));
        Assert.That((await fixture.Run(fixture.Alice, "tp", ["OtherWorld"])).Status, Is.EqualTo(CommandExecutionStatus.Failed));
    }

    [Test]
    public async Task ModuleLifecycleAndConsolePermissionAreEnforced()
    {
        var fixture = new Fixture();
        using (fixture.Start())
        {
            var command = new CommandInvocation("spawn", [], new ServerPrincipal(), new Output()) { WorldId = World };
            Assert.That((await fixture.Dispatcher.DispatchAsync(command)).Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
            Assert.That(fixture.Dispatcher.SnapshotCommands(), Has.Count.EqualTo(7));
        }
        Assert.That(fixture.Dispatcher.SnapshotCommands(), Is.Empty);
    }

    [Test]
    public async Task WarpsPersistAcrossReopenAndAreSeparatedByWorldFileIdentity()
    {
        string path = Path.Combine(Path.GetTempPath(), "vega-warp-tests", Guid.NewGuid().ToString("N"));
        var fixture = new Fixture();
        var world = new RecordingWorld();
        Guid original = world.FileIdentity;
        try
        {
            using (fixture.Start(world, new WarpStore(path)))
            {
                Assert.That((await fixture.Run(fixture.Alice, "warp", ["add", "Town"])).Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
                Assert.That((await fixture.Run(fixture.Alice, "warp", ["add", "Town"], "vega.navigation.warp.manage")).Status, Is.EqualTo(CommandExecutionStatus.Success));
                Assert.That((await fixture.Run(fixture.Alice, "warp", ["add", "town"], "vega.navigation.warp.manage")).Status, Is.EqualTo(CommandExecutionStatus.Failed));
                Assert.That((await fixture.Run(fixture.Alice, "warp", ["hide", "Town", "true"], "vega.navigation.warp.manage")).Status, Is.EqualTo(CommandExecutionStatus.Success));
            }
            using (fixture.Start(world, new WarpStore(path)))
            {
                var list = await fixture.Run(fixture.Alice, "warp", ["list"]);
                Assert.That(list.Messages!.Single().Arguments[2], Is.EqualTo(string.Empty));
                Assert.That((await fixture.Run(fixture.Bob, "warp", ["Town"])).Status, Is.EqualTo(CommandExecutionStatus.Success));
                Assert.That(fixture.Navigation.Position, Is.EqualTo(fixture.Alice.Position));
                world.FileIdentity = Guid.NewGuid();
                Assert.That((await fixture.Run(fixture.Bob, "warp", ["Town"])).ErrorCode, Is.EqualTo("navigation.warp_not_found"));
                world.FileIdentity = original;
                Assert.That((await fixture.Run(fixture.Alice, "warp", ["del", "Town"], "vega.navigation.warp.manage")).Status, Is.EqualTo(CommandExecutionStatus.Success));
            }
        }
        finally { if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true); }
    }

    private sealed class Fixture
    {
        public PlayerSnapshot Alice { get; } = Player(1, "Alice");
        public PlayerSnapshot Bob { get; } = Player(2, "Bob");
        public PlayerSnapshot Bobby { get; } = Player(3, "Bobby");
        public Directory Players { get; }
        public RecordingNavigation Navigation { get; } = new();
        public RecordingMessenger Messenger { get; } = new();
        public CommandDispatcher Dispatcher { get; } = new([]);

        public Fixture() => Players = new([Alice, Bob, Bobby, Player(4, "OtherWorld") with { WorldId = new("different") }]);

        public NavigationModule Start(IWorldEnvironmentService? environment = null, WarpStore? warps = null)
        {
            var module = new NavigationModule(Dispatcher, Players, Navigation, Players, environment, warps, messenger: Messenger);
            module.Start();
            return module;
        }

        public ValueTask<CommandExecutionResult> Run(PlayerSnapshot player, string command, string[] args, params string[] extra) =>
            Dispatcher.DispatchAsync(new(command, args, new Principal(player, extra), new Output()) { WorldId = World });
    }

    private static PlayerSnapshot Player(int slot, string name) => new(new(slot), name, false, 100, 100, 20, 20, new(1600, 1600), false, 0)
        { WorldId = World, SessionId = PlayerSessionId.New() };

    private sealed class Principal(PlayerSnapshot player, string[] extra) : IPlayerCommandPrincipal
    {
        public string Name => player.Name;
        public bool IsServer => false;
        public string CultureSubjectId => player.Name;
        public PlayerId PlayerId => player.Id;
        public PlayerSessionId? SessionId => player.SessionId;
        public string PlayerName => player.Name;
        public string? AccountName => null;
        public bool HasPermission(string permission) => permission is not ("vega.navigation.others" or "vega.navigation.override" or "vega.navigation.warp.manage") || extra.Contains(permission);
    }

    private sealed class ServerPrincipal : ICommandPrincipal
    {
        public string Name => "Server";
        public bool IsServer => true;
        public bool HasPermission(string permission) => true;
    }

    private sealed class Output : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class Directory(List<PlayerSnapshot> players) : IPlayerDirectory, IPlayerSessionLifecycle
    {
        public event Action<PlayerSessionOpened>? Opened { add { } remove { } }
        public event Action<PlayerSessionClosed>? Closed;
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => players;
        public bool TryGet(PlayerId id, out PlayerSnapshot? player) => (player = players.Find(p => p.Id == id)) is not null;
        public bool TryFindExact(string name, out PlayerSnapshot? player) => (player = players.Find(p => p.Name == name)) is not null;
        public void Replace(PlayerSnapshot previous, PlayerSnapshot replacement)
        {
            players[players.IndexOf(previous)] = replacement;
            Closed?.Invoke(new(previous.SessionId!.Value, previous.Id, previous.Name, DateTimeOffset.UtcNow));
        }
    }

    private sealed class RecordingNavigation : IPlayerNavigation
    {
        public PlayerOperationResult PositionResult { get; set; } = PlayerOperationResult.Applied();
        public Action? AfterPosition { get; set; }
        public (PlayerSnapshot, PlayerSnapshot)? Last { get; private set; }
        public PlayerPosition? Position { get; private set; }
        public PlayerSnapshot? Spawned { get; private set; }
        public ValueTask<PlayerOperationResult> ToPositionAsync(PlayerSnapshot player, PlayerPosition destination, CancellationToken cancellationToken = default)
        {
            Position = destination;
            AfterPosition?.Invoke();
            return ValueTask.FromResult(PositionResult);
        }
        public ValueTask<PlayerOperationResult> ToPlayerAsync(PlayerSnapshot player, PlayerSnapshot destination, CancellationToken cancellationToken = default)
        {
            Last = (player, destination);
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }
        public ValueTask<PlayerOperationResult> ToWorldSpawnAsync(PlayerSnapshot player, CancellationToken cancellationToken = default)
        {
            Spawned = player;
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }
        public ValueTask<PlayerOperationResult> ToHomeAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) => ToWorldSpawnAsync(player, cancellationToken);
    }

    private sealed class RecordingMessenger : IPlayerMessenger
    {
        public List<(PlayerSnapshot Player, string Text)> Notifications { get; } = [];
        public ValueTask<PlayerOperationResult> SendMessageAsync(PlayerId id, string message, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Expected session-bound delivery.");
        public ValueTask<PlayerOperationResult> SendMessageAsync(PlayerSnapshot player, string message, CancellationToken cancellationToken = default)
        {
            Notifications.Add((player, message));
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }
    }

    private sealed class RecordingWorld : IWorldEnvironmentService
    {
        public Guid FileIdentity { get; set; } = Guid.NewGuid();
        public ValueTask<WorldEnvironmentSnapshot?> CaptureAsync(WorldId worldId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<WorldEnvironmentSnapshot?>(new(worldId, 4200, 1200, 100, 100, true, 0, 0, false, false, false, 0, 0, false, DateTimeOffset.UtcNow)
                { FileIdentity = FileIdentity });
        public ValueTask<WorldEnvironmentMutationResult> PatchAsync(WorldEnvironmentMutationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
