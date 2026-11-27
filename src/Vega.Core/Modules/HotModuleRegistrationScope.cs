using System.Text.Json;
using Vega.Contracts.Commands;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

internal sealed partial class HotModuleRegistrationScope : IHotModuleContext, IVegaPluginContext, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object sync = new();
    private readonly List<IDisposable> registrations = [];
    private readonly List<Task> tasks = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly IOperationsLogger logger;
    private readonly string logSource;
    private readonly PluginExecutionGuard executionGuard;
    private Exception? earlyCancellationFailure;
    private Task? retirementCancellation;
    private bool disposed;

    public HotModuleRegistrationScope(
        ICommandRegistry commands,
        IOperationsLogger logger,
        string pluginName,
        string dataDirectory,
        IPlayerDirectory? playerDirectory = null,
        IPlayerMessenger? messenger = null,
        IPlayerOperations? playerOperations = null,
        IPlayerSessionLifecycle? playerLifecycle = null,
        IGameThreadDispatcher? gameThread = null,
        ISandboxDirectory? sandboxDirectory = null,
        ISandboxControlService? sandboxControl = null,
        IVegaDebugContext? debug = null,
        string? pluginConfigurationRoot = null,
        Guid? activationId = null, int faultThreshold = 5, TimeSpan? faultWindow = null,
        Action? onQuarantine = null, IWorldGameplayEvents? worldEvents = null,
        WorldId? activityWorldId = null,
        Vega.Contracts.Accounts.IPlayerAccountSessionService? authentication = null,
        Vega.Contracts.Messaging.IPublicChatObservations? publicChat = null,
        Vega.Contracts.Accounts.IAccountService? accounts = null,
        Vega.Contracts.Authorization.IAuthorizationService? groups = null,
        Vega.Contracts.Authorization.IPlayerAuthorizationService? playerPermissions = null,
        IWorldPublicWarpReader? warps = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        logSource = $"plugin:{pluginName}";
        ActivationId = activationId ?? Guid.NewGuid();
        executionGuard = new PluginExecutionGuard(logger, pluginName, faultThreshold, faultWindow, onQuarantine);

        string configurationDirectory = Path.Combine(
            pluginConfigurationRoot ?? Path.Combine(AppContext.BaseDirectory, "config", "plugins"),
            SafeDirectoryName(pluginName));

        VegaDebugProcess.EnsureConfigured(logger);
        VegaDebugProcess.RegisterRoot("commands", commands);
        if (playerDirectory is not null) VegaDebugProcess.RegisterRoot("players", playerDirectory);
        if (messenger is not null) VegaDebugProcess.RegisterRoot("player-messenger", messenger);
        if (playerOperations is not null) VegaDebugProcess.RegisterRoot("player-operations", playerOperations);
        if (playerLifecycle is not null) VegaDebugProcess.RegisterRoot("player-lifecycle", playerLifecycle);
        if (gameThread is not null) VegaDebugProcess.RegisterRoot("game-thread", gameThread);
        if (sandboxDirectory is not null) VegaDebugProcess.RegisterRoot("sandboxes", sandboxDirectory);
        if (sandboxControl is not null) VegaDebugProcess.RegisterRoot("sandbox-control", sandboxControl);

        Commands = new PluginCommands(this, commands);
        Players = new PluginPlayers(this, playerDirectory, messenger, playerOperations, playerLifecycle, activityWorldId);
        WorldEvents = new PluginWorldEvents(this, worldEvents);
        Activity = new PluginActivity(this, activityWorldId, playerDirectory, gameThread,
            authentication, publicChat, commands as ICommandCompletionObservations);
        Identity = new PluginIdentity(this, activityWorldId, playerDirectory, authentication, accounts, groups, playerPermissions);
        Warps = new PluginWarps(this, activityWorldId, warps);
        Tasks = new PluginTasks(this, gameThread);
        Resources = new PluginResources(this);
        Configuration = new PluginConfiguration(this, pluginName, configurationDirectory);
        Storage = new PluginStorage(dataDirectory);
        Log = new PluginLog(logger, logSource);
        Sandboxes = new PluginSandboxFacade(
            sandboxDirectory ?? VegaPluginSandboxRuntime.Directory,
            sandboxControl ?? VegaPluginSandboxRuntime.Control);
        Debug = debug ?? VegaDebugProcess.CreateContext(pluginName);
        VegaDebugProcess.RegisterRoot($"plugin:{pluginName}", this);
        VegaDebugProcess.RegisterRoot($"plugin-config:{pluginName}", Configuration);
    }

    public IPluginCommands Commands { get; }
    public IPluginPlayers Players { get; }
    public IPluginWorldEvents WorldEvents { get; }
    public IPluginActivity Activity { get; }
    public IPluginIdentity Identity { get; }
    public IPluginWarps Warps { get; }
    public IPluginTasks Tasks { get; }
    public IPluginResources Resources { get; }
    public IPluginConfiguration Configuration { get; }
    public IPluginStorage Storage { get; }
    public IPluginLog Log { get; }
    public IPluginSandboxes Sandboxes { get; }
    public IVegaDebugContext Debug { get; }
    internal Guid ActivationId { get; }
    internal PluginExecutionGuard.FaultSnapshot Faults => executionGuard.Snapshot();
    internal async Task DrainForRetirementAsync(CancellationToken token)
    {
        executionGuard.StopAcceptingCallbacks();
        // All retirement attempts share the original cancellation operation. A timeout
        // must not allow a retry to skip an outstanding non-cooperative cancel callback.
        Task cancellation;
        lock (sync) cancellation = BeginRetirementCancellationLocked();
        try { await cancellation.WaitAsync(token).ConfigureAwait(false); }
        catch (Exception ex) when (!token.IsCancellationRequested) { earlyCancellationFailure = ex; }
        await executionGuard.WaitForIdleAsync(token).ConfigureAwait(false);
        Task[] running;
        lock (sync) running = tasks.ToArray();
        try { await Task.WhenAll(running).WaitAsync(token).ConfigureAwait(false); }
        catch (Exception) when (!token.IsCancellationRequested) { /* Dispose observes tracked failures. */ }
    }

    public IDisposable RegisterCommand(ICommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Track(((PluginCommands)Commands).RegisterCore(handler));
    }

    public T Track<T>(T registration) where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            registrations.Add(registration);
            return registration;
        }
    }

    public IDisposable RegisterCleanup(Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        return Track(new CleanupRegistration(cleanup));
    }

    public async ValueTask DisposeAsync()
    {
        executionGuard.StopAcceptingCallbacks();
        IDisposable[] owned;
        Task[] running;
        Task cancellation;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            cancellation = BeginRetirementCancellationLocked();
            owned = registrations.ToArray();
            running = tasks.ToArray();
            registrations.Clear();
            tasks.Clear();
        }

        List<Exception>? failures = earlyCancellationFailure is { } prior ? [prior] : null;
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception ex) { if (earlyCancellationFailure is null) (failures ??= []).Add(ex); }
        try
        {
            await Task.WhenAll(running).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Cancellation while stopping a plugin must not skip its owned scene/command cleanup.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            (failures ??= []).Add(ex);
        }

        for (var index = owned.Length - 1; index >= 0; index--)
        {
            try
            {
                if (owned[index] is AsyncResourceRegistration asyncResource)
                    await asyncResource.DisposeAsync().ConfigureAwait(false);
                else owned[index].Dispose();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        lifetime.Dispose();
        if (failures is not null)
        {
            throw new AggregateException("One or more plugin-owned operations failed to detach.", failures);
        }
    }

    private void TrackTask(Task task) => TrackTask(() => task);

    private void TrackTask(Func<Task> start)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // Completed successful operations have no remaining unload work. Retain active,
            // canceled and faulted operations for the existing shutdown/exception behavior.
            tasks.RemoveAll(static tracked => tracked.IsCompletedSuccessfully);
            Task task = start();
            if (!task.IsCompletedSuccessfully)
                tasks.Add(task);
        }
    }

    internal int TrackedTaskCount
    {
        get { lock (sync) { tasks.RemoveAll(static tracked => tracked.IsCompletedSuccessfully); return tasks.Count; } }
    }

    private sealed class PluginCommands(HotModuleRegistrationScope owner, ICommandRegistry commands) : IPluginCommands
    {
        public void Register(ICommandHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            owner.Track(RegisterCore(handler));
        }

        public IDisposable RegisterCore(ICommandHandler handler) =>
            commands.Register(new GuardedCommandHandler(owner, handler));

        private sealed class GuardedCommandHandler : ICommandHandler
        {
            private readonly HotModuleRegistrationScope owner;
            private readonly ICommandHandler inner;
            private readonly string operation;

            public GuardedCommandHandler(HotModuleRegistrationScope owner, ICommandHandler inner)
            {
                this.owner = owner;
                this.inner = inner;
                Descriptor = inner.Descriptor;
                operation = $"command:{Descriptor.Name}";
            }

            public CommandDescriptor Descriptor { get; }

            public ValueTask<CommandExecutionResult> ExecuteAsync(
                CommandInvocation invocation,
                CancellationToken cancellationToken) =>
                owner.executionGuard.InvokeCommandAsync(
                    operation,
                    inner,
                    invocation,
                    cancellationToken);
        }
    }

    private sealed class PluginPlayers(
        HotModuleRegistrationScope owner,
        IPlayerDirectory? directory,
        IPlayerMessenger? messenger,
        IPlayerOperations? operations,
        IPlayerSessionLifecycle? lifecycle, WorldId? worldId) : IPluginPlayers
    {
        public IReadOnlyList<PlayerSnapshot> Online => directory?.SnapshotOnlinePlayers() ?? [];
        public IPluginPlayerActions Actions { get; } = new PluginPlayerActions(owner, directory, worldId,
            operations as IPlayerVitalOperations, operations as Vega.Contracts.Items.IPlayerItemDelivery);

        public bool TryGet(PlayerId id, out PlayerSnapshot? player)
        {
            if (directory is not null)
            {
                return directory.TryGet(id, out player);
            }

            player = null;
            return false;
        }

        public bool TryFindExact(string name, out PlayerSnapshot? player)
        {
            ArgumentNullException.ThrowIfNull(name);
            if (directory is not null)
            {
                return directory.TryFindExact(name, out player);
            }

            player = null;
            return false;
        }

        public void Message(PlayerId id, string message)
        {
            ArgumentNullException.ThrowIfNull(message);
            owner.TrackTask(SendMessageCoreAsync(id, message));
        }

        public void Message(PlayerSnapshot player, string message)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(message);
            owner.TrackTask(SendMessageCoreAsync(player.Id, message, player));
        }

        public ValueTask<PlayerOperationResult> MessageAsync(
            PlayerId id,
            string message,
            CancellationToken cancellationToken = default) =>
            messenger?.SendMessageAsync(id, message, cancellationToken) ??
            ValueTask.FromResult(PlayerOperationResult.DispatcherUnavailable());

        public ValueTask<PlayerOperationResult> MessageAsync(PlayerSnapshot player, string message, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(message);
            if (player.SessionId is not { } session || directory is null || !directory.TryGet(player.Id, out var current) ||
                current is null || current.SessionId != session || current.WorldId != player.WorldId)
                return ValueTask.FromResult(PlayerOperationResult.NotFound());
            return messenger?.SupportsSessionBoundMessages == true
                ? messenger.SendMessageAsync(player, message, cancellationToken)
                : ValueTask.FromResult(PlayerOperationResult.DispatcherUnavailable());
        }

        private async Task SendMessageCoreAsync(PlayerId id, string message, PlayerSnapshot? expected = null)
        {
            try
            {
                PlayerOperationResult result = await (expected is null ? MessageAsync(id, message, owner.lifetime.Token) :
                    MessageAsync(expected, message, owner.lifetime.Token))
                    .ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    owner.logger.Log(OperationsLogLevel.Warning, owner.logSource,
                        $"Player message was not applied: {result.ErrorCode ?? result.Status.ToString()}.");
                }
            }
            catch (OperationCanceledException) when (owner.lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                owner.logger.Log(OperationsLogLevel.Error, owner.logSource,
                    "Player message failed.", ex);
            }
        }

        public ValueTask<PlayerOperationResult> KickAsync(
            PlayerId id,
            string reason,
            CancellationToken cancellationToken = default) =>
            operations?.KickAsync(id, reason, cancellationToken) ??
            ValueTask.FromResult(PlayerOperationResult.DispatcherUnavailable());

        public ValueTask<PlayerOperationResult> TeleportAsync(
            PlayerId id,
            PlayerPosition destination,
            CancellationToken cancellationToken = default) =>
            operations?.TeleportAsync(id, destination, cancellationToken) ??
            ValueTask.FromResult(PlayerOperationResult.DispatcherUnavailable());

        public void OnJoined(Action<PlayerSessionOpened> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (lifecycle is null)
            {
                return;
            }

            Action<PlayerSessionOpened> guarded = opened =>
                owner.executionGuard.TryInvoke("players.joined", handler, opened);
            lifecycle.Opened += guarded;
            owner.Track(new CleanupRegistration(() => lifecycle.Opened -= guarded));
        }

        public void OnLeft(Action<PlayerSessionClosed> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (lifecycle is null)
            {
                return;
            }

            Action<PlayerSessionClosed> guarded = closed =>
                owner.executionGuard.TryInvoke("players.left", handler, closed);
            lifecycle.Closed += guarded;
            owner.Track(new CleanupRegistration(() => lifecycle.Closed -= guarded));
        }

        public void OnEnteredWorld(Action<PlayerSnapshot> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (lifecycle is not IPlayerSessionEnteredWorld entering) return;
            Action<PlayerSnapshot> guarded = player => owner.executionGuard.TryInvoke("players.entered-world", handler, player);
            entering.EnteredWorld += guarded;
            owner.Track(new CleanupRegistration(() => entering.EnteredWorld -= guarded));
        }

        public void OnClosing(Action<PlayerSessionClosed> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (lifecycle is not IPlayerSessionClosing closing) return;
            Action<PlayerSessionClosed> guarded = player => owner.executionGuard.TryInvoke("players.closing", handler, player);
            closing.Closing += guarded;
            owner.Track(new CleanupRegistration(() => closing.Closing -= guarded));
        }
    }

    private sealed class PluginResources(HotModuleRegistrationScope owner) : IPluginResources
    {
        public T Own<T>(T resource) where T : class, IDisposable => owner.Track(resource);
        public T OwnAsync<T>(T resource) where T : class, IAsyncDisposable
        {
            ArgumentNullException.ThrowIfNull(resource);
            owner.Track(new AsyncResourceRegistration(resource));
            return resource;
        }
        public void OnCleanup(Action cleanup) => owner.RegisterCleanup(cleanup);
    }

    private sealed class AsyncResourceRegistration(IAsyncDisposable resource) : IDisposable, IAsyncDisposable
    {
        private IAsyncDisposable? owned = resource;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref owned, null)?.DisposeAsync() ?? ValueTask.CompletedTask;
        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class PluginWorldEvents(HotModuleRegistrationScope owner, IWorldGameplayEvents? events) : IPluginWorldEvents
    {
        public void OnTickCompleted(Action<WorldTickCompletedEvent> handler) => Register("world.tick.completed", handler);
        public void OnSaveCompleted(Action<WorldSaveCompletedEvent> handler) => Register("world.save.completed", handler);
        public void OnNpcCreating(Action<NpcCreatingEvent> handler) => Register("world.npc.creating", handler);
        public void OnNpcKilled(Action<NpcKilledEvent> handler) => Register("world.npc.killed", handler);
        public void OnNpcStrikeCompleted(Action<NpcStrikeCompletedEvent> handler) => Register("world.npc.strike-completed", handler);
        public void OnNpcTransforming(Action<NpcTransformingEvent> handler) => Register("world.npc.transforming", handler);
        public void OnNpcLootDropped(Action<NpcLootDroppedEvent> handler) => Register("world.npc.loot", handler);
        public void OnProjectileSynchronized(Action<ProjectileSynchronizedEvent> handler) => Register("world.projectile.sync", handler);
        public void OnProjectileRemoved(Action<ProjectileRemovedEvent> handler) => Register("world.projectile.removed", handler);
        public void OnNpcActorInteracted(Action<NpcActorInteractedEvent> handler) => Register("world.npc.actor.interacted", handler);
        public void OnNpcActorPurchased(Action<NpcActorPurchasedEvent> handler) => Register("world.npc.actor.purchased", handler);
        public void OnNpcActorDialogueChoice(Action<NpcActorDialogueChoiceEvent> handler) => Register("world.npc.actor.dialogue", handler);
        public void OnNpcActorQuest(Action<NpcActorQuestEvent> handler) => Register("world.npc.actor.quest", handler);
        public void OnNpcActorThink(Action<NpcActorThinkEvent> handler) => Register("world.npc.actor.think", handler);
        public void AllowNpcTransform(Func<NpcTransformingEvent, bool> allow) => RegisterPolicy("world.npc.transform.policy", allow);
        public void AllowNpcLoot(Func<NpcLootCreatingEvent, bool> allow) => RegisterPolicy("world.npc.loot.policy", allow);

        private void RegisterPolicy<T>(string operation, Func<T, bool> allow) where T : struct
        {
            ArgumentNullException.ThrowIfNull(allow);
            if (events is null) throw new NotSupportedException("World gameplay events are unavailable in this host.");
            lock (owner.sync)
            {
                ObjectDisposedException.ThrowIf(owner.disposed, owner);
                owner.Track(events.SubscribePolicy<T>(value => owner.lifetime.IsCancellationRequested || owner.executionGuard.Allows(operation, allow, value)));
            }
        }

        private void Register<T>(string operation, Action<T> handler) where T : struct
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (events is null) throw new NotSupportedException("World gameplay events are unavailable in this host.");
            lock (owner.sync)
            {
                ObjectDisposedException.ThrowIf(owner.disposed, owner);
                var registration = events.Subscribe<T>(value =>
                {
                    if (!owner.lifetime.IsCancellationRequested) owner.executionGuard.TryInvoke(operation, handler, value);
                });
                owner.Track(registration);
            }
        }
    }

    private sealed class PluginTasks(HotModuleRegistrationScope owner, IGameThreadDispatcher? gameThread) : IPluginTasks
    {
        // One Vega native world per process. Reserve a core for its authoritative
        // simulation, bound admitted work globally and per activation.
        private static readonly SemaphoreSlim CpuLanes = new(Math.Clamp(Environment.ProcessorCount - 1, 1, 4));
        private const int WorldCpuCapacity = 32;
        private const int PluginCpuCapacity = 4;
        private static int worldCpuPending;
        private int pluginCpuPending;

        public bool Post(string name, Action action)
        {
            Validate(name, action);
            if (!owner.executionGuard.AcceptsCallbacks) return false;
            return gameThread?.TryPost(name, () =>
            {
                if (!owner.lifetime.IsCancellationRequested)
                {
                    owner.executionGuard.TryInvoke($"task:{name}", action);
                }
            }) ?? false;
        }

        public void Every(string name, TimeSpan interval, Action action)
        {
            Validate(name, action);
            if (interval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(interval), "The interval must be positive.");
            }

            owner.executionGuard.TryInvoke($"schedule:{name}", () => owner.TrackTask(() => RepeatAsync(name, interval, action)));
        }

        public void RunIo<T>(string name, Func<CancellationToken, ValueTask<T>> operation, Action<T> apply)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(apply);
            owner.executionGuard.TryInvoke($"schedule:{name}", () => owner.TrackTask(() => Task.Run(() => RunIoCoreAsync(name, operation, apply))));
        }

        public bool TryRunCpu<T>(string name, Func<CancellationToken, T> compute, Action<T> apply)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(compute);
            ArgumentNullException.ThrowIfNull(apply);
            if (gameThread is null || !owner.executionGuard.AcceptsCallbacks) return false;
            bool accepted = false;
            owner.executionGuard.TryInvoke($"schedule:{name}", () =>
            {
                if (!Reserve(ref pluginCpuPending, PluginCpuCapacity)) return;
                if (!Reserve(ref worldCpuPending, WorldCpuCapacity)) { Interlocked.Decrement(ref pluginCpuPending); return; }
                try
                {
                    owner.TrackTask(() => Task.Run(() => RunCpuCoreAsync(name, compute, apply)));
                    accepted = true;
                }
                catch
                {
                    Interlocked.Decrement(ref worldCpuPending);
                    Interlocked.Decrement(ref pluginCpuPending);
                    throw;
                }
            });
            return accepted;
        }

        private static bool Reserve(ref int pending, int capacity)
        {
            if (Interlocked.Increment(ref pending) <= capacity) return true;
            Interlocked.Decrement(ref pending);
            return false;
        }

        private async Task RunCpuCoreAsync<T>(string name, Func<CancellationToken, T> compute, Action<T> apply)
        {
            CancellationToken token = owner.lifetime.Token;
            try
            {
                await CpuLanes.WaitAsync(token).ConfigureAwait(false);
                T result;
                try { token.ThrowIfCancellationRequested(); result = compute(token); }
                finally { CpuLanes.Release(); }
                token.ThrowIfCancellationRequested();
                if (!Post(name, () => apply(result)))
                    owner.logger.Log(OperationsLogLevel.Warning, owner.logSource, $"CPU plugin result '{name}' could not be posted to the game thread.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { owner.executionGuard.Record($"cpu:{name}", ex); }
            finally
            {
                Interlocked.Decrement(ref worldCpuPending);
                Interlocked.Decrement(ref pluginCpuPending);
            }
        }

        private async Task RepeatAsync(string name, TimeSpan interval, Action action)
        {
            try
            {
                using var timer = new PeriodicTimer(interval);
                while (await timer.WaitForNextTickAsync(owner.lifetime.Token).ConfigureAwait(false))
                {
                    if (!Post(name, action))
                    {
                        owner.logger.Log(OperationsLogLevel.Warning, owner.logSource,
                            $"Scheduled plugin action '{name}' could not be posted to the game thread.");
                    }
                }
            }
            catch (OperationCanceledException) when (owner.lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                owner.logger.Log(OperationsLogLevel.Error, owner.logSource,
                    $"Scheduled plugin action '{name}' failed.", ex);
            }
        }

        private async Task RunIoCoreAsync<T>(
            string name,
            Func<CancellationToken, ValueTask<T>> operation,
            Action<T> apply)
        {
            try
            {
                owner.lifetime.Token.ThrowIfCancellationRequested();
                T result = await operation(owner.lifetime.Token).ConfigureAwait(false);
                if (!Post(name, () => apply(result)))
                {
                    owner.logger.Log(OperationsLogLevel.Warning, owner.logSource,
                        $"Async plugin result '{name}' could not be posted to the game thread.");
                }
            }
            catch (OperationCanceledException) when (owner.lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                owner.executionGuard.Record($"io:{name}", ex);
            }
        }

        private static void Validate(string name, Action action)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(action);
        }
    }

    private sealed class PluginConfiguration(
        HotModuleRegistrationScope owner,
        string pluginName,
        string directory) : IPluginConfiguration
    {
        private readonly object sync = new();
        private readonly Dictionary<string, object> configs = new(StringComparer.OrdinalIgnoreCase);

        public string Directory { get; } = Path.GetFullPath(directory);

        public IPluginConfig<T> Use<T>(
            string fileName,
            Func<T> defaults,
            Func<T, string?>? validate = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
            ArgumentNullException.ThrowIfNull(defaults);
            string path = Resolve(fileName);
            string logicalName = fileName.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            lock (sync)
            {
                if (configs.TryGetValue(path, out object? existing))
                {
                    return existing as IPluginConfig<T> ?? throw new InvalidOperationException(
                        $"Plugin configuration '{fileName}' is already registered with another value type.");
                }

                var config = new PluginConfig<T>(logicalName, path, defaults, validate);
                configs.Add(path, config);
                owner.Track(VegaManagedConfigurationDiagnostics.Register(
                    pluginName,
                    logicalName,
                    path,
                    () => InvokeDiagnostic($"config:{logicalName}:read", config.ReadJson),
                    json => InvokeDiagnostic($"config:{logicalName}:validate", () => config.ValidateJson(json)),
                    json => InvokeDiagnostic($"config:{logicalName}:replace", () => { config.ReplaceJson(json); return true; }),
                    () => InvokeDiagnostic($"config:{logicalName}:reload", () => { config.ReloadUntyped(); return true; })));
                return config;
            }
        }

        private TResult InvokeDiagnostic<TResult>(string operation, Func<TResult> callback)
        {
            TResult result = default!;
            Exception? expected = null;
            bool succeeded = owner.executionGuard.TryInvoke(operation, () =>
            {
                try { result = callback(); }
                // Invalid operator input or storage failure is not a plugin callback fault.
                catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or NotSupportedException)
                { expected = ex; }
            });
            if (!succeeded) throw new InvalidDataException("Plugin configuration callback failed or is quarantined.");
            if (expected is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(expected).Throw();
            return result;
        }

        private string Resolve(string fileName)
        {
            if (Path.IsPathRooted(fileName))
            {
                throw new ArgumentException("Plugin configuration paths must be relative.", nameof(fileName));
            }

            string path = Path.GetFullPath(Path.Combine(Directory, fileName));
            string prefix = Directory.EndsWith(Path.DirectorySeparatorChar)
                ? Directory
                : Directory + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Plugin configuration paths cannot leave the plugin configuration directory.", nameof(fileName));
            }

            return path;
        }
    }

    private sealed class PluginConfig<T> : IPluginConfig<T>
    {
        private readonly object sync = new();
        private readonly string path;
        private readonly Func<T> defaults;
        private readonly Func<T, string?>? validate;
        private T current;

        public PluginConfig(string fileName, string path, Func<T> defaults, Func<T, string?>? validate)
        {
            FileName = fileName;
            this.path = path;
            this.defaults = defaults;
            this.validate = validate;
            current = Load(writeDefaults: true);
        }

        public string FileName { get; }

        public T Current
        {
            get
            {
                lock (sync)
                {
                    return current;
                }
            }
        }

        public T Reload()
        {
            lock (sync)
            {
                current = Load(writeDefaults: false);
                return current;
            }
        }

        public void Save(T value)
        {
            lock (sync)
            {
                Validate(value);
                WriteAtomic(value);
                current = value;
            }
        }

        public void Save()
        {
            lock (sync)
            {
                Validate(current);
                WriteAtomic(current);
            }
        }

        public string ReadJson()
        {
            lock (sync)
            {
                return JsonSerializer.Serialize(current, JsonOptions);
            }
        }

        public string? ValidateJson(string json)
        {
            ArgumentNullException.ThrowIfNull(json);
            try
            {
                T value = JsonSerializer.Deserialize<T>(json, JsonOptions) ??
                    throw new InvalidDataException($"Plugin configuration '{path}' contains null.");
                Validate(value);
                return null;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
            {
                return ex.Message;
            }
        }

        public void ReplaceJson(string json)
        {
            ArgumentNullException.ThrowIfNull(json);
            T value = JsonSerializer.Deserialize<T>(json, JsonOptions) ??
                throw new InvalidDataException($"Plugin configuration '{path}' contains null.");
            Save(value);
        }

        public void ReloadUntyped() => Reload();

        private T Load(bool writeDefaults)
        {
            T value;
            if (File.Exists(path))
            {
                value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ??
                    throw new InvalidDataException($"Plugin configuration '{path}' contains null.");
            }
            else
            {
                value = defaults();
                Validate(value);
                if (writeDefaults)
                {
                    WriteAtomic(value);
                }
            }

            Validate(value);
            return value;
        }

        private void Validate(T value)
        {
            string? error = validate?.Invoke(value);
            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidDataException($"Plugin configuration '{path}' is invalid: {error}");
            }
        }

        private void WriteAtomic(T value)
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory is not null)
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
    }

    private sealed class PluginStorage : IPluginStorage
    {
        public PluginStorage(string dataDirectory) => DataDirectory = Path.GetFullPath(dataDirectory);

        public string DataDirectory { get; }

        public T LoadJson<T>(string fileName, Func<T> defaults, Func<T, string?>? validate = null)
        {
            ArgumentNullException.ThrowIfNull(defaults);
            string path = Resolve(fileName);
            T value = File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ??
                    throw new InvalidDataException($"Plugin JSON '{path}' contains null.")
                : defaults();
            string? error = validate?.Invoke(value);
            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidDataException($"Plugin JSON '{path}' is invalid: {error}");
            }

            return value;
        }

        public void SaveJson<T>(string fileName, T value)
        {
            string path = Resolve(fileName);
            Directory.CreateDirectory(DataDirectory);
            string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }

        private string Resolve(string fileName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
            if (Path.IsPathRooted(fileName))
            {
                throw new ArgumentException("Plugin storage paths must be relative.", nameof(fileName));
            }

            string path = Path.GetFullPath(Path.Combine(DataDirectory, fileName));
            string prefix = DataDirectory.EndsWith(Path.DirectorySeparatorChar)
                ? DataDirectory
                : DataDirectory + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Plugin storage paths cannot leave the plugin data directory.", nameof(fileName));
            }

            return path;
        }
    }

    private sealed class PluginSandboxFacade(
        ISandboxDirectory? directory,
        ISandboxControlService? control) : IPluginSandboxes
    {
        public bool CanControl => control is not null;

        public IReadOnlyList<SandboxStatus> Snapshot() => directory?.Snapshot() ?? [];

        public ValueTask<SandboxOperationResult> CreateAsync(
            SandboxCreateRequest request,
            CancellationToken cancellationToken = default) =>
            control?.CreateAsync(request, cancellationToken) ?? Unavailable();

        public ValueTask<SandboxOperationResult> StartAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) =>
            control?.StartAsync(worldId, cancellationToken) ?? Unavailable();

        public ValueTask<SandboxOperationResult> StopAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) =>
            control?.StopAsync(worldId, cancellationToken) ?? Unavailable();

        public ValueTask<SandboxOperationResult> RestartAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) =>
            control?.RestartAsync(worldId, cancellationToken) ?? Unavailable();

        public ValueTask<SandboxOperationResult> ResetAsync(
            WorldId worldId,
            string? replacementSource = null,
            CancellationToken cancellationToken = default) =>
            control?.ResetAsync(worldId, replacementSource, cancellationToken) ?? Unavailable();

        public ValueTask<SandboxOperationResult> DestroyAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) =>
            control?.DestroyAsync(worldId, cancellationToken) ?? Unavailable();

        private static ValueTask<SandboxOperationResult> Unavailable() => ValueTask.FromResult(
            new SandboxOperationResult(
                SandboxOperationStatus.Rejected,
                ErrorCode: "sandbox.control_unavailable",
                Message: "Sandbox control is unavailable in this Vega process."));
    }

    private sealed class PluginLog(IOperationsLogger logger, string source) : IPluginLog
    {
        public void Write(OperationsLogLevel level, string message, Exception? exception = null)
        {
            ArgumentNullException.ThrowIfNull(message);
            logger.Log(level, source, message, exception);
        }
    }

    private static string SafeDirectoryName(string pluginName)
    {
        char[] name = pluginName
            .Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '_')
            .ToArray();
        return name.Length == 0 ? "plugin" : new string(name);
    }

    private sealed class CleanupRegistration(Action cleanup) : IDisposable
    {
        private Action? cleanup = cleanup;

        public void Dispose() => Interlocked.Exchange(ref cleanup, null)?.Invoke();
    }
}
