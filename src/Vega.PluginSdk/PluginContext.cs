using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.PluginSdk;

/// <summary>
/// Curated capabilities available to a Vega plugin. This is a fixed facade,
/// not a service locator and not a runtime implementation surface.
/// </summary>
public interface IVegaPluginContext
{
    IPluginCommands Commands { get; }

    IPluginPlayers Players { get; }

    /// <summary>Curated immutable-snapshot access to the world hosted by this process.</summary>
    IPluginWorld World => VegaPluginRuntime.World(this);

    /// <summary>Game-thread notifications. Subscriptions detach automatically on unload.</summary>
    IPluginWorldEvents WorldEvents => throw new NotSupportedException("World gameplay events are unavailable in this host.");

    IPluginActivity Activity => throw new NotSupportedException("Player activity observations are unavailable in this host.");

    IPluginIdentity Identity => throw new NotSupportedException("Identity queries are unavailable in this host.");
    IPluginStatistics Statistics => throw new NotSupportedException("Player statistics are unavailable in this host.");
    IPluginWarps Warps => throw new NotSupportedException("Warp queries are unavailable in this host.");

    IPluginTasks Tasks { get; }
    /// <summary>Plugin-owned disposable resources, retired with this activation.</summary>
    IPluginResources Resources => throw new NotSupportedException("Resource ownership is unavailable in this host.");

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

public interface IPluginCommands
{
    /// <summary>Registers a command until the plugin is unloaded.</summary>
    void Register(ICommandHandler handler);
}

public interface IPluginPlayers
{
    IReadOnlyList<PlayerSnapshot> Online { get; }

    /// <summary>Optimistic, game-thread-dispatched access to player inventory.</summary>
    IPluginPlayerInventory Inventory => VegaPluginRuntime.Inventory;

    IPluginPlayerActions Actions => throw new NotSupportedException("Player actions are unavailable in this host.");

    bool TryGet(PlayerId id, out PlayerSnapshot? player);

    bool TryFindExact(string name, out PlayerSnapshot? player);

    /// <summary>Reads redacted metadata only while the observed world and native session still match.</summary>
    bool TryGetConnectionSummary(PlayerSnapshot expected, out PlayerConnectionSummary? summary)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.SessionId is { } session && TryGet(expected.Id, out var current) && current is not null &&
            current.SessionId == session && current.WorldId == expected.WorldId)
        {
            summary = new(current.Id, session, current.WorldId, current.Platform, current.ConnectedAt, current.Connection?.LoginPhase);
            return true;
        }

        summary = null;
        return false;
    }

    /// <summary>Counts reported platforms among current sessions in the specified world, including Unknown.</summary>
    IReadOnlyList<PlayerPlatformCount> SnapshotPlatformCounts(WorldId worldId) => PlayerPlatformStatistics.Snapshot(Online, worldId);

    /// <summary>Sends a message through a host-tracked player operation.</summary>
    void Message(PlayerId id, string message);

    /// <summary>Send only to the observed session, including when work is dispatched later.</summary>
    void Message(PlayerSnapshot player, string message) => throw new NotSupportedException("Session-bound messaging is unavailable.");

    ValueTask<PlayerOperationResult> MessageAsync(
        PlayerId id,
        string message,
        CancellationToken cancellationToken = default);

    ValueTask<PlayerOperationResult> MessageAsync(PlayerSnapshot player, string message, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(PlayerOperationResult.DispatcherUnavailable());

    ValueTask<PlayerOperationResult> KickAsync(
        PlayerId id,
        string reason,
        CancellationToken cancellationToken = default);

    ValueTask<PlayerOperationResult> TeleportAsync(
        PlayerId id,
        PlayerPosition destination,
        CancellationToken cancellationToken = default);

    /// <summary>Registers a game-thread player-join callback for this plugin lifetime.</summary>
    void OnJoined(Action<PlayerSessionOpened> handler);

    /// <summary>Registers a game-thread player-leave callback for this plugin lifetime.</summary>
    void OnLeft(Action<PlayerSessionClosed> handler);

    /// <summary>Game-thread player entry into the native world, bound to an exact session.</summary>
    void OnEnteredWorld(Action<PlayerSnapshot> handler) => throw new NotSupportedException("World-entry events are unavailable.");

    /// <summary>Game-thread notification before the native character/session is reset.</summary>
    void OnClosing(Action<PlayerSessionClosed> handler) => throw new NotSupportedException("Pre-reset events are unavailable.");
}

public interface IPluginResources
{
    /// <summary>Transfers ownership of a resource to the activation. Register each resource once.</summary>
    T Own<T>(T resource) where T : class, IDisposable;

    /// <summary>Transfers ownership of an asynchronous resource to the activation.</summary>
    T OwnAsync<T>(T resource) where T : class, IAsyncDisposable;

    /// <summary>Registers cleanup executed after callback/task drain, in reverse registration order.</summary>
    void OnCleanup(Action cleanup);
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

    /// <summary>Admits bounded CPU work on a detached input; publishes the result on the game thread.
    /// Returns false under load or retirement. Do not capture mutable Terraria state. Respect cancellation.</summary>
    bool TryRunCpu<T>(string name, Func<CancellationToken, T> compute, Action<T> apply) => false;
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
