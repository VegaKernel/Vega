using System.Data.Common;
using System.Globalization;
using Vega.Contracts.Accounts;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Persistence.Relational;

internal sealed class RelationalPlaytimeStore(
    Func<CancellationToken, ValueTask<DbConnection>> open, Action ensureReady, bool mysql = false) : IPlayerPlaytimeStore
{
    internal const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS vega_play_sessions (
            authentication_id VARCHAR(32) NOT NULL PRIMARY KEY,
            account_id INTEGER NOT NULL,
            session_id VARCHAR(32) NOT NULL,
            world_id VARCHAR(255) NOT NULL,
            started_at_utc VARCHAR(40) NOT NULL,
            elapsed_ms BIGINT NOT NULL CHECK (elapsed_ms >= 0),
            ended INTEGER NOT NULL CHECK (ended BETWEEN 0 AND 1),
            FOREIGN KEY (account_id) REFERENCES vega_accounts(id) ON DELETE CASCADE
        )
        """;
    internal const string AccountIndexSql = "CREATE INDEX IF NOT EXISTS ix_vega_play_sessions_account ON vega_play_sessions(account_id);";

    private const string Insert = """
        INSERT INTO vega_play_sessions (authentication_id, account_id, session_id, world_id, started_at_utc, elapsed_ms, ended)
        SELECT @id, @account, @session, @world, @started, @elapsed, @ended FROM vega_accounts WHERE id = @account
        """;

    // Only the holder of the immutable authentication id may advance a checkpoint; old retries never resurrect an ended tenure.
    private const string OnConflict = """
        ON CONFLICT (authentication_id) DO UPDATE SET
            elapsed_ms = CASE WHEN excluded.elapsed_ms > vega_play_sessions.elapsed_ms THEN excluded.elapsed_ms ELSE vega_play_sessions.elapsed_ms END,
            ended = excluded.ended
        WHERE vega_play_sessions.ended = 0 AND vega_play_sessions.account_id = excluded.account_id
            AND vega_play_sessions.session_id = excluded.session_id AND vega_play_sessions.world_id = excluded.world_id
            AND vega_play_sessions.started_at_utc = excluded.started_at_utc;
        """;

    private const string OnDuplicate = """
        ON DUPLICATE KEY UPDATE
            elapsed_ms = CASE WHEN vega_play_sessions.ended = 0 AND vega_play_sessions.account_id = VALUES(account_id)
                AND vega_play_sessions.session_id = VALUES(session_id) AND vega_play_sessions.world_id = VALUES(world_id)
                AND vega_play_sessions.started_at_utc = VALUES(started_at_utc)
                THEN GREATEST(vega_play_sessions.elapsed_ms, VALUES(elapsed_ms)) ELSE vega_play_sessions.elapsed_ms END,
            ended = CASE WHEN vega_play_sessions.ended = 0 AND vega_play_sessions.account_id = VALUES(account_id)
                AND vega_play_sessions.session_id = VALUES(session_id) AND vega_play_sessions.world_id = VALUES(world_id)
                AND vega_play_sessions.started_at_utc = VALUES(started_at_utc)
                THEN VALUES(ended) ELSE vega_play_sessions.ended END;
        """;

    public async ValueTask WriteAsync(IReadOnlyList<PlayerPlaytimeCheckpoint> checkpoints, CancellationToken cancellationToken = default)
    {
        if (checkpoints.Count == 0) return;
        ensureReady();
        await using DbConnection connection = await open(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Insert + "\n" + (mysql ? OnDuplicate : OnConflict);
        foreach (PlayerPlaytimeCheckpoint checkpoint in checkpoints)
        {
            if (checkpoint.AuthenticationId == Guid.Empty || checkpoint.SessionId.Value == Guid.Empty || checkpoint.AccountId.Value <= 0 ||
                checkpoint.Elapsed < TimeSpan.Zero || string.IsNullOrWhiteSpace(checkpoint.WorldId.Value) || checkpoint.WorldId.Value.Length > 255)
                throw new ArgumentException("Invalid gameplay checkpoint.", nameof(checkpoints));
            command.Parameters.Clear();
            command.AddParameter("@id", checkpoint.AuthenticationId.ToString("N"));
            command.AddParameter("@account", checkpoint.AccountId.Value);
            command.AddParameter("@session", checkpoint.SessionId.ToString());
            command.AddParameter("@world", checkpoint.WorldId.Value);
            command.AddParameter("@started", checkpoint.StartedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            command.AddParameter("@elapsed", checkpoint.Elapsed.Ticks / TimeSpan.TicksPerMillisecond);
            command.AddParameter("@ended", checkpoint.Ended ? 1 : 0);
            // Account deletion wins: an absent FK target is a deliberate no-op.
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PlayerPlaytimeSummary> ReadAsync(AccountId account, WorldId world, CancellationToken cancellationToken = default)
    {
        ensureReady();
        if (account.Value <= 0 || string.IsNullOrWhiteSpace(world.Value) || world.Value.Length > 255)
            throw new ArgumentException("Invalid playtime scope.");
        await using DbConnection connection = await open(cancellationToken).ConfigureAwait(false);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(elapsed_ms), 0), COALESCE(SUM(CASE WHEN world_id = @world THEN elapsed_ms ELSE 0 END), 0),
                COUNT(*), COALESCE(SUM(CASE WHEN world_id = @world THEN 1 ELSE 0 END), 0)
            FROM vega_play_sessions WHERE account_id = @account;
            """;
        command.AddParameter("@account", account.Value);
        command.AddParameter("@world", world.Value);
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        long Read(int ordinal) => Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        return new(account, world, TimeSpan.FromMilliseconds(Read(0)), TimeSpan.FromMilliseconds(Read(1)), Read(2), Read(3));
    }
}
