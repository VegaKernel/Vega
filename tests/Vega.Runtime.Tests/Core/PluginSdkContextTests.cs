using NUnit.Framework;
using Vega.Contracts.Accounts;
using Vega.Contracts.Commands;
using Vega.Contracts.Items;
using Vega.Contracts.Messaging;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Core.Modules;
using Vega.Core.Commands;
using Vega.PluginSdk;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class PluginSdkContextTests
{
    [Test]
    public async Task ReadOnlyWarpCapabilityCancelsPendingReadsDuringRetirementAndRejectsForeignCatalog()
    {
        var world = new WorldId("warp-test");
        var reader = new WarpReadFixture();
        var scope = new HotModuleRegistrationScope(new RecordingCommandRegistry(), new RecordingLogger(), "Plugin.Warps",
            Path.GetTempPath(), activityWorldId: world, warps: reader);
        var catalog = new WorldWarpCatalogSnapshot(world, Guid.NewGuid(), [new("Home", new(10, 20))]);
        Assert.That(scope.Warps.IsAvailable, Is.True);
        reader.Immediate = catalog with { WorldId = new("foreign") };
        Assert.That(await scope.Warps.SnapshotAsync(), Is.Null);
        reader.Immediate = catalog;
        Assert.That((await scope.Warps.SnapshotAsync())!.Warps, Is.EqualTo(catalog.Warps));
        Assert.That(reader.RequestedWorld, Is.EqualTo(world));
        reader.Immediate = null;
        try
        {
            Task<WorldWarpCatalogSnapshot?> pending = scope.Warps.SnapshotAsync().AsTask();
            Task retirement = scope.DrainForRetirementAsync(CancellationToken.None);
            Assert.That(reader.Cancellation.IsCancellationRequested, Is.True);
            Assert.That(retirement.IsCompleted, Is.False);
            reader.Complete(catalog);
            Assert.CatchAsync<OperationCanceledException>(async () => await pending);
            await retirement;
        }
        finally { reader.Complete(catalog); await scope.DisposeAsync(); }
        Assert.CatchAsync<OperationCanceledException>(async () => await scope.Warps.SnapshotAsync());
    }

    private sealed class WarpReadFixture : IWorldPublicWarpReader
    {
        private readonly TaskCompletionSource<WorldWarpCatalogSnapshot?> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WorldWarpCatalogSnapshot? Immediate { get; set; }
        public WorldId? RequestedWorld { get; private set; }
        public CancellationToken Cancellation { get; private set; }
        public ValueTask<WorldWarpCatalogSnapshot?> ReadAsync(WorldId worldId, CancellationToken cancellationToken = default)
        {
            RequestedWorld = worldId;
            Cancellation = cancellationToken;
            return Immediate is { } value ? ValueTask.FromResult<WorldWarpCatalogSnapshot?>(value) : new(pending.Task);
        }
        public void Complete(WorldWarpCatalogSnapshot? value) => pending.TrySetResult(value);
    }

    [Test]
    public async Task PlayerActionsUseExactSnapshotNativeValidationAndActivationLifetime()
    {
        var world = new WorldId("actions");
        var original = new PlayerSnapshot(new(1), "fixture", false, 100, 100, 20, 20, new(0, 0), false, 0)
            { WorldId = world, SessionId = PlayerSessionId.New() };
        var players = new RecordingPlayers { Current = original };
        var native = new PlayerActionsFixture(players);
        var scope = new HotModuleRegistrationScope(new RecordingCommandRegistry(), new RecordingLogger(), "Plugin.Actions",
            Path.GetTempPath(), playerDirectory: players, playerOperations: native, activityWorldId: world);
        IVegaPluginContext context = scope;
        IPluginPlayerActions actions = context.Players.Actions;
        try
        {
            Assert.That(actions.VitalsAvailable && actions.ItemDeliveryAvailable, Is.True);
            Assert.That((await actions.HealAsync(original, 10)).IsSuccess, Is.True);
            Assert.That((await actions.BuffAsync(original, "1", 10)).IsSuccess, Is.True);
            Assert.That((await actions.KillAsync(original)).IsSuccess, Is.True);
            Assert.That((await actions.SlapAsync(original, 1)).IsSuccess, Is.True);
            Assert.That((await actions.RespawnAsync(original)).IsSuccess, Is.True);
            Assert.That((await actions.GiveAsync(original, "1", 2, 0)).IsSuccess, Is.True);
            Assert.That((await actions.GiveAsync(original, "1", -1)).ErrorCode, Is.EqualTo("item.invalid_request"));
            Assert.That(native.Delivered, Is.EqualTo(new[] { "heal:10", "buff:1:10", "kill", "slap:1", "respawn", "give:1:2:0" }));
            int accepted = native.Accepted;
            Assert.That((await actions.HealAsync(original with { SessionId = null }, 1)).Status, Is.EqualTo(PlayerOperationStatus.PlayerNotFound));
            Assert.That((await actions.HealAsync(original with { WorldId = new("other") }, 1)).Status, Is.EqualTo(PlayerOperationStatus.PlayerNotFound));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Assert.That((await actions.HealAsync(original, 1, cancelled.Token)).Status, Is.EqualTo(PlayerOperationStatus.Cancelled));
            }
            Assert.That(native.Accepted, Is.EqualTo(accepted));

            native.Defer = true;
            Task<PlayerOperationResult> queued = actions.GiveAsync(original, "1").AsTask();
            var replacement = original with { SessionId = PlayerSessionId.New() };
            players.Current = replacement;
            native.Flush();
            Assert.That((await queued).Status, Is.EqualTo(PlayerOperationStatus.PlayerNotFound));
            Assert.That((await actions.HealAsync(original, 1)).Status, Is.EqualTo(PlayerOperationStatus.PlayerNotFound));

            Task<PlayerOperationResult> committed = actions.HealAsync(replacement, 15).AsTask();
            native.Flush(); // service already applied this operation, even if its continuation is still pending
            using var caller = new CancellationTokenSource();
            Task<PlayerOperationResult> pending = actions.HealAsync(replacement, 20).AsTask();
            Task<PlayerOperationResult> linkedPending = actions.GiveAsync(replacement, "1", cancellationToken: caller.Token).AsTask();
            Task retirement = scope.DrainForRetirementAsync(CancellationToken.None);
            Assert.That((await actions.GiveAsync(replacement, "1")).Status, Is.EqualTo(PlayerOperationStatus.Cancelled));
            Assert.That(retirement.IsCompleted, Is.False);
            native.Flush();
            Assert.That((await pending).Status, Is.EqualTo(PlayerOperationStatus.Cancelled));
            Assert.That((await linkedPending).Status, Is.EqualTo(PlayerOperationStatus.Cancelled));
            Assert.That((await committed).Status, Is.EqualTo(PlayerOperationStatus.Applied));
            await retirement;
            Assert.That(native.Delivered, Has.Count.EqualTo(7));
        }
        finally { native.Flush(); await scope.DisposeAsync(); }
        Assert.That((await actions.HealAsync(original, 1)).Status, Is.EqualTo(PlayerOperationStatus.Cancelled));
    }

    [Test]
    public async Task PlayerActionCapabilityDoesNotFallBackToNumericSlotOperations()
    {
        var players = new RecordingPlayers();
        var player = new PlayerSnapshot(new(1), "fixture", false, 100, 100, 20, 20, new(0, 0), false, 0)
            { SessionId = PlayerSessionId.New() };
        players.Current = player;
        await using var scope = new HotModuleRegistrationScope(new RecordingCommandRegistry(), new RecordingLogger(), "Plugin.NoActions",
            Path.GetTempPath(), playerDirectory: players, playerOperations: players);
        var actions = scope.Players.Actions;
        Assert.That(actions.VitalsAvailable || actions.ItemDeliveryAvailable, Is.False);
        Assert.That((await actions.HealAsync(player, 1)).Status, Is.EqualTo(PlayerOperationStatus.DispatcherUnavailable));
        Assert.That((await actions.GiveAsync(player, "1")).Status, Is.EqualTo(PlayerOperationStatus.DispatcherUnavailable));
    }

    private sealed class PlayerActionsFixture(RecordingPlayers players) : IPlayerOperations, IPlayerVitalOperations, IPlayerItemDelivery
    {
        public bool Defer { get; set; }
        public int Accepted { get; private set; }
        public List<string> Delivered { get; } = [];
        private readonly Queue<Action> pending = new();
        public void Flush() { while (pending.TryDequeue(out Action? deliver)) deliver(); }
        private ValueTask<PlayerOperationResult> Deliver(PlayerSnapshot player, string name, CancellationToken token, bool valid = true)
        {
            Accepted++;
            PlayerOperationResult Apply()
            {
                if (token.IsCancellationRequested) return PlayerOperationResult.Cancelled();
                if (players.Current?.SessionId != player.SessionId || players.Current?.WorldId != player.WorldId) return PlayerOperationResult.NotFound();
                if (!valid) return PlayerOperationResult.Failed("item.invalid_request");
                Delivered.Add(name);
                return PlayerOperationResult.Applied();
            }
            if (!Defer) return ValueTask.FromResult(Apply());
            var completion = new TaskCompletionSource<PlayerOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Enqueue(() => completion.TrySetResult(Apply()));
            return new(completion.Task);
        }
        public ValueTask<PlayerOperationResult> HealAsync(PlayerSnapshot p, int amount, CancellationToken t = default) => Deliver(p, $"heal:{amount}", t);
        public ValueTask<PlayerOperationResult> BuffAsync(PlayerSnapshot p, string buff, int seconds, CancellationToken t = default) => Deliver(p, $"buff:{buff}:{seconds}", t);
        public ValueTask<PlayerOperationResult> KillAsync(PlayerSnapshot p, CancellationToken t = default) => Deliver(p, "kill", t);
        public ValueTask<PlayerOperationResult> SlapAsync(PlayerSnapshot p, int damage, CancellationToken t = default) => Deliver(p, $"slap:{damage}", t);
        public ValueTask<PlayerOperationResult> RespawnAsync(PlayerSnapshot p, CancellationToken t = default) => Deliver(p, "respawn", t);
        public ValueTask<PlayerOperationResult> GiveAsync(PlayerSnapshot p, string item, int stack, int prefix, CancellationToken t = default) => Deliver(p, $"give:{item}:{stack}:{prefix}", t, stack > 0);
        public ValueTask<PlayerOperationResult> KickAsync(PlayerId player, string reason, CancellationToken t = default) => throw new NotSupportedException();
        public ValueTask<PlayerOperationResult> TeleportAsync(PlayerId player, PlayerPosition destination, CancellationToken t = default) => throw new NotSupportedException();
    }

    [Test]
    public async Task CommandCompletionObserverRetainsOnlyExactInvocationSessionAndGuardedStatus()
    {
        var world = new WorldId("fixture");
        var handler = new DeferredCommand();
        var commands = new CommandDispatcher([handler]);
        var dispatcher = new QueuedDispatcher();
        await using var scope = new HotModuleRegistrationScope(commands, new RecordingLogger(), "Plugin.CommandActivity",
            Path.GetTempPath(), gameThread: dispatcher, activityWorldId: world);
        Assert.That(scope.Activity.CommandCompletionAvailable, Is.True);
        var observed = new List<CommandCompletedObservation>();
        scope.Activity.OnCommandCompleted(observed.Add);
        var principal = new ActivityPrincipal(new PlayerId(4), PlayerSessionId.New());
        var invocation = new CommandInvocation("deferred", ["private"], principal, Output.Instance) { WorldId = world };
        Task<CommandExecutionResult> execution = commands.DispatchAsync(invocation).AsTask();
        dispatcher.Drain();
        Assert.That(observed, Is.Empty);
        handler.Complete();
        Assert.That((await execution).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        await commands.DispatchAsync(invocation with { WorldId = new("other") });
        dispatcher.Drain();
        Assert.That(observed, Is.EqualTo(new[] { new CommandCompletedObservation(world, "deferred", principal.PlayerId,
            principal.SessionId, false, CommandExecutionStatus.Failed) }));
        await scope.DisposeAsync();
        await commands.DispatchAsync(invocation);
        dispatcher.Drain();
        Assert.That(observed, Has.Count.EqualTo(1));
    }

    private sealed class ActivityPrincipal(PlayerId playerId, PlayerSessionId sessionId) : IPlayerCommandPrincipal
    {
        public PlayerId PlayerId => playerId;
        public PlayerSessionId? SessionId => sessionId;
        public string PlayerName => "fixture";
        public string? AccountName => null;
        public string Name => PlayerName;
        public string CultureSubjectId => "fixture";
        public bool IsServer => false;
        public bool HasPermission(string permission) => true;
    }

    private sealed class DeferredCommand : ICommandHandler
    {
        private readonly TaskCompletionSource<CommandExecutionResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CommandDescriptor Descriptor { get; } = new("deferred", [], []);
        public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token) => new(completion.Task);
        public void Complete() => completion.SetResult(CommandExecutionResult.Failed("fixture"));
    }

    [Test]
    public async Task ActivityObserversAreWorldScopedExactSessionGuardedAndAutomaticallyRetired()
    {
        var world = new WorldId("fixture");
        var player = new PlayerSnapshot(new(1), "fixture", false, 100, 100, 20, 20, new(0, 0), false, 0)
            { WorldId = world, SessionId = PlayerSessionId.New() };
        var players = new RecordingPlayers { Current = player };
        var auth = new ActivityAuth();
        var chat = new ActivityChat();
        var dispatcher = new QueuedDispatcher();
        var scope = new HotModuleRegistrationScope(new RecordingCommandRegistry(), new RecordingLogger(), "Plugin.Activity",
            Path.GetTempPath(), playerDirectory: players, gameThread: dispatcher, activityWorldId: world,
            authentication: auth, publicChat: chat);
        IVegaPluginContext plugin = scope;
        Assert.That(plugin.Activity.AuthenticationAvailable, Is.True);
        Assert.That(plugin.Activity.PublicChatAvailable, Is.True);
        Assert.That(plugin.Activity.CommandCompletionAvailable, Is.False);
        var established = new List<PlayerAccountBinding>();
        var ended = new List<PlayerAuthenticationEnded>();
        var observed = new List<PublicChatAcceptedObservation>();
        plugin.Activity.OnAuthenticationEstablished(established.Add);
        plugin.Activity.OnAuthenticationEnded(ended.Add);
        plugin.Activity.OnPublicChatAccepted(_ => throw new InvalidOperationException("fixture callback fault"));
        plugin.Activity.OnPublicChatAccepted(observed.Add);
        var old = new PlayerAccountBinding(player.Id, player.SessionId!.Value, new(7), Guid.NewGuid());
        auth.Establish(old);
        Assert.That(plugin.Activity.TryGetAuthentication(player, out var current), Is.True);
        Assert.That(current, Is.EqualTo(old));
        auth.End(old);
        var next = player with { SessionId = PlayerSessionId.New() };
        players.Current = next;
        var active = old with { SessionId = next.SessionId!.Value, AuthenticationId = Guid.NewGuid() };
        auth.Establish(active);
        var message = new PublicChatAcceptedObservation(world, next.Id, next.SessionId!.Value, "hi", 1, false);
        chat.Publish(message with { WorldId = new("other") });
        chat.Publish(message);
        Assert.That(established, Is.Empty);
        Assert.That(plugin.Activity.TryGetAuthentication(player, out _), Is.False);
        dispatcher.Drain();
        Assert.That(established, Is.EqualTo(new[] { active }));
        Assert.That(ended, Is.EqualTo(new[] { new PlayerAuthenticationEnded(old, PlayerAuthenticationEndReason.Disconnected) }));
        Assert.That(observed, Is.EqualTo(new[] { message }));
        Assert.That(scope.Faults.Count, Is.EqualTo(1));
        chat.Publish(message);
        await scope.DisposeAsync();
        dispatcher.Drain();
        Assert.That(auth.Subscribers + chat.Subscribers, Is.Zero);
        Assert.That(observed, Has.Count.EqualTo(1));
        Assert.That(plugin.Activity.TryGetAuthentication(next, out _), Is.False);
    }

    private sealed class ActivityAuth : IPlayerAccountSessionService
    {
        public event Action<PlayerAccountBinding>? BindingEstablished;
        public event Action<PlayerAuthenticationEnded>? AuthenticationEnded;
        private PlayerAccountBinding? binding;
        public int Subscribers => (BindingEstablished?.GetInvocationList().Length ?? 0) + (AuthenticationEnded?.GetInvocationList().Length ?? 0);
        public void Establish(PlayerAccountBinding value) { binding = value; BindingEstablished?.Invoke(value); }
        public void End(PlayerAccountBinding value) { binding = null; AuthenticationEnded?.Invoke(new(value, PlayerAuthenticationEndReason.Disconnected)); }
        public bool TryGetBinding(PlayerSnapshot player, out PlayerAccountBinding current)
        {
            current = binding.GetValueOrDefault();
            return binding.HasValue && current.PlayerId == player.Id && current.SessionId == player.SessionId;
        }
        public bool TryRunWithBinding<T>(PlayerAccountBinding expected, Func<T> action, out T result)
        {
            if (binding != expected) { result = default!; return false; }
            result = action(); return true;
        }
        public bool TryGetAccount(PlayerId playerId, out AccountSnapshot? account) { account = null; return false; }
        public ValueTask<bool> SetCultureAsync(PlayerId playerId, string? culture, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    }

    private sealed class ActivityChat : IPublicChatObservations
    {
        public event Action<PublicChatAcceptedObservation>? Accepted;
        public int Subscribers => Accepted?.GetInvocationList().Length ?? 0;
        public void Publish(PublicChatAcceptedObservation value) => Accepted?.Invoke(value);
    }

    private sealed class QueuedDispatcher : IGameThreadDispatcher
    {
        private readonly Queue<Action> pending = new();
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => false;
        public void AssertGameThread() => throw new NotSupportedException();
        public bool TryPost(string name, Action action) { pending.Enqueue(action); return true; }
        public bool TrySchedule(string name, Func<bool> step) => throw new NotSupportedException();
        public void Drain() { while (pending.TryDequeue(out Action? action)) action(); }
    }

    [Test]
    public async Task NativeEntryAndClosingEventsAreGuardedAndOwnedResourcesRetireInReverseOrder()
    {
        var players = new RecordingPlayers
        {
            Current = new PlayerSnapshot(new(1), "fixture", false, 100, 100, 20, 20, new(0, 0), false, 0)
                { SessionId = PlayerSessionId.New() }
        };
        var logger = new RecordingLogger();
        var scope = new HotModuleRegistrationScope(new RecordingCommandRegistry(), logger, "Plugin.Resources",
            Path.GetTempPath(), playerDirectory: players, playerLifecycle: players);
        IVegaPluginContext plugin = scope;
        int entered = 0, closing = 0;
        plugin.Players.OnEnteredWorld(player => { Assert.That(player.SessionId, Is.EqualTo(players.Current!.SessionId)); entered++; });
        plugin.Players.OnClosing(_ => throw new InvalidOperationException("fixture callback failure"));
        plugin.Players.OnClosing(_ => closing++);
        Assert.DoesNotThrow(players.RaiseEntered);
        Assert.DoesNotThrow(players.RaiseClosing);
        Assert.That(entered, Is.EqualTo(1)); Assert.That(closing, Is.EqualTo(1));
        Assert.That(scope.Faults.Count, Is.EqualTo(1));
        Assert.That(scope.Faults.Operation, Is.EqualTo("players.closing"));

        var order = new List<string>();
        var resource = new DisposableProbe(() => order.Add("sync"));
        Assert.That(plugin.Resources.Own(resource), Is.SameAs(resource));
        plugin.Resources.OwnAsync(new AsyncDisposableProbe(() => order.Add("async")));
        plugin.Resources.OnCleanup(() => { order.Add("cleanup"); throw new InvalidOperationException("fixture cleanup fault"); });
        var error = Assert.ThrowsAsync<AggregateException>(async () => await scope.DisposeAsync());
        Assert.That(error!.Flatten().InnerExceptions.Any(ex => ex.Message == "fixture cleanup fault"), Is.True);
        Assert.That(order, Is.EqualTo(new[] { "cleanup", "async", "sync" }));
        players.RaiseEntered(); players.RaiseClosing();
        Assert.That(entered, Is.EqualTo(1)); Assert.That(closing, Is.EqualTo(1));
        Assert.That(players.EnteredSubscribers + players.ClosingSubscribers, Is.Zero);
    }

    [Test]
    public async Task SessionBoundMessageNeverFallsBackToReusedPlayerSlot()
    {
        var players = new RecordingPlayers();
        var first = new PlayerSnapshot(new(1), "Viewer", false, 100, 100, 20, 20, new(0, 0), false, 0)
        { SessionId = PlayerSessionId.New() };
        players.Current = first;
        var scope = new HotModuleRegistrationScope(new RecordingCommandRegistry(), new RecordingLogger(), "Plugin.SessionMessages",
            Path.GetTempPath(), players, players);
        IVegaPluginContext plugin = scope;
        try
        {
            Assert.That((await plugin.Players.MessageAsync(first, "Welcome")).IsSuccess, Is.True);
            Assert.That(players.ExactMessages, Is.EqualTo(new[] { first }));
            var replacement = first with { SessionId = PlayerSessionId.New() };
            players.Current = replacement;
            plugin.Players.Message(replacement, "New session");
            Assert.That((await plugin.Players.MessageAsync(first, "Old session")).Status, Is.EqualTo(PlayerOperationStatus.PlayerNotFound));
            Assert.That((await plugin.Players.MessageAsync(replacement with { WorldId = new("other") }, "Other world")).IsSuccess, Is.False);
            players.SupportsSessionBoundMessages = false;
            Assert.That((await plugin.Players.MessageAsync(replacement, "Unsupported host")).Status,
                Is.EqualTo(PlayerOperationStatus.DispatcherUnavailable));
            Assert.That(players.ExactMessages, Is.EqualTo(new[] { first, replacement }));
            Assert.That(players.RawMessages, Is.Zero);
        }
        finally { await scope.DisposeAsync(); }
    }

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

        var faults = logger.Entries.Where(entry => entry.Source == "plugin:Vega.Test.FaultyPlugin").ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(command.Status, Is.EqualTo(CommandExecutionStatus.Failed));
            Assert.That(command.ErrorCode, Is.EqualTo("plugin.callback_failed"));
            Assert.That(faults, Has.Length.EqualTo(4));
            Assert.That(faults.All(entry => entry.Level == OperationsLogLevel.Error), Is.True);
            Assert.That(faults.All(entry => entry.Exception is InvalidOperationException), Is.True);
            Assert.That(faults.Any(entry => entry.Text.Contains("players.joined", StringComparison.Ordinal)), Is.True);
            Assert.That(faults.Any(entry => entry.Text.Contains("players.left", StringComparison.Ordinal)), Is.True);
            Assert.That(faults.Any(entry => entry.Text.Contains("task:throw-post", StringComparison.Ordinal)), Is.True);
            Assert.That(faults.Any(entry => entry.Text.Contains("command:sdkboom", StringComparison.Ordinal)), Is.True);
        });

        await scope.DisposeAsync();
    }

    [Test]
    public async Task BreakerUsesRollingWindowAndWaitsForAlreadyRunningCallbacks()
    {
        var logger = new RecordingLogger();
        var clock = new ManualFaultClock();
        int trips = 0;
        var guard = new PluginExecutionGuard(logger, "faulting", 2, TimeSpan.FromSeconds(1), () => trips++, clock);
        var healthy = new PluginExecutionGuard(logger, "healthy");
        Assert.That(guard.TryInvoke("first", () => throw new InvalidOperationException("test one")), Is.False);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.That(guard.TryInvoke("second", () => throw new InvalidOperationException("test two")), Is.False);
        Assert.That(guard.IsQuarantined, Is.False);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task active = Task.Run(() => guard.TryInvoke("in-flight", () => { entered.Set(); release.Wait(); }));
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(guard.TryInvoke("third", () => throw new InvalidOperationException("test three")), Is.False);
            Assert.That(guard.IsQuarantined, Is.True);
            Assert.That(trips, Is.EqualTo(1));
            bool called = false;
            Assert.That(guard.TryInvoke("suppressed", () => called = true), Is.False);
            Assert.That(called, Is.False);
            Assert.That(healthy.TryInvoke("ordinary", () => called = true), Is.True);
            Assert.That(called, Is.True);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task drain = guard.WaitForIdleAsync(deadline.Token);
            Assert.That(drain.IsCompleted, Is.False);
            release.Set();
            await drain;
        }
        finally { release.Set(); await active; }
    }

    private sealed class ManualFaultClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }

    [Test]
    public async Task ManagedConfigurationDiagnosticsCountPluginFaultsButNotOrdinaryValidationFailures()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-config-guard-" + Guid.NewGuid().ToString("N"));
        string plugin = "Vega.Test.ConfigGuard." + Guid.NewGuid().ToString("N");
        var scope = new HotModuleRegistrationScope(new RecordingCommandRegistry(), new RecordingLogger(), plugin, root,
            pluginConfigurationRoot: root, faultThreshold: 2);
        try
        {
            var config = scope.Configuration.Use("settings.json", () => 1,
                value => value == 2 ? throw new InvalidOperationException("Test-only validator fault") : value < 1 ? "Invalid value" : null);
            Assert.That(VegaManagedConfigurationDiagnostics.Replace(plugin, "settings.json", "0").Succeeded, Is.False);
            Assert.That(scope.Faults.Count, Is.Zero);
            Assert.That(VegaManagedConfigurationDiagnostics.Replace(plugin, "settings.json", "2").Succeeded, Is.False);
            Assert.That(scope.Faults.Count, Is.EqualTo(1));
            Assert.That(VegaManagedConfigurationDiagnostics.Replace(plugin, "settings.json", "3").Succeeded, Is.True);
            Assert.That(config.Current, Is.EqualTo(3));
            Assert.That(VegaManagedConfigurationDiagnostics.Replace(plugin, "settings.json", "2").Succeeded, Is.False);
            Assert.That(scope.Faults.Quarantined, Is.True);
            Assert.That(VegaManagedConfigurationDiagnostics.Replace(plugin, "settings.json", "4").Succeeded, Is.False);
            Assert.That(config.Current, Is.EqualTo(3));
        }
        finally { await scope.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task CompletedPluginIoIsNotRetainedAndUnloadStillWaitsForActiveWork()
    {
        string dataDirectory = Path.Combine(Path.GetTempPath(), "vega-plugin-task-tests", Guid.NewGuid().ToString("N"));
        var scope = new HotModuleRegistrationScope(new RecordingCommandRegistry(), new RecordingLogger(),
            "Vega.Test.TaskLifetime", dataDirectory, gameThread: new ImmediateDispatcher());
        var io = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int applied = 0;

        for (int index = 0; index < 2000; index++)
            scope.Tasks.RunIo("completed", _ => ValueTask.FromResult(1), _ => applied++);
        Assert.That(applied, Is.EqualTo(2000));
        Assert.That(scope.TrackedTaskCount, Is.Zero);

        scope.Tasks.RunIo("async-completed", _ => new ValueTask<int>(io.Task), _ => Interlocked.Increment(ref applied));
        Assert.That(scope.TrackedTaskCount, Is.EqualTo(1));
        io.SetResult(1);
        await WaitUntilAsync(() =>
        {
            scope.Tasks.RunIo("prune", _ => ValueTask.FromResult(0), _ => { });
            return scope.TrackedTaskCount == 0;
        });
        Assert.That(applied, Is.EqualTo(2001));

        var active = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.Tasks.RunIo("active", _ => new ValueTask<int>(active.Task), _ => Interlocked.Increment(ref applied));
        Task unload = scope.DisposeAsync().AsTask();
        Assert.That(unload.IsCompleted, Is.False);
        active.SetResult(1);
        await unload.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(applied, Is.EqualTo(2001));
    }

    [Test]
    public void CompletedFaultedTaskRemainsVisibleToUnload()
    {
        string dataDirectory = Path.Combine(Path.GetTempPath(), "vega-plugin-task-fault-tests", Guid.NewGuid().ToString("N"));
        var scope = new HotModuleRegistrationScope(new RecordingCommandRegistry(), new FaultingIoLogger(),
            "Vega.Test.TaskFault", dataDirectory, gameThread: new ImmediateDispatcher());
        // Plugin operation exceptions are normally logged inside the task. A logging fault is
        // an example of the remaining task fault that existing unload aggregation must retain.
        scope.Tasks.RunIo("fault", _ => ValueTask.FromException<int>(new InvalidOperationException("io")), _ => { });
        scope.Tasks.RunIo("completed", _ => ValueTask.FromResult(1), _ => { });
        Assert.That(scope.TrackedTaskCount, Is.EqualTo(1));
        var failure = Assert.ThrowsAsync<AggregateException>(async () => await scope.DisposeAsync());
        Assert.That(failure!.InnerExceptions, Has.Some.TypeOf<IOException>());
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
        IPlayerSessionLifecycle, IPlayerSessionEnteredWorld, IPlayerSessionClosing
    {
        public event Action<PlayerSessionOpened>? Opened;
        public event Action<PlayerSessionClosed>? Closed;
        public event Action<PlayerSnapshot>? EnteredWorld;
        public event Action<PlayerSessionClosed>? Closing;

        public int OpenedSubscribers => Opened?.GetInvocationList().Length ?? 0;
        public int ClosedSubscribers => Closed?.GetInvocationList().Length ?? 0;
        public int EnteredSubscribers => EnteredWorld?.GetInvocationList().Length ?? 0;
        public int ClosingSubscribers => Closing?.GetInvocationList().Length ?? 0;
        public void RaiseEntered() { if (Current is { } player) EnteredWorld?.Invoke(player); }
        public void RaiseClosing() { if (Current is { SessionId: { } session } player) Closing?.Invoke(new(session, player.Id, player.Name, DateTimeOffset.UtcNow)); }

        public PlayerSnapshot? Current { get; set; }
        public bool SupportsSessionBoundMessages { get; set; } = true;
        public List<PlayerSnapshot> ExactMessages { get; } = [];
        public int RawMessages { get; private set; }
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => Current is { } player ? [player] : [];

        public bool TryGet(PlayerId id, out PlayerSnapshot? player)
        {
            player = Current is { } current && current.Id == id ? current : null;
            return player is not null;
        }

        public bool TryFindExact(string name, out PlayerSnapshot? player)
        {
            player = Current is { } current && current.Name == name ? current : null;
            return player is not null;
        }

        public ValueTask<PlayerOperationResult> SendMessageAsync(
            PlayerId id,
            string message,
            CancellationToken cancellationToken = default)
        {
            RawMessages++;
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }

        public ValueTask<PlayerOperationResult> SendMessageAsync(PlayerSnapshot player, string message, CancellationToken cancellationToken = default)
        {
            ExactMessages.Add(player);
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }

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

    private sealed class DisposableProbe(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    private sealed class AsyncDisposableProbe(Action dispose) : IAsyncDisposable
    { public async ValueTask DisposeAsync() { await Task.Yield(); dispose(); } }

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

    private sealed class FaultingIoLogger : IOperationsLogger
    {
        public void Log(OperationsLogLevel level, string source, string text,
            Exception? exception = null, string? correlationId = null)
        {
            if (text.Contains("Plugin callback 'io:fault' failed", StringComparison.Ordinal))
                throw new IOException("Logging fault");
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
