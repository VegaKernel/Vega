using System.Data.Common;
using System.Globalization;

namespace Vega.Persistence.Relational;

/// <summary>The single release baseline shared by all three relational providers. Provider locks are held by callers.</summary>
internal static class RelationalSchemaBaseline
{
    internal const int Version = 1;
    internal const string Name = "vega-initial";
    internal const string HistoryTableSql = """
        CREATE TABLE IF NOT EXISTS vega_schema_history (
            version INTEGER NOT NULL PRIMARY KEY,
            name VARCHAR(200) NOT NULL,
            applied_at_utc VARCHAR(40) NOT NULL
        );
        """;

    private const string GroupPresentationSql = """
        CREATE TABLE IF NOT EXISTS vega_group_presentation (
            group_name VARCHAR(255) NOT NULL PRIMARY KEY,
            chat_prefix VARCHAR(64) NOT NULL,
            chat_suffix VARCHAR(64) NOT NULL,
            chat_color VARCHAR(6) NULL,
            FOREIGN KEY (group_name) REFERENCES vega_groups(name) ON DELETE CASCADE
        )
        """;

    internal static IEnumerable<string> AdditionalTables(bool mysql, bool sqlite)
    {
        string presentation = sqlite ? GroupPresentationSql.Replace("group_name VARCHAR(255)", "group_name TEXT COLLATE NOCASE", StringComparison.Ordinal) : GroupPresentationSql;
        string playtime = RelationalPlaytimeStore.SchemaSql;
        string muteIdentity = AccountMetadataSchema.MuteSql;
        string documents = RelationalPluginDocumentStore.SchemaSql;
        string homes = RelationalHomeStore.SchemaSql;
        if (mysql)
        {
            playtime = Utf8Binary(playtime, "world_id VARCHAR(255)");
            muteIdentity = muteIdentity.Replace("mute_id VARCHAR(36)", "mute_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin", StringComparison.Ordinal);
            foreach (string key in new[] { "VARCHAR(64)", "VARCHAR(80)", "VARCHAR(160)" })
                documents = documents.Replace(key, key + " CHARACTER SET ascii COLLATE ascii_bin", StringComparison.Ordinal);
            homes = Utf8Binary(Utf8Binary(homes, "world_id VARCHAR(255)"), "name_key VARCHAR(32)");
        }
        string Table(string sql) => sql + (mysql ? " ENGINE=InnoDB;" : ";");
        yield return Table(presentation);
        yield return Table(playtime);
        if (!mysql) yield return RelationalPlaytimeStore.AccountIndexSql;
        yield return Table(AccountMetadataSchema.Sql);
        yield return Table(muteIdentity);
        yield return Table(documents);
        yield return Table(homes);
    }

    private static string Utf8Binary(string sql, string column) => sql.Replace(column, column + " CHARACTER SET utf8mb4 COLLATE utf8mb4_bin", StringComparison.Ordinal);

    internal static async ValueTask<int> InitializeLockedAsync(DbConnection connection, DbTransaction? transaction,
        IEnumerable<string> sections, CancellationToken token)
    {
        await ExecuteAsync(connection, transaction, HistoryTableSql, token).ConfigureAwait(false);
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT version, name FROM vega_schema_history ORDER BY version;";
            await using var reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                int existing = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                if (existing > Version) throw NewerSchema(existing);
                bool current = existing == Version && reader.GetString(1) == Name;
                bool extra = await reader.ReadAsync(token).ConfigureAwait(false);
                if (current && extra && Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture) > Version)
                    throw NewerSchema(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
                if (!current || extra)
                    throw new InvalidDataException("This database uses a pre-release or unsupported Vega schema. Its schema and data were not changed. Create a fresh release database or retain the matching development binary to export its data.");
                return Version;
            }
        }
        foreach (string sql in sections) await ExecuteAsync(connection, transaction, sql, token).ConfigureAwait(false);
        await using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = "INSERT INTO vega_schema_history (version, name, applied_at_utc) VALUES (@version, @name, @appliedAt);";
        write.AddParameter("@version", Version);
        write.AddParameter("@name", Name);
        write.AddParameter("@appliedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await write.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        return Version;
    }

    private static InvalidDataException NewerSchema(int version) => new($"Database schema {version} is newer than supported schema {Version}. Its schema and data were not changed.");

    internal static async ValueTask ExecuteAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
