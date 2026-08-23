using Vega.Contracts.Accounts;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;

namespace Vega.Sandboxes;

/// <summary>
/// Reconciles a server-authenticated sandbox transfer with account/session identity and the
/// world-specific SSC snapshot. Character I/O remains behind the Terraria game-thread adapter.
/// </summary>
public sealed class SandboxSscSessionCoordinator : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(5);

    private readonly object sync = new();
    private readonly IPlayerDirectory players;
    private readonly IPlayerSessionLifecycle lifecycle;
    private readonly IPlayerAccountSessionService accounts;
    private readonly IPlayerOperations playerOperations;
    private readonly ISandboxSscStore store;
    private readonly ISandboxSscCharacterAdapter characters;
    private readonly WorldId worldId;
    private readonly SandboxSscMode mode;
    private readonly IOperationsLogger logger;
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource stopping = new();
    private readonly Dictionary<PlayerSessionId, SessionState> sessions = [];
    private readonly Task worker;
    private bool disposed;

    public SandboxSscSessionCoordinator(
        IPlayerDirectory players,
        IPlayerSessionLifecycle lifecycle,
        IPlayerAccountSessionService accounts,
        IPlayerOperations playerOperations,
        ISandboxSscStore store,
        ISandboxSscCharacterAdapter characters,
        WorldId worldId,
        SandboxSscMode mode,
        IOperationsLogger logger,
        TimeProvider? timeProvider = null)
    {
        this.players = players ?? throw new ArgumentNullException(nameof(players));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.playerOperations = playerOperations ?? throw new ArgumentNullException(nameof(playerOperations));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.characters = characters ?? throw new ArgumentNullException(nameof(characters));
        this.worldId = worldId;
        this.mode = mode;
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        lifecycle.Closed += OnClosed;
        worker = RunAsync(stopping.Token);
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            lifecycle.Closed -= OnClosed;
        }

        stopping.Cancel();
        try
        {
            worker.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            stopping.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            PlayerSnapshot[] snapshot = [.. players.SnapshotOnlinePlayers()];
            foreach (PlayerSnapshot player in snapshot)
            {
                if (player.SessionId is not PlayerSessionId sessionId ||
                    player.Connection is not ConnectionMetadataSnapshot connection ||
                    string.IsNullOrWhiteSpace(connection.TrustedTransferAccountId) ||
                    connection.LoginPhase < ConnectionLoginPhase.WorldRequested)
                {
                    continue;
                }

                SessionState session;
                bool initialize = false;
                lock (sync)
                {
                    if (!sessions.TryGetValue(sessionId, out session!))
                    {
                        session = new SessionState(
                            sessionId,
                            player.Id,
                            connection.TrustedTransferAccountId,
                            timeProvider.GetUtcNow());
                        sessions.Add(sessionId, session);
                        initialize = true;
                    }
                }

                if (initialize)
                {
                    await InitializeAsync(session, cancellationToken).ConfigureAwait(false);
                }
                else if (session.Initialized && !session.Failed && mode != SandboxSscMode.Disabled &&
                         timeProvider.GetUtcNow() - session.LastCheckpoint >= CheckpointInterval)
                {
                    await CheckpointAsync(session, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task InitializeAsync(SessionState session, CancellationToken cancellationToken)
    {
        if (int.TryParse(session.AccountId, out int numericAccountId) && numericAccountId > 0)
        {
            bool authenticated = await accounts.EstablishTrustedTransferAsync(
                session.PlayerId,
                session.AccountId,
                cancellationToken).ConfigureAwait(false);
            if (!authenticated)
            {
                await FailAsync(
                    session,
                    "sandbox.transfer.account_restore_failed",
                    "Transferred account identity could not be restored.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (mode != SandboxSscMode.Disabled)
        {
            SandboxSscStateSnapshot? state = store.Read(session.AccountId, worldId, mode);
            if (state is null)
            {
                await FailAsync(
                    session,
                    "sandbox.ssc.state_missing",
                    "Sandbox character state is unavailable for this transfer.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            SandboxSscCharacterApplyResult applied = await characters.ApplyAsync(
                session.PlayerId,
                state.Payload,
                cancellationToken).ConfigureAwait(false);
            if (!applied.Succeeded)
            {
                await FailAsync(
                    session,
                    applied.ErrorCode ?? "sandbox.ssc.apply_failed",
                    "Sandbox character state could not be applied.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            session.Revision = state.Revision;
        }

        session.Initialized = true;
        session.LastCheckpoint = timeProvider.GetUtcNow();
        logger.Log(
            OperationsLogLevel.Information,
            "sandboxes",
            $"Accepted transferred player slot {session.PlayerId.Slot} into world '{worldId}' with SSC mode {mode}.",
            correlationId: session.SessionId.ToString());
    }

    private async Task CheckpointAsync(SessionState session, CancellationToken cancellationToken)
    {
        SandboxSscCharacterCaptureResult captured = await characters.CaptureAsync(
            session.PlayerId,
            cancellationToken).ConfigureAwait(false);
        if (!captured.Succeeded || captured.Payload is null)
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "sandboxes",
                $"SSC checkpoint capture failed for slot {session.PlayerId.Slot}: {captured.ErrorCode ?? "unknown"}.",
                correlationId: session.SessionId.ToString());
            return;
        }

        SandboxSscWriteResult saved = store.Save(
            session.AccountId,
            worldId,
            mode,
            captured.Payload,
            session.Revision,
            timeProvider.GetUtcNow());
        if (saved.Status == SandboxSscWriteStatus.Conflict)
        {
            await FailAsync(
                session,
                "sandbox.ssc.revision_conflict",
                "Sandbox character state changed concurrently; reconnect to avoid overwriting it.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!saved.IsSuccess || saved.State is null)
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "sandboxes",
                $"SSC checkpoint save failed for slot {session.PlayerId.Slot}: {saved.ErrorCode ?? "unknown"}.",
                correlationId: session.SessionId.ToString());
            return;
        }

        session.Revision = saved.State.Revision;
        session.LastCheckpoint = timeProvider.GetUtcNow();
    }

    private void OnClosed(PlayerSessionClosed closed)
    {
        SessionState? session;
        lock (sync)
        {
            if (!sessions.Remove(closed.SessionId, out session) || session is null)
            {
                return;
            }
        }

        if (session.Initialized && !session.Failed && mode != SandboxSscMode.Disabled)
        {
            _ = PersistClosedAsync(session);
        }
    }

    private async Task PersistClosedAsync(SessionState session)
    {
        try
        {
            SandboxSscCharacterCaptureResult captured = await characters.CaptureAsync(session.PlayerId).ConfigureAwait(false);
            if (!captured.Succeeded || captured.Payload is null)
            {
                return;
            }

            SandboxSscWriteResult saved = store.Save(
                session.AccountId,
                worldId,
                mode,
                captured.Payload,
                session.Revision,
                timeProvider.GetUtcNow());
            if (saved.Status == SandboxSscWriteStatus.Conflict)
            {
                logger.Log(
                    OperationsLogLevel.Error,
                    "sandboxes",
                    $"SSC disconnect checkpoint conflicted for slot {session.PlayerId.Slot}; newer persisted state was preserved.",
                    correlationId: session.SessionId.ToString());
            }
        }
        catch (Exception ex)
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "sandboxes",
                $"SSC disconnect checkpoint failed for slot {session.PlayerId.Slot}.",
                ex,
                correlationId: session.SessionId.ToString());
        }
    }

    private async Task FailAsync(
        SessionState session,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        session.Failed = true;
        logger.Log(
            OperationsLogLevel.Error,
            "sandboxes",
            $"Sandbox session for slot {session.PlayerId.Slot} failed: {code}.",
            correlationId: session.SessionId.ToString());
        await playerOperations.KickAsync(session.PlayerId, message, cancellationToken).ConfigureAwait(false);
    }

    private sealed class SessionState(
        PlayerSessionId sessionId,
        PlayerId playerId,
        string accountId,
        DateTimeOffset now)
    {
        public PlayerSessionId SessionId { get; } = sessionId;
        public PlayerId PlayerId { get; } = playerId;
        public string AccountId { get; } = accountId;
        public long Revision { get; set; }
        public DateTimeOffset LastCheckpoint { get; set; } = now;
        public bool Initialized { get; set; }
        public bool Failed { get; set; }
    }
}
