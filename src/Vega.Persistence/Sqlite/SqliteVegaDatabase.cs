using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Runtime;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Moderation;
using Vega.Contracts.Regions;
using Vega.Contracts.Worlds;
using Vega.Persistence.Relational;

namespace Vega.Persistence.Sqlite;

public sealed class SqliteVegaDatabase : IVegaDatabase
{
    private readonly SqliteConnectionFactory connections;
    private readonly SemaphoreSlim initialization = new(1, 1);
    private readonly string databaseDirectory;
    private int schemaVersion;

    private SqliteVegaDatabase(
        SqliteVegaDatabaseOptions options,
        SqliteConnectionFactory connections)
    {
        this.connections = connections;
        DatabasePath = options.DatabasePath;
        databaseDirectory = Path.GetDirectoryName(options.DatabasePath) ??
            throw new InvalidOperationException(
                $"Cannot determine the database directory for '{options.DatabasePath}'.");
        GlobalBans = new SqliteGlobalBanReplica(connections, EnsureReady);
        var identities = new RelationalIdentityService(
            connections.Open,
            async cancellationToken => await connections.OpenAsync(cancellationToken).ConfigureAwait(false),
            EnsureReady);
        Accounts = identities;
        Authorization = identities;
        RoleLeases = new RelationalRoleLeaseService(
            connections.Open,
            async cancellationToken => await connections.OpenAsync(cancellationToken).ConfigureAwait(false),
            EnsureReady);
        Mutes = new RelationalMuteService(
            connections.Open,
            async cancellationToken => await connections.OpenAsync(cancellationToken).ConfigureAwait(false),
            EnsureReady);
        Regions = new RelationalRegionService(
            connections.Open,
            async cancellationToken => await connections.OpenAsync(cancellationToken).ConfigureAwait(false),
            EnsureReady);
    }

    public string Name => "SQLite persistence";

    public string Provider => "sqlite";

    public string DatabasePath { get; }

    public string BackupFileName => "vega.sqlite3";

    public int SchemaVersion => Volatile.Read(ref schemaVersion);

    public bool IsReady => SchemaVersion > 0;

    public IGlobalBanReplica GlobalBans { get; }

    public IAccountService Accounts { get; }

    public IAuthorizationService Authorization { get; }

    public IRoleLeaseService RoleLeases { get; }

    public IMuteService Mutes { get; }

    public IRegionService Regions { get; }

    /// <summary>Offline, transactional insert-only ban migration; existing and revoked IDs are preserved.</summary>
    public ValueTask<int> ImportBansAsync(IReadOnlyList<GlobalBanCreated> bans, CancellationToken token = default) =>
        ((SqliteGlobalBanReplica)GlobalBans).ImportNewAsync(bans, token);

    /// <summary>Import into an empty account table, preserving source IDs and using explicitly mapped existing Vega groups.</summary>
    public async ValueTask<int> ImportAccountsAsync(IReadOnlyList<LegacyAccountImport> imports, CancellationToken token = default)
    {
        EnsureReady();
        if (imports.Any(row => row.Id <= 0 || string.IsNullOrWhiteSpace(row.Name) || row.Name.Length > 255 ||
                row.Name != row.Name.Trim().ToLowerInvariant() || string.IsNullOrWhiteSpace(row.Group) ||
                row.Group != row.Group.Trim().ToLowerInvariant() || !LegacyPasswordHash.IsSupported(row.PasswordHash)) ||
            imports.Select(row => row.Id).Distinct().Count() != imports.Count ||
            imports.Select(row => row.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != imports.Count)
            throw new InvalidDataException("Imported account rows contain invalid or duplicate account identities or unsupported password encodings.");

        await using var connection = await connections.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM vega_accounts;";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 0)
            throw new InvalidDataException("Account import requires an empty Vega account table; no accounts were overwritten.");
        command.CommandText = "SELECT name FROM vega_groups;";
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false)) groups.Add(reader.GetString(0));
        if (imports.Any(row => !groups.Contains(row.Group)))
            throw new InvalidDataException("A mapped Vega group does not exist. Create and review all target groups before importing.");

        command.CommandText = """
            INSERT INTO vega_accounts (id, name, password_hash, group_name, culture, last_seen_utc, revision)
            VALUES ($id, $name, $hash, $group, NULL, $seen, 1);
            """;
        var id = command.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
        var name = command.Parameters.Add("$name", Microsoft.Data.Sqlite.SqliteType.Text);
        var hash = command.Parameters.Add("$hash", Microsoft.Data.Sqlite.SqliteType.Text);
        var group = command.Parameters.Add("$group", Microsoft.Data.Sqlite.SqliteType.Text);
        var seen = command.Parameters.Add("$seen", Microsoft.Data.Sqlite.SqliteType.Text);
        foreach (var row in imports)
        {
            id.Value = row.Id;
            name.Value = row.Name;
            hash.Value = row.PasswordHash;
            group.Value = row.Group;
            seen.Value = row.LastSeen is { } last ? last.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) : DBNull.Value;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return imports.Count;
    }

    /// <summary>Offline batch import of already mapped region data into an empty primary-world region set.</summary>
    public async ValueTask<int> ImportPrimaryRegionsAsync(IReadOnlyList<RegionSnapshot> regions, CancellationToken token = default)
    {
        EnsureReady();
        if (regions.Any(region => region.WorldId != WorldId.Primary || region.Id <= 0 || string.IsNullOrWhiteSpace(region.Name) ||
                region.Name.Length > 255 || region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
                (long)region.X + region.Width > int.MaxValue || (long)region.Y + region.Height > int.MaxValue) ||
            regions.Select(region => region.Id).Distinct().Count() != regions.Count ||
            regions.Select(region => region.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != regions.Count)
            throw new InvalidDataException("Imported regions have invalid or duplicate identities or bounds.");
        await using var connection = await connections.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "SELECT COUNT(*) FROM vega_regions WHERE world_id = 'primary';";
        if (Convert.ToInt64(await check.ExecuteScalarAsync(token).ConfigureAwait(false)) != 0)
            throw new InvalidDataException("Region import requires an empty primary-world region set; no regions were overwritten.");
        var accountNames = await ReadNames("SELECT name FROM vega_accounts;");
        var groups = await ReadNames("SELECT name FROM vega_groups;");
        if (regions.Any(region => (region.Owner is not null && !accountNames.Contains(region.Owner)) ||
                region.Members.Any(member => !accountNames.Contains(member)) || region.Groups.Any(group => !groups.Contains(group))))
            throw new InvalidDataException("A region refers to an unknown Vega account or group.");

        foreach (var region in regions)
        {
            await Insert("""
                INSERT INTO vega_regions (id, world_id, name, x, y, width, height, priority, owner, revision)
                VALUES ($id, 'primary', $name, $x, $y, $width, $height, $priority, $owner, 1);
                """, ("$id", region.Id), ("$name", region.Name), ("$x", region.X), ("$y", region.Y),
                ("$width", region.Width), ("$height", region.Height), ("$priority", region.Priority), ("$owner", region.Owner));
            foreach (string member in region.Members)
                await Insert("INSERT INTO vega_region_members (region_id, account_name) VALUES ($id, $value);", ("$id", region.Id), ("$value", member));
            foreach (string group in region.Groups)
                await Insert("INSERT INTO vega_region_groups (region_id, group_name) VALUES ($id, $value);", ("$id", region.Id), ("$value", group));
            foreach (var flag in region.Flags)
                await Insert("INSERT INTO vega_region_flags (region_id, flag, value) VALUES ($id, $flag, $value);", ("$id", region.Id), ("$flag", flag.Key), ("$value", flag.Value ? 1 : 0));
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return regions.Count;

        async ValueTask<HashSet<string>> ReadNames(string sql)
        {
            check.CommandText = sql;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await check.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) names.Add(reader.GetString(0));
            return names;
        }

        async ValueTask Insert(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    public static SqliteVegaDatabase Create(SqliteVegaDatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var connections = new SqliteConnectionFactory(options);
        return new SqliteVegaDatabase(options, connections);
    }

    public static async ValueTask<SqliteVegaDatabase> OpenAsync(
        SqliteVegaDatabaseOptions options,
        CancellationToken cancellationToken = default)
    {
        SqliteVegaDatabase database = Create(options);
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return database;
    }

    public async ValueTask WriteBackupAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination)) throw new IOException($"Backup destination '{destination}' already exists.");
        await using Microsoft.Data.Sqlite.SqliteConnection source = await connections.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = destination,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        await using var target = new Microsoft.Data.Sqlite.SqliteConnection(builder.ConnectionString);
        await target.OpenAsync(cancellationToken).ConfigureAwait(false);
        await Task.Run(() => source.BackupDatabase(target), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsReady)
        {
            return;
        }

        await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsReady)
            {
                return;
            }

            Directory.CreateDirectory(databaseDirectory);
            int version = await SqliteSchemaInitializer
                .InitializeAsync(connections, cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref schemaVersion, version);
            Authorization.SnapshotGroups();
            RoleLeases.SnapshotActive(DateTimeOffset.UtcNow);
            Mutes.SnapshotActive(DateTimeOffset.UtcNow);
            Regions.SnapshotRegions(WorldId.Primary);
        }
        finally
        {
            initialization.Release();
        }
    }

    private void EnsureReady()
    {
        if (!IsReady)
        {
            throw new InvalidOperationException(
                "SQLite persistence has not completed runtime initialization.");
        }
    }
}
