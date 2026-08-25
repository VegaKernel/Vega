using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Vega.Contracts.Accounts;

namespace Vega.Persistence.Relational;

internal sealed class RelationalAccountIdentityEvidenceService(
    Func<DbConnection> open,
    Func<CancellationToken, ValueTask<DbConnection>> openAsync,
    Action ensureReady) : IAccountIdentityEvidenceService
{
    private static readonly TimeSpan ReadCacheLifetime = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim mutations = new(1, 1);
    private readonly object cacheSync = new();
    private AccountIdentityEvidenceSnapshot[]? cachedEvidence;
    private long cachedAtTimestamp;

    public IReadOnlyList<AccountIdentityEvidenceSnapshot> SnapshotEvidence()
    {
        ensureReady();
        long now = Stopwatch.GetTimestamp();
        lock (cacheSync)
        {
            if (cachedEvidence is not null &&
                Stopwatch.GetElapsedTime(cachedAtTimestamp, now) < ReadCacheLifetime)
            {
                return Array.AsReadOnly(cachedEvidence);
            }
        }

        using DbConnection connection = open();
        using DbCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT e.account_id, a.name, e.kind, e.value,
                   e.first_seen_utc, e.last_seen_utc, e.observation_count
            FROM vega_account_identity_evidence e
            INNER JOIN vega_accounts a ON a.id = e.account_id
            ORDER BY a.name, e.kind, e.value;
            """;
        using DbDataReader reader = command.ExecuteReader();
        var evidence = new List<AccountIdentityEvidenceSnapshot>();
        while (reader.Read())
        {
            evidence.Add(ReadEvidence(reader));
        }

        AccountIdentityEvidenceSnapshot[] snapshot = evidence.ToArray();
        lock (cacheSync)
        {
            cachedEvidence = snapshot;
            cachedAtTimestamp = Stopwatch.GetTimestamp();
        }

        return Array.AsReadOnly(snapshot);
    }

    public IReadOnlyList<PossibleAccountLinkSnapshot> FindPossibleAccounts(AccountId accountId)
    {
        AccountIdentityEvidenceSnapshot[] all = SnapshotEvidence().ToArray();
        AccountIdentityEvidenceSnapshot[] source = all
            .Where(item => item.AccountId == accountId)
            .ToArray();
        if (source.Length == 0)
        {
            return Array.Empty<PossibleAccountLinkSnapshot>();
        }

        var sourceSignals = source
            .Select(static item => new IdentitySignal(item.Kind, item.Value))
            .ToHashSet();

        PossibleAccountLinkSnapshot[] result = all
            .Where(item => item.AccountId != accountId && sourceSignals.Contains(new IdentitySignal(item.Kind, item.Value)))
            .GroupBy(static item => new { item.AccountId, item.AccountName })
            .Select(group =>
            {
                AccountIdentityEvidenceSnapshot[] shared = group
                    .GroupBy(static item => new IdentitySignal(item.Kind, item.Value))
                    .Select(static signals => signals.OrderByDescending(item => item.LastSeenAt).First())
                    .ToArray();
                return new PossibleAccountLinkSnapshot(
                    group.Key.AccountId,
                    group.Key.AccountName,
                    shared.Length,
                    shared.Any(static item => item.Kind == AccountIdentityEvidenceKind.ClientUuid),
                    shared.Any(static item => item.Kind == AccountIdentityEvidenceKind.IpAddress),
                    shared.Max(static item => item.LastSeenAt));
            })
            .OrderByDescending(static item => item.SharedClientUuid)
            .ThenByDescending(static item => item.SharedSignals)
            .ThenByDescending(static item => item.LastMatchedAt)
            .ThenBy(static item => item.AccountName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Array.AsReadOnly(result);
    }

    public async ValueTask RecordAsync(
        AccountId accountId,
        string accountName,
        string? clientUuid,
        string? ipAddress,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken = default)
    {
        if (accountId.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(accountId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        string? normalizedUuid = NormalizeUuid(clientUuid);
        string? normalizedIp = NormalizeIp(ipAddress);
        if (normalizedUuid is null && normalizedIp is null)
        {
            return;
        }

        ensureReady();
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using DbConnection connection = await openAsync(cancellationToken).ConfigureAwait(false);
            await using DbTransaction transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (normalizedUuid is not null)
                {
                    await RecordSignalAsync(
                        connection,
                        transaction,
                        accountId,
                        AccountIdentityEvidenceKind.ClientUuid,
                        normalizedUuid,
                        seenAt,
                        cancellationToken).ConfigureAwait(false);
                }

                if (normalizedIp is not null)
                {
                    await RecordSignalAsync(
                        connection,
                        transaction,
                        accountId,
                        AccountIdentityEvidenceKind.IpAddress,
                        normalizedIp,
                        seenAt,
                        cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                InvalidateReadCache();
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

    private void InvalidateReadCache()
    {
        lock (cacheSync)
        {
            cachedEvidence = null;
            cachedAtTimestamp = 0;
        }
    }

    private static async ValueTask RecordSignalAsync(
        DbConnection connection,
        DbTransaction transaction,
        AccountId accountId,
        AccountIdentityEvidenceKind kind,
        string value,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken)
    {
        string timestamp = seenAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        await using DbCommand read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            """
            SELECT observation_count
            FROM vega_account_identity_evidence
            WHERE account_id = @accountId AND kind = @kind AND value = @value;
            """;
        read.AddParameter("@accountId", accountId.Value);
        read.AddParameter("@kind", (int)kind);
        read.AddParameter("@value", value);
        object? count = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand write = connection.CreateCommand();
        write.Transaction = transaction;
        write.AddParameter("@accountId", accountId.Value);
        write.AddParameter("@kind", (int)kind);
        write.AddParameter("@value", value);
        write.AddParameter("@seen", timestamp);
        if (count is null || count is DBNull)
        {
            write.CommandText =
                """
                INSERT INTO vega_account_identity_evidence (
                    account_id, kind, value, first_seen_utc, last_seen_utc, observation_count)
                VALUES (@accountId, @kind, @value, @seen, @seen, 1);
                """;
        }
        else
        {
            write.CommandText =
                """
                UPDATE vega_account_identity_evidence
                SET last_seen_utc = @seen,
                    observation_count = observation_count + 1
                WHERE account_id = @accountId AND kind = @kind AND value = @value;
                """;
        }

        await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AccountIdentityEvidenceSnapshot ReadEvidence(DbDataReader reader) => new(
        new AccountId(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)),
        reader.GetString(1),
        (AccountIdentityEvidenceKind)Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
        reader.GetString(3),
        DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Convert.ToInt64(reader.GetValue(6), CultureInfo.InvariantCulture));

    private static string? NormalizeUuid(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? NormalizeIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return IPAddress.TryParse(value.Trim(), out IPAddress? address)
            ? address.ToString()
            : null;
    }

    private readonly record struct IdentitySignal(AccountIdentityEvidenceKind Kind, string Value);
}
