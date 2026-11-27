using Vega.Contracts.Players;

namespace Vega.Contracts.Accounts;

public interface IPlayerAccountSessionService
{
    bool TryGetAccount(PlayerId playerId, out AccountSnapshot? account);

    /// <summary>Before explicit logout, while the old authentication tenure is still valid. Handlers must not wait for queued work.</summary>
    event Action<PlayerAccountBinding>? BindingClosing { add { } remove { } }

    /// <summary>After authentication is committed. Consumers must still verify the exact binding when handling this notification.</summary>
    event Action<PlayerAccountBinding>? BindingEstablished { add { } remove { } }

    /// <summary>After explicit logout removes authentication. Consumers must verify no newer binding exists before acting.</summary>
    event Action<PlayerAccountBinding>? BindingEnded { add { } remove { } }

    /// <summary>Observational end of an exact authentication tenure, including disconnect and replacement.</summary>
    event Action<PlayerAuthenticationEnded>? AuthenticationEnded { add { } remove { } }

    /// <summary>Identifies one authenticated tenure, distinct even after logout and relogin on the same connection.</summary>
    bool TryGetBinding(PlayerSnapshot player, out PlayerAccountBinding binding)
    {
        binding = default;
        return false;
    }

    /// <summary>Runs a short synchronous operation while account identity is still bound to the native session.</summary>
    bool TryRunWithBinding<T>(PlayerAccountBinding binding, Func<T> operation, out T result)
    {
        result = default!;
        return false;
    }

    ValueTask<bool> SetCultureAsync(
        PlayerId playerId,
        string? culture,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Establishes an account session from a server-authenticated transfer identity.
    /// Implementations must not treat arbitrary client input as trusted identity.
    /// </summary>
    ValueTask<bool> EstablishTrustedTransferAsync(
        PlayerId playerId,
        string accountId,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    ValueTask<bool> EstablishTrustedTransferAsync(PlayerSnapshot player, string accountId, CancellationToken cancellationToken = default) =>
        EstablishTrustedTransferAsync(player.Id, accountId, cancellationToken);

    /// <summary>Consumes a trusted transfer admission once and returns its exact authentication tenure.</summary>
    ValueTask<PlayerAccountBinding?> EstablishTrustedTransferBindingAsync(PlayerSnapshot player, string accountId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<PlayerAccountBinding?>(null);
}

public readonly record struct PlayerAccountBinding(PlayerId PlayerId, PlayerSessionId SessionId, AccountId AccountId, Guid AuthenticationId);

public enum PlayerAuthenticationEndReason { Logout, Disconnected, SessionReplaced }

public readonly record struct PlayerAuthenticationEnded(PlayerAccountBinding Binding, PlayerAuthenticationEndReason Reason);
