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
        IWorldPublicWarpReader? warps = null,
        IPlayerPlaytimeSessionReader? playtime = null,
        PluginServiceRegistry? serviceRegistry = null,
        IPluginDocumentStore? documentStore = null,
        Vega.Contracts.Authorization.IRoleLeaseService? roleLeases = null,
        PluginNpcEconomyRegistry? npcEconomyRegistry = null,
        IReadOnlyList<string>? identityAssignmentGroups = null,
        PluginRestRegistry? pluginRestRegistry = null,
        PluginClusterRegistry? pluginClusterRegistry = null,
        Vega.Contracts.Cluster.IClusterNotificationSender? clusterSender = null)
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
            authentication, publicChat, commands as ICommandCompletionObservations, commands as ICommandPolicyEvents);
        Identity = new PluginIdentity(this, activityWorldId, playerDirectory, authentication, accounts, groups, playerPermissions, gameThread, roleLeases, identityAssignmentGroups);
        Statistics = new PluginStatistics(this, activityWorldId, playerDirectory, playtime);
        Warps = new PluginWarps(this, activityWorldId, warps);
        InitializePluginServices(pluginName, serviceRegistry, documentStore);
        InitializeItemInstances(pluginName, documentStore, authentication, playerDirectory, activityWorldId);
        InitializeNpcEconomy(pluginName, npcEconomyRegistry);
        InitializePluginRest(pluginName, pluginRestRegistry);
        InitializePluginCluster(pluginName, pluginClusterRegistry, clusterSender);
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
    public IPluginStatistics Statistics { get; }
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

    public IDisposable RegisterCommand(ICommandHandler handler) => ((PluginCommands)Commands).RegisterCommand(handler);

    // Publish and claim the registration under the same lock used to collect registrations at unload.
    private IDisposable RegisterOwned(Func<IDisposable> register)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!executionGuard.AcceptsCallbacks) throw new InvalidOperationException("Plugin activation is retiring.");
            var registration = register();
            registrations.Add(registration);
            return registration;
        }
    }

    private void Subscribe(Action attach, Action detach) => RegisterOwned(() =>
    {
        attach();
        return new CleanupRegistration(detach);
    });

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

    internal static string SafeDirectoryName(string pluginName)
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
