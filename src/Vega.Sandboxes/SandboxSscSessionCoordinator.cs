using Vega.Contracts.Accounts;
using Vega.Contracts.Commands;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;
using System.Globalization;

namespace Vega.Sandboxes;

public enum SscCharacterReadiness
{
    Unavailable,
    Unauthenticated,
    NotManaged,
    Restoring,
    Ready,
    Failed
}

/// <summary>
/// Reconciles a server-authenticated sandbox transfer or opt-in primary character persistence
/// with account/session identity. Character I/O remains behind the native game-thread adapter.
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
    private readonly ISandboxSscStore? primaryCharacterStore;
    private readonly ISandboxSscCharacterAdapter characters;
    private readonly WorldId worldId;
    private readonly SandboxSscMode mode;
    private readonly IOperationsLogger logger;
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim reconciliation = new(0, 1);
    private readonly Dictionary<PlayerSessionId, SessionState> sessions = [];
    private readonly Dictionary<PlayerSessionId, string> joinPayloads = [];
    private readonly Dictionary<string, Task> finalWrites = new(StringComparer.Ordinal);
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
        TimeProvider? timeProvider = null,
        ISandboxSscStore? primaryCharacterStore = null)
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
        this.primaryCharacterStore = worldId == WorldId.Primary ? primaryCharacterStore : null;
        lifecycle.Closed += OnClosed;
        if (lifecycle is IPlayerSessionClosing closing) closing.Closing += OnClosing;
        if (this.primaryCharacterStore is not null && lifecycle is IPlayerSessionEnteredWorld entering) entering.EnteredWorld += OnEnteredWorld;
        accounts.BindingClosing += OnBindingClosing;
        accounts.BindingEstablished += OnBindingEstablished;
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
            if (lifecycle is IPlayerSessionClosing closing) closing.Closing -= OnClosing;
            if (lifecycle is IPlayerSessionEnteredWorld entering) entering.EnteredWorld -= OnEnteredWorld;
            accounts.BindingClosing -= OnBindingClosing;
            accounts.BindingEstablished -= OnBindingEstablished;
            joinPayloads.Clear();
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
            Task[] pending;
            lock (sync) pending = finalWrites.Values.ToArray();
            Task.WhenAll(pending).GetAwaiter().GetResult();
            stopping.Dispose();
            reconciliation.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // Periodic reconciliation is a fallback for lifecycle implementations
        // without an authentication event; a successful login wakes it immediately.
        using var timer = timeProvider.CreateTimer(_ => RequestReconciliation(), null, PollInterval, PollInterval);
        while (!cancellationToken.IsCancellationRequested)
        {
            await reconciliation.WaitAsync(cancellationToken).ConfigureAwait(false);
            PlayerSnapshot[] snapshot = [.. players.SnapshotOnlinePlayers()];
            List<SessionState>? checkpoints = null;
            foreach (PlayerSnapshot player in snapshot)
            {
                if (player.SessionId is not PlayerSessionId sessionId ||
                    player.Connection is not ConnectionMetadataSnapshot connection ||
                    connection.LoginPhase < ConnectionLoginPhase.WorldRequested || player.WorldId != worldId)
                {
                    continue;
                }

                string? accountId = connection.TrustedTransferAccountId;
                PlayerAccountBinding? binding = null;
                if (string.IsNullOrWhiteSpace(accountId))
                {
                    if (primaryCharacterStore is null || connection.LoginPhase != ConnectionLoginPhase.FullyConnected ||
                        !accounts.TryGetBinding(player, out PlayerAccountBinding authenticated))
                    {
                        // Logout ends this authenticated tenure even if the socket remains alive.
                        lock (sync)
                        {
                            if (sessions.TryGetValue(sessionId, out var previous) && previous.Binding.HasValue)
                            {
                                previous.Closing = true;
                                sessions.Remove(sessionId);
                            }
                        }
                        continue;
                    }
                    binding = authenticated;
                    accountId = authenticated.AccountId.Value.ToString(CultureInfo.InvariantCulture);
                }

                SessionState session;
                bool initialize = false;
                lock (sync)
                {
                    if (sessions.TryGetValue(sessionId, out var previous) && previous.Binding != binding)
                    {
                        // A transferred connection remains a transfer session until native close,
                        // even if its metadata disappears or the player explicitly relogs.
                        if (previous.Binding is null) continue;
                        previous.Closing = true;
                        sessions.Remove(sessionId);
                    }
                    if (!sessions.TryGetValue(sessionId, out session!))
                    {
                        // Do not stall restoration of other accounts behind this
                        // account's final checkpoint. The next wake retries it.
                        if (finalWrites.TryGetValue(accountId, out var pendingWrite) && !pendingWrite.IsCompleted)
                            continue;
                        session = new SessionState(
                            sessionId,
                            player,
                            accountId,
                            timeProvider.GetUtcNow(), binding);
                        sessions.Add(sessionId, session);
                        initialize = true;
                    }
                }

                try
                {
                    if (initialize)
                    {
                        using var restoreCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.RestoreCancellation.Token);
                        await InitializeAsync(session, restoreCancellation.Token).ConfigureAwait(false);
                    }
                    else if (session.Initialized && !session.Failed && IsCurrent(session) && CharacterMode(session) != SandboxSscMode.Disabled &&
                             timeProvider.GetUtcNow() - session.LastCheckpoint >= CheckpointInterval)
                        (checkpoints ??= []).Add(session);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) when (session.Closing) { }
                catch (Exception ex)
                {
                    session.Failed = true;
                    logger.Log(OperationsLogLevel.Error, "sandboxes", "Character persistence failed; existing persisted state was preserved.", ex,
                        correlationId: session.SessionId.ToString());
                }
            }
            if (checkpoints is not null)
            {
                await Parallel.ForEachAsync(checkpoints,
                    new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
                    async (session, token) => await CheckpointAsync(session, token).ConfigureAwait(false)).ConfigureAwait(false);
            }
        }
    }

    private void RequestReconciliation()
    {
        lock (sync)
        {
            if (!disposed && reconciliation.CurrentCount == 0) reconciliation.Release();
        }
    }

    private void OnBindingEstablished(PlayerAccountBinding binding)
    {
        // No persistence or native dispatch from an account callback; the
        // worker always resolves the exact current player/binding again.
        if (primaryCharacterStore is not null) RequestReconciliation();
    }

    private async Task InitializeAsync(SessionState session, CancellationToken cancellationToken)
    {
        // A previous login's final write must finish before this account reads
        // its state. An unrelated account's pending write is not a dependency.
        Task? previousWrite;
        lock (sync) finalWrites.TryGetValue(session.AccountId, out previousWrite);
        if (previousWrite is not null) await previousWrite.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!IsCurrent(session)) return;
        if (session.Binding.HasValue)
        {
            await InitializePrimaryAsync(session, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (int.TryParse(session.AccountId, out int numericAccountId) && numericAccountId > 0)
        {
            PlayerAccountBinding? authenticated = await accounts.EstablishTrustedTransferBindingAsync(
                session.Player,
                session.AccountId,
                cancellationToken).ConfigureAwait(false);
            if (!IsCurrent(session)) return;
            if (authenticated is not { } binding || binding.AccountId.Value != numericAccountId ||
                binding.SessionId != session.SessionId || binding.PlayerId != session.PlayerId)
            {
                await FailAsync(
                    session,
                    "sandbox.transfer.account_restore_failed",
                    "Transferred account identity could not be restored.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            session.SetTransferBinding(binding);
        }

        if (!IsCurrent(session)) return;

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

            SandboxSscCharacterApplyResult applied = session.AccountBinding is { } accountBinding
                ? await characters.ApplyAuthenticatedAsync(session.Player, accounts, accountBinding, state.Payload, cancellationToken).ConfigureAwait(false)
                : await characters.ApplyAsync(session.Player, state.Payload, cancellationToken).ConfigureAwait(false);
            if (!IsCurrent(session)) return;
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

        if (!IsCurrent(session)) return;
        session.Initialized = true;
        session.LastCheckpoint = timeProvider.GetUtcNow();
        logger.Log(
            OperationsLogLevel.Information,
            "sandboxes",
            $"Accepted transferred player slot {session.PlayerId.Slot} into world '{worldId}' with SSC mode {mode}.",
            correlationId: session.SessionId.ToString());
    }

    private async Task InitializePrimaryAsync(SessionState session, CancellationToken cancellationToken)
    {
        if (!IsCurrent(session) || session.Binding is not { } binding) return;
        ISandboxSscStore persistence = CharacterStore(session);
        SandboxSscStateSnapshot? state = persistence.Read(session.AccountId, worldId, SandboxSscMode.Inherit);
        if (state is null)
        {
            var initial = await characters.CreateInitialAsync(session.Player, accounts, binding, cancellationToken).ConfigureAwait(false);
            if (!IsCurrent(session)) return;
            if (!initial.Succeeded || initial.Payload is null)
            {
                await FailAsync(session, initial.ErrorCode ?? "ssc.initial_failed", "Unable to provision character.", cancellationToken).ConfigureAwait(false);
                return;
            }
            var saved = persistence.Save(session.AccountId, worldId, SandboxSscMode.Inherit, initial.Payload, 0, timeProvider.GetUtcNow());
            state = saved.IsSuccess ? saved.State : saved.Status == SandboxSscWriteStatus.Conflict
                ? persistence.Read(session.AccountId, worldId, SandboxSscMode.Inherit) : null;
            if (state is null)
            {
                await FailAsync(session, saved.ErrorCode ?? "ssc.initial_save_failed", "Unable to provision character.", cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        if (!IsCurrent(session)) return;
        var applied = await characters.ApplyAuthenticatedAsync(session.Player, accounts, binding, state.Payload, cancellationToken).ConfigureAwait(false);
        if (!IsCurrent(session)) return;
        if (!applied.Succeeded)
        {
            await FailAsync(session, applied.ErrorCode ?? "ssc.apply_failed", "Unable to restore character.", cancellationToken).ConfigureAwait(false);
            return;
        }
        session.Revision = state.Revision;
        session.LastCheckpoint = timeProvider.GetUtcNow();
        session.Initialized = true;
        logger.Log(OperationsLogLevel.Information, "sandboxes", "Authenticated primary character restored.", correlationId: session.SessionId.ToString());
    }

    internal Func<bool>? AuthorizeCommand(CommandInvocation invocation, params string[] permissions)
    {
        if (invocation.WorldId != worldId || !permissions.All(invocation.Principal.HasPermission)) return null;
        if (invocation.Principal.IsServer) return () => permissions.All(invocation.Principal.HasPermission);
        if (invocation.Principal is not IPlayerCommandPrincipal { SessionId: { } sessionId } principal ||
            !players.TryGet(principal.PlayerId, out var actor) || actor?.SessionId != sessionId || actor.WorldId != worldId ||
            !accounts.TryGetBinding(actor, out var binding)) return null;
        return () => permissions.All(invocation.Principal.HasPermission) &&
            players.TryGet(principal.PlayerId, out var current) && current?.SessionId == sessionId && current.WorldId == worldId &&
            accounts.TryGetBinding(current, out var currentBinding) && currentBinding == binding;
    }

    internal async ValueTask<(int Saved, int Failed)> SaveAllAsync(Func<bool> authorized, CancellationToken cancellationToken)
    {
        SessionState[] snapshot;
        lock (sync) snapshot = sessions.Values.Where(session => session.AccountBinding.HasValue && session.Initialized && !session.Failed && !session.Closing &&
            CharacterMode(session) != SandboxSscMode.Disabled).ToArray();
        int saved = 0, failed = 0;
        foreach (SessionState session in snapshot)
        {
            if (!authorized()) { failed++; continue; }
            if ((await SaveCurrentAsync(session, authorized, cancellationToken).ConfigureAwait(false)).Succeeded) saved++;
            else failed++;
        }
        return (saved, failed);
    }

    internal ValueTask<SscManagementResult> SaveCurrentAsync(PlayerSnapshot target, Func<bool> authorized, CancellationToken cancellationToken)
    {
        var session = ManagementSession(target);
        return session is null ? ValueTask.FromResult(SscManagementResult.Failure("ssc.authenticated_session_not_ready")) :
            SaveCurrentAsync(session, authorized, cancellationToken);
    }

    /// <summary>Persist native inventory after an actor transaction before its external ledger may finalize.</summary>
    public async ValueTask<bool> CheckpointActorTransactionAsync(PlayerSnapshot player, PlayerAccountBinding expectedBinding,
        CancellationToken cancellationToken = default)
    {
        var session = ManagementSession(player);
        if (session is null || session.AccountBinding != expectedBinding) return false;
        bool StillBound() => accounts.TryGetBinding(player, out var binding) && binding == expectedBinding;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { return (await SaveCurrentAsync(session, StillBound, timeout.Token, waitForGate: true).ConfigureAwait(false)).Succeeded; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    internal async ValueTask<SscManagementResult> UploadJoinAsync(PlayerSnapshot target, Func<bool> authorized, CancellationToken cancellationToken)
    {
        var session = ManagementSession(target);
        if (session?.Binding is not { } binding) return SscManagementResult.Failure("ssc.primary_session_not_ready");
        string? joinPayload;
        lock (sync) joinPayloads.TryGetValue(session.SessionId, out joinPayload);
        if (joinPayload is null) return SscManagementResult.Failure("ssc.join_snapshot_unavailable");
        if (!authorized()) return SscManagementResult.Failure("ssc.command_identity_changed");
        if (!session.OperationGate.Wait(0)) return SscManagementResult.Failure("ssc.operation_busy");
        bool persisted = false;
        session.Importing = true;
        try
        {
            if (!Ready(session)) return SscManagementResult.Failure("ssc.authenticated_session_not_ready");
            var saved = await PersistPayloadAsync(session, joinPayload, authorized, cancellationToken).ConfigureAwait(false);
            if (!saved.Succeeded) return saved;
            persisted = true;
            // Persist first with CAS. If native apply cannot finish (including disconnect),
            // preserve the imported revision for next login rather than saving stale live data.
            if (!Ready(session))
            {
                session.Failed = true;
                return SscManagementResult.Failure("ssc.import_saved_apply_failed");
            }
            var applied = await characters.ApplyAuthenticatedAsync(session.Player, accounts, binding, joinPayload, cancellationToken).ConfigureAwait(false);
            if (!applied.Succeeded || !IsCurrent(session))
            {
                session.Failed = true;
                return SscManagementResult.Failure("ssc.import_saved_apply_failed");
            }
            return SscManagementResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (persisted) session.Failed = true;
            throw;
        }
        catch (Exception ex)
        {
            if (persisted) session.Failed = true;
            logger.Log(OperationsLogLevel.Error, "sandboxes", "Explicit SSC join import failed.", ex, correlationId: session.SessionId.ToString());
            return SscManagementResult.Failure(persisted ? "ssc.import_saved_apply_failed" : "ssc.import_failed");
        }
        finally { session.Importing = false; session.OperationGate.Release(); }
    }

    private SessionState? ManagementSession(PlayerSnapshot target)
    {
        if (target.WorldId != worldId || target.SessionId is not { } sessionId) return null;
        SessionState? session;
        lock (sync) sessions.TryGetValue(sessionId, out session);
        return session is not null && session.PlayerId == target.Id && session.AccountBinding.HasValue && Ready(session) ? session : null;
    }

    private bool Ready(SessionState session) => !Volatile.Read(ref disposed) && session.Initialized && !session.Failed &&
        CharacterMode(session) != SandboxSscMode.Disabled && IsCurrent(session);

    /// <summary>Checks the exact live authentication tenure, optionally also requiring its character restore.</summary>
    public bool IsGameplayAllowed(PlayerSnapshot player, bool requireCharacterReady)
    {
        return requireCharacterReady ? IsCharacterReady(player) :
            TryResolveConnectedPlayer(player, out var live) && accounts.TryGetBinding(live, out _);
    }

    public bool IsCharacterReady(PlayerSnapshot player) => GetCharacterReadiness(player) == SscCharacterReadiness.Ready;

    /// <summary>Reports readiness only for the exact currently connected authentication tenure.</summary>
    public SscCharacterReadiness GetCharacterReadiness(PlayerSnapshot player)
    {
        if (!TryResolveConnectedPlayer(player, out var live)) return SscCharacterReadiness.Unavailable;
        PlayerSessionId sessionId = live.SessionId!.Value;
        string? trustedIdentity = live.Connection!.TrustedTransferAccountId;

        SessionState? session;
        lock (sync) sessions.TryGetValue(sessionId, out session);

        if (!accounts.TryGetBinding(live, out var binding))
        {
            // Legacy guest transfers are server-authenticated admissions, not
            // ordinary unauthenticated primary connections. Keep them bound to
            // the exact transfer/session identity and never grant numeric accounts
            // readiness without their corresponding authentication tenure.
            if (worldId == WorldId.Primary || string.IsNullOrWhiteSpace(trustedIdentity) ||
                (int.TryParse(trustedIdentity, out int numericId) && numericId > 0))
                return SscCharacterReadiness.Unauthenticated;
            if (mode == SandboxSscMode.Disabled) return SscCharacterReadiness.NotManaged;
            return session is not null && session.PlayerId == live.Id && session.AccountBinding is null &&
                string.Equals(session.AccountId, trustedIdentity, StringComparison.Ordinal)
                ? ReadinessOf(session) : SscCharacterReadiness.Restoring;
        }
        if (session is not null && session.PlayerId == live.Id && session.AccountBinding == binding)
            return ReadinessOf(session);
        bool managed = !string.IsNullOrWhiteSpace(trustedIdentity)
            ? mode != SandboxSscMode.Disabled : primaryCharacterStore is not null;
        return managed ? SscCharacterReadiness.Restoring : SscCharacterReadiness.NotManaged;
    }

    private SscCharacterReadiness ReadinessOf(SessionState session)
    {
        if (CharacterMode(session) == SandboxSscMode.Disabled) return SscCharacterReadiness.NotManaged;
        if (session.Closing) return SscCharacterReadiness.Restoring;
        if (session.Failed) return SscCharacterReadiness.Failed;
        return !session.Importing && Ready(session) ? SscCharacterReadiness.Ready : SscCharacterReadiness.Restoring;
    }

    private bool TryResolveConnectedPlayer(PlayerSnapshot player, out PlayerSnapshot live)
    {
        live = null!;
        if (Volatile.Read(ref disposed) || player.WorldId != worldId || player.SessionId is not { } sessionId ||
            player.Connection?.LoginPhase != ConnectionLoginPhase.FullyConnected ||
            !players.TryGet(player.Id, out var current) || current?.SessionId != sessionId || current.WorldId != worldId ||
            current.Connection?.LoginPhase != ConnectionLoginPhase.FullyConnected)
            return false;
        live = current;
        return true;
    }

    private async Task CheckpointAsync(SessionState session, CancellationToken cancellationToken)
    {
        var result = await SaveCurrentAsync(session, null, cancellationToken, checkpoint: true).ConfigureAwait(false);
        if (!result.Succeeded && result.ErrorCode is not ("ssc.operation_busy" or "ssc.authenticated_session_not_ready" or "sandbox.ssc.revision_conflict"))
            logger.Log(OperationsLogLevel.Warning, "sandboxes", $"SSC checkpoint failed for slot {session.PlayerId.Slot}: {result.ErrorCode}.",
                correlationId: session.SessionId.ToString());
    }

    private async ValueTask<SscManagementResult> SaveCurrentAsync(SessionState session, Func<bool>? authorized, CancellationToken cancellationToken,
        bool checkpoint = false, bool waitForGate = false)
    {
        if (!Ready(session)) return SscManagementResult.Failure("ssc.authenticated_session_not_ready");
        if (waitForGate) await session.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        else if (!session.OperationGate.Wait(0)) return SscManagementResult.Failure("ssc.operation_busy");
        try
        {
            if (!Ready(session) || authorized?.Invoke() == false) return SscManagementResult.Failure("ssc.command_identity_changed");
            var captured = await CaptureAsync(session, cancellationToken).ConfigureAwait(false);
            if (!captured.Succeeded || captured.Payload is null) return SscManagementResult.Failure(captured.ErrorCode ?? "ssc.capture_failed");
            return await PersistPayloadAsync(session, captured.Payload, authorized, cancellationToken, checkpoint).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.Log(OperationsLogLevel.Error, "sandboxes", "SSC character save failed.", ex, correlationId: session.SessionId.ToString());
            return SscManagementResult.Failure("ssc.save_failed");
        }
        finally { session.OperationGate.Release(); }
    }

    private async ValueTask<SscManagementResult> PersistPayloadAsync(SessionState session, string payload, Func<bool>? authorized, CancellationToken cancellationToken,
        bool checkpoint = false)
    {
        // Manual commands can begin on the game thread. Keep the synchronous
        // cross-process file lock and durable write on a worker.
        SandboxSscWriteResult? saved = await Task.Run(() =>
        {
            lock (session.WriteSync)
            {
                if (!Ready(session) || authorized?.Invoke() == false) return null;
                var store = CharacterStore(session);
                var result = checkpoint
                    ? store.SaveCheckpoint(session.AccountId, worldId, CharacterMode(session), payload, session.Revision, timeProvider.GetUtcNow())
                    : store.Save(session.AccountId, worldId, CharacterMode(session), payload, session.Revision, timeProvider.GetUtcNow());
                if (result.IsSuccess && result.State is not null)
                {
                    session.Revision = result.State.Revision;
                    session.LastCheckpoint = timeProvider.GetUtcNow();
                }
                return result;
            }
        }, cancellationToken).ConfigureAwait(false);
        if (saved is null) return SscManagementResult.Failure("ssc.command_identity_changed");
        if (saved.Status == SandboxSscWriteStatus.Conflict)
            await FailAsync(session, "sandbox.ssc.revision_conflict", "Sandbox character state changed concurrently; reconnect to avoid overwriting it.", cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess && saved.State is not null ? SscManagementResult.Success : SscManagementResult.Failure(saved.ErrorCode ?? "ssc.save_failed");
    }

    private void OnEnteredWorld(PlayerSnapshot player)
    {
        if (primaryCharacterStore is null || player.WorldId != worldId || player.SessionId is not { } sessionId ||
            player.Connection is not { LoginPhase: ConnectionLoginPhase.FullyConnected } connection || !string.IsNullOrWhiteSpace(connection.TrustedTransferAccountId)) return;
        // Client join-state is never used for initial provisioning: retain it only
        // for an explicitly authorized /uploadssc confirmation on this connection.
        lock (sync) if (disposed || joinPayloads.ContainsKey(sessionId)) return;
        try
        {
            using var captureCancellation = new CancellationTokenSource();
            var capture = characters.CaptureJoinAsync(player, captureCancellation.Token);
            if (capture.IsCompletedSuccessfully && capture.Result is { Succeeded: true, Payload: { Length: <= 131072 } payload })
            {
                lock (sync) if (!disposed) joinPayloads.TryAdd(sessionId, payload);
            }
            else captureCancellation.Cancel();
        }
        catch (Exception ex)
        {
            logger.Log(OperationsLogLevel.Warning, "sandboxes", "SSC native join snapshot unavailable.", ex, correlationId: sessionId.ToString());
        }
    }

    private bool IsCurrent(SessionState session) => !session.Closing &&
        players.TryGet(session.PlayerId, out PlayerSnapshot? player) && player?.SessionId == session.SessionId && player.WorldId == worldId &&
        (session.AccountBinding is not { } binding || accounts.TryGetBinding(player, out var current) && current == binding);

    private ISandboxSscStore CharacterStore(SessionState session) => session.Binding.HasValue ? primaryCharacterStore! : store;
    private SandboxSscMode CharacterMode(SessionState session) => session.Binding.HasValue ? SandboxSscMode.Inherit : mode;
    private ValueTask<SandboxSscCharacterCaptureResult> CaptureAsync(SessionState session, CancellationToken token = default) =>
        session.AccountBinding is { } binding
            ? characters.CaptureAuthenticatedAsync(session.Player, accounts, binding, token)
            : characters.CaptureAsync(session.Player, token);

    private void OnClosing(PlayerSessionClosed closed) => CaptureBeforeClosing(closed.SessionId, null);

    private void OnBindingClosing(PlayerAccountBinding binding)
    {
        CaptureBeforeClosing(binding.SessionId, binding);
        RequestReconciliation();
    }

    private void CaptureBeforeClosing(PlayerSessionId sessionId, PlayerAccountBinding? binding)
    {
        SessionState? session;
        lock (sync)
        {
            if (disposed || !sessions.TryGetValue(sessionId, out session) || session.Closing ||
                binding.HasValue && session.AccountBinding != binding) return;
            session.Closing = true;
        }
        session.RestoreCancellation.Cancel();
        if (!session.Initialized || session.Failed || session.Importing || CharacterMode(session) == SandboxSscMode.Disabled) return;
        // Only the synchronous pre-reset capture may read the retiring character. Never
        // queue a slot-based capture after it could have been assigned to another player.
        using var captureCancellation = new CancellationTokenSource();
        var capture = CaptureAsync(session, captureCancellation.Token);
        if (capture.IsCompletedSuccessfully && capture.Result is { Succeeded: true, Payload: { } payload })
        {
            lock (sync)
            {
                if (disposed) return;
                foreach (string account in finalWrites.Where(static entry => entry.Value.IsCompleted).Select(static entry => entry.Key).ToArray())
                    finalWrites.Remove(account);
                finalWrites.TryGetValue(session.AccountId, out var previous);
                finalWrites[session.AccountId] = Task.Run(async () =>
                {
                    try
                    {
                        if (previous is not null) await previous.ConfigureAwait(false);
                        PersistClosed(session, payload);
                    }
                    finally { RequestReconciliation(); }
                });
            }
        }
        else captureCancellation.Cancel();
    }

    private void OnClosed(PlayerSessionClosed closed)
    {
        SessionState? session;
        lock (sync)
        {
            joinPayloads.Remove(closed.SessionId);
            if (!sessions.Remove(closed.SessionId, out session) || session is null)
            {
                return;
            }
        }

        session.Closing = true;
        session.RestoreCancellation.Cancel();
    }

    private void PersistClosed(SessionState session, string payload)
    {
        try
        {
            SandboxSscWriteResult saved;
            lock (session.WriteSync)
            {
                if (session.Failed) return;
                saved = CharacterStore(session).Save(session.AccountId, worldId, CharacterMode(session), payload, session.Revision, timeProvider.GetUtcNow());
            }
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
        // Primary persistence is a foundation, not full character authority. Never
        // kick a newly authenticated tenure for a stale persistence failure.
        if (!session.Binding.HasValue && IsCurrent(session) &&
            (session.AccountBinding.HasValue || !accounts.TryGetBinding(session.Player, out _)))
            await playerOperations.KickAsync(session.Player, message, cancellationToken).ConfigureAwait(false);
    }

    private sealed class SessionState(
        PlayerSessionId sessionId,
        PlayerSnapshot player,
        string accountId,
        DateTimeOffset now,
        PlayerAccountBinding? binding)
    {
        public PlayerSessionId SessionId { get; } = sessionId;
        public PlayerSnapshot Player { get; } = player;
        public PlayerId PlayerId => Player.Id;
        public string AccountId { get; } = accountId;
        public PlayerAccountBinding? Binding { get; } = binding;
        private TransferBinding? transferBinding;
        public PlayerAccountBinding? AccountBinding => Binding ?? Volatile.Read(ref transferBinding)?.Value;
        public void SetTransferBinding(PlayerAccountBinding value) => Volatile.Write(ref transferBinding, new(value));
        public long Revision { get; set; }
        public DateTimeOffset LastCheckpoint { get; set; } = now;
        public volatile bool Initialized;
        public volatile bool Failed;
        public volatile bool Closing;
        public volatile bool Importing;
        public CancellationTokenSource RestoreCancellation { get; } = new();
        public object WriteSync { get; } = new();
        public SemaphoreSlim OperationGate { get; } = new(1, 1);
        private sealed record TransferBinding(PlayerAccountBinding Value);
    }
}

internal readonly record struct SscManagementResult(bool Succeeded, string? ErrorCode = null)
{
    public static SscManagementResult Success => new(true);
    public static SscManagementResult Failure(string errorCode) => new(false, errorCode);
}
