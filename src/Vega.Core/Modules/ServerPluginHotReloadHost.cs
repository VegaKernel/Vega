using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Vega.Contracts.Commands;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

public sealed class ServerPluginHotReloadHost :
    IServerPluginController,
    IModuleDirectoryStatusReader,
    IDisposable
{
    private const int MaximumChangedFiles = 32;
    private readonly object sync = new();
    private readonly string directory;
    private readonly ServerPluginHotReloadOptions options;
    private readonly ICommandRegistry commands;
    private readonly IOperationsLogger logger;
    private readonly IPlayerDirectory? playerDirectory;
    private readonly IPlayerMessenger? playerMessenger;
    private readonly IPlayerOperations? playerOperations;
    private readonly IPlayerSessionLifecycle? playerLifecycle;
    private readonly IGameThreadDispatcher? gameThread;
    private readonly IWorldGameplayEvents? worldEvents;
    private readonly WorldId? activityWorldId;
    private readonly Vega.Contracts.Accounts.IPlayerAccountSessionService? authentication;
    private readonly Vega.Contracts.Messaging.IPublicChatObservations? publicChat;
    private readonly string pluginDataRoot;
    private readonly string? pluginConfigurationRoot;
    private readonly Dictionary<string, LoadedPlugin> loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModuleOperationsSnapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> changeVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> changedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim mutation = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private FileSystemWatcher? watcher;
    private DateTimeOffset? changedAt;
    private string? failure;
    private string? watcherFailure;
    private bool restartRequired;
    private bool started;
    private bool disposed;
    private long nextChangeVersion;

    public ServerPluginHotReloadHost(
        string directory,
        ServerPluginHotReloadOptions options,
        ICommandRegistry commands,
        IOperationsLogger logger,
        IPlayerDirectory? playerDirectory = null,
        IPlayerMessenger? playerMessenger = null,
        IPlayerOperations? playerOperations = null,
        IPlayerSessionLifecycle? playerLifecycle = null,
        IGameThreadDispatcher? gameThread = null,
        string? pluginDataRoot = null,
        string? pluginConfigurationRoot = null, IWorldGameplayEvents? worldEvents = null,
        WorldId? activityWorldId = null,
        Vega.Contracts.Accounts.IPlayerAccountSessionService? authentication = null,
        Vega.Contracts.Messaging.IPublicChatObservations? publicChat = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.GetFullPath(directory);
        this.options = ServerPluginHotReloadOptions.Normalize(options);
        this.commands = commands ?? throw new ArgumentNullException(nameof(commands));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.playerDirectory = playerDirectory;
        this.playerMessenger = playerMessenger;
        this.playerOperations = playerOperations;
        this.playerLifecycle = playerLifecycle;
        this.gameThread = gameThread;
        this.worldEvents = worldEvents;
        this.activityWorldId = activityWorldId;
        this.authentication = authentication;
        this.publicChat = publicChat;
        this.pluginDataRoot = Path.GetFullPath(pluginDataRoot ?? Path.Combine(this.directory, ".data"));
        this.pluginConfigurationRoot = pluginConfigurationRoot is null ? null : Path.GetFullPath(pluginConfigurationRoot);
    }

    public bool HotReloadEnabled => options.Enabled;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
        {
            return;
        }

        started = true;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            failure = ex.Message;
            watcherFailure = ex.Message;
            restartRequired = true;
            logger.Log(
                OperationsLogLevel.Warning,
                "plugin-loader",
                "ServerPlugins directory is unavailable; plugin monitoring and hot reload are disabled for this process.",
                ex);
            return;
        }

        if (options.Enabled)
        {
            string[] initialPaths;
            try
            {
                initialPaths = Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                initialPaths = [];
                MarkRestartRequired(directory, ex.Message);
                logger.Log(
                    OperationsLogLevel.Warning,
                    "plugin-loader",
                    "Existing ServerPlugins could not be enumerated; startup continues without dynamic plugin activation.",
                    ex);
            }

            foreach (string path in initialPaths.Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    LoadInitialAsync(path, shutdown.Token).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    MarkRestartRequired(path, ex.Message);
                    logger.Log(
                        OperationsLogLevel.Error,
                        "plugin-loader",
                        $"Hot-loadable server plugin '{Path.GetFileName(path)}' failed during startup.",
                        ex);
                }
            }
        }

        try
        {
            watcher = new FileSystemWatcher(directory, "*.dll")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName |
                    NotifyFilters.LastWrite |
                    NotifyFilters.Size |
                    NotifyFilters.CreationTime
            };
            watcher.Created += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            failure = ex.Message;
            watcherFailure = ex.Message;
            restartRequired = true;
            logger.Log(
                OperationsLogLevel.Warning,
                "plugin-loader",
                "ServerPlugins monitoring is unavailable; hot replacement cannot be trusted.",
                ex);
        }

        logger.Log(
            OperationsLogLevel.Information,
            "plugin-loader",
            $"Server plugin hot reload is {(options.Enabled ? "enabled" : "disabled")}; " +
            $"configuration: config/modules.json.");
    }

    public IReadOnlyList<ModuleOperationsSnapshot> SnapshotModules()
    {
        lock (sync)
        {
            return Array.AsReadOnly(snapshots.Select(pair =>
                loaded.TryGetValue(pair.Key, out var plugin) && plugin.Scope is { } scope
                    ? WithFaults(pair.Value, scope.Faults) : pair.Value)
                .OrderBy(static snapshot => snapshot.Name, StringComparer.OrdinalIgnoreCase)
                .Select(static snapshot => snapshot with
                {
                    Dependencies = Array.AsReadOnly(snapshot.Dependencies.ToArray())
                })
                .ToArray());
        }
    }

    public ModuleDirectoryStatusSnapshot Read()
    {
        lock (sync)
        {
            return new ModuleDirectoryStatusSnapshot(
                watcher is not null && watcherFailure is null && !disposed,
                options.Enabled,
                restartRequired,
                changedAt,
                Array.AsReadOnly(changedFiles.Order(StringComparer.OrdinalIgnoreCase).ToArray()),
                failure);
        }
    }

    public async ValueTask<ServerPluginOperationResult> ReloadAsync(
        string plugin,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return DisabledResult();
        }

        string? path = ResolvePluginPath(plugin);
        if (path is null || !File.Exists(path))
        {
            return new ServerPluginOperationResult(false, false, $"Server plugin '{plugin}' was not found.");
        }

        await mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReplaceAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutation.Release();
        }
    }

    public async ValueTask<ServerPluginOperationResult> UnloadAsync(
        string plugin,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return DisabledResult();
        }

        await mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LoadedPlugin? current = FindLoaded(plugin);
            if (current is null)
            {
                return new ServerPluginOperationResult(false, false, $"Hot-loadable server plugin '{plugin}' is not loaded.");
            }

            return await UnloadCoreAsync(current, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutation.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        shutdown.Cancel();
        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnChanged;
            watcher.Changed -= OnChanged;
            watcher.Deleted -= OnChanged;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
            watcher = null;
        }

        mutation.Wait();
        try
        {
            foreach (LoadedPlugin plugin in loaded.Values.ToArray())
            {
                try
                {
                    UnloadCoreAsync(plugin, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    logger.Log(
                        OperationsLogLevel.Error,
                        "plugin-loader",
                        $"Server plugin '{plugin.Name}' failed to stop during shutdown.",
                        ex);
                }
            }
        }
        finally
        {
            mutation.Release();
        }
    }

    private async Task LoadInitialAsync(string path, CancellationToken cancellationToken)
    {
        StagedPlugin? staged = await StageAsync(path, cancellationToken).ConfigureAwait(false);
        if (staged is null)
        {
            return;
        }

        ServerPluginOperationResult result = await ActivateAsync(staged, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded && result.RestartRequired)
        {
            throw new InvalidOperationException(result.Message);
        }
    }

    private async Task<ServerPluginOperationResult> ReplaceAsync(
        string path,
        CancellationToken cancellationToken)
    {
        StagedPlugin? staged;
        try
        {
            staged = await StageAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Log(
                OperationsLogLevel.Error,
                "plugin-loader",
                $"Replacement image for '{Path.GetFileName(path)}' could not be staged; the active revision was preserved.",
                ex);
            return new ServerPluginOperationResult(false, false, ex.Message);
        }

        if (staged is null)
        {
            MarkRestartRequired(path, "Assembly does not implement IHotReloadableVegaModule.");
            return new ServerPluginOperationResult(
                false,
                true,
                $"'{Path.GetFileName(path)}' is not a hot-reloadable Vega plugin; restart is required.");
        }

        LoadedPlugin? previous;
        bool unreclaimed;
        lock (sync)
        {
            loaded.TryGetValue(path, out previous);
            previous ??= loaded.Values.FirstOrDefault(plugin =>
                string.Equals(plugin.Name, staged.Name, StringComparison.OrdinalIgnoreCase));
            unreclaimed = previous is null && snapshots.Any(pair => pair.Value.State == ModuleLoadState.Degraded &&
                !loaded.ContainsKey(pair.Key) && (string.Equals(pair.Key, path, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pair.Value.Name, staged.Name, StringComparison.OrdinalIgnoreCase)));
        }

        if (unreclaimed)
        {
            await DiscardAsync(staged).ConfigureAwait(false);
            return new ServerPluginOperationResult(false, true, "The previous plugin activation was not reclaimed; restart is required.");
        }

        if (previous is not null)
        {
            ServerPluginOperationResult unloaded = await UnloadCoreAsync(previous, cancellationToken).ConfigureAwait(false);
            if (!unloaded.Succeeded)
            {
                await DiscardAsync(staged).ConfigureAwait(false);
                MarkRestartRequired(path, unloaded.Message);
                return unloaded with { RestartRequired = true };
            }

            if (!string.Equals(previous.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                lock (sync)
                {
                    snapshots.Remove(previous.Path);
                }
            }
        }

        ServerPluginOperationResult activated = await ActivateAsync(staged, cancellationToken).ConfigureAwait(false);
        if (!activated.Succeeded)
        {
            return activated;
        }

        logger.Log(
            OperationsLogLevel.Information,
            "plugin-loader",
            $"Server plugin '{staged.Name}' was hot-replaced from '{Path.GetFileName(path)}'.");
        return activated;
    }

    private async Task<ServerPluginOperationResult> ActivateAsync(
        StagedPlugin staged,
        CancellationToken cancellationToken)
    {
        bool duplicate;
        lock (sync)
        {
            duplicate = loaded.Values.Any(plugin =>
                string.Equals(plugin.Name, staged.Name, StringComparison.OrdinalIgnoreCase));
        }

        if (duplicate)
        {
            await DiscardAsync(staged).ConfigureAwait(false);
            return new ServerPluginOperationResult(
                false,
                false,
                $"A server plugin named '{staged.Name}' is already loaded.");
        }

        Guid activationId = Guid.NewGuid();
        string activationPath = staged.Path;
        var scope = new HotModuleRegistrationScope(
            commands,
            logger,
            staged.Name,
            Path.Combine(pluginDataRoot, SafeDirectoryName(staged.Name)),
            playerDirectory,
            playerMessenger,
            playerOperations,
            playerLifecycle,
            gameThread,
            pluginConfigurationRoot: pluginConfigurationRoot,
            activationId: activationId, faultThreshold: options.FaultThreshold,
            faultWindow: TimeSpan.FromSeconds(options.FaultWindowSeconds),
            onQuarantine: () => QueueFaultRetirement(activationPath, activationId), worldEvents: worldEvents,
            activityWorldId: activityWorldId, authentication: authentication, publicChat: publicChat);
        long startedAt = Stopwatch.GetTimestamp();
        string startupFailure;
        try
        {
            await staged.Module!.StartAsync(scope, cancellationToken).ConfigureAwait(false);
            var active = new LoadedPlugin(staged, scope);
            lock (sync)
            {
                loaded.Add(active.Path, active);
                snapshots[active.Path] = Snapshot(active, ModuleLoadState.Loaded, Stopwatch.GetElapsedTime(startedAt));
            }

            staged.Detach();
            if (scope.Faults.Quarantined) QueueFaultRetirement(activationPath, activationId);
            return new ServerPluginOperationResult(true, false, $"Server plugin '{active.Name}' is loaded.");
        }
        catch (Exception ex)
        {
            startupFailure = ex.Message;
            logger.Log(
                OperationsLogLevel.Error,
                "plugin-loader",
                $"Server plugin '{staged.Name}' failed to start.",
                ex);
        }

        // A partially initialized activation may have already registered callbacks or
        // launched owned tasks. Retain and retire it through the same bounded path.
        var failedPlugin = new LoadedPlugin(staged, scope);
        staged.Detach();
        scope = null!;
        lock (sync)
        {
            loaded.Add(failedPlugin.Path, failedPlugin);
            snapshots[failedPlugin.Path] = Snapshot(failedPlugin, ModuleLoadState.Failed, Stopwatch.GetElapsedTime(startedAt)) with
            { Failure = startupFailure };
        }
        ServerPluginOperationResult cleanup = await UnloadCoreAsync(failedPlugin, CancellationToken.None).ConfigureAwait(false);
        string failure = cleanup.Succeeded ? startupFailure : $"{startupFailure} Cleanup: {cleanup.Message}";
        lock (sync)
        {
            snapshots[failedPlugin.Path] = snapshots[failedPlugin.Path] with
            { State = cleanup.Succeeded ? ModuleLoadState.Failed : ModuleLoadState.Degraded, Failure = failure };
        }
        return new ServerPluginOperationResult(false, cleanup.RestartRequired, failure);
    }

    private void QueueFaultRetirement(string path, Guid activationId) =>
        _ = Task.Run(() => RetireFaultedAsync(path, activationId));

    private async Task RetireFaultedAsync(string path, Guid activationId)
    {
        try
        {
            await mutation.WaitAsync(shutdown.Token).ConfigureAwait(false);
            try
            {
                LoadedPlugin? plugin;
                lock (sync)
                {
                    if (!loaded.TryGetValue(path, out plugin) || plugin.Scope?.ActivationId != activationId ||
                        !plugin.Scope!.Faults.Quarantined) return;
                }
                if (!shutdown.IsCancellationRequested) await UnloadCoreAsync(plugin, CancellationToken.None).ConfigureAwait(false);
            }
            finally { mutation.Release(); }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            MarkRestartRequired(path, "Faulted plugin retirement failed.");
            logger.Log(OperationsLogLevel.Error, "plugin-loader", "Could not retire quarantined server plugin.", ex);
        }
    }

    private static ModuleOperationsSnapshot WithFaults(ModuleOperationsSnapshot snapshot, PluginExecutionGuard.FaultSnapshot faults) =>
        snapshot with
        {
            State = faults.Quarantined && snapshot.State == ModuleLoadState.Loaded ? ModuleLoadState.Degraded : snapshot.State,
            FaultCount = faults.Count, LastFaultOperation = faults.Operation, LastFaultSummary = faults.Summary,
            Failure = faults.Quarantined ? snapshot.Failure ?? "Plugin callback breaker tripped." : snapshot.Failure
        };

    private async Task<ServerPluginOperationResult> UnloadCoreAsync(
        LoadedPlugin plugin,
        CancellationToken cancellationToken)
    {
        // Keep the activation in the catalog until cleanup actually completes. This
        // prevents a replacement from running beside non-cooperative callbacks.
        TimeSpan timeout = TimeSpan.FromMilliseconds(options.UnloadTimeoutMilliseconds);
        long retirementStarted = Stopwatch.GetTimestamp();
        using var deadline = new CancellationTokenSource(timeout);
        if (plugin.ReleaseTask is null && plugin.Scope is not null)
        {
            try { await plugin.Scope.DrainForRetirementAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                return RetirementTimeout(plugin, "Plugin callbacks or lifetime tasks did not drain within the unload timeout.");
            }
        }

        var faults = plugin.Scope?.Faults ?? plugin.RetiredFaults;
        plugin.RetiredFaults = faults;
        // Even synchronously blocked plugin Stop/Dispose code must not monopolize
        // the mutation lane. Track the actual release and never execute it twice.
        if (plugin.ReleaseTask is null)
        {
            TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(retirementStarted);
            // Use a separate token from the host's wait: plugin cancellation callbacks
            // may themselves block. Its source survives until the actual release ends.
            var releaseDeadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            plugin.ReleaseTask = Task.Run(async () =>
            {
                using (releaseDeadline)
                    return await ReleaseAsync(plugin, releaseDeadline.Token).ConfigureAwait(false);
            });
        }
        (WeakReference contextReference, Exception? cleanupFailure) release;
        try { release = await plugin.ReleaseTask.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return RetirementTimeout(plugin, "Plugin stop or disposal did not complete within the unload timeout.");
        }
        var (contextReference, cleanupFailure) = release;
        lock (sync) loaded.Remove(plugin.Path);
        bool collected = await WaitForCollectionAsync(contextReference, CancellationToken.None).ConfigureAwait(false);
        bool succeeded = cleanupFailure is null && collected;
        string? error = cleanupFailure?.Message;
        if (!collected)
        {
            error = "The collectible AssemblyLoadContext is still referenced after cleanup.";
        }

        lock (sync)
        {
            snapshots[plugin.Path] = WithFaults(new ModuleOperationsSnapshot(
                plugin.Name,
                plugin.Version,
                succeeded ? ModuleLoadState.Disabled : ModuleLoadState.Degraded,
                plugin.Dependencies,
                TimeSpan.Zero,
                error), faults);
        }

        if (!succeeded)
        {
            MarkRestartRequired(plugin.Path, error!);
            logger.Log(
                OperationsLogLevel.Error,
                "plugin-loader",
                $"Server plugin '{plugin.Name}' detached incompletely; restart is required.",
                cleanupFailure);
            return new ServerPluginOperationResult(false, true, error!);
        }

        logger.Log(
            OperationsLogLevel.Information,
            "plugin-loader",
            $"Server plugin '{plugin.Name}' was stopped, detached and unloaded.");
        return new ServerPluginOperationResult(true, false, $"Server plugin '{plugin.Name}' is unloaded.");
    }

    private ServerPluginOperationResult RetirementTimeout(LoadedPlugin plugin, string message)
    {
        lock (sync)
        {
            snapshots[plugin.Path] = WithFaults(snapshots[plugin.Path] with
            {
                State = ModuleLoadState.Degraded,
                Failure = message
            }, plugin.Scope?.Faults ?? plugin.RetiredFaults);
        }
        MarkRestartRequired(plugin.Path, message);
        logger.Log(OperationsLogLevel.Error, "plugin-loader",
            $"Server plugin '{plugin.Name}' is quarantined; {message}");
        return new ServerPluginOperationResult(false, true, message);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference ContextReference, Exception? Failure)> ReleaseAsync(
        LoadedPlugin plugin,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        IHotReloadableVegaModule? module = plugin.Module;
        HotModuleRegistrationScope? scope = plugin.Scope;
        ServerPluginLoadContext? context = plugin.Context;
        var contextReference = new WeakReference(context, trackResurrection: false);

        try
        {
            if (scope is not null)
            {
                await scope.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            failure = failure is null ? ex : new AggregateException(failure, ex);
        }

        try
        {
            if (module is not null)
            {
                await module.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            failure = failure is null ? ex : new AggregateException(failure, ex);
        }

        try
        {
            if (module is not null)
            {
                await module.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            failure = failure is null ? ex : new AggregateException(failure, ex);
        }

        plugin.Detach();
        module = null;
        scope = null;
        context?.Unload();
        context = null;
        return (contextReference, failure);
    }

    private async Task<bool> WaitForCollectionAsync(
        WeakReference contextReference,
        CancellationToken cancellationToken)
    {
        long deadline = Stopwatch.GetTimestamp() +
            (long)(options.UnloadTimeoutMilliseconds / 1000d * Stopwatch.Frequency);
        do
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!contextReference.IsAlive)
            {
                return true;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        while (Stopwatch.GetTimestamp() < deadline);

        return !contextReference.IsAlive;
    }

    private static async Task DiscardAsync(StagedPlugin staged)
    {
        IHotReloadableVegaModule? module = staged.Module;
        ServerPluginLoadContext? context = staged.Context;
        staged.Detach();
        if (module is not null)
        {
            await module.DisposeAsync().ConfigureAwait(false);
        }

        module = null;
        context?.Unload();
        context = null;
    }

    private static async Task<StagedPlugin?> StageAsync(string path, CancellationToken cancellationToken)
    {
        byte[] image = [];
        Exception? readFailure = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                image = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                readFailure = null;
                break;
            }
            catch (IOException ex)
            {
                readFailure = ex;
                await Task.Delay(100 * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
        }

        if (readFailure is not null)
        {
            throw readFailure;
        }

        if (!ReferencesHotModuleEntryPoint(image))
        {
            return null;
        }

        ValidatePluginMetadata(image, path);

        var context = new ServerPluginLoadContext(path);
        try
        {
            using var stream = new MemoryStream(image, writable: false);
            Assembly assembly = context.LoadFromStream(stream);
            Type[] moduleTypes = assembly.GetTypes()
                .Where(static type =>
                    !type.IsAbstract &&
                    !type.IsInterface &&
                    typeof(IHotReloadableVegaModule).IsAssignableFrom(type))
                .ToArray();
            if (moduleTypes.Length == 0)
            {
                context.Unload();
                return null;
            }

            if (moduleTypes.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Assembly '{Path.GetFileName(path)}' must export exactly one IHotReloadableVegaModule.");
            }

            if (Activator.CreateInstance(moduleTypes[0]) is not IHotReloadableVegaModule module)
            {
                throw new InvalidOperationException(
                    $"Hot module '{moduleTypes[0].FullName}' requires a public parameterless constructor.");
            }

            return new StagedPlugin(path, context, module);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    private void ScheduleChange(string path)
    {
        string fullPath = Path.GetFullPath(path);
        long version;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            version = ++nextChangeVersion;
            changeVersions[fullPath] = version;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(options.DebounceMilliseconds, shutdown.Token).ConfigureAwait(false);
                lock (sync)
                {
                    if (!changeVersions.TryGetValue(fullPath, out long current) || current != version)
                    {
                        return;
                    }

                    changeVersions.Remove(fullPath);
                }

                await ProcessDeploymentAsync(fullPath, shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                MarkRestartRequired(fullPath, ex.Message);
                logger.Log(
                    OperationsLogLevel.Error,
                    "plugin-loader",
                    $"ServerPlugins change processing failed for '{Path.GetFileName(fullPath)}'.",
                    ex);
            }
        });
    }

    private async Task ProcessDeploymentAsync(string path, CancellationToken cancellationToken)
    {
        RecordChangedFile(path);
        if (!options.Enabled)
        {
            MarkRestartRequired(path, null);
            logger.Log(
                OperationsLogLevel.Warning,
                "plugin-loader",
                $"ServerPlugins changed ({Path.GetFileName(path)}); hot reload is disabled and restart is required.");
            return;
        }

        await mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ServerPluginOperationResult result;
            if (File.Exists(path))
            {
                result = await ReplaceAsync(path, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                LoadedPlugin? current;
                lock (sync)
                {
                    loaded.TryGetValue(path, out current);
                }

                if (current is null)
                {
                    bool knownHotPlugin;
                    lock (sync)
                    {
                        knownHotPlugin = snapshots.ContainsKey(path);
                    }
                    if (!knownHotPlugin)
                    {
                        MarkRestartRequired(path, "A non-hot-reloadable server module was removed.");
                    }
                    return;
                }

                result = await UnloadCoreAsync(current, cancellationToken).ConfigureAwait(false);
            }

            if (!result.Succeeded && result.RestartRequired)
            {
                MarkRestartRequired(path, result.Message);
            }
        }
        finally
        {
            mutation.Release();
        }
    }

    private string? ResolvePluginPath(string plugin)
    {
        if (string.IsNullOrWhiteSpace(plugin))
        {
            return null;
        }

        LoadedPlugin? active = FindLoaded(plugin);
        if (active is not null)
        {
            return active.Path;
        }

        string fileName = Path.GetFileName(plugin);
        string direct = Path.Combine(directory, fileName);
        if (File.Exists(direct))
        {
            return Path.GetFullPath(direct);
        }

        // A retired plugin remains in the catalog under its descriptor name, not
        // necessarily its assembly filename. Permit explicit reactivation by that name.
        lock (sync)
        {
            string[] matches = snapshots.Where(pair =>
                string.Equals(pair.Value.Name, plugin, StringComparison.OrdinalIgnoreCase) && File.Exists(pair.Key))
                .Select(static pair => pair.Key).Take(2).ToArray();
            if (matches.Length == 1) return matches[0];
        }

        return Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path =>
                string.Equals(Path.GetFileNameWithoutExtension(path), plugin, StringComparison.OrdinalIgnoreCase));
    }

    private LoadedPlugin? FindLoaded(string plugin)
    {
        lock (sync)
        {
            return loaded.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, plugin, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(candidate.Path), plugin, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileNameWithoutExtension(candidate.Path), plugin, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void RecordChangedFile(string path)
    {
        lock (sync)
        {
            changedAt = DateTimeOffset.UtcNow;
            if (changedFiles.Count < MaximumChangedFiles)
            {
                changedFiles.Add(Path.GetFileName(path));
            }
        }
    }

    private void MarkRestartRequired(string path, string? reason)
    {
        lock (sync)
        {
            restartRequired = true;
            changedAt ??= DateTimeOffset.UtcNow;
            if (changedFiles.Count < MaximumChangedFiles)
            {
                changedFiles.Add(Path.GetFileName(path));
            }

            if (!string.IsNullOrWhiteSpace(reason))
            {
                failure = reason;
            }
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs args) => ScheduleChange(args.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        ScheduleChange(args.OldFullPath);
        ScheduleChange(args.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs args)
    {
        Exception exception = args.GetException();
        lock (sync)
        {
            watcherFailure = exception.Message;
        }
        MarkRestartRequired(directory, exception.Message);
        logger.Log(
            OperationsLogLevel.Error,
            "plugin-loader",
            "ServerPlugins watcher failed; restart is required and further changes may not be detected.",
            exception);
    }

    private static ModuleOperationsSnapshot Snapshot(
        LoadedPlugin plugin,
        ModuleLoadState state,
        TimeSpan startupDuration,
        string? failure = null) =>
        new(plugin.Name, plugin.Version, state, plugin.Dependencies, startupDuration, failure);

    private static bool ReferencesHotModuleEntryPoint(byte[] image)
    {
        using var stream = new MemoryStream(image, writable: false);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
        {
            return false;
        }

        MetadataReader metadata = pe.GetMetadataReader();
        foreach (TypeReferenceHandle handle in metadata.TypeReferences)
        {
            TypeReference reference = metadata.GetTypeReference(handle);
            bool referencesLegacyContract =
                metadata.StringComparer.Equals(reference.Namespace, "Vega.Contracts.Modules") &&
                metadata.StringComparer.Equals(reference.Name, nameof(IHotReloadableVegaModule));
            bool referencesPluginSdk =
                metadata.StringComparer.Equals(reference.Namespace, "Vega.PluginSdk") &&
                metadata.StringComparer.Equals(reference.Name, nameof(VegaPlugin));
            if (referencesLegacyContract || referencesPluginSdk)
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidatePluginMetadata(byte[] image, string path)
    {
        using var stream = new MemoryStream(image, writable: false);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        string? targetFramework = null;
        int? declaredGeneration = null;

        foreach (CustomAttributeHandle handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            CustomAttribute attribute = metadata.GetCustomAttribute(handle);
            if (IsAttribute(metadata, attribute.Constructor, "Vega.PluginSdk", nameof(VegaPluginApiGenerationAttribute)))
            {
                BlobReader sdk = metadata.GetBlobReader(attribute.Value);
                if (declaredGeneration.HasValue || sdk.RemainingBytes != 8 || sdk.ReadUInt16() != 1)
                    throw new InvalidOperationException($"Server plugin '{Path.GetFileName(path)}' has an invalid or duplicate SDK API generation declaration.");
                declaredGeneration = sdk.ReadInt32();
                if (declaredGeneration <= 0 || sdk.ReadUInt16() != 0)
                    throw new InvalidOperationException($"Server plugin '{Path.GetFileName(path)}' has an invalid SDK API generation declaration.");
                continue;
            }

            if (targetFramework is not null || !IsAttribute(metadata, attribute.Constructor, "System.Runtime.Versioning", nameof(TargetFrameworkAttribute)))
                continue;

            BlobReader value = metadata.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() == 1)
            {
                targetFramework = value.ReadSerializedString();
            }

        }

        if (declaredGeneration is { } generation && generation != VegaPluginApiGenerationAttribute.Current)
            throw new InvalidOperationException($"Server plugin '{Path.GetFileName(path)}' declares SDK API generation {generation}, " +
                $"but this Vega host supports generation {VegaPluginApiGenerationAttribute.Current}. Rebuild and qualify the plugin against the deployed Vega PluginSdk.");

        string? hostTargetFramework = typeof(ServerPluginHotReloadHost).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()?
            .FrameworkName;
        if (string.IsNullOrWhiteSpace(targetFramework) || string.IsNullOrWhiteSpace(hostTargetFramework))
        {
            throw new InvalidOperationException(
                $"Server plugin '{Path.GetFileName(path)}' does not declare a target framework that Vega can validate.");
        }

        FrameworkName pluginFramework;
        FrameworkName hostFramework;
        try
        {
            pluginFramework = new FrameworkName(targetFramework);
            hostFramework = new FrameworkName(hostTargetFramework);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"Server plugin '{Path.GetFileName(path)}' declares invalid target framework '{targetFramework}'.",
                ex);
        }

        if (!string.Equals(pluginFramework.Identifier, hostFramework.Identifier, StringComparison.OrdinalIgnoreCase) ||
            pluginFramework.Version > hostFramework.Version)
        {
            throw new InvalidOperationException(
                $"Server plugin '{Path.GetFileName(path)}' targets '{targetFramework}', but this Vega host targets " +
                $"'net{hostFramework.Version.Major}.{hostFramework.Version.Minor}'. Rebuild the plugin for the host " +
                "framework or an earlier compatible .NETCoreApp target.");
        }
    }

    private static bool IsAttribute(MetadataReader metadata, EntityHandle constructor, string expectedNamespace, string expectedName)
    {
        EntityHandle declaringType = constructor.Kind switch
        {
            HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            _ => default
        };

        return declaringType.Kind switch
        {
            HandleKind.TypeReference => AttributeTypeMatches(metadata, metadata.GetTypeReference((TypeReferenceHandle)declaringType), expectedNamespace, expectedName),
            HandleKind.TypeDefinition => AttributeTypeMatches(metadata, metadata.GetTypeDefinition((TypeDefinitionHandle)declaringType), expectedNamespace, expectedName),
            _ => false
        };
    }

    private static bool AttributeTypeMatches(MetadataReader metadata, TypeReference type, string expectedNamespace, string expectedName) =>
        metadata.StringComparer.Equals(type.Namespace, expectedNamespace) && metadata.StringComparer.Equals(type.Name, expectedName);

    private static bool AttributeTypeMatches(MetadataReader metadata, TypeDefinition type, string expectedNamespace, string expectedName) =>
        metadata.StringComparer.Equals(type.Namespace, expectedNamespace) && metadata.StringComparer.Equals(type.Name, expectedName);

    private static ServerPluginOperationResult DisabledResult() => new(
        false,
        false,
        "Server plugin hot reload is disabled in config/modules.json.");

    private static string SafeDirectoryName(string pluginName)
    {
        char[] name = pluginName
            .Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '_')
            .ToArray();
        return name.Length == 0 ? "plugin" : new string(name);
    }

    private sealed class StagedPlugin
    {
        public StagedPlugin(string path, ServerPluginLoadContext context, IHotReloadableVegaModule module)
        {
            Path = System.IO.Path.GetFullPath(path);
            Context = context;
            Module = module;
            Name = module.Descriptor.Name;
            Version = module.Descriptor.Version;
            Dependencies = Array.AsReadOnly(module.Descriptor.Dependencies.ToArray());
        }

        public string Path { get; }
        public string Name { get; }
        public string Version { get; }
        public IReadOnlyList<string> Dependencies { get; }
        public ServerPluginLoadContext? Context { get; private set; }
        public IHotReloadableVegaModule? Module { get; private set; }

        public void Detach()
        {
            Context = null;
            Module = null;
        }
    }

    private sealed class LoadedPlugin
    {
        public LoadedPlugin(StagedPlugin staged, HotModuleRegistrationScope scope)
        {
            Path = staged.Path;
            Name = staged.Name;
            Version = staged.Version;
            Dependencies = staged.Dependencies;
            Context = staged.Context!;
            Module = staged.Module!;
            Scope = scope;
        }

        public string Path { get; }
        public string Name { get; }
        public string Version { get; }
        public IReadOnlyList<string> Dependencies { get; }
        public ServerPluginLoadContext? Context { get; private set; }
        public IHotReloadableVegaModule? Module { get; private set; }
        public HotModuleRegistrationScope? Scope { get; private set; }
        public Task<(WeakReference ContextReference, Exception? Failure)>? ReleaseTask { get; set; }
        public PluginExecutionGuard.FaultSnapshot RetiredFaults { get; set; }

        public void Detach()
        {
            Context = null;
            Module = null;
            Scope = null;
        }
    }
}
