using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Core.Modules;
using Vega.PluginSdk;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class PluginSdkContextTests
{
    [Test]
    public async Task ScopeOwnsCommandsHooksTasksAndStorageUntilUnload()
    {
        string dataDirectory = Path.Combine(
            Path.GetTempPath(), "vega-plugin-sdk-tests", Guid.NewGuid().ToString("N"));
        var commands = new RecordingCommandRegistry();
        var players = new RecordingPlayers();
        var dispatcher = new ImmediateDispatcher();
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
        var repeats = 0;
        var applied = 0;

        plugin.Commands.Register(new ProbeCommand());
        plugin.Players.OnJoined(_ => Interlocked.Increment(ref joins));
        plugin.Players.OnLeft(_ => Interlocked.Increment(ref leaves));
        plugin.Tasks.Every("probe-repeat", TimeSpan.FromMilliseconds(5),
            () => Interlocked.Increment(ref repeats));
        plugin.Tasks.RunIo("probe-io",
            _ => ValueTask.FromResult(42),
            value => Volatile.Write(ref applied, value));
        plugin.Storage.SaveJson("settings.json", new ProbeSettings("ready"));

        players.RaiseOpened();
        players.RaiseClosed();
        await WaitUntilAsync(() => Volatile.Read(ref repeats) > 0 && Volatile.Read(ref applied) == 42);
        ProbeSettings stored = plugin.Storage.LoadJson(
            "settings.json",
            () => new ProbeSettings("missing"),
            value => value.Value.Length == 0 ? "value is required" : null);

        Assert.Multiple(() =>
        {
            Assert.That(commands.Active, Is.EqualTo(1));
            Assert.That(players.OpenedSubscribers, Is.EqualTo(1));
            Assert.That(players.ClosedSubscribers, Is.EqualTo(1));
            Assert.That(joins, Is.EqualTo(1));
            Assert.That(leaves, Is.EqualTo(1));
            Assert.That(stored.Value, Is.EqualTo("ready"));
            Assert.That(
                () => plugin.Storage.LoadJson("../escape.json", () => new ProbeSettings("bad")),
                Throws.TypeOf<ArgumentException>());
        });

        await scope.DisposeAsync();
        int repeatsAfterUnload = Volatile.Read(ref repeats);
        players.RaiseOpened();
        players.RaiseClosed();
        await Task.Delay(20);

        Assert.Multiple(() =>
        {
            Assert.That(commands.Active, Is.Zero);
            Assert.That(players.OpenedSubscribers, Is.Zero);
            Assert.That(players.ClosedSubscribers, Is.Zero);
            Assert.That(joins, Is.EqualTo(1));
            Assert.That(leaves, Is.EqualTo(1));
            Assert.That(repeats, Is.EqualTo(repeatsAfterUnload));
        });
    }


    [Test]
    public async Task PluginCallbackFaultsAreContainedAndAttributed()
    {
        string dataDirectory = Path.Combine(
            Path.GetTempPath(), "vega-plugin-sdk-fault-tests", Guid.NewGuid().ToString("N"));
        var commands = new RecordingCommandRegistry();
        var players = new RecordingPlayers();
        var dispatcher = new ImmediateDispatcher();
        var logger = new RecordingLogger();
        var scope = new HotModuleRegistrationScope(
            commands,
            logger,
            "Vega.Test.FaultyPlugin",
            dataDirectory,
            players,
            players,
            players,
            players,
            dispatcher);
        IVegaPluginContext plugin = scope;

        plugin.Commands.Register(new ThrowingCommand());
        plugin.Players.OnJoined(_ => throw new InvalidOperationException("join boom"));
        plugin.Players.OnLeft(_ => throw new InvalidOperationException("left boom"));

        Assert.DoesNotThrow(players.RaiseOpened);
        Assert.DoesNotThrow(players.RaiseClosed);
        Assert.That(
            () => plugin.Tasks.Post("throw-post", () => throw new InvalidOperationException("post boom")),
            Throws.Nothing);

        Assert.That(commands.Handler, Is.Not.Null);
        CommandExecutionResult command = await commands.Handler!.ExecuteAsync(
            new CommandInvocation("sdkboom", [], Principal.Instance, Output.Instance),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(command.Status, Is.EqualTo(CommandExecutionStatus.Failed));
            Assert.That(command.ErrorCode, Is.EqualTo("plugin.callback_failed"));
            Assert.That(logger.Entries, Has.Count.EqualTo(4));
            Assert.That(logger.Entries.All(entry => entry.Source == "plugin:Vega.Test.FaultyPlugin"), Is.True);
            Assert.That(logger.Entries.All(entry => entry.Level == OperationsLogLevel.Error), Is.True);
            Assert.That(logger.Entries.All(entry => entry.Exception is InvalidOperationException), Is.True);
            Assert.That(logger.Entries.Any(entry => entry.Text.Contains("players.joined", StringComparison.Ordinal)), Is.True);
            Assert.That(logger.Entries.Any(entry => entry.Text.Contains("players.left", StringComparison.Ordinal)), Is.True);
            Assert.That(logger.Entries.Any(entry => entry.Text.Contains("task:throw-post", StringComparison.Ordinal)), Is.True);
            Assert.That(logger.Entries.Any(entry => entry.Text.Contains("command:sdkboom", StringComparison.Ordinal)), Is.True);
        });

        await scope.DisposeAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private sealed record ProbeSettings(string Value);

    private sealed class ProbeCommand : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new("sdkprobe", [], []);

        public ValueTask<CommandExecutionResult> ExecuteAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(CommandExecutionResult.Success);
    }


    private sealed class ThrowingCommand : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new("sdkboom", [], []);

        public ValueTask<CommandExecutionResult> ExecuteAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("command boom");
    }

    private sealed class RecordingCommandRegistry : ICommandRegistry
    {
        public int Active { get; private set; }
        public ICommandHandler? Handler { get; private set; }

        public IDisposable Register(ICommandHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            Active++;
            Handler = handler;
            return new Registration(this, handler);
        }

        private sealed class Registration(RecordingCommandRegistry owner, ICommandHandler handler) : IDisposable
        {
            private RecordingCommandRegistry? owner = owner;
            private ICommandHandler? handler = handler;

            public void Dispose()
            {
                RecordingCommandRegistry? current = Interlocked.Exchange(ref owner, null);
                ICommandHandler? currentHandler = Interlocked.Exchange(ref handler, null);
                if (current is not null)
                {
                    current.Active--;
                    if (ReferenceEquals(current.Handler, currentHandler))
                    {
                        current.Handler = null;
                    }
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
        private readonly List<LogEntry> entries = [];

        public IReadOnlyList<LogEntry> Entries => entries;

        public void Log(
            OperationsLogLevel level,
            string source,
            string text,
            Exception? exception = null,
            string? correlationId = null)
        {
            entries.Add(new LogEntry(level, source, text, exception));
        }
    }

    private sealed record LogEntry(
        OperationsLogLevel Level,
        string Source,
        string Text,
        Exception? Exception);

    private sealed class Principal : ICommandPrincipal
    {
        public static Principal Instance { get; } = new();
        public string Name => "server";
        public bool IsServer => true;
        public bool HasPermission(string permission) => true;
    }

    private sealed class Output : ICommandOutput
    {
        public static Output Instance { get; } = new();
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
