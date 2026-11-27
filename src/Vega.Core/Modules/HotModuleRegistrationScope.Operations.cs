namespace Vega.Core.Modules;

internal sealed partial class HotModuleRegistrationScope
{
    // CancelAsync marks its own token synchronously, but propagation to a linked token
    // is an asynchronous callback. Track the actual service tokens and cancel each
    // directly at retirement, before awaiting general activation cancellation.
    private readonly Dictionary<CancellationTokenSource, Task?> pendingActionTokens = [];

    private Task BeginRetirementCancellationLocked()
    {
        if (retirementCancellation is not null) return retirementCancellation;
        var cancellations = new List<Task>(pendingActionTokens.Count + 1);
        foreach (CancellationTokenSource source in pendingActionTokens.Keys.ToArray())
        {
            Task cancellation = source.CancelAsync();
            pendingActionTokens[source] = cancellation;
            cancellations.Add(cancellation);
        }
        cancellations.Add(lifetime.CancelAsync());
        return retirementCancellation = Task.WhenAll(cancellations);
    }

    private int scopedOperations;
    private const int MaximumScopedOperations = 64;
    private sealed class PluginScopeBusyException() : InvalidOperationException("Plugin service/database work queue is full.");

    // Service/data/REST/cluster work starts on a worker. Snapshot reads may initiate native
    // dispatch synchronously but discard results after retirement. Both use one bounded drain.
    private ValueTask<T> RunScopedAsync<T>(Func<CancellationToken, ValueTask<T>> operation, CancellationToken token) =>
        RunTrackedOperationAsync(operation, token, offload: true, discardAfterRetirement: false);

    private ValueTask<T> ReadServiceAsync<T>(Func<CancellationToken, ValueTask<T>> operation, CancellationToken token) =>
        RunTrackedOperationAsync(operation, token, offload: false, discardAfterRetirement: true);

    private ValueTask<T> RunTrackedOperationAsync<T>(Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken callerToken, bool offload, bool discardAfterRetirement)
    {
        CancellationTokenSource source;
        TaskCompletionSource settled;
        lock (sync)
        {
            if (disposed || !executionGuard.AcceptsCallbacks || lifetime.IsCancellationRequested || callerToken.IsCancellationRequested)
                return ValueTask.FromCanceled<T>(new CancellationToken(true));
            if (scopedOperations >= MaximumScopedOperations) throw new PluginScopeBusyException();
            scopedOperations++;
            source = callerToken.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(callerToken) : new CancellationTokenSource();
            pendingActionTokens.Add(source, null);
            settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // Track settlement rather than the caller-visible result; handled operation failures do not fail unload.
            TrackTask(settled.Task);
        }
        return RunTrackedCoreAsync(operation, source, settled, offload, discardAfterRetirement);
    }

    private async ValueTask<T> RunTrackedCoreAsync<T>(Func<CancellationToken, ValueTask<T>> operation,
        CancellationTokenSource source, TaskCompletionSource settled, bool offload, bool discardAfterRetirement)
    {
        try
        {
            source.Token.ThrowIfCancellationRequested();
            if (!executionGuard.AcceptsCallbacks) throw new OperationCanceledException(new CancellationToken(true));
            T result = offload
                ? await Task.Run(async () => await operation(source.Token).ConfigureAwait(false), source.Token).ConfigureAwait(false)
                : await operation(source.Token).ConfigureAwait(false);
            if (discardAfterRetirement)
            {
                source.Token.ThrowIfCancellationRequested();
                if (!executionGuard.AcceptsCallbacks) throw new OperationCanceledException(new CancellationToken(true));
            }
            return result;
        }
        finally
        {
            Task? cancellation;
            lock (sync)
            {
                pendingActionTokens.Remove(source, out cancellation);
                scopedOperations--;
            }
            if (cancellation is not null)
            {
                try { await cancellation.ConfigureAwait(false); }
                catch (Exception) { /* Retirement observes cancellation failure. */ }
            }
            source.Dispose();
            settled.TrySetResult();
        }
    }
}
