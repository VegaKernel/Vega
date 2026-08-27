using Vega.Contracts.Commands;
using Vega.Contracts.Events;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;

namespace Vega.PluginSdk;

/// <summary>
/// Curated capabilities available to a Vega plugin. This is a fixed facade,
/// not a service locator and not a runtime implementation surface.
/// </summary>
public interface IVegaPluginContext
{
    IPluginCommands Commands { get; }

    IPluginPlayers Players { get; }

    /// <summary>Typed lifecycle/gameplay hooks owned by this plugin lifetime.</summary>
    IPluginEvents Events => VegaPluginEventRuntime.Events(this);

    /// <summary>Curated immutable-snapshot access to the world hosted by this process.</summary>
    IPluginWorld World => VegaPluginRuntime.World(this);

    IPluginTasks Tasks { get; }

    /// <summary>Managed plugin configuration with typed load, validation, reload and atomic save.</summary>
    IPluginConfiguration Configuration { get; }

    /// <summary>Arbitrary plugin-owned data files. Use <see cref="Configuration"/> for settings.</summary>
    IPluginStorage Storage { get; }

    IPluginLog Log { get; }

    /// <summary>Sandbox discovery and lifecycle control supplied by the host.</summary>
    IPluginSandboxes Sandboxes { get; }

    /// <summary>
    /// Process-internal diagnostics. The host always supplies a context; its capabilities may be None
    /// unless DEBUG API access is enabled for this plugin.
    /// </summary>
    IVegaDebugContext Debug { get; }
}

/// <summary>Executes one plugin command through Vega's normal command pipeline.</summary>
public delegate ValueTask<CommandExecutionResult> PluginCommandCallback(
    CommandInvocation invocation,
    CancellationToken cancellationToken);

public interface IPluginCommands
{
    /// <summary>Registers a command until the plugin is unloaded.</summary>
    void Register(ICommandHandler handler);

    /// <summary>
    /// Registers a command callback without requiring a dedicated <see cref="ICommandHandler"/> type.
    /// The callback still uses the normal command registry and is removed automatically on unload.
    /// </summary>
    void Register(CommandDescriptor descriptor, PluginCommandCallback callback)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(callback);
        Register(new PluginCallbackCommandHandler(descriptor, callback));
    }

    /// <summary>
    /// Registers a simple command with inline metadata. Use the handler overload when a command
    /// deserves its own type or needs to be shared outside the plugin facade.
    /// </summary>
    void Register(
        string name,
        PluginCommandCallback callback,
        IReadOnlyList<string>? aliases = null,
        IReadOnlyList<string>? permissions = null,
        bool allowServer = true,
        string syntax = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Register(
            new CommandDescriptor(name, aliases ?? [], permissions ?? [], allowServer, syntax),
            callback);
    }
}

internal sealed class PluginCallbackCommandHandler(
    CommandDescriptor descriptor,
    PluginCommandCallback callback) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } =
        descriptor ?? throw new ArgumentNullException(nameof(descriptor));

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken) =>
        callback(invocation, cancellationToken);
}

/// <summary>
/// Curated dynamic event surface. Ordinary handlers remain synchronous-looking; the host owns
/// registration lifetime, cancellation ordering and asynchronous pipeline plumbing.
/// </summary>
public interface IPluginEvents
{
    /// <summary>Registers a mutable/cancellable event handler until the plugin is unloaded.</summary>
    void On<TEvent>(
        Action<EventContext<TEvent>> handler,
        EventPriority priority = EventPriority.Normal,
        bool invokeWhenCancelled = false);

    /// <summary>Registers a read-only final-outcome observer until the plugin is unloaded.</summary>
    void Observe<TEvent>(
        Action<EventOutcome<TEvent>> monitor,
        EventPriority priority = EventPriority.Normal);
}

public interface IPluginPlayers
{
    IReadOnlyList<PlayerSnapshot> Online { get; }

    /// <summary>Optimistic, game-thread-dispatched access to player inventory.</summary>
    IPluginPlayerInventory Inventory => VegaPluginRuntime.Inventory;

    bool TryGet(PlayerId id, out PlayerSnapshot? player);

    bool TryFindExact(string name, out PlayerSnapshot? player);

    /// <summary>Sends a message through a host-tracked player operation.</summary>
    void Message(PlayerId id, string message);

    ValueTask<PlayerOperationResult> MessageAsync(
        PlayerId id,
        string message,
        CancellationToken cancellationToken = default);

    ValueTask<PlayerOperationResult> KickAsync(
        PlayerId id,
        string reason,
        CancellationToken cancellationToken = default);

    ValueTask<PlayerOperationResult> TeleportAsync(
        PlayerId id,
        PlayerPosition destination,
        CancellationToken cancellationToken = default);

    ValueTask<PlayerOperationResult> HealAsync(
        PlayerId id,
        int? amount = null,
        CancellationToken cancellationToken = default);

    ValueTask<PlayerOperationResult> KillAsync(
        PlayerId id,
        string reason,
        CancellationToken cancellationToken = default);

    ValueTask<PlayerOperationResult> ApplyBuffAsync(
        PlayerId id,
        int buffType,
        int durationTicks,
        CancellationToken cancellationToken = default);

    ValueTask<PlayerOperationResult> RespawnAsync(
        PlayerId id,
        CancellationToken cancellationToken = default);

    /// <summary>Registers a game-thread player-join callback for this plugin lifetime.</summary>
    void OnJoined(Action<PlayerSessionOpened> handler);

    /// <summary>Registers a game-thread player-leave callback for this plugin lifetime.</summary>
    void OnLeft(Action<PlayerSessionClosed> handler);
}

public interface IPluginTasks
{
    /// <summary>Queues one action on the authoritative game thread.</summary>
    bool Post(string name, Action action);

    /// <summary>
    /// Runs a repeating action on the game thread. Scheduling and cancellation are owned by the host.
    /// </summary>
    void Every(string name, TimeSpan interval, Action action);

    /// <summary>
    /// Starts genuine asynchronous I/O outside the game thread and applies its result on the game thread.
    /// The host cancels and joins the operation during unload.
    /// </summary>
    void RunIo<T>(
        string name,
        Func<CancellationToken, ValueTask<T>> operation,
        Action<T> apply);
}

public interface IPluginStorage
{
    string DataDirectory { get; }

    T LoadJson<T>(string fileName, Func<T> defaults, Func<T, string?>? validate = null);

    void SaveJson<T>(string fileName, T value);
}

public interface IPluginLog
{
    void Write(OperationsLogLevel level, string message, Exception? exception = null);
}
