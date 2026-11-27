using System.Diagnostics;

namespace Vega.Core.Modules;

// A synchronous Start cannot be cancelled. Its owner must retire it after completion.
internal sealed class ModuleStartupInProgressException(Task startup, Exception cause)
    : InvalidOperationException("Module startup is still running. A process restart is required.", cause)
{
    internal static async Task RunAsync(Action start, TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Task task = Task.Run(start, token);
        try { await task.WaitAsync(timeout, token).ConfigureAwait(false); }
        catch (Exception ex) when (!task.IsCompleted && ex is TimeoutException or OperationCanceledException)
        {
            throw new ModuleStartupInProgressException(task, ex);
        }
    }

    internal void Retire(Action cleanup) => _ = startup.ContinueWith(completed =>
    {
        _ = completed.Exception;
        try { cleanup(); }
        catch (Exception ex) { Trace.TraceError($"Deferred module cleanup failed: {ex}"); }
    }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
}
