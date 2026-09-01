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
    private long faultCount;

    public PluginExecutionGuard(IOperationsLogger logger, string pluginName)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        source = $"plugin:{pluginName}";
    }

    public long FaultCount => Interlocked.Read(ref faultCount);

    public bool TryInvoke(string operation, Action callback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(callback);

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
        logger.Log(
            OperationsLogLevel.Error,
            source,
            $"Plugin callback '{operation}' failed and was isolated from the host. fault={count}.",
            exception);
    }
}
