namespace Vega.Persistence.Relational;

internal static class AccountMetadataSchema
{
    internal const string Sql = """
        CREATE TABLE IF NOT EXISTS vega_account_metadata (
            account_id INTEGER NOT NULL PRIMARY KEY,
            registered_utc VARCHAR(64) NULL,
            last_ip VARCHAR(64) NULL,
            last_uuid VARCHAR(255) NULL,
            FOREIGN KEY (account_id) REFERENCES vega_accounts(id) ON DELETE CASCADE
        )
        """;
    internal const string MuteSql = """
        CREATE TABLE IF NOT EXISTS vega_mute_identity (
            mute_id VARCHAR(36) NOT NULL PRIMARY KEY,
            last_ip VARCHAR(64) NULL,
            last_uuid VARCHAR(255) NULL,
            FOREIGN KEY (mute_id) REFERENCES vega_mutes(id) ON DELETE CASCADE
        )
        """;
}
