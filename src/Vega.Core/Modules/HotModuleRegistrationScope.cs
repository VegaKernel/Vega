using System.Text.Json;
using Vega.Contracts.Commands;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

internal sealed class HotModuleRegistrationScope : IHotModuleContext, IVegaPluginContext, IAsyncDisposable
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
        IGameThreadDispatcher? gameThread = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        logSource = $"plugin:{pluginName}";

        Commands = new PluginCommands(this, commands);
        Players = new PluginPlayers(this, playerDirectory, messenger, playerOperations, playerLifecycle);
        Tasks = new PluginTasks(this, gameThread);
        Storage = new PluginStorage(dataDirectory);
        Log = new PluginLog(logger, logSource);
    }

    public IPluginCommands Commands { get; }
    public IPluginPlayers Players { get; }
    public IPluginTasks Tasks { get; }
    public IPluginStorage Storage { get; }
    public IPluginLog Log { get; }

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
        IDisposable[] owned;
        Task[] running;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            lifetime.Cancel();
            owned = registrations.ToArray();
            running = tasks.ToArray();
            registrations.Clear();
            tasks.Clear();
        }

        List<Exception>? failures = null;
        try
        {
            await Task.WhenAll(running).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            (failures ??= []).Add(ex);
        }

        for (var index = owned.Length - 1; index >= 0; index--)
        {
            try
            {
                owned[index].Dispose();
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

    private void TrackTask(Task task)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            tasks.Add(task);
        }
    }

    private sealed class PluginCommands(HotModuleRegistrationScope owner, ICommandRegistry commands) : IPluginCommands
    {
        public void Register(ICommandHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            owner.Track(RegisterCore(handler));
        }

        public IDisposable RegisterCore(ICommandHandler handler) => commands.Register(handler);
    }

    private sealed class PluginPlayers(
        HotModuleRegistrationScope owner,
        IPlayerDirectory? directory,
        IPlayerMessenger? messenger,
        IPlayerOperations? operations,
        IPlayerSessionLifecycle? lifecycle) : IPluginPlayers
    {
        public IReadOnlyList<PlayerSnapshot> Online => directory?.SnapshotOnlinePlayers() ?? [];

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

        public ValueTask<PlayerOperationResult> MessageAsync(
            PlayerId id,
            string message,
            CancellationToken cancellationToken = default) =>
            messenger?.SendMessageAsync(id, message, cancellationToken) ??
            ValueTask.FromResult(PlayerOperationResult.DispatcherUnavailable());

        private async Task SendMessageCoreAsync(PlayerId id, string message)
        {
            try
            {
                PlayerOperationResult result = await MessageAsync(id, message, owner.lifetime.Token)
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

            lifecycle.Opened += handler;
            owner.Track(new CleanupRegistration(() => lifecycle.Opened -= handler));
        }

        public void OnLeft(Action<PlayerSessionClosed> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (lifecycle is null)
            {
                return;
            }

            lifecycle.Closed += handler;
            owner.Track(new CleanupRegistration(() => lifecycle.Closed -= handler));
        }
    }

    private sealed class PluginTasks(HotModuleRegistrationScope owner, IGameThreadDispatcher? gameThread) : IPluginTasks
    {
        public bool Post(string name, Action action)
        {
            Validate(name, action);
            return gameThread?.TryPost(name, () =>
            {
                if (!owner.lifetime.IsCancellationRequested)
                {
                    action();
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

            owner.TrackTask(RepeatAsync(name, interval, action));
        }

        public void RunIo<T>(string name, Func<CancellationToken, ValueTask<T>> operation, Action<T> apply)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(apply);
            owner.TrackTask(RunIoCoreAsync(name, operation, apply));
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
                owner.logger.Log(OperationsLogLevel.Error, owner.logSource,
                    $"Async plugin operation '{name}' failed.", ex);
            }
        }

        private static void Validate(string name, Action action)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(action);
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

    private sealed class PluginLog(IOperationsLogger logger, string source) : IPluginLog
    {
        public void Write(OperationsLogLevel level, string message, Exception? exception = null)
        {
            ArgumentNullException.ThrowIfNull(message);
            logger.Log(level, source, message, exception);
        }
    }

    private sealed class CleanupRegistration(Action cleanup) : IDisposable
    {
        private Action? cleanup = cleanup;

        public void Dispose() => Interlocked.Exchange(ref cleanup, null)?.Invoke();
    }
}
