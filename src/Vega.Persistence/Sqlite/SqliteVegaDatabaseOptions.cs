namespace Vega.Persistence.Sqlite;

public sealed record SqliteVegaDatabaseOptions
{
    public SqliteVegaDatabaseOptions(string databasePath, TimeSpan? busyTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        TimeSpan timeout = busyTimeout ?? TimeSpan.FromSeconds(5);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(busyTimeout),
                "SQLite busy timeout must be greater than zero and no more than one minute.");
        }

        DatabasePath = Path.GetFullPath(databasePath);
        BusyTimeout = timeout;
    }

    public string DatabasePath { get; }

    public TimeSpan BusyTimeout { get; }

    public static SqliteVegaDatabaseOptions CreateDefault(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        return new SqliteVegaDatabaseOptions(
            Path.Combine(baseDirectory, "data", "vega.db"));
    }
}
