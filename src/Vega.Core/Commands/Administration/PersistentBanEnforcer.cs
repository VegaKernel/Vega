using System.Net;
using Vega.Contracts.Accounts;
using Vega.Contracts.Bans;
using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Core.Bans;

namespace Vega.Core.Commands.Administration;

/// <summary>Rechecks this process's connected sessions against the shared persistent ban repository.</summary>
public sealed class PersistentBanEnforcer : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromSeconds(30);
    private readonly IBanService bans;
    private readonly IPlayerDirectory players;
    private readonly IPlayerOperations playerOperations;
    private readonly IOperationsLogger logger;
    private readonly IPlayerAccountSessionService? accounts;
    private readonly IGameThreadDispatcher? dispatcher;
    private readonly TimeProvider time;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task worker;
    private readonly Dictionary<PlayerSessionId, (GlobalBanId BanId, DateTimeOffset AttemptedAt)> attempts = [];
    private Task? deferredKick;
    private DateTimeOffset? lastFailureLog;
    private int disposed;

    public PersistentBanEnforcer(IBanService bans, IPlayerDirectory players, IPlayerOperations playerOperations,
        IPlayerSessionLifecycle sessions, IOperationsLogger logger, IPlayerAccountSessionService? accounts = null,
        IGameThreadDispatcher? dispatcher = null, TimeProvider? timeProvider = null, TimeSpan? interval = null)
        : this(bans, players, playerOperations, sessions, logger, accounts, dispatcher, timeProvider, interval, startWorker: true)
    {
    }

    internal PersistentBanEnforcer(IBanService bans, IPlayerDirectory players, IPlayerOperations playerOperations,
        IPlayerSessionLifecycle sessions, IOperationsLogger logger, IPlayerAccountSessionService? accounts,
        IGameThreadDispatcher? dispatcher, TimeProvider? timeProvider, TimeSpan? interval, bool startWorker)
    {
        this.bans = bans ?? throw new ArgumentNullException(nameof(bans));
        this.players = players ?? throw new ArgumentNullException(nameof(players));
        this.playerOperations = playerOperations ?? throw new ArgumentNullException(nameof(playerOperations));
        // Preserve the existing constructor contract, but do not spawn a database read per lifecycle event.
        ArgumentNullException.ThrowIfNull(sessions);
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.accounts = accounts;
        this.dispatcher = dispatcher;
        time = timeProvider ?? TimeProvider.System;
        TimeSpan period = interval ?? TimeSpan.FromSeconds(2);
        if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        worker = startWorker ? Task.Run(() => RunAsync(period, shutdown.Token)) : Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        shutdown.Cancel();
        try { worker.GetAwaiter().GetResult(); }
        finally { shutdown.Dispose(); }
    }

    private async Task RunAsync(TimeSpan period, CancellationToken token)
    {
        using var timer = new PeriodicTimer(period, time);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try { await ScanOnceAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { LogFailure("Unable to refresh shared persistent bans.", ex); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    // Only the periodic worker calls this in production. One shared database snapshot per scan,
    // no per-player database reads or unbounded game-thread requests.
    internal async Task ScanOnceAsync(CancellationToken token = default)
    {
        PlayerSnapshot[] online = players.SnapshotOnlinePlayers().Where(player => player.SessionId is not null).ToArray();
        if (online.Length == 0)
        {
            attempts.Clear();
            return;
        }

        var onlineSessions = new HashSet<PlayerSessionId>(online.Select(player => player.SessionId!.Value));
        foreach (PlayerSessionId session in attempts.Keys.Where(session => !onlineSessions.Contains(session)).ToArray())
            attempts.Remove(session);

        token.ThrowIfCancellationRequested();
        if (deferredKick is { IsCompleted: false }) return;
        deferredKick = null;
        DateTimeOffset now = time.GetUtcNow();
        IReadOnlyList<BanSnapshot> active = bans.SnapshotActive(now);
        foreach (PlayerSnapshot observed in online)
        {
            token.ThrowIfCancellationRequested();
            if (deferredKick is { IsCompleted: false }) break;
            PlayerSessionId session = observed.SessionId!.Value;
            (PlayerAccountBinding? binding, string? accountName) = CaptureAccount(observed);
            BanSnapshot? matched = GlobalBanService.MatchActive(active, Identity(observed, accountName), now);
            if (matched is null) continue;
            if (attempts.TryGetValue(session, out var previous) && previous.BanId == matched.Id &&
                now - previous.AttemptedAt < RetryInterval) continue;

            PlayerOperationResult? result = await ApplyAsync(observed, binding, matched, token).ConfigureAwait(false);
            if (result is not { } applied) continue; // Session/identity changed while this scan was queued.
            if (applied.IsSuccess) attempts[session] = (matched.Id, now);
            else if (applied.Status is not (PlayerOperationStatus.PlayerNotFound or PlayerOperationStatus.Cancelled))
                LogFailure($"An active shared ban could not be enforced ({applied.ErrorCode ?? "unknown"}); will retry.");
        }
    }

    private (PlayerAccountBinding?, string?) CaptureAccount(PlayerSnapshot player)
    {
        // A slot-only account snapshot is not authority. Account kicks must run under the exact
        // authenticated tenure on the game thread, even if logout/relogin happens after the scan.
        if (accounts is null || dispatcher is null || !accounts.TryGetBinding(player, out PlayerAccountBinding binding))
            return (null, null);
        return accounts.TryRunWithBinding(binding, () => ReadAccountName(binding), out string? name) && name is not null
            ? (binding, name) : (null, null);
    }

    private string? ReadAccountName(PlayerAccountBinding binding) =>
        accounts is not null && accounts.TryGetAccount(binding.PlayerId, out AccountSnapshot? account) && account is not null && account.Id == binding.AccountId
            ? account.Name : null;

    private static BanIdentity Identity(PlayerSnapshot player, string? accountName)
    {
        IPAddress? address = BanCommand.TryGetIpAddress(player.Connection?.EffectiveEndpoint ?? player.RemoteEndpoint, out string? raw)
            && IPAddress.TryParse(raw, out IPAddress? parsed) ? parsed : null;
        return new BanIdentity(player.Name, accountName, player.Connection?.ClientUuid, address);
    }

    private bool TryCurrent(PlayerSnapshot observed, out PlayerSnapshot? current) =>
        players.TryGet(observed.Id, out current) && current is not null && current.SessionId == observed.SessionId && current.WorldId == observed.WorldId;

    private async ValueTask<PlayerOperationResult?> ApplyAsync(PlayerSnapshot observed, PlayerAccountBinding? binding, BanSnapshot ban, CancellationToken token)
    {
        if (dispatcher is null)
        {
            // Compatibility for hosts without an explicit dispatcher. Never enforce account-only
            // matches here, where the account tenure cannot be checked at the actual mutation.
            if (!TryCurrent(observed, out PlayerSnapshot? current) || ban.Subject.Kind == GlobalBanSubjectKind.Account ||
                !StillMatches(ban, current!, null)) return null;
            return await playerOperations.KickAsync(current!, $"Banned: {ban.Reason}", token).AsTask().WaitAsync(token).ConfigureAwait(false);
        }

        if (!dispatcher.IsGameThreadKnown) return PlayerOperationResult.DispatcherUnavailable();
        if (dispatcher.IsGameThread) return ApplyOnGameThread(observed, binding, ban, token);
        var completion = new TaskCompletionSource<PlayerOperationResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryPost("ban.enforce", () =>
        {
            if (token.IsCancellationRequested) { completion.TrySetCanceled(token); return; }
            try { completion.TrySetResult(ApplyOnGameThread(observed, binding, ban, token)); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) return PlayerOperationResult.DispatcherUnavailable();
        return await completion.Task.WaitAsync(token).ConfigureAwait(false);
    }

    private PlayerOperationResult? ApplyOnGameThread(PlayerSnapshot observed, PlayerAccountBinding? binding, BanSnapshot ban, CancellationToken token)
    {
        dispatcher!.AssertGameThread();
        token.ThrowIfCancellationRequested();
        if (!TryCurrent(observed, out PlayerSnapshot? current)) return null;
        if (ban.Subject.Kind != GlobalBanSubjectKind.Account)
            return StillMatches(ban, current!, null) ? KickNow(current!, ban, token) : null;

        if (accounts is null || binding is not { } exact) return null;
        return accounts.TryRunWithBinding(exact, () =>
        {
            string? accountName = ReadAccountName(exact);
            return accountName is not null && StillMatches(ban, current!, accountName)
                ? KickNow(current!, ban, token) : (PlayerOperationResult?)null;
        }, out PlayerOperationResult? result) ? result : null;
    }

    private bool StillMatches(BanSnapshot ban, PlayerSnapshot current, string? accountName) =>
        GlobalBanService.MatchActive([ban], Identity(current, accountName), time.GetUtcNow()) is not null;

    private PlayerOperationResult KickNow(PlayerSnapshot current, BanSnapshot ban, CancellationToken token)
    {
        // The native player boundary completes synchronously when already on its authoritative game thread.
        // If a different adapter tries to defer the operation, cancel rather than holding an auth lock over I/O.
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
        ValueTask<PlayerOperationResult> operation = playerOperations.KickAsync(current, $"Banned: {ban.Reason}", guard.Token);
        if (operation.IsCompleted) return operation.GetAwaiter().GetResult();
        guard.Cancel();
        // Keep at most one deferred operation outstanding, even if an incompatible adapter ignores cancellation.
        deferredKick = ObserveDeferredKickAsync(operation);
        return PlayerOperationResult.Failed("player.kick_not_synchronous");
    }

    private static async Task ObserveDeferredKickAsync(ValueTask<PlayerOperationResult> operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch (Exception) { }
    }

    private void LogFailure(string message, Exception? exception = null)
    {
        DateTimeOffset now = time.GetUtcNow();
        if (lastFailureLog is { } previous && now - previous < FailureLogInterval) return;
        lastFailureLog = now;
        logger.Log(exception is null ? OperationsLogLevel.Warning : OperationsLogLevel.Error, "bans", message, exception);
    }
}
