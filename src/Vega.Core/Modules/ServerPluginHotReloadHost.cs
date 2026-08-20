using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using Vega.Contracts.Commands;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;

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
        IOperationsLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.GetFullPath(directory);
        this.options = ServerPluginHotReloadOptions.Normalize(options);
        this.commands = commands ?? throw new ArgumentNullException(nameof(commands));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            return Array.AsReadOnly(snapshots.Values
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
        lock (sync)
        {
            loaded.TryGetValue(path, out previous);
            previous ??= loaded.Values.FirstOrDefault(plugin =>
                string.Equals(plugin.Name, staged.Name, StringComparison.OrdinalIgnoreCase));
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

        var scope = new HotModuleRegistrationScope(commands);
        long startedAt = Stopwatch.GetTimestamp();
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
            return new ServerPluginOperationResult(true, false, $"Server plugin '{active.Name}' is loaded.");
        }
        catch (Exception ex)
        {
            var failedPlugin = new LoadedPlugin(staged, scope);
            staged.Detach();
            (WeakReference contextReference, Exception? cleanupFailure) =
                await ReleaseAsync(failedPlugin, CancellationToken.None).ConfigureAwait(false);
            bool collected = await WaitForCollectionAsync(contextReference, CancellationToken.None).ConfigureAwait(false);
            if (cleanupFailure is not null)
            {
                ex = new AggregateException(ex, cleanupFailure);
            }
            if (!collected)
            {
                ex = new AggregateException(
                    ex,
                    new InvalidOperationException("The failed plugin's collectible context is still referenced."));
            }
            lock (sync)
            {
                snapshots[staged.Path] = new ModuleOperationsSnapshot(
                    staged.Name,
                    staged.Version,
                    ModuleLoadState.Failed,
                    staged.Dependencies,
                    Stopwatch.GetElapsedTime(startedAt),
                    ex.Message);
            }

            logger.Log(
                OperationsLogLevel.Error,
                "plugin-loader",
                $"Server plugin '{staged.Name}' failed to start.",
                ex);
            return new ServerPluginOperationResult(
                false,
                cleanupFailure is not null || !collected,
                ex.Message);
        }
    }

    private async Task<ServerPluginOperationResult> UnloadCoreAsync(
        LoadedPlugin plugin,
        CancellationToken cancellationToken)
    {
        lock (sync)
        {
            loaded.Remove(plugin.Path);
        }

        (WeakReference contextReference, Exception? cleanupFailure) =
            await ReleaseAsync(plugin, CancellationToken.None).ConfigureAwait(false);
        bool collected = await WaitForCollectionAsync(contextReference, CancellationToken.None).ConfigureAwait(false);
        bool succeeded = cleanupFailure is null && collected;
        string? error = cleanupFailure?.Message;
        if (!collected)
        {
            error = "The collectible AssemblyLoadContext is still referenced after cleanup.";
        }

        lock (sync)
        {
            snapshots[plugin.Path] = new ModuleOperationsSnapshot(
                plugin.Name,
                plugin.Version,
                succeeded ? ModuleLoadState.Disabled : ModuleLoadState.Degraded,
                plugin.Dependencies,
                TimeSpan.Zero,
                error);
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
            scope?.Dispose();
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

        if (!ReferencesHotModuleContract(image))
        {
            return null;
        }

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

    private static bool ReferencesHotModuleContract(byte[] image)
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
            if (metadata.StringComparer.Equals(reference.Namespace, "Vega.Contracts.Modules") &&
                metadata.StringComparer.Equals(reference.Name, nameof(IHotReloadableVegaModule)))
            {
                return true;
            }
        }

        return false;
    }

    private static ServerPluginOperationResult DisabledResult() => new(
        false,
        false,
        "Server plugin hot reload is disabled in config/modules.json.");

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

        public void Detach()
        {
            Context = null;
            Module = null;
            Scope = null;
        }
    }
}
