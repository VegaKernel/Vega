using System.Collections.Frozen;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;

namespace Vega.Persistence.Relational;

internal sealed class RelationalIdentityService(
    Func<DbConnection> open,
    Func<CancellationToken, ValueTask<DbConnection>> openAsync,
    Action ensureReady) : IAccountService, IAuthorizationService
{
    private const int PasswordIterations = 210_000;
    private readonly SemaphoreSlim mutations = new(1, 1);
    private readonly object groupCacheSync = new();
    private IReadOnlyList<GroupSnapshot>? groupCache;

    public IReadOnlyList<AccountSnapshot> SnapshotAccounts()
    {
        ensureReady();
        using DbConnection connection = open();
        using DbCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, group_name, last_seen_utc, culture, revision FROM vega_accounts ORDER BY name;";
        using DbDataReader reader = command.ExecuteReader();
        var accounts = new List<AccountSnapshot>();
        while (reader.Read())
        {
            accounts.Add(ReadAccount(reader));
        }

        return accounts.AsReadOnly();
    }

    public async ValueTask<AccountSnapshot?> FindByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ensureReady();
        await using DbConnection connection = await openAsync(cancellationToken).ConfigureAwait(false);
        return await FindAccountAsync(connection, null, name, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AccountMutationResult> CreateAsync(
        string name,
        string password,
        string groupName,
        string actor,
        CancellationToken cancellationToken = default)
    {
        string normalizedName = NormalizeName(name, nameof(name));
        string normalizedGroup = NormalizeName(groupName, nameof(groupName));
        ValidatePassword(password);
        ValidateActor(actor);
        ensureReady();
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using DbConnection connection = await openAsync(cancellationToken).ConfigureAwait(false);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (await FindAccountAsync(connection, transaction, normalizedName, cancellationToken)
                        .ConfigureAwait(false) is not null)
                {
                    return new AccountMutationResult(false, "account.exists");
                }

                if (!await GroupExistsAsync(connection, transaction, normalizedGroup, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return new AccountMutationResult(false, "group.not-found");
                }

                int id = await AllocateAccountIdAsync(connection, transaction, cancellationToken)
                    .ConfigureAwait(false);
                await using (DbCommand insert = CreateCommand(connection, transaction,
                    "INSERT INTO vega_accounts (id, name, password_hash, group_name, culture, last_seen_utc, revision) " +
                    "VALUES (@id, @name, @password, @group, NULL, NULL, 1);"))
                {
                    insert.AddParameter("@id", id);
                    insert.AddParameter("@name", normalizedName);
                    insert.AddParameter("@password", HashPassword(password));
                    insert.AddParameter("@group", normalizedGroup);
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                AccountSnapshot after = new(new AccountId(id), normalizedName, normalizedGroup, null)
                {
                    Revision = 1
                };
                await WriteAuditAsync(
                    connection,
                    transaction,
                    actor,
                    "account.create",
                    normalizedName,
                    0,
                    1,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new AccountMutationResult(true, "account.created", After: after);
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

    public ValueTask<AccountMutationResult> DeleteAsync(
        string name,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default) =>
        MutateAccountAsync(
            name,
            expectedRevision,
            actor,
            "account.delete",
            static (command, _) => command.CommandText =
                "DELETE FROM vega_accounts WHERE name = @name AND revision = @revision;",
            delete: true,
            cancellationToken: cancellationToken);

    public ValueTask<AccountMutationResult> SetGroupAsync(
        string name,
        string groupName,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default)
    {
        string normalizedGroup = NormalizeName(groupName, nameof(groupName));
        return MutateAccountAsync(
            name,
            expectedRevision,
            actor,
            "account.group",
            (command, transaction) =>
            {
                command.CommandText =
                    "UPDATE vega_accounts SET group_name = @group, revision = revision + 1 " +
                    "WHERE name = @name AND revision = @revision;";
                command.AddParameter("@group", normalizedGroup);
            },
            requiredGroup: normalizedGroup,
            cancellationToken: cancellationToken);
    }

    public ValueTask<AccountMutationResult> SetCultureAsync(
        string name,
        string? culture,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default)
    {
        string? normalizedCulture = string.IsNullOrWhiteSpace(culture)
            ? null
            : CultureInfo.GetCultureInfo(culture.Trim()).Name;
        return MutateAccountAsync(
            name,
            expectedRevision,
            actor,
            "account.culture",
            (command, transaction) =>
            {
                command.CommandText =
                    "UPDATE vega_accounts SET culture = @culture, revision = revision + 1 " +
                    "WHERE name = @name AND revision = @revision;";
                command.AddParameter("@culture", normalizedCulture);
            },
            cancellationToken: cancellationToken);
    }

    public ValueTask<AccountMutationResult> ChangePasswordAsync(
        string name,
        string password,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ValidatePassword(password);
        string passwordHash = HashPassword(password);
        return MutateAccountAsync(
            name,
            expectedRevision,
            actor,
            "account.password",
            (command, _) =>
            {
                command.CommandText =
                    "UPDATE vega_accounts SET password_hash = @password, revision = revision + 1 " +
                    "WHERE name = @name AND revision = @revision;";
                command.AddParameter("@password", passwordHash);
            },
            cancellationToken: cancellationToken);
    }

    public ValueTask<AccountMutationResult> RecordLoginAsync(
        string name,
        DateTimeOffset seenAt,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default) =>
        MutateAccountAsync(
            name,
            expectedRevision,
            actor,
            "account.login",
            (command, _) =>
            {
                command.CommandText =
                    "UPDATE vega_accounts SET last_seen_utc = @seen, revision = revision + 1 " +
                    "WHERE name = @name AND revision = @revision;";
                command.AddParameter("@seen", seenAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            },
            cancellationToken: cancellationToken);

    public async ValueTask<bool> VerifyPasswordAsync(
        string name,
        string password,
        CancellationToken cancellationToken = default)
    {
        ensureReady();
        await using DbConnection connection = await openAsync(cancellationToken).ConfigureAwait(false);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT password_hash FROM vega_accounts WHERE name = @name;";
        string normalized = NormalizeName(name, nameof(name));
        command.AddParameter("@name", normalized);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is not string hash) return false;
        if (!LegacyPasswordHash.IsSupported(hash)) return VerifyPassword(password, hash);
        if (!LegacyPasswordHash.Verify(password, hash)) return false;

        // Same logical credential: replace only the original verified encoding, never a concurrent reset.
        await using DbCommand upgrade = connection.CreateCommand();
        upgrade.CommandText = "UPDATE vega_accounts SET password_hash = @upgraded WHERE name = @name AND password_hash = @original;";
        upgrade.AddParameter("@upgraded", HashPassword(password));
        upgrade.AddParameter("@name", normalized);
        upgrade.AddParameter("@original", hash);
        if (await upgrade.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1) return true;
        // Another login may have upgraded the same credential, or an operator may have reset it.
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string current && VerifyPassword(password, current);
    }

    public IReadOnlyList<GroupSnapshot> SnapshotGroups()
    {
        ensureReady();
        lock (groupCacheSync)
        {
            if (groupCache is null)
            {
                using DbConnection connection = open();
                groupCache = ReadGroups(connection);
            }

            return groupCache;
        }
    }

    public bool TryGetGroup(string groupName, out GroupSnapshot? group)
    {
        group = SnapshotGroups().FirstOrDefault(candidate =>
            candidate.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));
        return group is not null;
    }

    public bool HasPermission(string groupName, string permission)
    {
        if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(permission))
        {
            return false;
        }

        IReadOnlyDictionary<string, GroupSnapshot> groups = SnapshotGroups()
            .ToDictionary(static group => group.Name, StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? current = groupName;
        while (current is not null && visited.Count < 64 && groups.TryGetValue(current, out GroupSnapshot? group))
        {
            if (!visited.Add(current))
            {
                return false;
            }

            if (group.DeniedPermissions.Contains(permission) || group.DeniedPermissions.Contains("*"))
            {
                return false;
            }

            if (group.Permissions.Contains(permission) || group.Permissions.Contains("*"))
            {
                return true;
            }

            current = group.ParentName;
        }

        return false;
    }

    public async ValueTask<GroupMutationResult> CreateGroupAsync(
        string name,
        string? parentName,
        string actor,
        CancellationToken cancellationToken = default)
    {
        string normalized = NormalizeName(name, nameof(name));
        string? parent = string.IsNullOrWhiteSpace(parentName) ? null : NormalizeName(parentName, nameof(parentName));
        ValidateActor(actor);
        ensureReady();
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using DbConnection connection = await openAsync(cancellationToken).ConfigureAwait(false);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (await GroupExistsAsync(connection, transaction, normalized, cancellationToken).ConfigureAwait(false))
                {
                    return new GroupMutationResult(false, "group.exists");
                }

                if (parent is not null &&
                    !await GroupExistsAsync(connection, transaction, parent, cancellationToken).ConfigureAwait(false))
                {
                    return new GroupMutationResult(false, "group.parent-not-found");
                }

                await using (DbCommand insert = CreateCommand(connection, transaction,
                    "INSERT INTO vega_groups (name, parent_name, revision) VALUES (@name, @parent, 1);"))
                {
                    insert.AddParameter("@name", normalized);
                    insert.AddParameter("@parent", parent);
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                GroupSnapshot after = new(normalized, parent, new HashSet<string>()) { Revision = 1 };
                await WriteAuditAsync(connection, transaction, actor, "group.create", normalized, 0, 1, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                InvalidateGroups();
                return new GroupMutationResult(true, "group.created", After: after);
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

    public ValueTask<GroupMutationResult> DeleteGroupAsync(
        string name,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default) =>
        MutateGroupAsync(
            name,
            expectedRevision,
            actor,
            "group.delete",
            static command => command.CommandText =
                "DELETE FROM vega_groups WHERE name = @name AND revision = @revision;",
            delete: true,
            cancellationToken: cancellationToken);

    public ValueTask<GroupMutationResult> SetPresentationAsync(string name,
        GroupChatPresentation presentation, long expectedRevision, string actor,
        CancellationToken cancellationToken = default)
    {
        if (presentation is null || !presentation.IsValid)
            return ValueTask.FromResult(new GroupMutationResult(false, "group.invalid-presentation"));
        return MutateGroupAsync(name, expectedRevision, actor, "group.presentation",
            static command => command.CommandText =
                "UPDATE vega_groups SET revision = revision + 1 WHERE name = @name AND revision = @revision;",
            cancellationToken: cancellationToken,
            afterUpdate: async (connection, transaction, normalized, token) =>
            {
                await using (DbCommand delete = CreateCommand(connection, transaction,
                    "DELETE FROM vega_group_presentation WHERE group_name = @name;"))
                {
                    delete.AddParameter("@name", normalized);
                    await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                if (presentation == GroupChatPresentation.Default) return;
                await using DbCommand insert = CreateCommand(connection, transaction,
                    "INSERT INTO vega_group_presentation (group_name, chat_prefix, chat_suffix, chat_color) VALUES (@name, @prefix, @suffix, @color);");
                insert.AddParameter("@name", normalized);
                insert.AddParameter("@prefix", presentation.Prefix);
                insert.AddParameter("@suffix", presentation.Suffix);
                insert.AddParameter("@color", presentation.Color?.ToUpperInvariant());
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            });
    }

    public ValueTask<GroupMutationResult> SetParentAsync(
        string name,
        string? parentName,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default)
    {
        string? parent = string.IsNullOrWhiteSpace(parentName) ? null : NormalizeName(parentName, nameof(parentName));
        return MutateGroupAsync(
            name,
            expectedRevision,
            actor,
            "group.parent",
            command =>
            {
                command.CommandText =
                    "UPDATE vega_groups SET parent_name = @parent, revision = revision + 1 " +
                    "WHERE name = @name AND revision = @revision;";
                command.AddParameter("@parent", parent);
            },
            parent,
            cancellationToken: cancellationToken);
    }

    public async ValueTask<GroupMutationResult> SetPermissionAsync(
        string name,
        string permission,
        PermissionEffect? effect,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default)
    {
        string normalized = NormalizeName(name, nameof(name));
        string normalizedPermission = NormalizeName(permission, nameof(permission));
        ValidateRevision(expectedRevision);
        ValidateActor(actor);
        ensureReady();
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using DbConnection connection = await openAsync(cancellationToken).ConfigureAwait(false);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                GroupSnapshot? before = await FindGroupAsync(connection, transaction, normalized, cancellationToken)
                    .ConfigureAwait(false);
                if (before is null)
                {
                    return new GroupMutationResult(false, "group.not-found");
                }

                if (before.Revision != expectedRevision)
                {
                    return new GroupMutationResult(false, "group.revision-conflict", before);
                }

                await using (DbCommand delete = CreateCommand(connection, transaction,
                    "DELETE FROM vega_group_permissions WHERE group_name = @name AND permission = @permission;"))
                {
                    delete.AddParameter("@name", normalized);
                    delete.AddParameter("@permission", normalizedPermission);
                    await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                if (effect is not null)
                {
                    await using DbCommand insert = CreateCommand(connection, transaction,
                        "INSERT INTO vega_group_permissions (group_name, permission, effect) " +
                        "VALUES (@name, @permission, @effect);");
                    insert.AddParameter("@name", normalized);
                    insert.AddParameter("@permission", normalizedPermission);
                    insert.AddParameter("@effect", (int)effect.Value);
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (DbCommand revision = CreateCommand(connection, transaction,
                    "UPDATE vega_groups SET revision = revision + 1 WHERE name = @name AND revision = @revision;"))
                {
                    revision.AddParameter("@name", normalized);
                    revision.AddParameter("@revision", expectedRevision);
                    if (await revision.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    {
                        return new GroupMutationResult(false, "group.revision-conflict", before);
                    }
                }

                GroupSnapshot after = (await FindGroupAsync(connection, transaction, normalized, cancellationToken)
                    .ConfigureAwait(false))!;
                await WriteAuditAsync(connection, transaction, actor, "group.permission", normalized,
                        before.Revision, after.Revision, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                InvalidateGroups();
                return new GroupMutationResult(true, "group.permission-updated", before, after);
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

    private async ValueTask<AccountMutationResult> MutateAccountAsync(
        string name,
        long expectedRevision,
        string actor,
        string operation,
        Action<DbCommand, DbTransaction> configure,
        bool delete = false,
        string? requiredGroup = null,
        CancellationToken cancellationToken = default)
    {
        string normalized = NormalizeName(name, nameof(name));
        ValidateRevision(expectedRevision);
        ValidateActor(actor);
        ensureReady();
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using DbConnection connection = await openAsync(cancellationToken).ConfigureAwait(false);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                AccountSnapshot? before = await FindAccountAsync(connection, transaction, normalized, cancellationToken)
                    .ConfigureAwait(false);
                if (before is null)
                {
                    return new AccountMutationResult(false, "account.not-found");
                }

                if (before.Revision != expectedRevision)
                {
                    return new AccountMutationResult(false, "account.revision-conflict", before);
                }

                if (requiredGroup is not null &&
                    !await GroupExistsAsync(connection, transaction, requiredGroup, cancellationToken).ConfigureAwait(false))
                {
                    return new AccountMutationResult(false, "group.not-found", before);
                }

                await using DbCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                configure(command, transaction);
                command.AddParameter("@name", normalized);
                command.AddParameter("@revision", expectedRevision);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    return new AccountMutationResult(false, "account.revision-conflict", before);
                }

                AccountSnapshot? after = delete ? null :
                    await FindAccountAsync(connection, transaction, normalized, cancellationToken).ConfigureAwait(false);
                await WriteAuditAsync(connection, transaction, actor, operation, normalized,
                        before.Revision, after?.Revision ?? checked(before.Revision + 1), cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new AccountMutationResult(true, operation, before, after);
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

    private async ValueTask<GroupMutationResult> MutateGroupAsync(
        string name,
        long expectedRevision,
        string actor,
        string operation,
        Action<DbCommand> configure,
        string? requiredParent = null,
        bool delete = false,
        CancellationToken cancellationToken = default,
        Func<DbConnection, DbTransaction, string, CancellationToken, ValueTask>? afterUpdate = null)
    {
        string normalized = NormalizeName(name, nameof(name));
        ValidateRevision(expectedRevision);
        ValidateActor(actor);
        if (requiredParent is not null && requiredParent.Equals(normalized, StringComparison.OrdinalIgnoreCase))
        {
            return new GroupMutationResult(false, "group.inheritance-cycle");
        }

        ensureReady();
        await mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using DbConnection connection = await openAsync(cancellationToken).ConfigureAwait(false);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                GroupSnapshot? before = await FindGroupAsync(connection, transaction, normalized, cancellationToken)
                    .ConfigureAwait(false);
                if (before is null)
                {
                    return new GroupMutationResult(false, "group.not-found");
                }

                if (before.Revision != expectedRevision)
                {
                    return new GroupMutationResult(false, "group.revision-conflict", before);
                }

                if (requiredParent is not null)
                {
                    IReadOnlyDictionary<string, GroupSnapshot> groups = ReadGroups(connection, transaction)
                        .ToDictionary(static group => group.Name, StringComparer.OrdinalIgnoreCase);
                    if (!groups.ContainsKey(requiredParent))
                    {
                        return new GroupMutationResult(false, "group.parent-not-found", before);
                    }

                    string? ancestor = requiredParent;
                    var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    while (ancestor is not null && visited.Add(ancestor) && groups.TryGetValue(ancestor, out GroupSnapshot? group))
                    {
                        if (ancestor.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                        {
                            return new GroupMutationResult(false, "group.inheritance-cycle", before);
                        }

                        ancestor = group.ParentName;
                    }
                }

                await using DbCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                configure(command);
                command.AddParameter("@name", normalized);
                command.AddParameter("@revision", expectedRevision);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    return new GroupMutationResult(false, "group.revision-conflict", before);
                }

                if (afterUpdate is not null)
                    await afterUpdate(connection, transaction, normalized, cancellationToken).ConfigureAwait(false);
                GroupSnapshot? after = delete ? null :
                    await FindGroupAsync(connection, transaction, normalized, cancellationToken).ConfigureAwait(false);
                await WriteAuditAsync(connection, transaction, actor, operation, normalized,
                        before.Revision, after?.Revision ?? checked(before.Revision + 1), cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                InvalidateGroups();
                return new GroupMutationResult(true, operation, before, after);
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

    private static IReadOnlyList<GroupSnapshot> ReadGroups(
        DbConnection connection,
        DbTransaction? transaction = null)
    {
        using DbCommand groupsCommand = CreateCommand(connection, transaction,
            "SELECT g.name, g.parent_name, g.revision, p.chat_prefix, p.chat_suffix, p.chat_color " +
            "FROM vega_groups g LEFT JOIN vega_group_presentation p ON p.group_name = g.name ORDER BY g.name;");
        using DbDataReader groupsReader = groupsCommand.ExecuteReader();
        var groups = new Dictionary<string, GroupSnapshot>(StringComparer.OrdinalIgnoreCase);
        while (groupsReader.Read())
        {
            string name = groupsReader.GetString(0);
            groups[name] = new GroupSnapshot(
                name,
                groupsReader.IsDBNull(1) ? null : groupsReader.GetString(1),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
            {
                DeniedPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                Revision = Convert.ToInt64(groupsReader.GetValue(2), CultureInfo.InvariantCulture),
                Presentation = new GroupChatPresentation(
                    groupsReader.IsDBNull(3) ? "" : groupsReader.GetString(3),
                    groupsReader.IsDBNull(4) ? "" : groupsReader.GetString(4),
                    groupsReader.IsDBNull(5) ? null : groupsReader.GetString(5))
            };
        }

        groupsReader.Close();
        using DbCommand permissionsCommand = CreateCommand(connection, transaction,
            "SELECT group_name, permission, effect FROM vega_group_permissions ORDER BY group_name, permission;");
        using DbDataReader permissionsReader = permissionsCommand.ExecuteReader();
        while (permissionsReader.Read())
        {
            if (!groups.TryGetValue(permissionsReader.GetString(0), out GroupSnapshot? group))
            {
                continue;
            }

            string permission = permissionsReader.GetString(1);
            int effect = Convert.ToInt32(permissionsReader.GetValue(2), CultureInfo.InvariantCulture);
            if (effect == (int)PermissionEffect.Grant)
            {
                ((HashSet<string>)group.Permissions).Add(permission);
            }
            else if (effect == (int)PermissionEffect.Deny)
            {
                ((HashSet<string>)group.DeniedPermissions).Add(permission);
            }
        }

        return Array.AsReadOnly(groups.Values
            .Select(static group => group with
            {
                Permissions = group.Permissions.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
                DeniedPermissions = group.DeniedPermissions.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
            })
            .OrderBy(static group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    private static async ValueTask<AccountSnapshot?> FindAccountAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string name,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = CreateCommand(connection, transaction,
            "SELECT id, name, group_name, last_seen_utc, culture, revision FROM vega_accounts WHERE name = @name;");
        command.AddParameter("@name", NormalizeName(name, nameof(name)));
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAccount(reader) : null;
    }

    private static async ValueTask<GroupSnapshot?> FindGroupAsync(
        DbConnection connection,
        DbTransaction transaction,
        string name,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<GroupSnapshot> groups = ReadGroups(connection, transaction);
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return groups.FirstOrDefault(group => group.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static AccountSnapshot ReadAccount(DbDataReader reader) =>
        new(
            new AccountId(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : ParseTime(reader.GetString(3)))
        {
            Culture = reader.IsDBNull(4) ? null : reader.GetString(4),
            Revision = Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture)
        };

    private static async ValueTask<bool> GroupExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string name,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = CreateCommand(connection, transaction,
            "SELECT COUNT(*) FROM vega_groups WHERE name = @name;");
        command.AddParameter("@name", name);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async ValueTask<int> AllocateAccountIdAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            int candidate = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            await using DbCommand command = CreateCommand(connection, transaction,
                "SELECT COUNT(*) FROM vega_accounts WHERE id = @id;");
            command.AddParameter("@id", candidate);
            if (Convert.ToInt32(
                    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture) == 0)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Unable to allocate a stable account identifier.");
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
        command.AddParameter("@actor", actor.Trim());
        command.AddParameter("@operation", operation);
        command.AddParameter("@target", target);
        command.AddParameter("@before", beforeRevision);
        command.AddParameter("@after", afterRevision);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DbCommand CreateCommand(
        DbConnection connection,
        DbTransaction? transaction,
        string sql)
    {
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            PasswordIterations,
            HashAlgorithmName.SHA256,
            32);
        return $"pbkdf2-sha256${PasswordIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string encoded)
    {
        string[] parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int iterations))
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expected = Convert.FromBase64String(parts[3]);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string NormalizeName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        string normalized = value.Trim();
        if (normalized.Length > 255)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Value must not exceed 255 characters.");
        }

        return normalized.ToLowerInvariant();
    }

    private static void ValidatePassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (password.Length < 8 || password.Length > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(password), "Password length must be between 8 and 1024 characters.");
        }
    }

    private static void ValidateActor(string actor) => _ = NormalizeName(actor, nameof(actor));

    private static void ValidateRevision(long revision)
    {
        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }
    }

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private void InvalidateGroups()
    {
        lock (groupCacheSync)
        {
            groupCache = null;
        }
    }
}
