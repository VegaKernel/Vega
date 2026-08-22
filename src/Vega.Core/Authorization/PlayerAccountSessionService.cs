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
    private readonly Dictionary<PlayerId, AccountSnapshot> authenticated = [];
    private bool disposed;

    public PlayerAccountSessionService(
        IAccountService accounts,
        IAuthorizationService authorization,
        PlayerSessionAuthorizationService playerAuthorization,
        IPlayerCultureService cultures,
        IPlayerSessionLifecycle lifecycle,
        IBanService? bans = null)
    {
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        this.playerAuthorization = playerAuthorization ?? throw new ArgumentNullException(nameof(playerAuthorization));
        this.cultures = cultures ?? throw new ArgumentNullException(nameof(cultures));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.bans = bans;
        lifecycle.Closed += OnClosed;
    }

    public bool TryGetAccount(PlayerId playerId, out AccountSnapshot? account)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return authenticated.TryGetValue(playerId, out account);
        }
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
            if (!disposed && authenticated.ContainsKey(playerId))
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
        CancellationToken cancellationToken = default)
    {
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

        return await EstablishAsync(playerId, created.After, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PlayerAccountSessionResult> LoginAsync(
        PlayerId playerId,
        string accountName,
        string password,
        CancellationToken cancellationToken = default)
    {
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
            : await EstablishAsync(playerId, account, cancellationToken).ConfigureAwait(false);
    }

    public PlayerAccountSessionResult Logout(PlayerId playerId)
    {
        AccountSnapshot? account;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!authenticated.Remove(playerId, out account))
            {
                return PlayerAccountSessionResult.Failure("account.not-authenticated");
            }
        }

        playerAuthorization.Replace(playerId, []);
        cultures.ResetSession(CultureSubject(playerId));
        return PlayerAccountSessionResult.Success(account);
    }

    public async ValueTask<PlayerAccountSessionResult> ChangePasswordAsync(
        PlayerId playerId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
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
            if (!disposed && authenticated.ContainsKey(playerId))
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
            players = authenticated.Keys.ToArray();
            authenticated.Clear();
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
        CancellationToken cancellationToken)
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
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (authenticated.Any(pair =>
                    pair.Key != playerId &&
                    pair.Value.Name.Equals(current.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return PlayerAccountSessionResult.Failure("account.in-use");
            }

            authenticated[playerId] = current;
        }

        playerAuthorization.ReplaceAccountGroup(playerId, current.GroupName, authorization);
        cultures.ResetSession(CultureSubject(playerId));
        if (current.Culture is not null)
        {
            cultures.TrySetSession(CultureSubject(playerId), current.Culture, out _);
        }

        return PlayerAccountSessionResult.Success(current);
    }

    private void OnClosed(PlayerSessionClosed closed)
    {
        lock (sync)
        {
            if (!disposed)
            {
                authenticated.Remove(closed.PlayerId);
            }
        }

        playerAuthorization.Replace(closed.PlayerId, []);
        cultures.RemoveSession(CultureSubject(closed.PlayerId));
    }

    private static string CultureSubject(PlayerId playerId) => $"player:{playerId.Slot}";

    private static bool IsValidPassword(string password) =>
        !string.IsNullOrWhiteSpace(password) && password.Length is >= 8 and <= 1024;

    private static bool IsValidAccountName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 255;
}

public sealed record PlayerAccountSessionResult(bool Succeeded, string Code, AccountSnapshot? Account = null)
{
    public static PlayerAccountSessionResult Success(AccountSnapshot account) =>
        new(true, "account.authenticated", account);

    public static PlayerAccountSessionResult Failure(string code) => new(false, code);
}
