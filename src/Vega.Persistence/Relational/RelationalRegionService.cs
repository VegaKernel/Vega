using System.Collections.Frozen;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using Vega.Contracts.Regions;
using Vega.Contracts.Worlds;

namespace Vega.Persistence.Relational;

internal sealed class RelationalRegionService(
    Func<DbConnection> open,
    Func<CancellationToken, ValueTask<DbConnection>> openAsync,
    Action ensureReady) : IRegionService, IRegionChangeObservations
{
    private readonly SemaphoreSlim mutations = new(1, 1);
    private readonly object cacheSync = new();
    private readonly Dictionary<WorldId, (IReadOnlyList<RegionSnapshot> Regions, long LoadedAt)> regionCache = [];
    public event Action<RegionChangedEvent>? Changed;

    public IReadOnlyList<RegionSnapshot> SnapshotRegions() => SnapshotRegions(WorldId.Primary);

    public RegionSnapshot? FindTopRegion(int tileX, int tileY) =>
        FindTopRegion(WorldId.Primary, tileX, tileY);

    public IReadOnlyList<RegionSnapshot> SnapshotRegions(WorldId worldId)
    {
        ValidateWorld(worldId);
        ensureReady();
        lock (cacheSync)
        {
            if (!regionCache.TryGetValue(worldId, out var snapshot) || Environment.TickCount64 - snapshot.LoadedAt >= 1000)
            {
                using DbConnection connection = open();
                snapshot = (ReadRegions(connection, null, worldId), Environment.TickCount64);
                regionCache[worldId] = snapshot;
            }

            return snapshot.Regions;
        }
    }

    public RegionSnapshot? FindTopRegion(WorldId worldId, int tileX, int tileY) =>
        SnapshotRegions(worldId)
            .Where(region => region.Contains(tileX, tileY))
            .OrderByDescending(static region => region.Priority)
            .ThenBy(static region => checked(region.Width * (long)region.Height))
            .ThenBy(static region => region.Id)
            .FirstOrDefault();

    public async ValueTask<RegionMutationResult> CreateAsync(
        WorldId worldId,
        string name,
        int x,
        int y,
        int width,
        int height,
        int priority,
        string? owner,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ValidateWorld(worldId);
        string normalizedName = Normalize(name, nameof(name));
        ValidateBounds(x, y, width, height);
        string? normalizedOwner = string.IsNullOrWhiteSpace(owner) ? null : Normalize(owner, nameof(owner));
        ValidateActor(actor);
        ensureReady();
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        RegionChangedEvent? committed = null;
        try
        {
            await using DbConnection connection = await openAsync(cancellationToken).ConfigureAwait(false);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (FindRegion(connection, transaction, worldId, normalizedName) is not null)
                {
                    return new RegionMutationResult(false, "region.exists");
                }

                int id = await AllocateIdAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                await using (DbCommand command = CreateCommand(connection, transaction,
                    "INSERT INTO vega_regions " +
                    "(id, world_id, name, x, y, width, height, priority, owner, revision) " +
                    "VALUES (@id, @world, @name, @x, @y, @width, @height, @priority, @owner, 1);"))
                {
                    command.AddParameter("@id", id);
                    command.AddParameter("@world", worldId.Value);
                    command.AddParameter("@name", normalizedName);
                    command.AddParameter("@x", x);
                    command.AddParameter("@y", y);
                    command.AddParameter("@width", width);
                    command.AddParameter("@height", height);
                    command.AddParameter("@priority", priority);
                    command.AddParameter("@owner", normalizedOwner);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                RegionSnapshot after = new(id, normalizedName, x, y, width, height, normalizedOwner)
                {
                    WorldId = worldId,
                    Priority = priority,
                    Revision = 1
                };
                await WriteAuditAsync(connection, transaction, actor, "region.create", Target(worldId, normalizedName),
                        0, 1, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                Invalidate(worldId);
                committed = new(worldId, RegionChangeKind.Created, null, after);
                return new RegionMutationResult(true, "region.created", After: after);
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
            if (committed is { } change) NotifyChanged(change);
        }
    }

    public ValueTask<RegionMutationResult> DeleteAsync(
        WorldId worldId,
        string name,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default) =>
        DeleteCoreAsync(worldId, name, expectedRevision, actor, cancellationToken);

    public ValueTask<RegionMutationResult> DeleteAsync(RegionSnapshot expected, string actor, CancellationToken cancellationToken = default) =>
        DeleteCoreAsync(expected.WorldId, expected.Name, expected.Revision, actor, cancellationToken, expected.Id);

    private ValueTask<RegionMutationResult> DeleteCoreAsync(WorldId worldId, string name, long expectedRevision,
        string actor, CancellationToken cancellationToken, int? expectedId = null) =>
        MutateAsync(
            worldId,
            name,
            expectedRevision,
            actor,
            "region.delete",
            static (connection, transaction, before, cancellationToken) =>
                ExecuteMutationAsync(
                    connection,
                    transaction,
                    "DELETE FROM vega_regions " +
                    "WHERE world_id = @world AND id = @id AND name = @name AND revision = @revision;",
                    before,
                    cancellationToken),
            delete: true,
            cancellationToken, expectedId: expectedId);

    public ValueTask<RegionMutationResult> UpdateAsync(
        WorldId worldId,
        string name,
        RegionUpdate update,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default) =>
        UpdateCoreAsync(worldId, name, update, expectedRevision, actor, cancellationToken);

    public ValueTask<RegionMutationResult> UpdateAsync(RegionSnapshot expected, RegionUpdate update, string actor, CancellationToken cancellationToken = default) =>
        UpdateCoreAsync(expected.WorldId, expected.Name, update, expected.Revision, actor, cancellationToken, expected.Id);

    private ValueTask<RegionMutationResult> UpdateCoreAsync(WorldId worldId, string name, RegionUpdate update,
        long expectedRevision, string actor, CancellationToken cancellationToken, int? expectedId = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        return MutateAsync(
            worldId,
            name,
            expectedRevision,
            actor,
            "region.update",
            async (connection, transaction, before, token) =>
            {
                int x = update.X ?? before.X;
                int y = update.Y ?? before.Y;
                int width = update.Width ?? before.Width;
                int height = update.Height ?? before.Height;
                ValidateBounds(x, y, width, height);
                string? owner = update.ClearOwner
                    ? null
                    : update.Owner is null ? before.Owner : Normalize(update.Owner, nameof(update.Owner));
                await using DbCommand command = CreateCommand(connection, transaction,
                    "UPDATE vega_regions SET x = @x, y = @y, width = @width, height = @height, " +
                    "priority = @priority, owner = @owner, revision = revision + 1 " +
                    "WHERE world_id = @world AND id = @id AND name = @name AND revision = @revision;");
                AddIdentityParameters(command, before);
                command.AddParameter("@x", x);
                command.AddParameter("@y", y);
                command.AddParameter("@width", width);
                command.AddParameter("@height", height);
                command.AddParameter("@priority", update.Priority ?? before.Priority);
                command.AddParameter("@owner", owner);
                int changed = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                if (changed == 1 && update.Flag is not null && update.FlagValue is bool value)
                {
                    await SetFlagAsync(connection, transaction, before.Id, Normalize(update.Flag, nameof(update.Flag)), value, token)
                        .ConfigureAwait(false);
                }

                return changed;
            },
            cancellationToken: cancellationToken, expectedId: expectedId);
    }

    public ValueTask<RegionMutationResult> SetAccessAsync(
        WorldId worldId,
        string name,
        RegionAccessKind kind,
        string subject,
        bool enabled,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default) =>
        SetAccessCoreAsync(worldId, name, kind, subject, enabled, expectedRevision, actor, cancellationToken);

    public ValueTask<RegionMutationResult> SetAccessAsync(RegionSnapshot expected, RegionAccessKind kind, string subject, bool enabled, string actor, CancellationToken cancellationToken = default) =>
        SetAccessCoreAsync(expected.WorldId, expected.Name, kind, subject, enabled, expected.Revision, actor, cancellationToken, expected.Id);

    private ValueTask<RegionMutationResult> SetAccessCoreAsync(WorldId worldId, string name, RegionAccessKind kind, string subject, bool enabled,
        long expectedRevision, string actor, CancellationToken cancellationToken, int? expectedId = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        string normalizedSubject = Normalize(subject, nameof(subject));
        return MutateAsync(
            worldId,
            name,
            expectedRevision,
            actor,
            kind == RegionAccessKind.Member ? "region.member" : "region.group",
            async (connection, transaction, before, token) =>
            {
                string table = kind == RegionAccessKind.Member ? "vega_region_members" : "vega_region_groups";
                string column = kind == RegionAccessKind.Member ? "account_name" : "group_name";
                await using (DbCommand delete = CreateCommand(connection, transaction,
                    $"DELETE FROM {table} WHERE region_id = @id AND {column} = @subject;"))
                {
                    delete.AddParameter("@id", before.Id);
                    delete.AddParameter("@subject", normalizedSubject);
                    await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                if (enabled)
                {
                    await using DbCommand insert = CreateCommand(connection, transaction,
                        $"INSERT INTO {table} (region_id, {column}) VALUES (@id, @subject);");
                    insert.AddParameter("@id", before.Id);
                    insert.AddParameter("@subject", normalizedSubject);
                    await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                return await IncrementRevisionAsync(connection, transaction, before, token).ConfigureAwait(false);
            },
            cancellationToken: cancellationToken, expectedId: expectedId);
    }

    public ValueTask<RegionMutationResult> RenameAsync(WorldId worldId, string name, string newName, long expectedRevision,
        string actor, CancellationToken cancellationToken = default) =>
        RenameCoreAsync(worldId, name, newName, expectedRevision, actor, cancellationToken);

    public ValueTask<RegionMutationResult> RenameAsync(RegionSnapshot expected, string newName, string actor, CancellationToken cancellationToken = default) =>
        RenameCoreAsync(expected.WorldId, expected.Name, newName, expected.Revision, actor, cancellationToken, expected.Id);

    private ValueTask<RegionMutationResult> RenameCoreAsync(WorldId worldId, string name, string newName, long expectedRevision,
        string actor, CancellationToken cancellationToken, int? expectedId = null)
    {
        string normalizedNewName = Normalize(newName, nameof(newName));
        return MutateAsync(worldId, name, expectedRevision, actor, "region.rename",
            async (connection, transaction, before, token) =>
            {
                await using DbCommand command = CreateCommand(connection, transaction,
                    "UPDATE vega_regions SET name = @new_name, revision = revision + 1 " +
                    "WHERE world_id = @world AND id = @id AND name = @name AND revision = @revision;");
                AddIdentityParameters(command, before);
                command.AddParameter("@new_name", normalizedNewName);
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }, cancellationToken: cancellationToken, renameTo: normalizedNewName, expectedId: expectedId);
    }

    private async ValueTask<RegionMutationResult> MutateAsync(
        WorldId worldId,
        string name,
        long expectedRevision,
        string actor,
        string operation,
        Func<DbConnection, DbTransaction, RegionSnapshot, CancellationToken, ValueTask<int>> mutate,
        bool delete = false,
        CancellationToken cancellationToken = default,
        string? renameTo = null,
        int? expectedId = null)
    {
        ValidateWorld(worldId);
        string normalizedName = Normalize(name, nameof(name));
        ValidateRevision(expectedRevision);
        ValidateActor(actor);
        ensureReady();
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        RegionChangedEvent? committed = null;
        try
        {
            await using DbConnection connection = await openAsync(cancellationToken).ConfigureAwait(false);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                RegionSnapshot? before = FindRegion(connection, transaction, worldId, normalizedName);
                if (before is null)
                {
                    return new RegionMutationResult(false, "region.not-found");
                }

                if (expectedId is int id && before.Id != id)
                {
                    return new RegionMutationResult(false, "region.identity-conflict", before);
                }

                if (before.Revision != expectedRevision)
                {
                    return new RegionMutationResult(false, "region.revision-conflict", before);
                }

                if (renameTo is not null)
                {
                    if (normalizedName.Equals(renameTo, StringComparison.OrdinalIgnoreCase))
                        return new RegionMutationResult(false, "region.same-name", before);
                    if (FindRegion(connection, transaction, worldId, renameTo) is not null)
                        return new RegionMutationResult(false, "region.exists", before);
                }

                if (await mutate(connection, transaction, before, cancellationToken).ConfigureAwait(false) != 1)
                {
                    return new RegionMutationResult(false, "region.revision-conflict", before);
                }

                RegionSnapshot? after = delete ? null : FindRegion(connection, transaction, worldId, renameTo ?? normalizedName);
                string target = renameTo is null ? Target(worldId, normalizedName) : $"{Target(worldId, normalizedName)} -> {renameTo}";
                await WriteAuditAsync(connection, transaction, actor, operation, target,
                        before.Revision, after?.Revision ?? checked(before.Revision + 1), cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                Invalidate(worldId);
                committed = new(worldId, operation switch
                {
                    "region.delete" => RegionChangeKind.Deleted,
                    "region.rename" => RegionChangeKind.Renamed,
                    "region.member" or "region.group" => RegionChangeKind.AccessChanged,
                    _ => RegionChangeKind.Updated
                }, before, after);
                return new RegionMutationResult(true, operation, before, after);
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
            if (committed is { } change) NotifyChanged(change);
        }
    }

    private void NotifyChanged(RegionChangedEvent change)
    {
        // Commit is already durable; an observer cannot roll back a mutation or hold its lock.
        if (Changed is not { } handlers) return;
        change = change with { Before = Freeze(change.Before), After = Freeze(change.After) };
        foreach (Action<RegionChangedEvent> handler in handlers.GetInvocationList())
        {
            try { handler(change); }
            catch (Exception) { System.Diagnostics.Trace.TraceWarning("Region change observer failed."); }
        }
    }

    private static RegionSnapshot? Freeze(RegionSnapshot? region) => region is null ? null : region with
    {
        Members = region.Members.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
        Groups = region.Groups.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
        Flags = region.Flags.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)
    };

    private static IReadOnlyList<RegionSnapshot> ReadRegions(
        DbConnection connection,
        DbTransaction? transaction,
        WorldId worldId)
    {
        using DbCommand command = CreateCommand(connection, transaction,
            "SELECT id, name, x, y, width, height, owner, priority, revision " +
            "FROM vega_regions WHERE world_id = @world ORDER BY priority DESC, name;");
        command.AddParameter("@world", worldId.Value);
        using DbDataReader reader = command.ExecuteReader();
        var regions = new Dictionary<int, RegionSnapshot>();
        while (reader.Read())
        {
            int id = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            regions[id] = new RegionSnapshot(
                id,
                reader.GetString(1),
                Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : reader.GetString(6))
            {
                WorldId = worldId,
                Priority = Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture),
                Revision = Convert.ToInt64(reader.GetValue(8), CultureInfo.InvariantCulture),
                Members = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                Groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                Flags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            };
        }

        reader.Close();
        ReadSubjects(connection, transaction, regions, "vega_region_members", "account_name", static region => region.Members);
        ReadSubjects(connection, transaction, regions, "vega_region_groups", "group_name", static region => region.Groups);
        using DbCommand flags = CreateCommand(connection, transaction,
            "SELECT region_id, flag, value FROM vega_region_flags ORDER BY region_id, flag;");
        using DbDataReader flagReader = flags.ExecuteReader();
        while (flagReader.Read())
        {
            int id = Convert.ToInt32(flagReader.GetValue(0), CultureInfo.InvariantCulture);
            if (regions.TryGetValue(id, out RegionSnapshot? region))
            {
                ((Dictionary<string, bool>)region.Flags)[flagReader.GetString(1)] =
                    Convert.ToInt32(flagReader.GetValue(2), CultureInfo.InvariantCulture) != 0;
            }
        }

        return Array.AsReadOnly(regions.Values
            .Select(static region => region with
            {
                Members = region.Members.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
                Groups = region.Groups.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
                Flags = region.Flags.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)
            })
            .ToArray());
    }

    private static void ReadSubjects(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyDictionary<int, RegionSnapshot> regions,
        string table,
        string column,
        Func<RegionSnapshot, IReadOnlySet<string>> select)
    {
        using DbCommand command = CreateCommand(connection, transaction,
            $"SELECT region_id, {column} FROM {table} ORDER BY region_id, {column};");
        using DbDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            int id = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (regions.TryGetValue(id, out RegionSnapshot? region))
            {
                ((HashSet<string>)select(region)).Add(reader.GetString(1));
            }
        }
    }

    private static RegionSnapshot? FindRegion(
        DbConnection connection,
        DbTransaction transaction,
        WorldId worldId,
        string name) => ReadRegions(connection, transaction, worldId)
        .FirstOrDefault(region => region.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static async ValueTask<int> ExecuteMutationAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        RegionSnapshot before,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = CreateCommand(connection, transaction, sql);
        AddIdentityParameters(command, before);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ValueTask<int> IncrementRevisionAsync(
        DbConnection connection,
        DbTransaction transaction,
        RegionSnapshot before,
        CancellationToken cancellationToken) =>
        ExecuteMutationAsync(
            connection,
            transaction,
            "UPDATE vega_regions SET revision = revision + 1 " +
            "WHERE world_id = @world AND id = @id AND name = @name AND revision = @revision;",
            before,
            cancellationToken);

    private static async ValueTask SetFlagAsync(
        DbConnection connection,
        DbTransaction transaction,
        int regionId,
        string flag,
        bool value,
        CancellationToken cancellationToken)
    {
        await using (DbCommand delete = CreateCommand(connection, transaction,
            "DELETE FROM vega_region_flags WHERE region_id = @id AND flag = @flag;"))
        {
            delete.AddParameter("@id", regionId);
            delete.AddParameter("@flag", flag);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using DbCommand insert = CreateCommand(connection, transaction,
            "INSERT INTO vega_region_flags (region_id, flag, value) VALUES (@id, @flag, @value);");
        insert.AddParameter("@id", regionId);
        insert.AddParameter("@flag", flag);
        insert.AddParameter("@value", value ? 1 : 0);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> AllocateIdAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            int candidate = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            await using DbCommand command = CreateCommand(connection, transaction,
                "SELECT COUNT(*) FROM vega_regions WHERE id = @id;");
            command.AddParameter("@id", candidate);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture) == 0)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Unable to allocate a stable region identifier.");
    }

    private static async ValueTask WriteAuditAsync(
        DbConnection connection,
        DbTransaction transaction,
        string actor,
        string operation,
        string target,
        long before,
        long after,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = CreateCommand(connection, transaction,
            "INSERT INTO vega_administration_audit " +
            "(id, occurred_at_utc, actor, operation, target, before_revision, after_revision, outcome) " +
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

    private static void AddIdentityParameters(DbCommand command, RegionSnapshot region)
    {
        command.AddParameter("@world", region.WorldId.Value);
        command.AddParameter("@id", region.Id);
        command.AddParameter("@name", region.Name);
        command.AddParameter("@revision", region.Revision);
    }

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
    {
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static string Normalize(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        string normalized = value.Trim().ToLowerInvariant();
        return normalized.Length <= 255
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName);
    }

    private static void ValidateWorld(WorldId worldId)
    {
        if (string.IsNullOrWhiteSpace(worldId.Value) || worldId.Value.Length > 255)
        {
            throw new ArgumentException("World identity is invalid.", nameof(worldId));
        }
    }

    private static void ValidateBounds(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 ||
            (long)x + width > int.MaxValue || (long)y + height > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Region bounds are invalid.");
        }
    }

    private static void ValidateRevision(long revision)
    {
        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }
    }

    private static void ValidateActor(string actor) => ArgumentException.ThrowIfNullOrWhiteSpace(actor);

    private static string Target(WorldId worldId, string name) => $"{worldId.Value}:{name}";

    internal void Invalidate(WorldId worldId)
    {
        lock (cacheSync)
        {
            regionCache.Remove(worldId);
        }
    }
}
