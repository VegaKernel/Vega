using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Bans;
using Vega.Contracts.Localization;
using Vega.Contracts.Players;

namespace Vega.Core.Authorization;

public sealed class PlayerAccountSessionService : IPlayerAccountSessionService, IDisposable
{
    private readonly object sync = new();
    private readonly IAccountService accounts;
    private readonly IAuthorizationService authorization;
    private readonly PlayerSessionAuthorizationService playerAuthorization;
    private readonly IPlayerCultureService cultures;
    private readonly IPlayerSessionLifecycle lifecycle;
    private readonly IBanService? bans;
    private readonly IRoleLeaseService? roleLeases;
    private readonly Dictionary<PlayerId, AccountSnapshot> authenticated = [];
    private readonly Dictionary<PlayerId, Guid> authenticationIds = [];
    private readonly Dictionary<PlayerId, SessionStamp> sessionStamps = [];
    private bool disposed;

    public event Action<PlayerAccountBinding>? BindingClosing;
    public event Action<PlayerAccountBinding>? BindingEstablished;
    public event Action<PlayerAccountBinding>? BindingEnded;

    public PlayerAccountSessionService(
        IAccountService accounts,
        IAuthorizationService authorization,
        PlayerSessionAuthorizationService playerAuthorization,
        IPlayerCultureService cultures,
        IPlayerSessionLifecycle lifecycle,
        IBanService? bans = null,
        IRoleLeaseService? roleLeases = null)
    {
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        this.playerAuthorization = playerAuthorization ?? throw new ArgumentNullException(nameof(playerAuthorization));
        this.cultures = cultures ?? throw new ArgumentNullException(nameof(cultures));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.bans = bans;
        this.roleLeases = roleLeases;
        lifecycle.Closed += OnClosed;
        lifecycle.Opened += OnOpened;
    }

    public bool TryGetAccount(PlayerId playerId, out AccountSnapshot? account)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return authenticated.TryGetValue(playerId, out account);
        }
    }

    public bool TryGetBinding(PlayerSnapshot player, out PlayerAccountBinding binding)
    {
        lock (sync)
        {
            binding = default;
            if (disposed || player.SessionId is not PlayerSessionId sessionId ||
                sessionStamps.GetValueOrDefault(player.Id)?.Id != sessionId ||
                !authenticated.TryGetValue(player.Id, out var account) ||
                !authenticationIds.TryGetValue(player.Id, out var authenticationId)) return false;
            binding = new(player.Id, sessionId, account.Id, authenticationId);
            return true;
        }
    }

    public bool TryRunWithBinding<T>(PlayerAccountBinding binding, Func<T> operation, out T result)
    {
        lock (sync)
        {
            result = default!;
            if (disposed || sessionStamps.GetValueOrDefault(binding.PlayerId)?.Id != binding.SessionId ||
                !authenticated.TryGetValue(binding.PlayerId, out var account) || account.Id != binding.AccountId ||
                authenticationIds.GetValueOrDefault(binding.PlayerId) != binding.AuthenticationId) return false;
            result = operation();
            return true;
        }
    }

    public async ValueTask<bool> EstablishTrustedTransferAsync(
        PlayerId playerId,
        string accountId,
        CancellationToken cancellationToken = default) =>
        (await EstablishTrustedTransferCoreAsync(playerId, null, accountId, cancellationToken).ConfigureAwait(false)).Succeeded;

    public async ValueTask<bool> EstablishTrustedTransferAsync(PlayerSnapshot player, string accountId, CancellationToken cancellationToken = default) =>
        (await EstablishTrustedTransferCoreAsync(player.Id, player.SessionId, accountId, cancellationToken).ConfigureAwait(false)).Succeeded;

    public async ValueTask<PlayerAccountBinding?> EstablishTrustedTransferBindingAsync(PlayerSnapshot player, string accountId, CancellationToken cancellationToken = default)
    {
        if (player.SessionId is not { } sessionId) return null;
        return (await EstablishTrustedTransferCoreAsync(player.Id, sessionId, accountId, cancellationToken).ConfigureAwait(false)).Binding;
    }

    private async ValueTask<PlayerAccountSessionResult> EstablishTrustedTransferCoreAsync(PlayerId playerId, PlayerSessionId? expected, string accountId, CancellationToken cancellationToken)
    {
        if (!int.TryParse(accountId, out int numericId) || numericId <= 0)
        {
            return PlayerAccountSessionResult.Failure("account.invalid-transfer-identity");
        }

        SessionStamp stamp;
        long authenticationEpoch;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!sessionStamps.TryGetValue(playerId, out var active) || active.Id is not { } sessionId ||
                expected is { } expectedId && expectedId != sessionId)
                return PlayerAccountSessionResult.Failure("account.session-changed");
            stamp = active;
            if (stamp.TrustedTransferConsumed) return PlayerAccountSessionResult.Failure("account.transfer-consumed");
            stamp.TrustedTransferConsumed = true;
            authenticationEpoch = stamp.AuthenticationEpoch;

            if (authenticated.TryGetValue(playerId, out var current))
            {
                return current.Id.Value == numericId && authenticationIds.TryGetValue(playerId, out Guid authenticationId)
                    ? PlayerAccountSessionResult.Success(current) with { Binding = new(playerId, sessionId, current.Id, authenticationId) }
                    : PlayerAccountSessionResult.Failure("account.already-authenticated");
            }
        }

        AccountSnapshot? account = accounts.SnapshotAccounts()
            .FirstOrDefault(candidate => candidate.Id.Value == numericId);
        if (account is null)
        {
            return PlayerAccountSessionResult.Failure("account.not-found");
        }

        return await EstablishAsync(
            playerId,
            account,
            stamp,
            cancellationToken,
            expectedAuthenticationEpoch: authenticationEpoch).ConfigureAwait(false);
    }

    public async ValueTask<bool> SetCultureAsync(
        PlayerId playerId,
        string? culture,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetAccount(playerId, out AccountSnapshot? account) || account is null)
        {
            return false;
        }

        AccountMutationResult changed = await accounts.SetCultureAsync(
            account.Name,
            culture,
            account.Revision,
            account.Name,
            cancellationToken).ConfigureAwait(false);
        if (!changed.Succeeded || changed.After is null)
        {
            return false;
        }

        lock (sync)
        {
            if (!disposed && ReferenceEquals(authenticated.GetValueOrDefault(playerId), account))
            {
                authenticated[playerId] = changed.After;
            }
        }

        return true;
    }

    public async ValueTask<PlayerAccountSessionResult> RegisterAsync(
        PlayerId playerId,
        string accountName,
        string password,
        CancellationToken cancellationToken = default,
        PlayerSessionId? expectedSession = null)
    {
        if (!TryCaptureSession(playerId, expectedSession, out var stamp)) return PlayerAccountSessionResult.Failure("account.session-changed");
        if (!IsValidAccountName(accountName))
        {
            return PlayerAccountSessionResult.Failure("account.invalid-name");
        }

        if (!IsValidPassword(password))
        {
            return PlayerAccountSessionResult.Failure("account.invalid-password");
        }

        if (TryGetAccount(playerId, out _))
        {
            return PlayerAccountSessionResult.Failure("account.already-authenticated");
        }

        AccountMutationResult created = await accounts.CreateAsync(
            accountName,
            password,
            "default",
            accountName,
            cancellationToken).ConfigureAwait(false);
        if (!created.Succeeded || created.After is null)
        {
            return PlayerAccountSessionResult.Failure(created.Code);
        }

        return await EstablishAsync(playerId, created.After, stamp, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PlayerAccountSessionResult> LoginAsync(
        PlayerId playerId,
        string accountName,
        string password,
        CancellationToken cancellationToken = default,
        PlayerSessionId? expectedSession = null)
    {
        if (!TryCaptureSession(playerId, expectedSession, out var stamp)) return PlayerAccountSessionResult.Failure("account.session-changed");
        if (!IsValidAccountName(accountName))
        {
            return PlayerAccountSessionResult.Failure("account.invalid-credentials");
        }

        if (TryGetAccount(playerId, out _))
        {
            return PlayerAccountSessionResult.Failure("account.already-authenticated");
        }

        bool verified;
        try
        {
            verified = await accounts.VerifyPasswordAsync(accountName, password, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            verified = false;
        }

        if (!verified)
        {
            return PlayerAccountSessionResult.Failure("account.invalid-credentials");
        }

        AccountSnapshot? account = await accounts.FindByNameAsync(accountName, cancellationToken)
            .ConfigureAwait(false);
        return account is null
            ? PlayerAccountSessionResult.Failure("account.invalid-credentials")
            : await EstablishAsync(playerId, account, stamp, cancellationToken).ConfigureAwait(false);
    }

    public PlayerAccountSessionResult Logout(PlayerId playerId, PlayerSessionId? expectedSession = null)
    {
        AccountSnapshot? account;
        PlayerAccountBinding? endedBinding = null;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (expectedSession is not null && sessionStamps.GetValueOrDefault(playerId)?.Id != expectedSession)
                return PlayerAccountSessionResult.Failure("account.session-changed");
            if (!authenticated.TryGetValue(playerId, out account))
            {
                return PlayerAccountSessionResult.Failure("account.not-authenticated");
            }
            if (sessionStamps.GetValueOrDefault(playerId) is { } stamp)
            {
                stamp.TrustedTransferConsumed = true;
                stamp.AuthenticationEpoch++;
            }
            if (sessionStamps.GetValueOrDefault(playerId)?.Id is { } sessionId &&
                authenticationIds.TryGetValue(playerId, out var authenticationId))
            {
                var binding = new PlayerAccountBinding(playerId, sessionId, account.Id, authenticationId);
                endedBinding = binding;
                NotifyBinding(BindingClosing, binding, "closing");
            }
            authenticated.Remove(playerId);
            authenticationIds.Remove(playerId);
            playerAuthorization.Replace(playerId, []);
            cultures.ResetSession(CultureSubject(playerId));
        }
        if (endedBinding is { } ended) NotifyBinding(BindingEnded, ended, "ended");
        return PlayerAccountSessionResult.Success(account);
    }

    public async ValueTask<PlayerAccountSessionResult> ChangePasswordAsync(
        PlayerId playerId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default,
        PlayerSessionId? expectedSession = null)
    {
        if (!TryCaptureSession(playerId, expectedSession, out _)) return PlayerAccountSessionResult.Failure("account.session-changed");
        if (!IsValidPassword(newPassword))
        {
            return PlayerAccountSessionResult.Failure("account.invalid-password");
        }

        if (!TryGetAccount(playerId, out AccountSnapshot? account) || account is null)
        {
            return PlayerAccountSessionResult.Failure("account.not-authenticated");
        }

        if (!await accounts.VerifyPasswordAsync(account.Name, currentPassword, cancellationToken)
                .ConfigureAwait(false))
        {
            return PlayerAccountSessionResult.Failure("account.invalid-credentials");
        }

        AccountMutationResult changed = await accounts.ChangePasswordAsync(
            account.Name,
            newPassword,
            account.Revision,
            account.Name,
            cancellationToken).ConfigureAwait(false);
        if (!changed.Succeeded || changed.After is null)
        {
            return PlayerAccountSessionResult.Failure(changed.Code);
        }

        lock (sync)
        {
            if (!disposed && ReferenceEquals(authenticated.GetValueOrDefault(playerId), account))
            {
                authenticated[playerId] = changed.After;
            }
        }

        return PlayerAccountSessionResult.Success(changed.After);
    }

    public void Dispose()
    {
        PlayerId[] players;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            lifecycle.Closed -= OnClosed;
            lifecycle.Opened -= OnOpened;
            players = authenticated.Keys.ToArray();
            authenticated.Clear();
            authenticationIds.Clear();
        }

        foreach (PlayerId playerId in players)
        {
            playerAuthorization.Replace(playerId, []);
            cultures.RemoveSession(CultureSubject(playerId));
        }
    }

    private async ValueTask<PlayerAccountSessionResult> EstablishAsync(
        PlayerId playerId,
        AccountSnapshot account,
        SessionStamp? stamp,
        CancellationToken cancellationToken,
        long? expectedAuthenticationEpoch = null)
    {
        if (bans is not null && await bans.FindActiveBanAsync(
                new BanIdentity(account.Name, account.Name, null, null),
                cancellationToken).ConfigureAwait(false) is not null)
        {
            return PlayerAccountSessionResult.Failure("account.banned");
        }

        AccountMutationResult seen = await accounts.RecordLoginAsync(
            account.Name,
            DateTimeOffset.UtcNow,
            account.Revision,
            account.Name,
            cancellationToken).ConfigureAwait(false);
        AccountSnapshot current = seen.Succeeded && seen.After is not null ? seen.After : account;
        PlayerAccountBinding? establishedBinding = null;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!ReferenceEquals(sessionStamps.GetValueOrDefault(playerId), stamp))
                return PlayerAccountSessionResult.Failure("account.session-changed");
            if (expectedAuthenticationEpoch is long expected && stamp?.AuthenticationEpoch != expected)
                return PlayerAccountSessionResult.Failure("account.authentication-changed");
            if (authenticated.ContainsKey(playerId)) return PlayerAccountSessionResult.Failure("account.already-authenticated");
            if (authenticated.Any(pair =>
                    pair.Key != playerId &&
                    pair.Value.Name.Equals(current.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return PlayerAccountSessionResult.Failure("account.in-use");
            }

            authenticated[playerId] = current;
            if (stamp is not null) stamp.AuthenticationEpoch++;
            Guid authenticationId = Guid.NewGuid();
            authenticationIds[playerId] = authenticationId;
            if (stamp?.Id is { } sessionId) establishedBinding = new(playerId, sessionId, current.Id, authenticationId);
            playerAuthorization.ReplaceAccountGroup(
                playerId,
                current.GroupName,
                authorization,
                current.Id,
                roleLeases);
            cultures.ResetSession(CultureSubject(playerId));
            if (current.Culture is not null)
                cultures.TrySetSession(CultureSubject(playerId), current.Culture, out _);
        }

        // Notifications run after releasing the account lock. A queued consumer
        // must revalidate the binding because logout may already have won the race.
        if (establishedBinding is { } binding) NotifyBinding(BindingEstablished, binding, "established");

        return PlayerAccountSessionResult.Success(current) with { Binding = establishedBinding };
    }

    private static void NotifyBinding(Action<PlayerAccountBinding>? handlers, PlayerAccountBinding binding, string phase)
    {
        if (handlers is null) return;
        foreach (Action<PlayerAccountBinding> handler in handlers.GetInvocationList())
        {
            try { handler(binding); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Account {0} callback failed: {1}", phase, ex.Message); }
        }
    }

    private void OnClosed(PlayerSessionClosed closed)
    {
        lock (sync)
        {
            if (!disposed)
            {
                if (sessionStamps.GetValueOrDefault(closed.PlayerId)?.Id is { } id && id != closed.SessionId) return;
                sessionStamps[closed.PlayerId] = new SessionStamp(null);
                authenticated.Remove(closed.PlayerId);
                authenticationIds.Remove(closed.PlayerId);
                playerAuthorization.Replace(closed.PlayerId, []);
                cultures.RemoveSession(CultureSubject(closed.PlayerId));
            }
        }
    }

    private void OnOpened(PlayerSessionOpened opened)
    {
        lock (sync)
        {
            if (disposed) return;
            sessionStamps[opened.PlayerId] = new SessionStamp(opened.SessionId);
            authenticated.Remove(opened.PlayerId);
            authenticationIds.Remove(opened.PlayerId);
            playerAuthorization.Replace(opened.PlayerId, []);
            cultures.RemoveSession(CultureSubject(opened.PlayerId));
        }
    }

    private bool TryCaptureSession(PlayerId playerId, PlayerSessionId? expected, out SessionStamp? stamp)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            stamp = sessionStamps.GetValueOrDefault(playerId);
            return expected is null || stamp?.Id == expected;
        }
    }

    private sealed class SessionStamp(PlayerSessionId? id)
    {
        public PlayerSessionId? Id { get; } = id;
        public bool TrustedTransferConsumed { get; set; }
        public long AuthenticationEpoch { get; set; }
    }

    private static string CultureSubject(PlayerId playerId) => $"player:{playerId.Slot}";

    private static bool IsValidPassword(string password) =>
        !string.IsNullOrWhiteSpace(password) && password.Length is >= 8 and <= 1024;

    private static bool IsValidAccountName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 255;
}

public sealed record PlayerAccountSessionResult(bool Succeeded, string Code, AccountSnapshot? Account = null)
{
    internal PlayerAccountBinding? Binding { get; init; }
    public static PlayerAccountSessionResult Success(AccountSnapshot account) =>
        new(true, "account.authenticated", account);

    public static PlayerAccountSessionResult Failure(string code) => new(false, code);
}
