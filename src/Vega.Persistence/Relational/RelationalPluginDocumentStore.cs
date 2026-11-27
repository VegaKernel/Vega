using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vega.Contracts.Modules;

namespace Vega.Persistence.Relational;

internal sealed class RelationalPluginDocumentStore(
    Func<CancellationToken, ValueTask<DbConnection>> open, Action ensureReady, bool mysql = false) : IPluginDocumentStore
{
    internal const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS vega_plugin_documents (
            plugin_id VARCHAR(64) NOT NULL,
            collection_name VARCHAR(80) NOT NULL,
            document_key VARCHAR(160) NOT NULL,
            revision BIGINT NOT NULL CHECK (revision >= 0),
            value_json TEXT NULL,
            PRIMARY KEY (plugin_id, collection_name, document_key)
        )
        """;

    private const int MaximumDocumentBytes = 48 * 1024;
    private const string MatchKey = "plugin_id = @owner AND collection_name = @collection AND document_key = @key";

    public async ValueTask<PluginDocument?> ReadAsync(string owner, string collection, string key, CancellationToken cancellationToken = default)
    {
        ensureReady();
        string id = OwnerId(owner);
        collection = Normalize(collection, 80, nameof(collection));
        key = Normalize(key, 160, nameof(key));
        await using DbConnection connection = await open(cancellationToken).ConfigureAwait(false);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT document_key, revision, value_json FROM vega_plugin_documents WHERE {MatchKey} AND value_json IS NOT NULL";
        AddKey(command, id, collection, key);
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDocument(reader) : null;
    }

    public async ValueTask<IReadOnlyList<PluginDocument>> ListAsync(string owner, string collection, string? afterKey, int limit,
        CancellationToken cancellationToken = default)
    {
        ensureReady();
        string id = OwnerId(owner);
        collection = Normalize(collection, 80, nameof(collection));
        if (afterKey is not null) afterKey = Normalize(afterKey, 160, nameof(afterKey));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using DbConnection connection = await open(cancellationToken).ConfigureAwait(false);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT document_key, revision, value_json FROM vega_plugin_documents " +
            "WHERE plugin_id = @owner AND collection_name = @collection AND value_json IS NOT NULL " +
            (afterKey is null ? "" : "AND document_key > @key ") + "ORDER BY document_key LIMIT @limit";
        command.AddParameter("@owner", id);
        command.AddParameter("@collection", collection);
        if (afterKey is not null) command.AddParameter("@key", afterKey);
        command.AddParameter("@limit", limit);
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var documents = new List<PluginDocument>(limit);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) documents.Add(ReadDocument(reader));
        return documents.AsReadOnly();
    }

    public async ValueTask<bool> CommitAsync(string owner, IReadOnlyList<PluginDocumentMutation> mutations,
        CancellationToken cancellationToken = default)
    {
        ensureReady();
        ArgumentNullException.ThrowIfNull(mutations);
        string id = OwnerId(owner);
        if (mutations.Count is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(mutations), "Between 1 and 32 mutations are allowed.");
        var normalized = new List<PluginDocumentMutation>(mutations.Count);
        var keys = new HashSet<(string Collection, string Key)>();
        foreach (PluginDocumentMutation mutation in mutations)
        {
            ArgumentNullException.ThrowIfNull(mutation);
            string collection = Normalize(mutation.Collection, 80, nameof(mutations));
            string key = Normalize(mutation.Key, 160, nameof(mutations));
            if (!keys.Add((collection, key)) || mutation.ExpectedRevision < 0 || mutation.ExpectedRevision == long.MaxValue ||
                (mutation.ExpectedRevision == 0 && mutation.Json is null)) throw new ArgumentException("Invalid or repeated document mutation.", nameof(mutations));
            if (mutation.Json is { } json)
            {
                if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes) throw new ArgumentException("Document exceeds 48 KiB.", nameof(mutations));
                using JsonDocument parsed = JsonDocument.Parse(json);
            }
            normalized.Add(mutation with { Collection = collection, Key = key });
        }

        await using DbConnection connection = await open(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Always obtain key locks in the same order for multi-document transactions.
        foreach (PluginDocumentMutation mutation in normalized.OrderBy(m => m.Collection, StringComparer.Ordinal).ThenBy(m => m.Key, StringComparer.Ordinal))
        {
            if (mutation.ExpectedRevision == 0)
            {
                // Materialize a tombstone if absent. The subsequent conditional UPDATE is the only success test;
                // MySQL affected-row conventions for no-op upserts differ by connection configuration.
                await using DbCommand seed = connection.CreateCommand();
                seed.Transaction = transaction;
                seed.CommandText = "INSERT INTO vega_plugin_documents (plugin_id, collection_name, document_key, revision, value_json) VALUES (@owner, @collection, @key, 0, NULL) " +
                    (mysql ? "ON DUPLICATE KEY UPDATE plugin_id = plugin_id" : "ON CONFLICT (plugin_id, collection_name, document_key) DO NOTHING");
                AddKey(seed, id, mutation.Collection, mutation.Key);
                await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using DbCommand update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"UPDATE vega_plugin_documents SET revision = revision + 1, value_json = @json WHERE {MatchKey} AND " +
                (mutation.ExpectedRevision == 0 ? "value_json IS NULL" : "revision = @revision AND value_json IS NOT NULL");
            AddKey(update, id, mutation.Collection, mutation.Key);
            update.AddParameter("@json", (object?)mutation.Json ?? DBNull.Value);
            if (mutation.ExpectedRevision > 0) update.AddParameter("@revision", mutation.ExpectedRevision);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static PluginDocument ReadDocument(DbDataReader reader) => new(reader.GetString(0),
        Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture), reader.GetString(2));

    private static string OwnerId(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner.ToUpperInvariant())));
    }

    private static string Normalize(string value, int maximumLength, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (value.Length > maximumLength || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':')))
            throw new ArgumentException("Document names use ASCII letters, digits, '.', '_', '-' and ':'.", parameter);
        return value.ToLowerInvariant();
    }

    private static void AddKey(DbCommand command, string owner, string collection, string key)
    {
        command.AddParameter("@owner", owner);
        command.AddParameter("@collection", collection);
        command.AddParameter("@key", key);
    }
}
