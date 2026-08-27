using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Events;
using Vega.Contracts.Messaging;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Core.Events;
using Vega.Core.Modules;
using Vega.PluginSdk;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class PluginSdkContextTests
{
    [Test]
    public async Task ScopeOwnsCommandsHooksTasksEventsAndStorageUntilUnload()
    {
        string dataDirectory = Path.Combine(
            Path.GetTempPath(), "vega-plugin-sdk-tests", Guid.NewGuid().ToString("N"));
        var commands = new RecordingCommandRegistry();
        var players = new RecordingPlayers();
        var dispatcher = new ImmediateDispatcher();
        var eventHub = new AsyncEventHub();
        VegaPluginEventRuntime.Configure(eventHub);
        var scope = new HotModuleRegistrationScope(
            commands,
            new RecordingLogger(),
            "Vega.Test.PluginSdk",
            dataDirectory,
            players,
            players,
            players,
            players,
            dispatcher);
        IVegaPluginContext plugin = scope;
        var joins = 0;
        var leaves = 0;
        var eventCalls = 0;
        var repeats = 0;
        var applied = 0;

        plugin.Commands.Register(
            "sdkprobe",
            (_, _) => ValueTask.FromResult(CommandExecutionResult.Success),
            aliases: ["probe"],
            permissions: ["vega.test.sdkprobe"],
            syntax: "sdkprobe");
        plugin.Players.OnJoined(_ => Interlocked.Increment(ref joins));
        plugin.Players.OnLeft(_ => Interlocked.Increment(ref leaves));
        plugin.Events.On<ProbeEvent>(_ => Interlocked.Increment(ref eventCalls));
        plugin.Tasks.Every("probe-repeat", TimeSpan.FromMilliseconds(5),
            () => Interlocked.Increment(ref repeats));
        plugin.Tasks.RunIo("probe-io",
            _ => ValueTask.FromResult(42),
            value => Volatile.Write(ref applied, value));
        plugin.Storage.SaveJson("settings.json", new ProbeSettings("ready"));

        players.RaiseOpened();
        players.RaiseClosed();
        await eventHub.DispatchAsync(new ProbeEvent());
        await WaitUntilAsync(() => Volatile.Read(ref repeats) > 0 && Volatile.Read(ref applied) == 42);
        ProbeSettings stored = plugin.Storage.LoadJson(
            "settings.json",
            () => new ProbeSettings("missing"),
            value => value.Value.Length == 0 ? "value is required" : null);

        Assert.Multiple(() =>
        {
            Assert.That(commands.Active, Is.EqualTo(1));
            Assert.That(commands.LastHandler, Is.Not.Null);
            Assert.That(commands.LastHandler!.Descriptor.Name, Is.EqualTo("sdkprobe"));
            Assert.That(commands.LastHandler.Descriptor.Aliases, Is.EqualTo(new[] { "probe" }));
            Assert.That(commands.LastHandler.Descriptor.Permissions, Is.EqualTo(new[] { "vega.test.sdkprobe" }));
            Assert.That(commands.LastHandler.Descriptor.Syntax, Is.EqualTo("sdkprobe"));
            Assert.That(players.OpenedSubscribers, Is.EqualTo(1));
            Assert.That(players.ClosedSubscribers, Is.EqualTo(1));
            Assert.That(joins, Is.EqualTo(1));
            Assert.That(leaves, Is.EqualTo(1));
            Assert.That(eventCalls, Is.EqualTo(1));
            Assert.That(stored.Value, Is.EqualTo("ready"));
            Assert.That(
                () => plugin.Storage.LoadJson("../escape.json", () => new ProbeSettings("bad")),
                Throws.TypeOf<ArgumentException>());
        });

        await scope.DisposeAsync();
        int repeatsAfterUnload = Volatile.Read(ref repeats);
        players.RaiseOpened();
        players.RaiseClosed();
        await eventHub.DispatchAsync(new ProbeEvent());
        await Task.Delay(20);

        Assert.Multiple(() =>
        {
            Assert.That(commands.Active, Is.Zero);
            Assert.That(players.OpenedSubscribers, Is.Zero);
            Assert.That(players.ClosedSubscribers, Is.Zero);
            Assert.That(joins, Is.EqualTo(1));
            Assert.That(leaves, Is.EqualTo(1));
            Assert.That(eventCalls, Is.EqualTo(1));
            Assert.That(repeats, Is.EqualTo(repeatsAfterUnload));
        });
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private sealed record ProbeEvent;
    private sealed record ProbeSettings(string Value);

    private sealed class RecordingCommandRegistry : ICommandRegistry
    {
        public int Active { get; private set; }
        public ICommandHandler? LastHandler { get; private set; }

        public IDisposable Register(ICommandHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            LastHandler = handler;
            Active++;
            return new Registration(this);
        }

        private sealed class Registration(RecordingCommandRegistry owner) : IDisposable
        {
            private RecordingCommandRegistry? owner = owner;

            public void Dispose()
            {
                RecordingCommandRegistry? current = Interlocked.Exchange(ref owner, null);
                if (current is not null)
                {
                    current.Active--;
                }
            }
        }
    }

    private sealed class RecordingPlayers :
        IPlayerDirectory,
        IPlayerMessenger,
        IPlayerOperations,
        IPlayerSessionLifecycle
    {
        public event Action<PlayerSessionOpened>? Opened;
        public event Action<PlayerSessionClosed>? Closed;

        public int OpenedSubscribers => Opened?.GetInvocationList().Length ?? 0;
        public int ClosedSubscribers => Closed?.GetInvocationList().Length ?? 0;

        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => [];

        public bool TryGet(PlayerId id, out PlayerSnapshot? player)
        {
            player = null;
            return false;
        }

        public bool TryFindExact(string name, out PlayerSnapshot? player)
        {
            player = null;
            return false;
        }

        public ValueTask<PlayerOperationResult> SendMessageAsync(
            PlayerId id,
            string message,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());

        public ValueTask<PlayerOperationResult> KickAsync(
            PlayerId id,
            string reason,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());

        public ValueTask<PlayerOperationResult> TeleportAsync(
            PlayerId id,
            PlayerPosition destination,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());

        public void RaiseOpened() => Opened?.Invoke(new PlayerSessionOpened(
            new PlayerSessionId(Guid.NewGuid()),
            new PlayerId(1),
            "player",
            DateTimeOffset.UtcNow));

        public void RaiseClosed() => Closed?.Invoke(new PlayerSessionClosed(
            PlayerSessionId.New(),
            new PlayerId(1),
            "player",
            DateTimeOffset.UtcNow));
    }

    private sealed class ImmediateDispatcher : IGameThreadDispatcher
    {
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => true;
        public void AssertGameThread() { }

        public bool TryPost(string name, Action action)
        {
            action();
            return true;
        }

        public bool TrySchedule(string name, Func<bool> step)
        {
            step();
            return true;
        }
    }

    private sealed class RecordingLogger : IOperationsLogger
    {
        public void Log(
            OperationsLogLevel level,
            string source,
            string text,
            Exception? exception = null,
            string? correlationId = null)
        {
        }
    }
}
