using System.Data.Common;
using Npgsql;
using Vega.Persistence.Relational;

namespace Vega.Persistence.PostgreSql;

internal sealed class PostgreSqlConnectionFactory : IRelationalConnectionFactory
{
    private readonly string connectionString;

    public PostgreSqlConnectionFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = "Vega",
            Pooling = true
        };
        this.connectionString = builder.ConnectionString;
    }

    public async ValueTask<DbConnection> OpenAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public DbConnection Open()
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
