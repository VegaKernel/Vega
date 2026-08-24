using System.Data.Common;
using System.Globalization;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;

namespace Vega.Persistence.Relational;

internal sealed class RelationalRoleLeaseService(
    Func<DbConnection> open,
    Func<CancellationToken, ValueTask<DbConnection>> openAsync,
    Action ensureReady) : IRoleLeaseService
{
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromDays(365);
    private readonly SemaphoreSlim mutations = new(1, 1);
    private readonly object cacheSync = new();
    private Dictionary<AccountId, RoleLeaseSnapshot>? activeByAccount;

    public IReadOnlyList<RoleLeaseSnapshot> SnapshotActive(DateTimeOffset now)
    {
        EnsureCache();
        lock (cacheSync)
        {
            return activeByAccount!.Values
                .Where(lease => lease.IsActive(now))
                .OrderBy(static lease => lease.ExpiresAt)
                .ThenBy(static lease => lease.Id)
                .ToArray();
        }
    }

    public RoleLeaseSnapshot? FindActive(AccountId accountId, DateTimeOffset now)
    {
        EnsureCache();
        lock (cacheSync)
        {
            return activeByAccount!.TryGetValue(accountId, out RoleLeaseSnapshot? lease) && lease.IsActive(now)
                ? lease
                : null;
        }
    }

    public async ValueTask<RoleLeaseMutationResult> GrantAsync(
        AccountId accountId,
        string groupName,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ValidateAccountId(accountId);
        string normalizedGroup = Normalize(groupName, nameof(groupName));
        string normalizedActor = Normalize(actor, nameof(actor));
        issuedAt = issuedAt.ToUniversalTime();
        expiresAt = expiresAt.ToUniversalTime();
        TimeSpan duration = expiresAt - issuedAt;
        if (duration <= TimeSpan.Zero || duration > MaximumDuration)
        {
            return new RoleLeaseMutationResult(false, "role-lease.invalid-expiry");
        }

        ensureReady();
        EnsureCache();
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using DbConnection connection = await openAsync(cancellationToken).ConfigureAwait(false);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (!await ExistsAsync(connection, transaction, "vega_accounts", "id", accountId.Value, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return new RoleLeaseMutationResult(false, "account.not-found");
                }

                if (!await ExistsAsync(connection, transaction, "vega_groups", "name", normalizedGroup, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return new RoleLeaseMutationResult(false, "group.not-found");
                }

                RoleLeaseSnapshot? before = await FindCurrentAsync(
                    connection, transaction, accountId, cancellationToken).ConfigureAwait(false);
                await using (DbCommand revoke = CreateCommand(connection, transaction,
                    "UPDATE vega_role_leases SET revoked = @revoked, revision = revision + 1 " +
                    "WHERE account_id = @account AND revoked = @active;"))
                {
                    revoke.AddParameter("@account", accountId.Value);
                    revoke.AddParameter("@revoked", true);
                    revoke.AddParameter("@active", false);
                    await revoke.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                var after = new RoleLeaseSnapshot(
                    Guid.NewGuid(),
                    accountId,
                    normalizedGroup,
                    normalizedActor,
                    issuedAt,
                    expiresAt,
                    1,
                    false);
                await using (DbCommand insert = CreateCommand(connection, transaction,
                    "INSERT INTO vega_role_leases " +
                    "(id, account_id, group_name, issued_by, issued_at_utc, expires_at_utc, revision, revoked) " +
                    "VALUES (@id, @account, @group, @actor, @issued, @expires, 1, @revoked);"))
                {
                    AddLeaseParameters(insert, after);
                    insert.AddParameter("@revoked", false);
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await WriteAuditAsync(
                    connection,
                    transaction,
                    normalizedActor,
                    "role-lease.grant",
                    after.Id.ToString("D"),
                    before?.Revision ?? 0,
                    after.Revision,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                lock (cacheSync)
                {
                    activeByAccount ??= [];
                    activeByAccount[accountId] = after;
                }

                return new RoleLeaseMutationResult(true, "role-lease.granted", before, after);
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

    public async ValueTask<RoleLeaseMutationResult> RevokeAsync(
        Guid leaseId,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (leaseId == Guid.Empty) throw new ArgumentException("A role lease identifier is required.", nameof(leaseId));
        if (expectedRevision <= 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        string normalizedActor = Normalize(actor, nameof(actor));
        ensureReady();
        EnsureCache();
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using DbConnection connection = await openAsync(cancellationToken).ConfigureAwait(false);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                RoleLeaseSnapshot? before = await FindByIdAsync(
                    connection, transaction, leaseId, cancellationToken).ConfigureAwait(false);
                if (before is null)
                {
                    return new RoleLeaseMutationResult(false, "role-lease.not-found");
                }

                if (before.Revision != expectedRevision)
                {
                    return new RoleLeaseMutationResult(false, "role-lease.revision-conflict", before);
                }

                if (before.Revoked)
                {
                    return new RoleLeaseMutationResult(false, "role-lease.already-revoked", before);
                }

                await using DbCommand update = CreateCommand(connection, transaction,
                    "UPDATE vega_role_leases SET revoked = @revoked, revision = revision + 1 " +
                    "WHERE id = @id AND revision = @revision AND revoked = @active;");
                update.AddParameter("@id", leaseId.ToString("D"));
                update.AddParameter("@revision", expectedRevision);
                update.AddParameter("@revoked", true);
                update.AddParameter("@active", false);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    return new RoleLeaseMutationResult(false, "role-lease.revision-conflict", before);
                }

                RoleLeaseSnapshot after = before with { Revision = before.Revision + 1, Revoked = true };
                await WriteAuditAsync(
                    connection,
                    transaction,
                    normalizedActor,
                    "role-lease.revoke",
                    leaseId.ToString("D"),
                    before.Revision,
                    after.Revision,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                lock (cacheSync)
                {
                    if (activeByAccount?.TryGetValue(before.AccountId, out RoleLeaseSnapshot? current) == true &&
                        current.Id == before.Id)
                    {
                        activeByAccount.Remove(before.AccountId);
                    }
                }

                return new RoleLeaseMutationResult(true, "role-lease.revoked", before, after);
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
            if (activeByAccount is not null) return;
            using DbConnection connection = open();
            using DbCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT id, account_id, group_name, issued_by, issued_at_utc, expires_at_utc, revision, revoked " +
                "FROM vega_role_leases WHERE revoked = @active ORDER BY issued_at_utc, id;";
            command.AddParameter("@active", false);
            using DbDataReader reader = command.ExecuteReader();
            activeByAccount = [];
            while (reader.Read())
            {
                RoleLeaseSnapshot lease = Read(reader);
                activeByAccount[lease.AccountId] = lease;
            }
        }
    }

    private static async ValueTask<RoleLeaseSnapshot?> FindCurrentAsync(
        DbConnection connection,
        DbTransaction transaction,
        AccountId accountId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = CreateCommand(connection, transaction,
            "SELECT id, account_id, group_name, issued_by, issued_at_utc, expires_at_utc, revision, revoked " +
            "FROM vega_role_leases WHERE account_id = @account AND revoked = @active " +
            "ORDER BY issued_at_utc DESC, id DESC LIMIT 1;");
        command.AddParameter("@account", accountId.Value);
        command.AddParameter("@active", false);
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static async ValueTask<RoleLeaseSnapshot?> FindByIdAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = CreateCommand(connection, transaction,
            "SELECT id, account_id, group_name, issued_by, issued_at_utc, expires_at_utc, revision, revoked " +
            "FROM vega_role_leases WHERE id = @id;");
        command.AddParameter("@id", leaseId.ToString("D"));
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static async ValueTask<bool> ExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string column,
        object value,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = CreateCommand(
            connection,
            transaction,
            $"SELECT COUNT(*) FROM {table} WHERE {column} = @value;");
        command.AddParameter("@value", value);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    private static RoleLeaseSnapshot Read(DbDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        new AccountId(Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture)),
        reader.GetString(2),
        reader.GetString(3),
        DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Convert.ToInt64(reader.GetValue(6), CultureInfo.InvariantCulture),
        Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture) != 0);

    private static void AddLeaseParameters(DbCommand command, RoleLeaseSnapshot lease)
    {
        command.AddParameter("@id", lease.Id.ToString("D"));
        command.AddParameter("@account", lease.AccountId.Value);
        command.AddParameter("@group", lease.GroupName);
        command.AddParameter("@actor", lease.IssuedBy);
        command.AddParameter("@issued", lease.IssuedAt.ToString("O", CultureInfo.InvariantCulture));
        command.AddParameter("@expires", lease.ExpiresAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static async ValueTask WriteAuditAsync(
        DbConnection connection,
        DbTransaction transaction,
        string actor,
        string operation,
        string target,
        long beforeRevision,
        long afterRevision,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = CreateCommand(connection, transaction,
            "INSERT INTO vega_administration_audit " +
            "(id, occurred_at_utc, actor, operation, target, before_revision, after_revision, outcome) " +
            "VALUES (@id, @at, @actor, @operation, @target, @before, @after, 'applied');");
        command.AddParameter("@id", Guid.NewGuid().ToString("D"));
        command.AddParameter("@at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.AddParameter("@actor", actor);
        command.AddParameter("@operation", operation);
        command.AddParameter("@target", target);
        command.AddParameter("@before", beforeRevision);
        command.AddParameter("@after", afterRevision);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction transaction, string sql)
    {
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static string Normalize(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        string normalized = value.Trim();
        if (normalized.Length > 255) throw new ArgumentOutOfRangeException(parameter);
        return normalized;
    }

    private static void ValidateAccountId(AccountId accountId)
    {
        if (accountId.Value <= 0) throw new ArgumentOutOfRangeException(nameof(accountId));
    }
}
