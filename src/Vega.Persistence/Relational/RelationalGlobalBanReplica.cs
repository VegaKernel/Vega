using System.Data.Common;
using System.Data;
using System.Globalization;
using Vega.Contracts.Cluster.Events;

namespace Vega.Persistence.Relational;

internal sealed record RelationalGlobalBanSql(
    string ApplyCreated,
    string ApplyRevoked,
    string SnapshotActive);

internal sealed class RelationalGlobalBanReplica(
    IRelationalConnectionFactory connections,
    RelationalGlobalBanSql sql,
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
            await using DbConnection connection = await connections
                .OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = sql.ApplyCreated;
            command.AddParameter("@id", created.BanId.Value.ToString("D"));
            command.AddParameter("@subjectKind", (int)created.Subject.Kind);
            command.AddParameter("@subjectValue", created.Subject.Value);
            command.AddParameter("@reason", created.Reason);
            command.AddParameter("@createdBy", created.CreatedBy);
            command.AddParameter("@createdAt", Format(created.CreatedAt));
            command.AddParameter(
                "@expiresAt",
                created.ExpiresAt is DateTimeOffset expiresAt ? Format(expiresAt) : null);
            command.AddParameter("@revision", created.Revision);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
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
            await using DbConnection connection = await connections
                .OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = sql.ApplyRevoked;
            command.AddParameter("@id", revoked.BanId.Value.ToString("D"));
            command.AddParameter("@revision", revoked.Revision);
            command.AddParameter("@revokedBy", revoked.RevokedBy);
            command.AddParameter("@revokedAt", Format(revoked.RevokedAt));
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            writes.Release();
        }
    }

    /// <summary>Offline insert-only migration; existing rows (including tombstones) never change.</summary>
    internal async ValueTask<int> ImportNewAsync(IReadOnlyList<GlobalBanCreated> bans, CancellationToken token)
    {
        ensureReady();
        foreach (var ban in bans) Validate(ban);
        if (bans.Select(ban => ban.BanId).Distinct().Count() != bans.Count)
            throw new InvalidDataException("Ban import contains duplicate identifiers.");
        await writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await using DbConnection connection = await connections.OpenAsync(token).ConfigureAwait(false);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false);
            int imported = 0;
            foreach (var ban in bans)
            {
                string id = ban.BanId.Value.ToString("D");
                // Explicit existing-ID check rather than MySQL INSERT IGNORE,
                // which can silently suppress validation errors other than duplicates.
                await using (DbCommand exists = connection.CreateCommand())
                {
                    exists.Transaction = transaction;
                    exists.CommandText = "SELECT COUNT(*) FROM global_bans WHERE id = @id;";
                    exists.AddParameter("@id", id);
                    if (Convert.ToInt64(await exists.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
                        continue;
                }
                await using DbCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO global_bans (id, subject_kind, subject_value, reason, created_by, created_at_utc, expires_at_utc, revision, revoked)
                    VALUES (@id, @subjectKind, @subjectValue, @reason, @createdBy, @createdAt, @expiresAt, @revision, FALSE);
                    """;
                command.AddParameter("@id", id);
                command.AddParameter("@subjectKind", (int)ban.Subject.Kind);
                command.AddParameter("@subjectValue", ban.Subject.Value);
                command.AddParameter("@reason", ban.Reason);
                command.AddParameter("@createdBy", ban.CreatedBy);
                command.AddParameter("@createdAt", Format(ban.CreatedAt));
                command.AddParameter("@expiresAt", ban.ExpiresAt is { } expiresAt ? Format(expiresAt) : null);
                command.AddParameter("@revision", ban.Revision);
                if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    throw new InvalidDataException("An imported ban was not inserted.");
                imported++;
            }
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return imported;
        }
        finally { writes.Release(); }
    }

    public IReadOnlyList<GlobalBanCreated> SnapshotActive(DateTimeOffset now)
    {
        ensureReady();
        using DbConnection connection = connections.Open();
        using DbCommand command = connection.CreateCommand();
        command.CommandText = sql.SnapshotActive;
        command.AddParameter("@now", Format(now));

        using DbDataReader reader = command.ExecuteReader();
        var active = new List<GlobalBanCreated>();
        while (reader.Read())
        {
            var kind = (GlobalBanSubjectKind)Convert.ToInt32(
                reader.GetValue(1),
                CultureInfo.InvariantCulture);
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
                Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture)));
        }

        return active.AsReadOnly();
    }

    private static void Validate(GlobalBanCreated created)
    {
        ArgumentNullException.ThrowIfNull(created);
        if (created.BanId.Value == Guid.Empty ||
            !Enum.IsDefined(created.Subject.Kind) ||
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
