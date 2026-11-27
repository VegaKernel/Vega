using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using MySqlConnector;

namespace Vega.Runtime;

/// <summary>Explicit offline TShock source. Never initializes a schema or opens a writable source transaction.</summary>
internal sealed class TShockMigrationSource
{
    public const string MySqlArgument = "@mysql";
    public const string MySqlEnvironmentVariable = "VEGA_TSHOCK_MYSQL_CONNECTION";
    private readonly string? mysqlConnectionString;
    private readonly string? mysqlDatabase;

    private TShockMigrationSource(string sqlitePath) => SqlitePath = sqlitePath;
    private TShockMigrationSource(MySqlConnectionStringBuilder mysql)
    {
        mysqlConnectionString = mysql.ConnectionString;
        mysqlDatabase = mysql.Database;
    }

    public string? SqlitePath { get; }
    public bool IsMySql => mysqlConnectionString is not null;

    public static TShockMigrationSource Parse(string argument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument);
        return argument == MySqlArgument
            ? FromMySqlConnectionString(Environment.GetEnvironmentVariable(MySqlEnvironmentVariable))
            : new(Path.GetFullPath(argument));
    }

    // Programmatic/test API only: the CLI deliberately accepts a sentinel, not credentials.
    internal static TShockMigrationSource FromMySqlConnectionString(string? connectionString)
    {
        var settings = ParseMySqlSettings(connectionString);
        if (string.IsNullOrWhiteSpace(settings.Server) || string.IsNullOrWhiteSpace(settings.UserID))
            throw new InvalidDataException("The TShock MySQL source must explicitly identify a server, user and database.");
        settings.Pooling = false;
        settings.PersistSecurityInfo = false;
        settings.AllowLoadLocalInfile = false;
        settings.ConnectionTimeout = settings.ConnectionTimeout is 0 or > 15 ? 15 : settings.ConnectionTimeout;
        settings.DefaultCommandTimeout = settings.DefaultCommandTimeout is 0 or > 60 ? 60 : settings.DefaultCommandTimeout;
        return new(settings);
    }

    private static MySqlConnectionStringBuilder ParseMySqlSettings(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidDataException("Explicit MySQL connection settings and a database name are required.");
        try
        {
            var settings = new MySqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(settings.Database))
                throw new InvalidDataException("An explicit MySQL database name is required.");
            return settings;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            // Never forward parser diagnostics that might echo connection-string values.
            throw new InvalidDataException("MySQL connection settings are invalid.");
        }
    }

    public void EnsureDifferentMySqlDestination(string connectionString)
    {
        if (!IsMySql) return;
        var target = ParseMySqlSettings(connectionString);
        // Conservative even across aliases/hosts: identical database names cannot
        // prove the stores distinct without contacting the destination. Reject
        // before any destination schema initialization can touch the source.
        if (string.Equals(mysqlDatabase, target.Database, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("TShock and Vega MySQL database names must differ for offline migration.");
    }

    internal string GroupsIdentifier => IsMySql ? "`Groups`" : "\"Groups\"";

    public TShockMigrationReadSession OpenReadOnly()
    {
        DbConnection connection = IsMySql
            ? new MySqlConnection(mysqlConnectionString!)
            : new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = SqlitePath!, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ConnectionString);
        try
        {
            connection.Open();
            // MySQL explicitly enforces read-only and supplies a consistent
            // snapshot across source-account and region queries. SQLite keeps
            // the previous read-only file semantics (no writer lock).
            DbTransaction? transaction = connection is MySqlConnection mysql
                ? mysql.BeginTransaction(IsolationLevel.RepeatableRead, isReadOnly: true) : null;
            return new(connection, transaction);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static void AddParameter(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

internal sealed class TShockMigrationReadSession(DbConnection connection, DbTransaction? transaction) : IDisposable
{
    public DbCommand CreateCommand()
    {
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        return command;
    }

    public void Dispose()
    {
        try { transaction?.Dispose(); }
        finally { connection.Dispose(); }
    }
}
