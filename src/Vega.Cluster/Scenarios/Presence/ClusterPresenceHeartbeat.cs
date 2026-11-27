using Vega.Cluster.Transport;
using Vega.Contracts.Cluster;
using Vega.Contracts.Operations;

namespace Vega.Cluster.Scenarios.Presence;

/// <summary>One host-owned heartbeat loop. No queued overlapping publications; stale peers expire if unavailable.</summary>
public sealed class ClusterPresenceHeartbeat : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private readonly AuthenticatedClusterTransport transport;
    private readonly Func<ClusterPresenceSample> sample;
    private readonly IOperationsLogger logger;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task loop;
    private int disposed;

    public ClusterPresenceHeartbeat(AuthenticatedClusterTransport transport, Func<ClusterPresenceSample> sample, IOperationsLogger logger)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.sample = sample ?? throw new ArgumentNullException(nameof(sample));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        CancellationToken token = shutdown.Token;
        loop = Task.Run(() => RunAsync(token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                token.ThrowIfCancellationRequested();
                try { await transport.AnnouncePresenceAsync(sample(), token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.Log(OperationsLogLevel.Warning, "cluster", "Presence sampling/publication failed; old observations will expire.");
                }
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { shutdown.Cancel(); } catch (AggregateException) { }
        try
        {
            // Withdraw only after the last announcement settled. A crashed or unavailable peer is
            // removed by its bounded lease; clean shutdown never waits indefinitely on remote peers.
            if (loop.Wait(TimeSpan.FromSeconds(6)))
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                transport.WithdrawPresenceAsync(deadline.Token).AsTask().Wait(deadline.Token);
            }
        }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException or ObjectDisposedException) { }
        finally { shutdown.Dispose(); }
    }
}
