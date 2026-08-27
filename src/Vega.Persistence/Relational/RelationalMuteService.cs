using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Vega.Contracts.Moderation;
using Vega.Contracts.Networking;

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

    public MuteSnapshot? FindActive(string playerName, string? accountName, DateTimeOffset now) =>
        FindActive(new MuteIdentity(playerName, accountName), now);

    public MuteSnapshot? FindActive(MuteIdentity identity, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(identity);
        MuteSubject[] candidates = BuildIdentitySubjects(identity);
        EnsureCache();
        lock (cacheSync)
        {
            MuteSnapshot? best = null;
            int bestSpecificity = -1;
            foreach (MuteSnapshot mute in active!.Values)
            {
                if (mute.ExpiresAt <= now) continue;
                int specificity = MatchSpecificity(GetSubjects(mute), candidates);
                if (specificity >= 0 &&
                    (specificity > bestSpecificity ||
                     specificity == bestSpecificity && (best is null || mute.IssuedAt > best.IssuedAt)))
                {
                    best = mute;
                    bestSpecificity = specificity;
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
        MuteSubject[] subjects = NormalizeSubjects(mute);
        mute = mute with { Subjects = subjects };
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

                foreach (MuteSubject subject in subjects)
                {
                    await using DbCommand subjectCommand = CreateCommand(connection, transaction,
                        "INSERT INTO vega_mute_subjects (mute_id, kind, value) VALUES (@id, @kind, @value);");
                    subjectCommand.AddParameter("@id", mute.Id.ToString());
                    subjectCommand.AddParameter("@kind", (int)subject.Kind);
                    subjectCommand.AddParameter("@value", subject.Value);
                    await subjectCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                "SELECT id, subject_kind, subject_value, reason, issued_by, issued_at_utc, expires_at_utc, revision " +
                "FROM vega_mutes WHERE revoked = FALSE AND expires_at_utc > @now ORDER BY issued_at_utc, id;";
            command.AddParameter("@now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            using DbDataReader reader = command.ExecuteReader();
            active = [];
            while (reader.Read())
            {
                MuteSnapshot mute = Read(reader);
                active[mute.Id] = mute;
            }
            reader.Close();

            if (active.Count == 0) return;
            var subjects = new Dictionary<MuteId, List<MuteSubject>>();
            using DbCommand subjectCommand = connection.CreateCommand();
            subjectCommand.CommandText = "SELECT mute_id, kind, value FROM vega_mute_subjects ORDER BY mute_id, kind, value;";
            using DbDataReader subjectReader = subjectCommand.ExecuteReader();
            while (subjectReader.Read())
            {
                var id = new MuteId(Guid.Parse(subjectReader.GetString(0)));
                if (!active.ContainsKey(id)) continue;
                if (!subjects.TryGetValue(id, out List<MuteSubject>? list))
                {
                    list = [];
                    subjects.Add(id, list);
                }
                list.Add(new MuteSubject(
                    (MuteIdentityKind)Convert.ToInt32(subjectReader.GetValue(1), CultureInfo.InvariantCulture),
                    subjectReader.GetString(2)));
            }

            foreach ((MuteId id, MuteSnapshot mute) in active.ToArray())
            {
                active[id] = mute with
                {
                    Subjects = subjects.TryGetValue(id, out List<MuteSubject>? list)
                        ? list.ToArray()
                        : GetSubjects(mute)
                };
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
        Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture));

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

    private static MuteSubject[] NormalizeSubjects(MuteSnapshot mute)
    {
        IEnumerable<MuteSubject> source = mute.Subjects.Count == 0
            ? [new MuteSubject((MuteIdentityKind)mute.SubjectKind, mute.SubjectValue)]
            : mute.Subjects;
        return source
            .Select(NormalizeSubject)
            .DistinctBy(static subject => (subject.Kind, subject.Value), MuteSubjectKeyComparer.Instance)
            .ToArray();
    }

    private static MuteSubject[] GetSubjects(MuteSnapshot mute) => mute.Subjects.Count == 0
        ? [new MuteSubject((MuteIdentityKind)mute.SubjectKind, mute.SubjectValue)]
        : mute.Subjects.ToArray();

    private static MuteSubject[] BuildIdentitySubjects(MuteIdentity identity)
    {
        var values = new List<MuteSubject>
        {
            NormalizeSubject(new MuteSubject(MuteIdentityKind.PlayerName, identity.PlayerName))
        };
        if (!string.IsNullOrWhiteSpace(identity.AccountName))
            values.Add(NormalizeSubject(new MuteSubject(MuteIdentityKind.Account, identity.AccountName)));
        if (!string.IsNullOrWhiteSpace(identity.ClientUuid))
            values.Add(NormalizeSubject(new MuteSubject(MuteIdentityKind.ClientUuid, identity.ClientUuid)));
        if (!string.IsNullOrWhiteSpace(identity.IpAddress) &&
            TryNormalizeIpv4(identity.IpAddress, out string ipAddress))
            values.Add(new MuteSubject(MuteIdentityKind.IpAddress, ipAddress));
        return values.ToArray();
    }

    private static int MatchSpecificity(IReadOnlyList<MuteSubject> muteSubjects, IReadOnlyList<MuteSubject> candidates)
    {
        int best = -1;
        foreach (MuteSubject muteSubject in muteSubjects)
        {
            foreach (MuteSubject candidate in candidates)
            {
                if (muteSubject.Kind != candidate.Kind || !SubjectEquals(muteSubject, candidate)) continue;
                best = Math.Max(best, muteSubject.Kind switch
                {
                    MuteIdentityKind.Account => 400,
                    MuteIdentityKind.ClientUuid => 400,
                    MuteIdentityKind.IpAddress => 300,
                    MuteIdentityKind.PlayerName => 100,
                    _ => -1
                });
            }
        }
        return best;
    }

    private static bool SubjectEquals(MuteSubject left, MuteSubject right) => left.Kind switch
    {
        MuteIdentityKind.PlayerName or MuteIdentityKind.Account =>
            left.Value.Equals(right.Value, StringComparison.OrdinalIgnoreCase),
        _ => left.Value.Equals(right.Value, StringComparison.Ordinal)
    };

    private static MuteSubject NormalizeSubject(MuteSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        string value = Normalize(subject.Value, nameof(subject.Value), 512);
        return subject.Kind switch
        {
            MuteIdentityKind.PlayerName or MuteIdentityKind.Account => subject with { Value = value },
            MuteIdentityKind.ClientUuid => value.Length <= ConnectionMetadataLimits.MaxClientUuidLength
                ? subject with { Value = value }
                : throw new ArgumentOutOfRangeException(nameof(subject.Value)),
            MuteIdentityKind.IpAddress => TryNormalizeIpv4(value, out string address)
                ? subject with { Value = address }
                : throw new ArgumentException("Mute IP subject is invalid.", nameof(subject.Value)),
            _ => throw new ArgumentOutOfRangeException(nameof(subject.Kind))
        };
    }

    private static bool TryNormalizeIpv4(string value, out string normalized)
    {
        normalized = string.Empty;
        if (!IPAddress.TryParse(value, out IPAddress? address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        normalized = address.ToString();
        return true;
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
        if (mute.Subjects.Count == 0) throw new ArgumentException("At least one mute identity subject is required.", nameof(mute));
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

    private sealed class MuteSubjectKeyComparer : IEqualityComparer<(MuteIdentityKind Kind, string Value)>
    {
        public static MuteSubjectKeyComparer Instance { get; } = new();

        public bool Equals((MuteIdentityKind Kind, string Value) x, (MuteIdentityKind Kind, string Value) y) =>
            x.Kind == y.Kind && (x.Kind is MuteIdentityKind.PlayerName or MuteIdentityKind.Account
                ? x.Value.Equals(y.Value, StringComparison.OrdinalIgnoreCase)
                : x.Value.Equals(y.Value, StringComparison.Ordinal));

        public int GetHashCode((MuteIdentityKind Kind, string Value) obj) =>
            HashCode.Combine(obj.Kind, obj.Kind is MuteIdentityKind.PlayerName or MuteIdentityKind.Account
                ? StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Value)
                : StringComparer.Ordinal.GetHashCode(obj.Value));
    }
}
