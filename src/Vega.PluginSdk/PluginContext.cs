using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;

namespace Vega.PluginSdk;

/// <summary>
/// Curated capabilities available to an ordinary plugin. This is a fixed facade,
/// not a service locator and not a runtime implementation surface.
/// </summary>
public interface IVegaPluginContext
{
    IPluginCommands Commands { get; }

    IPluginPlayers Players { get; }

    IPluginTasks Tasks { get; }

    IPluginStorage Storage { get; }

    IPluginLog Log { get; }

    /// <summary>
    /// Process-internal diagnostics. Disabled unless the host explicitly enables the DEBUG API
    /// and trusts this plugin by name.
    /// </summary>
    IVegaDebugContext Debug => VegaDebug.Disabled;
}

public interface IPluginCommands
{
    /// <summary>Registers a command until the plugin is unloaded.</summary>
    void Register(ICommandHandler handler);
}

public interface IPluginPlayers
{
    IReadOnlyList<PlayerSnapshot> Online { get; }

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
