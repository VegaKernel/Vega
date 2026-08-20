using System.Text.Json;

namespace Vega.Persistence;

public enum VegaDatabaseProvider
{
    Sqlite,
    PostgreSql,
    MySql
}

public sealed record VegaDatabaseOptions(
    string Provider,
    string SqlitePath,
    int BusyTimeoutMilliseconds,
    string ConnectionStringEnvironmentVariable,
    string? ConnectionString)
{
    public static VegaDatabaseOptions Default { get; } = new(
        "sqlite",
        "data/vega.db",
        5_000,
        "VEGA_DATABASE_CONNECTION_STRING",
        null);

    public VegaDatabaseProvider ParseProvider()
    {
        if (string.IsNullOrWhiteSpace(Provider))
        {
            throw new InvalidDataException("Database provider must not be empty.");
        }

        return Provider.Trim().ToLowerInvariant() switch
        {
            "sqlite" => VegaDatabaseProvider.Sqlite,
            "postgres" or "postgresql" => VegaDatabaseProvider.PostgreSql,
            "mysql" or "mariadb" => VegaDatabaseProvider.MySql,
            _ => throw new InvalidDataException(
                $"Unknown Vega database provider '{Provider}'. Expected sqlite, postgresql or mysql.")
        };
    }

    public string ResolveExternalConnectionString()
    {
        if (string.IsNullOrWhiteSpace(ConnectionStringEnvironmentVariable))
        {
            throw new InvalidDataException(
                "connectionStringEnvironmentVariable must not be empty.");
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable(
            ConnectionStringEnvironmentVariable);
        string? resolved = !string.IsNullOrWhiteSpace(fromEnvironment)
            ? fromEnvironment
            : ConnectionString;
        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new InvalidDataException(
                $"Database provider '{Provider}' requires a connection string. " +
                $"Set environment variable '{ConnectionStringEnvironmentVariable}' " +
                "or the connectionString configuration value.");
        }

        return resolved;
    }
}

public static class VegaDatabaseOptionsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static VegaDatabaseOptions Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return VegaDatabaseOptions.Default;
        }

        try
        {
            VegaDatabaseOptions options = JsonSerializer.Deserialize<VegaDatabaseOptions>(
                File.ReadAllText(path),
                SerializerOptions) ?? throw new InvalidDataException(
                    $"Database configuration '{Path.GetFullPath(path)}' is empty.");
            Validate(options);
            return options;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Database configuration '{Path.GetFullPath(path)}' is invalid JSON.",
                ex);
        }
        catch (IOException ex)
        {
            throw new InvalidDataException(
                $"Database configuration '{Path.GetFullPath(path)}' cannot be read.",
                ex);
        }
    }

    private static void Validate(VegaDatabaseOptions options)
    {
        _ = options.ParseProvider();
        if (string.IsNullOrWhiteSpace(options.SqlitePath))
        {
            throw new InvalidDataException("sqlitePath must not be empty.");
        }

        if (options.BusyTimeoutMilliseconds is < 100 or > 60_000)
        {
            throw new InvalidDataException(
                "busyTimeoutMilliseconds must be between 100 and 60000.");
        }

        if (string.IsNullOrWhiteSpace(options.ConnectionStringEnvironmentVariable))
        {
            throw new InvalidDataException(
                "connectionStringEnvironmentVariable must not be empty.");
        }
    }
}
