using System.Globalization;
using Microsoft.Data.Sqlite;
using Vega.Contracts.Cluster.Events;

namespace Vega.Persistence.Sqlite;

internal sealed class SqliteGlobalBanReplica(
    SqliteConnectionFactory connections,
    Action ensureReady) : IGlobalBanReplica
{
    private readonly SemaphoreSlim writes = new(1, 1);

    public async ValueTask<bool> ApplyCreatedAsync(
        GlobalBanCreated created,
        CancellationToken cancellationToken = default)
    {
        ensureReady();
        Validate(created);
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await connections
                .OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO global_bans (
                    id, subject_kind, subject_value, reason, created_by,
                    created_at_utc, expires_at_utc, revision, revoked,
                    revoked_by, revoked_at_utc)
                VALUES (
                    $id, $subjectKind, $subjectValue, $reason, $createdBy,
                    $createdAt, $expiresAt, $revision, 0, NULL, NULL)
                ON CONFLICT(id) DO UPDATE SET
                    subject_kind = excluded.subject_kind,
                    subject_value = excluded.subject_value,
                    reason = excluded.reason,
                    created_by = excluded.created_by,
                    created_at_utc = excluded.created_at_utc,
                    expires_at_utc = excluded.expires_at_utc,
                    revision = excluded.revision,
                    revoked = 0,
                    revoked_by = NULL,
                    revoked_at_utc = NULL
                WHERE excluded.revision > global_bans.revision;
                """;
            command.Parameters.AddWithValue("$id", created.BanId.Value.ToString("D"));
            command.Parameters.AddWithValue("$subjectKind", (int)created.Subject.Kind);
            command.Parameters.AddWithValue("$subjectValue", created.Subject.Value);
            command.Parameters.AddWithValue("$reason", created.Reason);
            command.Parameters.AddWithValue("$createdBy", created.CreatedBy);
            command.Parameters.AddWithValue("$createdAt", Format(created.CreatedAt));
            command.Parameters.AddWithValue(
                "$expiresAt",
                created.ExpiresAt is DateTimeOffset expiresAt
                    ? Format(expiresAt)
                    : DBNull.Value);
            command.Parameters.AddWithValue("$revision", created.Revision);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
        finally
        {
            writes.Release();
        }
    }

    public async ValueTask<bool> ApplyRevokedAsync(
        GlobalBanRevoked revoked,
        CancellationToken cancellationToken = default)
    {
        ensureReady();
        Validate(revoked);
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await connections
                .OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO global_bans (
                    id, revision, revoked, revoked_by, revoked_at_utc)
                VALUES ($id, $revision, 1, $revokedBy, $revokedAt)
                ON CONFLICT(id) DO UPDATE SET
                    revision = excluded.revision,
                    revoked = 1,
                    revoked_by = excluded.revoked_by,
                    revoked_at_utc = excluded.revoked_at_utc
                WHERE excluded.revision > global_bans.revision;
                """;
            command.Parameters.AddWithValue("$id", revoked.BanId.Value.ToString("D"));
            command.Parameters.AddWithValue("$revision", revoked.Revision);
            command.Parameters.AddWithValue("$revokedBy", revoked.RevokedBy);
            command.Parameters.AddWithValue("$revokedAt", Format(revoked.RevokedAt));
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
        finally
        {
            writes.Release();
        }
    }

    public IReadOnlyList<GlobalBanCreated> SnapshotActive(DateTimeOffset now)
    {
        ensureReady();
        using SqliteConnection connection = connections.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, subject_kind, subject_value, reason, created_by,
                   created_at_utc, expires_at_utc, revision
            FROM global_bans
            WHERE revoked = 0
              AND subject_kind IS NOT NULL
              AND (expires_at_utc IS NULL OR expires_at_utc > $now)
            ORDER BY created_at_utc, id;
            """;
        command.Parameters.AddWithValue("$now", Format(now));

        using SqliteDataReader reader = command.ExecuteReader();
        var active = new List<GlobalBanCreated>();
        while (reader.Read())
        {
            var kind = (GlobalBanSubjectKind)reader.GetInt32(1);
            if (!Enum.IsDefined(kind))
            {
                throw new InvalidDataException($"Unknown global ban subject kind '{(int)kind}'.");
            }

            active.Add(new GlobalBanCreated(
                new GlobalBanId(Guid.Parse(reader.GetString(0))),
                new GlobalBanSubject(kind, reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : Parse(reader.GetString(6)),
                reader.GetInt64(7)));
        }

        return active.AsReadOnly();
    }

    private static void Validate(GlobalBanCreated created)
    {
        ArgumentNullException.ThrowIfNull(created);
        if (created.BanId.Value == Guid.Empty)
        {
            throw new ArgumentException("Global ban ID must not be empty.", nameof(created));
        }

        if (!Enum.IsDefined(created.Subject.Kind) ||
            string.IsNullOrWhiteSpace(created.Subject.Value) ||
            string.IsNullOrWhiteSpace(created.Reason) ||
            string.IsNullOrWhiteSpace(created.CreatedBy) ||
            created.Revision <= 0 ||
            created.ExpiresAt <= created.CreatedAt)
        {
            throw new ArgumentException("Global ban data is invalid.", nameof(created));
        }
    }

    private static void Validate(GlobalBanRevoked revoked)
    {
        ArgumentNullException.ThrowIfNull(revoked);
        if (revoked.BanId.Value == Guid.Empty ||
            string.IsNullOrWhiteSpace(revoked.RevokedBy) ||
            revoked.Revision <= 0)
        {
            throw new ArgumentException("Global ban revocation data is invalid.", nameof(revoked));
        }
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
