using System.Data;
using System.Data.Common;
using System.Globalization;
using Vega.Contracts.Regions;
using Vega.Contracts.Worlds;
using Vega.Persistence.Sqlite;

namespace Vega.Persistence.Relational;

/// <summary>Same validated insert-only semantics for all SQL destination providers.</summary>
internal sealed class RelationalOfflineImport(
    Func<CancellationToken, ValueTask<DbConnection>> openAsync, Action ensureReady,
    Action invalidatePrimaryRegions)
{
    public async ValueTask<int> ImportAccountsAsync(IReadOnlyList<LegacyAccountImport> imports, CancellationToken token)
    {
        ensureReady();
        if (imports.Any(row => row.Id <= 0 || string.IsNullOrWhiteSpace(row.Name) || row.Name.Length > 255 ||
                row.Name != row.Name.Trim().ToLowerInvariant() || string.IsNullOrWhiteSpace(row.Group) ||
                row.Group != row.Group.Trim().ToLowerInvariant() || !LegacyPasswordHash.IsSupported(row.PasswordHash)) ||
            imports.Select(row => row.Id).Distinct().Count() != imports.Count ||
            imports.Select(row => row.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != imports.Count)
            throw new InvalidDataException("Imported account rows contain invalid or duplicate account identities or unsupported password encodings.");

        await using DbConnection connection = await openAsync(token).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false);
        if (await CountAsync(connection, transaction, "SELECT COUNT(*) FROM vega_accounts;", token).ConfigureAwait(false) != 0)
            throw new InvalidDataException("Account import requires an empty Vega account table; no accounts were overwritten.");
        var groups = await ReadNamesAsync(connection, transaction, "SELECT name FROM vega_groups;", token).ConfigureAwait(false);
        if (imports.Any(row => !groups.ContainsKey(row.Group)))
            throw new InvalidDataException("A mapped Vega group does not exist. Create and review all target groups before importing.");

        foreach (var row in imports)
        {
            await InsertAsync(connection, transaction, """
                INSERT INTO vega_accounts (id, name, password_hash, group_name, culture, last_seen_utc, revision)
                VALUES (@id, @name, @hash, @group, NULL, @seen, 1);
                """, token, ("@id", row.Id), ("@name", row.Name), ("@hash", row.PasswordHash),
                ("@group", groups[row.Group]), ("@seen", row.LastSeen?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)))
                .ConfigureAwait(false);
            if (row.RegisteredUtc is not null || row.LastKnownIp is not null || row.LastKnownUuid is not null)
                await InsertAsync(connection, transaction,
                    "INSERT INTO vega_account_metadata (account_id, registered_utc, last_ip, last_uuid) VALUES (@id, @registered, @ip, @uuid);",
                    token, ("@id", row.Id), ("@registered", row.RegisteredUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                    ("@ip", System.Net.IPAddress.TryParse(row.LastKnownIp, out var ip) ? ip.ToString() : null),
                    ("@uuid", row.LastKnownUuid is { Length: > 0 and <= 255 } && !row.LastKnownUuid.Any(char.IsControl) ? row.LastKnownUuid : null)).ConfigureAwait(false);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return imports.Count;
    }

    public async ValueTask<int> ImportPrimaryRegionsAsync(IReadOnlyList<RegionSnapshot> regions, CancellationToken token)
    {
        ensureReady();
        if (regions.Any(region => region.WorldId != WorldId.Primary || region.Id <= 0 || string.IsNullOrWhiteSpace(region.Name) ||
                region.Name.Length > 255 || region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
                (long)region.X + region.Width > int.MaxValue || (long)region.Y + region.Height > int.MaxValue) ||
            regions.Select(region => region.Id).Distinct().Count() != regions.Count ||
            regions.Select(region => region.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != regions.Count)
            throw new InvalidDataException("Imported regions have invalid or duplicate identities or bounds.");
        await using DbConnection connection = await openAsync(token).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false);
        if (await CountAsync(connection, transaction, "SELECT COUNT(*) FROM vega_regions WHERE world_id = 'primary';", token).ConfigureAwait(false) != 0)
            throw new InvalidDataException("Region import requires an empty primary-world region set; no regions were overwritten.");
        var accounts = await ReadNamesAsync(connection, transaction, "SELECT name FROM vega_accounts;", token).ConfigureAwait(false);
        var groups = await ReadNamesAsync(connection, transaction, "SELECT name FROM vega_groups;", token).ConfigureAwait(false);
        if (regions.Any(region => (region.Owner is not null && !accounts.ContainsKey(region.Owner)) ||
                region.Members.Any(member => !accounts.ContainsKey(member)) || region.Groups.Any(group => !groups.ContainsKey(group))))
            throw new InvalidDataException("A region refers to an unknown Vega account or group.");
        // Region IDs are global across worlds; never overwrite a sandbox's region.
        var existingIds = new HashSet<int>();
        await using (DbCommand checkIds = CreateCommand(connection, transaction, "SELECT id FROM vega_regions;"))
        await using (DbDataReader reader = await checkIds.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                existingIds.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
        if (regions.Any(region => existingIds.Contains(region.Id)))
            throw new InvalidDataException("An imported region identifier is already used by another world.");

        foreach (var region in regions)
        {
            await InsertAsync(connection, transaction, """
                INSERT INTO vega_regions (id, world_id, name, x, y, width, height, priority, owner, revision)
                VALUES (@id, 'primary', @name, @x, @y, @width, @height, @priority, @owner, 1);
                """, token, ("@id", region.Id), ("@name", region.Name), ("@x", region.X), ("@y", region.Y),
                ("@width", region.Width), ("@height", region.Height), ("@priority", region.Priority),
                ("@owner", region.Owner is null ? null : accounts[region.Owner])).ConfigureAwait(false);
            foreach (string member in region.Members)
                await InsertAsync(connection, transaction, "INSERT INTO vega_region_members (region_id, account_name) VALUES (@id, @value);",
                    token, ("@id", region.Id), ("@value", accounts[member])).ConfigureAwait(false);
            foreach (string group in region.Groups)
                await InsertAsync(connection, transaction, "INSERT INTO vega_region_groups (region_id, group_name) VALUES (@id, @value);",
                    token, ("@id", region.Id), ("@value", groups[group])).ConfigureAwait(false);
            foreach (var flag in region.Flags)
                await InsertAsync(connection, transaction, "INSERT INTO vega_region_flags (region_id, flag, value) VALUES (@id, @flag, @value);",
                    token, ("@id", region.Id), ("@flag", flag.Key), ("@value", flag.Value ? 1 : 0)).ConfigureAwait(false);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        invalidatePrimaryRegions();
        return regions.Count;
    }

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction transaction, string sql)
    {
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static async ValueTask<long> CountAsync(DbConnection connection, DbTransaction transaction,
        string sql, CancellationToken token)
    {
        await using DbCommand command = CreateCommand(connection, transaction, sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async ValueTask<Dictionary<string, string>> ReadNamesAsync(DbConnection connection,
        DbTransaction transaction, string sql, CancellationToken token)
    {
        await using DbCommand command = CreateCommand(connection, transaction, sql);
        await using DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            string name = reader.GetString(0);
            if (!names.TryAdd(name, name)) throw new InvalidDataException("Destination contains ambiguous case-insensitive names.");
        }
        return names;
    }

    private static async ValueTask InsertAsync(DbConnection connection, DbTransaction transaction,
        string sql, CancellationToken token, params (string Name, object? Value)[] parameters)
    {
        await using DbCommand command = CreateCommand(connection, transaction, sql);
        foreach (var (name, value) in parameters) command.AddParameter(name, value);
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new InvalidDataException("An imported row was not inserted.");
    }
}
