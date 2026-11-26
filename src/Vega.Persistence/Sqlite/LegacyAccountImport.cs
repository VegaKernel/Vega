namespace Vega.Persistence.Sqlite;

/// <summary>An offline account import row. The password hash is never included in a generated ToString.</summary>
public sealed class LegacyAccountImport(int id, string name, string group, string passwordHash, DateTimeOffset? lastSeen)
{
    public int Id { get; } = id;
    public string Name { get; } = name;
    public string Group { get; } = group;
    public string PasswordHash { get; } = passwordHash;
    public DateTimeOffset? LastSeen { get; } = lastSeen;
}
