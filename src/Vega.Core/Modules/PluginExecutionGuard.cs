using System.Diagnostics;
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
    internal const int DefaultFailureThreshold = 5;
    internal static readonly TimeSpan DefaultFailureWindow = TimeSpan.FromMinutes(1);

    private readonly object sync = new();
    private readonly IOperationsLogger logger;
    private readonly string source;
    private readonly int failureThreshold;
    private readonly long failureWindowTicks;
    private readonly Queue<long> recentFaults = new();
    private long faultCount;
    private int circuitOpen;
    private string? lastOperation;
    private string? lastFailure;

    public PluginExecutionGuard(
        IOperationsLogger logger,
        string pluginName,
        int failureThreshold = DefaultFailureThreshold,
        TimeSpan? failureWindow = null)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        if (failureThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(failureThreshold));
        }

        TimeSpan window = failureWindow ?? DefaultFailureWindow;
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(failureWindow));
        }

        source = $"plugin:{pluginName}";
        this.failureThreshold = failureThreshold;
        failureWindowTicks = Math.Max(
            1,
            (long)Math.Ceiling(window.TotalSeconds * Stopwatch.Frequency));
    }

    public long FaultCount => Interlocked.Read(ref faultCount);
    public bool CircuitOpen => Volatile.Read(ref circuitOpen) != 0;

    public PluginExecutionGuardSnapshot Snapshot()
    {
        lock (sync)
        {
            return new PluginExecutionGuardSnapshot(
                FaultCount,
                CircuitOpen,
                lastOperation,
                lastFailure);
        }
    }

    public bool TryInvoke(string operation, Action callback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(callback);
        if (CircuitOpen)
        {
            return false;
        }

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
        if (CircuitOpen)
        {
            return CommandExecutionResult.Failed("plugin.circuit_open");
        }

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
    }

    private void Record(string operation, Exception exception)
    {
        long count = Interlocked.Increment(ref faultCount);
        bool opened = false;
        long now = Stopwatch.GetTimestamp();
        lock (sync)
        {
            lastOperation = operation;
            lastFailure = exception.ToString();
            recentFaults.Enqueue(now);
            while (recentFaults.TryPeek(out long recordedAt) && now - recordedAt > failureWindowTicks)
            {
                recentFaults.Dequeue();
            }

            if (recentFaults.Count >= failureThreshold &&
                Interlocked.CompareExchange(ref circuitOpen, 1, 0) == 0)
            {
                opened = true;
            }
        }

        logger.Log(
            OperationsLogLevel.Error,
            source,
            $"Plugin callback '{operation}' failed and was isolated from the host. fault={count}.",
            exception);

        if (opened)
        {
            logger.Log(
                OperationsLogLevel.Critical,
                source,
                $"Plugin callback circuit opened after {failureThreshold} faults within " +
                $"{TimeSpan.FromSeconds((double)failureWindowTicks / Stopwatch.Frequency)}; new callbacks are suppressed.",
                exception);
        }
    }
}

internal sealed record PluginExecutionGuardSnapshot(
    long FaultCount,
    bool CircuitOpen,
    string? LastOperation,
    string? LastFailure);
