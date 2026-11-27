using Vega.Contracts.Accounts;
using Vega.Contracts.Worlds;

namespace Vega.Contracts.Players;

/// <summary>Authenticated residence in an entered native world, not an AFK measurement.</summary>
public sealed record PlayerPlaytimeCheckpoint(
    Guid AuthenticationId, AccountId AccountId, PlayerSessionId SessionId, WorldId WorldId,
    DateTimeOffset StartedAt, TimeSpan Elapsed, bool Ended);

public sealed record PlayerPlaytimeSummary(AccountId AccountId, WorldId WorldId,
    TimeSpan Total, TimeSpan InWorld, long Sessions, long SessionsInWorld);

/// <summary>Persisted totals and live current duration. Current may already be partly included in Total.</summary>
public sealed record PlayerPlaytimeView(PlayerPlaytimeSummary Total, TimeSpan Current);

public interface IPlayerPlaytimeSessionReader
{
    ValueTask<PlayerPlaytimeView?> ReadCurrentAsync(PlayerSnapshot player, CancellationToken cancellationToken = default);
}

public interface IPlayerPlaytimeReader
{
    /// <summary>Persisted totals, including successful checkpoints of still-open sessions.</summary>
    ValueTask<PlayerPlaytimeSummary> ReadAsync(AccountId account, WorldId world, CancellationToken cancellationToken = default);
}

public interface IPlayerPlaytimeStore : IPlayerPlaytimeReader
{
    /// <summary>Idempotent absolute checkpoints; an ended authentication tenure cannot be reopened.</summary>
    ValueTask WriteAsync(IReadOnlyList<PlayerPlaytimeCheckpoint> checkpoints, CancellationToken cancellationToken = default);
}
