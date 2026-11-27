using Vega.Contracts.Accounts;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Core.Players;

/// <summary>One lightweight in-memory ledger per world process. Native callbacks never perform database I/O.</summary>
public sealed class PlayerPlaytimeService : IPlayerPlaytimeSessionReader, IDisposable
{
    private const int MaximumTrackedTenures = 8192;
    private readonly object sync = new();
    private readonly IPlayerSessionLifecycle lifecycle;
    private readonly IPlayerSessionClosing closing;
    private readonly IPlayerSessionEnteredWorld entering;
    private readonly IPlayerAccountSessionService accounts;
    private readonly IPlayerPlaytimeStore store;
    private readonly WorldId world;
    private readonly IOperationsLogger logger;
    private readonly TimeProvider time;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task worker;
    private readonly Dictionary<PlayerId, PlayerSessionId> entered = [];
    private readonly Dictionary<PlayerId, PlayerSessionId> sessions = [];
    private readonly Dictionary<PlayerId, ActiveTenure> active = [];
    private readonly Dictionary<Guid, PlayerPlaytimeCheckpoint> completed = [];
    private bool disposed;
    private bool backlogWarning;

    public PlayerPlaytimeService(IPlayerSessionLifecycle lifecycle, IPlayerSessionClosing closing,
        IPlayerSessionEnteredWorld entering, IPlayerAccountSessionService accounts, IPlayerPlaytimeStore store,
        WorldId world, IOperationsLogger logger)
        : this(lifecycle, closing, entering, accounts, store, world, logger, TimeProvider.System, true) { }

    internal PlayerPlaytimeService(IPlayerSessionLifecycle lifecycle, IPlayerSessionClosing closing,
        IPlayerSessionEnteredWorld entering, IPlayerAccountSessionService accounts, IPlayerPlaytimeStore store,
        WorldId world, IOperationsLogger logger, TimeProvider time, bool startWorker)
    {
        this.lifecycle = lifecycle;
        this.closing = closing;
        this.entering = entering;
        this.accounts = accounts;
        this.store = store;
        this.world = world;
        this.logger = logger;
        this.time = time;
        lifecycle.Opened += OnOpened;
        lifecycle.Closed += OnClosed;
        closing.Closing += OnClosed;
        entering.EnteredWorld += OnEntered;
        accounts.BindingEstablished += OnAuthenticated;
        accounts.AuthenticationEnded += OnAuthenticationEnded;
        worker = startWorker ? Task.Run(RunAsync) : Task.CompletedTask;
    }

    private void OnOpened(PlayerSessionOpened opened)
    {
        lock (sync)
        {
            if (disposed || sessions.GetValueOrDefault(opened.PlayerId) == opened.SessionId) return;
            End(opened.PlayerId);
            entered.Remove(opened.PlayerId);
            sessions[opened.PlayerId] = opened.SessionId;
        }
    }

    private void OnClosed(PlayerSessionClosed closed)
    {
        lock (sync)
        {
            if (disposed || !sessions.TryGetValue(closed.PlayerId, out var session) || session != closed.SessionId) return;
            End(closed.PlayerId);
            entered.Remove(closed.PlayerId);
            sessions.Remove(closed.PlayerId);
        }
    }

    private void OnEntered(PlayerSnapshot player)
    {
        if (player.WorldId != world || player.SessionId is not { } session) return;
        lock (sync)
        {
            if (disposed || !sessions.TryGetValue(player.Id, out var current) || current != session) return;
            entered[player.Id] = session;
        }
        if (accounts.TryGetBinding(player, out var binding)) OnAuthenticated(binding);
    }

    private void OnAuthenticated(PlayerAccountBinding binding)
    {
        // Account lifetime lock is always outermost; never acquire it while holding the ledger lock.
        bool warned = false;
        accounts.TryRunWithBinding(binding, () =>
        {
            lock (sync)
            {
                if (disposed || !entered.TryGetValue(binding.PlayerId, out var session) || session != binding.SessionId) return false;
                if (active.TryGetValue(binding.PlayerId, out var old) && old.Binding == binding) return false;
                End(binding.PlayerId);
                if (active.Count + completed.Count >= MaximumTrackedTenures)
                {
                    warned = !backlogWarning;
                    backlogWarning = true;
                    return false;
                }
                active[binding.PlayerId] = new(binding, time.GetUtcNow(), time.GetTimestamp());
                return true;
            }
        }, out _);
        if (warned) logger.Log(OperationsLogLevel.Warning, "playtime", "Checkpoint backlog exhausted; new tenures cannot be tracked until persistence recovers.");
    }

    private void OnAuthenticationEnded(PlayerAuthenticationEnded ended)
    {
        lock (sync)
        {
            if (!disposed && active.TryGetValue(ended.Binding.PlayerId, out var tenure) && tenure.Binding == ended.Binding)
                End(ended.Binding.PlayerId);
        }
    }

    private void End(PlayerId player)
    {
        if (active.Remove(player, out var tenure))
        {
            var checkpoint = Snapshot(tenure, time.GetTimestamp(), ended: true);
            completed.Add(checkpoint.AuthenticationId, checkpoint);
        }
    }

    private PlayerPlaytimeCheckpoint Snapshot(ActiveTenure tenure, long now, bool ended) =>
        new(tenure.Binding.AuthenticationId, tenure.Binding.AccountId, tenure.Binding.SessionId, world, tenure.StartedAt,
            time.GetElapsedTime(tenure.StartTimestamp, now), ended);

    /// <summary>Returns persisted totals and live session elapsed independently (do not sum the two).</summary>
    public async ValueTask<PlayerPlaytimeView?> ReadCurrentAsync(PlayerSnapshot player, CancellationToken cancellationToken = default)
    {
        if (player.WorldId != world || !accounts.TryGetBinding(player, out var binding)) return null;
        if (!TryGetElapsed(binding, out _)) return null;
        // SQLite's nominal async operations may perform synchronous I/O. Keep the entire query off the owner thread.
        var result = await Task.Run(async () => await store.ReadAsync(binding.AccountId, world, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        if (!accounts.TryRunWithBinding(binding, () => TryGetElapsed(binding, out var elapsed) ? elapsed : (TimeSpan?)null, out var current) || current is null)
            return null;
        return new(result, current.Value);
    }

    private bool TryGetElapsed(PlayerAccountBinding binding, out TimeSpan elapsed)
    {
        lock (sync)
        {
            elapsed = default;
            if (disposed || !active.TryGetValue(binding.PlayerId, out var tenure) || tenure.Binding != binding) return false;
            elapsed = time.GetElapsedTime(tenure.StartTimestamp);
            return true;
        }
    }

    // Only the single periodic worker (then the shutdown flush) invokes this in production.
    internal async Task FlushOnceAsync(CancellationToken token = default)
    {
        PlayerPlaytimeCheckpoint[] snapshot;
        lock (sync)
        {
            long now = time.GetTimestamp();
            snapshot = [.. completed.Values, .. active.Values.Select(tenure => Snapshot(tenure, now, ended: false))];
        }
        if (snapshot.Length == 0) return;
        await store.WriteAsync(snapshot, token).ConfigureAwait(false);
        lock (sync)
        {
            foreach (var entry in snapshot)
                if (entry.Ended) completed.Remove(entry.AuthenticationId);
            if (active.Count + completed.Count < MaximumTrackedTenures) backlogWarning = false;
        }
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        CancellationToken token = shutdown.Token;
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try { await FlushOnceAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { logger.Log(OperationsLogLevel.Warning, "playtime", "Gameplay time checkpoint failed; retained for retry."); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            foreach (var player in active.Keys.ToArray()) End(player);
            entered.Clear();
            sessions.Clear();
        }
        lifecycle.Opened -= OnOpened;
        lifecycle.Closed -= OnClosed;
        closing.Closing -= OnClosed;
        entering.EnteredWorld -= OnEntered;
        accounts.BindingEstablished -= OnAuthenticated;
        accounts.AuthenticationEnded -= OnAuthenticationEnded;
        shutdown.Cancel();
        // Runtime retirement, not disconnect/gameplay callbacks. Bound the final drain even if the DB is unavailable.
        try
        {
            Task.Run(async () =>
            {
                try
                {
                    await worker.ConfigureAwait(false);
                    using var final = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await FlushOnceAsync(final.Token).ConfigureAwait(false);
                }
                finally { shutdown.Dispose(); }
            }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch (Exception) { logger.Log(OperationsLogLevel.Warning, "playtime", "Final gameplay time checkpoint did not complete before shutdown."); }
    }

    private sealed record ActiveTenure(PlayerAccountBinding Binding, DateTimeOffset StartedAt, long StartTimestamp);
}
