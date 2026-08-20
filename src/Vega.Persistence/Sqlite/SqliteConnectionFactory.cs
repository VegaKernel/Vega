using Microsoft.Data.Sqlite;

namespace Vega.Persistence.Sqlite;

internal sealed class SqliteConnectionFactory
{
    private readonly string connectionString;
    private readonly int busyTimeoutMilliseconds;

    public SqliteConnectionFactory(SqliteVegaDatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };
        connectionString = builder.ToString();
        busyTimeoutMilliseconds = checked((int)options.BusyTimeout.TotalMilliseconds);
    }

    public async ValueTask<SqliteConnection> OpenAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            Configure(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private async ValueTask ConfigureAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = {busyTimeoutMilliseconds};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Configure(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = {busyTimeoutMilliseconds};";
        command.ExecuteNonQuery();
    }
}
