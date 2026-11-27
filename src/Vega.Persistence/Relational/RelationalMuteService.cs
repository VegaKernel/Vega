using System.Data.Common;
using System.Globalization;
using Vega.Contracts.Moderation;

namespace Vega.Persistence.Relational;

internal sealed class RelationalMuteService(
    Func<DbConnection> open,
    Func<CancellationToken, ValueTask<DbConnection>> openAsync,
    Action ensureReady) : IMuteService
{
    private readonly SemaphoreSlim mutations = new(1, 1);
    private readonly object cacheSync = new();
    private Dictionary<MuteId, MuteSnapshot>? active;

    public IReadOnlyList<MuteSnapshot> SnapshotActive(DateTimeOffset now)
    {
        EnsureCache();
        lock (cacheSync)
        {
            RemoveExpired(now);
            return active!.Values
                .OrderBy(static mute => mute.ExpiresAt)
                .ThenBy(static mute => mute.Id.Value)
                .ToArray();
        }
    }

    public MuteSnapshot? FindActive(string playerName, string? accountName, DateTimeOffset now) => FindActive(playerName, accountName, null, null, now);

    public MuteSnapshot? FindActive(string playerName, string? accountName, string? ip, string? uuid, DateTimeOffset now)
    {
        string normalizedPlayer = Normalize(playerName, nameof(playerName));
        string? normalizedAccount = string.IsNullOrWhiteSpace(accountName) ? null : accountName.Trim();
        EnsureCache();
        lock (cacheSync)
        {
            MuteSnapshot? best = null;
            foreach (MuteSnapshot mute in active!.Values)
            {
                if (mute.ExpiresAt <= now) continue;
                bool matches = mute.SubjectKind switch
                {
                    MuteSubjectKind.Account => normalizedAccount is not null &&
                        mute.SubjectValue.Equals(normalizedAccount, StringComparison.OrdinalIgnoreCase),
                    MuteSubjectKind.PlayerName =>
                        mute.SubjectValue.Equals(normalizedPlayer, StringComparison.OrdinalIgnoreCase),
                    _ => false
                };
                matches |= mute.IpAddress is not null && mute.IpAddress.Equals(ip, StringComparison.OrdinalIgnoreCase) ||
                    mute.Uuid is not null && mute.Uuid.Equals(uuid, StringComparison.Ordinal);
                if (matches && (best is null ||
                    mute.SubjectKind > best.SubjectKind ||
                    mute.SubjectKind == best.SubjectKind && mute.IssuedAt > best.IssuedAt))
                {
                    best = mute;
                }
            }

            return best;
        }
    }

    public async ValueTask<MuteMutationResult> CreateAsync(
        MuteSnapshot mute,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mute);
        Validate(mute);
        ensureReady();
        EnsureCache();
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (cacheSync)
            {
                if (active!.ContainsKey(mute.Id))
                {
                    return new MuteMutationResult(false, "mute.exists", active[mute.Id]);
                }
            }

            await using DbConnection connection = await openAsync(cancellationToken).ConfigureAwait(false);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await using (DbCommand command = CreateCommand(connection, transaction,
                    "INSERT INTO vega_mutes " +
                    "(id, subject_kind, subject_value, reason, issued_by, issued_at_utc, expires_at_utc, revision, revoked, revoked_by, revoked_at_utc) " +
                    "VALUES (@id, @kind, @subject, @reason, @issuedBy, @issuedAt, @expiresAt, @revision, FALSE, NULL, NULL);"))
                {
                    AddParameters(command, mute);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                if (mute.IpAddress is not null || mute.Uuid is not null)
                {
                    await using DbCommand identity = CreateCommand(connection, transaction, "INSERT INTO vega_mute_identity (mute_id, last_ip, last_uuid) VALUES (@id, @ip, @uuid);");
                    identity.AddParameter("@id", mute.Id.ToString());
                    identity.AddParameter("@ip", mute.IpAddress);
                    identity.AddParameter("@uuid", mute.Uuid);
                    await identity.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await WriteAuditAsync(connection, transaction, mute.IssuedBy, "mute.create", mute.Id.ToString(), 0, mute.Revision, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                lock (cacheSync) active![mute.Id] = mute;
                return new MuteMutationResult(true, "mute.created", After: mute);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            mutations.Release();
        }
    }

    public async ValueTask<MuteMutationResult> RevokeAsync(
        MuteId muteId,
        long expectedRevision,
        string actor,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default)
    {
        if (muteId.Value == Guid.Empty) throw new ArgumentException("A mute identifier is required.", nameof(muteId));
        if (expectedRevision <= 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        string normalizedActor = Normalize(actor, nameof(actor));
        ensureReady();
        EnsureCache();
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MuteSnapshot? before;
            lock (cacheSync) active!.TryGetValue(muteId, out before);
            if (before is null) return new MuteMutationResult(false, "mute.not-found");
            if (before.Revision != expectedRevision)
            {
                return new MuteMutationResult(false, "mute.revision-conflict", before);
            }

            await using DbConnection connection = await openAsync(cancellationToken).ConfigureAwait(false);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await using DbCommand command = CreateCommand(connection, transaction,
                    "UPDATE vega_mutes SET revoked = TRUE, revoked_by = @actor, revoked_at_utc = @at, revision = revision + 1 " +
                    "WHERE id = @id AND revision = @revision AND revoked = FALSE;");
                command.AddParameter("@actor", normalizedActor);
                command.AddParameter("@at", revokedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                command.AddParameter("@id", muteId.ToString());
                command.AddParameter("@revision", expectedRevision);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    return new MuteMutationResult(false, "mute.revision-conflict", before);
                }

                await WriteAuditAsync(connection, transaction, normalizedActor, "mute.revoke", muteId.ToString(), before.Revision, before.Revision + 1, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                lock (cacheSync) active!.Remove(muteId);
                return new MuteMutationResult(true, "mute.revoked", before);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            mutations.Release();
        }
    }

    private void EnsureCache()
    {
        ensureReady();
        lock (cacheSync)
        {
            if (active is not null) return;
            using DbConnection connection = open();
            using DbCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT m.id, m.subject_kind, m.subject_value, m.reason, m.issued_by, m.issued_at_utc, m.expires_at_utc, m.revision, i.last_ip, i.last_uuid " +
                "FROM vega_mutes m LEFT JOIN vega_mute_identity i ON m.id = i.mute_id WHERE m.revoked = FALSE AND m.expires_at_utc > @now ORDER BY m.issued_at_utc, m.id;";
            command.AddParameter("@now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            using DbDataReader reader = command.ExecuteReader();
            active = [];
            while (reader.Read())
            {
                MuteSnapshot mute = Read(reader);
                active[mute.Id] = mute;
            }
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        List<MuteId>? expired = null;
        foreach ((MuteId id, MuteSnapshot mute) in active!)
        {
            if (mute.ExpiresAt <= now) (expired ??= []).Add(id);
        }

        if (expired is null) return;
        foreach (MuteId id in expired) active.Remove(id);
    }

    private static MuteSnapshot Read(DbDataReader reader) => new(
        new MuteId(Guid.Parse(reader.GetString(0))),
        (MuteSubjectKind)Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture))
    {
        IpAddress = reader.IsDBNull(8) ? null : reader.GetString(8),
        Uuid = reader.IsDBNull(9) ? null : reader.GetString(9)
    };

    private static void AddParameters(DbCommand command, MuteSnapshot mute)
    {
        command.AddParameter("@id", mute.Id.ToString());
        command.AddParameter("@kind", (int)mute.SubjectKind);
        command.AddParameter("@subject", mute.SubjectValue.Trim());
        command.AddParameter("@reason", mute.Reason.Trim());
        command.AddParameter("@issuedBy", mute.IssuedBy.Trim());
        command.AddParameter("@issuedAt", mute.IssuedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.AddParameter("@expiresAt", mute.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.AddParameter("@revision", mute.Revision);
    }

    private static async ValueTask WriteAuditAsync(DbConnection connection, DbTransaction transaction, string actor, string operation, string target, long before, long after, CancellationToken cancellationToken)
    {
        await using DbCommand command = CreateCommand(connection, transaction,
            "INSERT INTO vega_administration_audit (id, occurred_at_utc, actor, operation, target, before_revision, after_revision, outcome) " +
            "VALUES (@id, @at, @actor, @operation, @target, @before, @after, 'applied');");
        command.AddParameter("@id", Guid.NewGuid().ToString("D"));
        command.AddParameter("@at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.AddParameter("@actor", actor.Trim());
        command.AddParameter("@operation", operation);
        command.AddParameter("@target", target);
        command.AddParameter("@before", before);
        command.AddParameter("@after", after);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction transaction, string sql)
    {
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Validate(MuteSnapshot mute)
    {
        if (mute.Id.Value == Guid.Empty) throw new ArgumentException("A mute identifier is required.", nameof(mute));
        if (!Enum.IsDefined(mute.SubjectKind)) throw new ArgumentOutOfRangeException(nameof(mute));
        _ = Normalize(mute.SubjectValue, nameof(mute.SubjectValue));
        _ = Normalize(mute.Reason, nameof(mute.Reason), 2048);
        _ = Normalize(mute.IssuedBy, nameof(mute.IssuedBy));
        if (mute.IpAddress is not null && !System.Net.IPAddress.TryParse(mute.IpAddress, out _)) throw new ArgumentException("Invalid mute IP address.");
        if (mute.Uuid is not null) _ = Normalize(mute.Uuid, nameof(mute.Uuid));
        if (mute.Revision <= 0 || mute.ExpiresAt <= mute.IssuedAt)
        {
            throw new ArgumentException("Mute revision and expiration are invalid.", nameof(mute));
        }
    }

    private static string Normalize(string value, string parameter, int maximumLength = 255)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        string normalized = value.Trim();
        if (normalized.Length > maximumLength) throw new ArgumentOutOfRangeException(parameter);
        return normalized;
    }
}
