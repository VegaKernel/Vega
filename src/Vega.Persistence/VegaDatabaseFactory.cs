using Vega.Persistence.MySql;
using Vega.Persistence.PostgreSql;
using Vega.Persistence.Sqlite;

namespace Vega.Persistence;

public static class VegaDatabaseFactory
{
    public static IVegaDatabase Create(
        VegaDatabaseOptions options,
        string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        VegaDatabaseProvider provider = options.ParseProvider();
        if (provider == VegaDatabaseProvider.Sqlite &&
            string.IsNullOrWhiteSpace(options.SqlitePath))
        {
            throw new InvalidDataException("sqlitePath must not be empty.");
        }

        return provider switch
        {
            VegaDatabaseProvider.Sqlite => SqliteVegaDatabase.Create(
                new SqliteVegaDatabaseOptions(
                    ResolvePath(baseDirectory, options.SqlitePath),
                    TimeSpan.FromMilliseconds(options.BusyTimeoutMilliseconds))),
            VegaDatabaseProvider.PostgreSql => PostgreSqlVegaDatabase.Create(
                options.ResolveExternalConnectionString()),
            VegaDatabaseProvider.MySql => MySqlVegaDatabase.Create(
                options.ResolveExternalConnectionString()),
            _ => throw new InvalidDataException(
                $"Unsupported Vega database provider '{options.Provider}'.")
        };
    }

    private static string ResolvePath(string baseDirectory, string configuredPath) =>
        Path.GetFullPath(
            Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(baseDirectory, configuredPath));
}
