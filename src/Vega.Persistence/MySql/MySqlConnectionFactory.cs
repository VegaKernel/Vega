using System.Data.Common;
using MySqlConnector;
using Vega.Persistence.Relational;

namespace Vega.Persistence.MySql;

internal sealed class MySqlConnectionFactory : IRelationalConnectionFactory
{
    private readonly string connectionString;

    public MySqlConnectionFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = "Vega",
            Pooling = true,
            UseAffectedRows = false,
            // All Vega relational identifiers use textual storage and readers.
            // MySqlConnector otherwise exposes CHAR(36) identifiers as Guid.
            GuidFormat = MySqlGuidFormat.None
        };
        this.connectionString = builder.ConnectionString;
    }

    public async ValueTask<DbConnection> OpenAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = new MySqlConnection(connectionString);
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
        var connection = new MySqlConnection(connectionString);
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
