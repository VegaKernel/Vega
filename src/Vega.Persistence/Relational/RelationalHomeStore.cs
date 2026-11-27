using System.Data.Common;
using System.Globalization;
using Vega.Contracts.Accounts;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Persistence.Relational;

internal sealed class RelationalHomeStore(Func<CancellationToken, ValueTask<DbConnection>> open, Action ensureReady) : IPlayerHomeStore
{
    internal const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS vega_homes (
            account_id INTEGER NOT NULL,
            world_id VARCHAR(255) NOT NULL,
            world_file_id VARCHAR(32) NOT NULL,
            name_key VARCHAR(32) NOT NULL,
            home_name VARCHAR(32) NOT NULL,
            x DOUBLE PRECISION NOT NULL,
            y DOUBLE PRECISION NOT NULL,
            PRIMARY KEY (account_id, world_id, world_file_id, name_key),
            FOREIGN KEY (account_id) REFERENCES vega_accounts(id) ON DELETE CASCADE
        )
        """;

    private const string Scope = "account_id = @account AND world_id = @world AND world_file_id = @file";

    public ValueTask<IReadOnlyList<PlayerHome>> ListAsync(AccountId account, WorldId world, Guid worldFile, CancellationToken token = default) =>
        ReadAsync(account, world, worldFile, null, token);

    public async ValueTask<PlayerHome?> FindAsync(AccountId account, WorldId world, Guid worldFile, string name, CancellationToken token = default)
    {
        ValidateName(name);
        return (await ReadAsync(account, world, worldFile, name, token).ConfigureAwait(false)).SingleOrDefault();
    }

    private async ValueTask<IReadOnlyList<PlayerHome>> ReadAsync(AccountId account, WorldId world, Guid worldFile, string? name, CancellationToken token)
    {
        ValidateScope(account, world, worldFile);
        ensureReady();
        await using var connection = await open(token).ConfigureAwait(false);
        await using var command = Command(connection, null, "SELECT home_name, x, y FROM vega_homes WHERE " + Scope +
            (name is null ? " ORDER BY name_key;" : " AND name_key = @key;"), account, world, worldFile, name);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var points = new List<PlayerHome>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            points.Add(new(account, world, worldFile, reader.GetString(0), new(Convert.ToSingle(reader.GetValue(1), CultureInfo.InvariantCulture), Convert.ToSingle(reader.GetValue(2), CultureInfo.InvariantCulture))));
        return points.AsReadOnly();
    }

    public async ValueTask<HomeWriteResult> SetAsync(PlayerHome home, int limit, CancellationToken token = default)
    {
        ValidateScope(home.AccountId, home.WorldId, home.WorldFileId);
        ValidateName(home.Name);
        if (limit < 0 || !float.IsFinite(home.Position.X) || !float.IsFinite(home.Position.Y) || home.Position.X < 0 || home.Position.Y < 0)
            throw new ArgumentException("Invalid home limit or coordinates.", nameof(home));
        if (limit == 0) return HomeWriteResult.LimitReached;
        ensureReady();
        await using var connection = await open(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);

        // Serialize writes for the same account across runtimes, including quota checks. The no-op update
        // takes the provider's write lock without changing account revisions or permissions.
        if (!await LockAccountAsync(connection, transaction, home.AccountId, token).ConfigureAwait(false))
            return HomeWriteResult.AccountNotFound;
        await using var count = Command(connection, transaction, "SELECT COUNT(*) FROM vega_homes WHERE " + Scope + " AND name_key = @key;", home.AccountId, home.WorldId, home.WorldFileId, home.Name);
        bool exists = Convert.ToInt64(await count.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            count.CommandText = "SELECT COUNT(*) FROM vega_homes WHERE " + Scope + ";";
            // Some providers reject unused parameters in a command.
            count.Parameters.RemoveAt("@key");
            if (Convert.ToInt64(await count.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) >= limit)
                return HomeWriteResult.LimitReached;
        }
        await using var write = Command(connection, transaction, exists
            ? "UPDATE vega_homes SET home_name = @name, x = @x, y = @y WHERE " + Scope + " AND name_key = @key;"
            : "INSERT INTO vega_homes (account_id, world_id, world_file_id, name_key, home_name, x, y) VALUES (@account,@world,@file,@key,@name,@x,@y);",
            home.AccountId, home.WorldId, home.WorldFileId, home.Name);
        write.AddParameter("@name", home.Name);
        write.AddParameter("@x", (double)home.Position.X);
        write.AddParameter("@y", (double)home.Position.Y);
        await write.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return HomeWriteResult.Applied;
    }

    public async ValueTask<bool> DeleteAsync(AccountId account, WorldId world, Guid worldFile, string name, CancellationToken token = default)
    {
        ValidateScope(account, world, worldFile);
        ValidateName(name);
        ensureReady();
        await using var connection = await open(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        if (!await LockAccountAsync(connection, transaction, account, token).ConfigureAwait(false)) return false;
        await using var command = Command(connection, transaction, "DELETE FROM vega_homes WHERE " + Scope + " AND name_key = @key;", account, world, worldFile, name);
        bool deleted = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return deleted;
    }

    public async ValueTask<int> ImportAsync(WorldId world, Guid worldFile, IReadOnlyList<PlayerHome> homes, CancellationToken token)
    {
        if (homes.Count > 100_000 || homes.Any(home => home.WorldId != world || home.WorldFileId != worldFile ||
            home.AccountId.Value <= 0 || !PlayerHome.IsValidName(home.Name) || !float.IsFinite(home.Position.X) || !float.IsFinite(home.Position.Y) ||
            home.Position.X < 0 || home.Position.Y < 0) || homes.Select(home => (home.AccountId, home.Name.ToLowerInvariant())).Distinct().Count() != homes.Count)
            throw new InvalidDataException("Homes import contains invalid or duplicate entries.");
        ValidateScope(new AccountId(1), world, worldFile);
        ensureReady();
        await using var connection = await open(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM vega_homes WHERE world_id = @world AND world_file_id = @file;";
            count.AddParameter("@world", world.Value);
            count.AddParameter("@file", worldFile.ToString("N"));
            if (Convert.ToInt64(await count.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
                throw new InvalidDataException("Destination world already contains homes; no existing home was replaced.");
        }
        foreach (AccountId account in homes.Select(home => home.AccountId).Distinct().OrderBy(account => account.Value))
            if (!await LockAccountAsync(connection, transaction, account, token).ConfigureAwait(false))
                throw new InvalidDataException("Homes reference missing accounts. Import accounts first.");
        foreach (var home in homes)
        {
            await using var command = Command(connection, transaction, "INSERT INTO vega_homes (account_id, world_id, world_file_id, name_key, home_name, x, y) VALUES (@account,@world,@file,@key,@name,@x,@y);",
                home.AccountId, world, worldFile, home.Name);
            command.AddParameter("@name", home.Name);
            command.AddParameter("@x", (double)home.Position.X);
            command.AddParameter("@y", (double)home.Position.Y);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return homes.Count;
    }

    private static async ValueTask<bool> LockAccountAsync(DbConnection connection, DbTransaction transaction, AccountId account, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE vega_accounts SET revision = revision WHERE id = @account;";
        command.AddParameter("@account", account.Value);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        command.CommandText = "SELECT COUNT(*) FROM vega_accounts WHERE id = @account;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
    }

    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, AccountId account, WorldId world, Guid file, string? name)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.AddParameter("@account", account.Value);
        command.AddParameter("@world", world.Value);
        command.AddParameter("@file", file.ToString("N"));
        if (name is not null) command.AddParameter("@key", name.ToLowerInvariant());
        return command;
    }

    private static void ValidateName(string name)
    {
        if (!PlayerHome.IsValidName(name)) throw new ArgumentException("Invalid home name.", nameof(name));
    }

    private static void ValidateScope(AccountId account, WorldId world, Guid file)
    {
        if (account.Value <= 0 || string.IsNullOrWhiteSpace(world.Value) || world.Value.Length > 255 || file == Guid.Empty)
            throw new ArgumentException("Invalid home scope.");
    }
}
