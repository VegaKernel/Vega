using Vega.Contracts.Commands;
using Vega.Contracts.Operations;

namespace Vega.Core.Modules;

/// <summary>
/// Contains exceptions thrown by ordinary PluginSdk callbacks at the Vega-owned boundary.
/// This guard deliberately does not attempt to recover from process-fatal failures or from
/// authoritative Terraria corruption; it only isolates ordinary managed plugin callback faults.
/// </summary>
internal sealed class PluginExecutionGuard
{
    private readonly IOperationsLogger logger;
    private readonly string source;
    private readonly int threshold;
    private readonly TimeSpan window;
    private readonly TimeProvider time;
    private readonly Action? onQuarantine;
    private readonly object sync = new();
    private readonly Queue<long> faults = new();
    private long faultCount;
    private string? lastOperation;
    private string? lastFailure;
    private int quarantined;
    private int activeCalls;

    public PluginExecutionGuard(IOperationsLogger logger, string pluginName, int threshold = 5,
        TimeSpan? window = null, Action? onQuarantine = null, TimeProvider? timeProvider = null)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        source = $"plugin:{pluginName}";
        this.threshold = Math.Max(1, threshold);
        this.window = window ?? TimeSpan.FromSeconds(60);
        time = timeProvider ?? TimeProvider.System;
        this.onQuarantine = onQuarantine;
    }

    public long FaultCount => Interlocked.Read(ref faultCount);
    public bool IsQuarantined => Volatile.Read(ref quarantined) != 0;

    public FaultSnapshot Snapshot()
    {
        lock (sync) return new(faultCount, lastOperation, lastFailure, IsQuarantined);
    }

    public readonly record struct FaultSnapshot(long Count, string? Operation, string? Summary, bool Quarantined);

    private bool TryEnter()
    {
        if (IsQuarantined) return false;
        Interlocked.Increment(ref activeCalls);
        if (!IsQuarantined) return true;
        Interlocked.Decrement(ref activeCalls);
        return false;
    }

    public async Task WaitForIdleAsync(CancellationToken token)
    {
        while (Volatile.Read(ref activeCalls) != 0) await Task.Delay(10, token).ConfigureAwait(false);
    }

    public bool TryInvoke(string operation, Action callback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(callback);
        if (!TryEnter()) return false;

        try
        {
            callback();
            return true;
        }
        catch (Exception exception)
        {
            Record(operation, exception);
            return false;
        }
        finally { Interlocked.Decrement(ref activeCalls); }
    }

    public bool TryInvoke<T>(string operation, Action<T> callback, T argument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(callback);
        if (!TryEnter()) return false;

        try
        {
            callback(argument);
            return true;
        }
        catch (Exception exception)
        {
            Record(operation, exception);
            return false;
        }
        finally { Interlocked.Decrement(ref activeCalls); }
    }

    public bool Allows<T>(string operation, Func<T, bool> callback, T argument)
    {
        if (!TryEnter()) return true;
        try { return callback(argument); }
        catch (Exception exception)
        {
            Record(operation, exception);
            return true;
        }
        finally { Interlocked.Decrement(ref activeCalls); }
    }

    public async ValueTask<CommandExecutionResult> InvokeCommandAsync(
        string operation,
        ICommandHandler handler,
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(invocation);
        if (!TryEnter()) return CommandExecutionResult.Failed("plugin.quarantined");

        try
        {
            return await handler.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Record(operation, exception);
            return CommandExecutionResult.Failed("plugin.callback_failed");
        }
        finally { Interlocked.Decrement(ref activeCalls); }
    }

    public void Record(string operation, Exception exception)
    {
        long count;
        bool trip = false;
        lock (sync)
        {
            count = Interlocked.Increment(ref faultCount);
            lastOperation = operation;
            lastFailure = exception.Message.Length <= 512 ? exception.Message : exception.Message[..512];
            if (!IsQuarantined)
            {
                long now = time.GetTimestamp();
                while (faults.TryPeek(out long oldest) && time.GetElapsedTime(oldest, now) > window) faults.Dequeue();
                faults.Enqueue(now);
                if (faults.Count >= threshold)
                {
                    Volatile.Write(ref quarantined, 1);
                    faults.Clear();
                    trip = true;
                }
            }
        }
        try
        {
            logger.Log(OperationsLogLevel.Error, source,
                $"Plugin callback '{operation}' failed and was isolated from the host. fault={count}.", exception);
            if (trip) logger.Log(OperationsLogLevel.Warning, source, "Plugin callback breaker tripped; new callbacks are quarantined.");
        }
        finally { if (trip) onQuarantine?.Invoke(); }
    }
}
